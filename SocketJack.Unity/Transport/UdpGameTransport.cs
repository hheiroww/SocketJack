using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace SocketJack.Multiplayer.Transport
{
    /// <summary>
    /// A manually pumped, bounded datagram transport for native Unity players and dedicated servers.
    /// Provides reliable unordered delivery by default, explicit reliable ordered delivery,
    /// and unreliable sequenced channels without a thread per peer.
    /// Cookies prevent off-path source spoofing from allocating a connection. This protocol does not encrypt traffic.
    /// </summary>
    public sealed class UdpGameTransport : IGameTransport
    {
        // SJG1 protocol family, wire version 2. Integer fields are little endian.
        private const uint Magic = 0x31474a53;
        private const byte WireVersion = 2;
        private const int HeaderSize = PacketBudget.ReliableHeaderSize;
        private const int CookieSize = 24;
        private const int SettingsSize = 5;
        private enum Kind : byte { Hello = 1, Challenge, Connect, Welcome, Reject, Data, Ack, Heartbeat, Disconnect }

        private sealed class Pending
        {
            public byte[] Packet;
            public double SentAt;
            public int Retransmits;
            public LinkedListNode<ulong> Node;
        }

        private sealed class ChannelState
        {
            public uint NextSendReliable = 1;
            public uint NextSendUnordered = 1;
            public uint NextSendSequenced;
            public uint NextReceiveReliable = 1;
            public uint LastReceiveSequenced;
            public bool HasReceiveSequenced;
            public uint NextReceiveUnordered = 1;
            public readonly HashSet<uint> ReceivedUnordered = new HashSet<uint>();
            public readonly Dictionary<uint, byte[]> Reordered = new Dictionary<uint, byte[]>();
        }

        private sealed class Peer
        {
            public ulong Id;
            public IPEndPoint Endpoint;
            public byte[] Token;
            public byte[] Nonce;
            public int PacketSize;
            public ChannelState[] Channels;
            public double LastReceive;
            public double LastSend;
            public int PendingBytes;
            public int BufferedBytes;
            public int BufferedPackets;
            public bool Active = true;
            public LinkedListNode<Peer> Node;
            public readonly Dictionary<ulong, Pending> Pending = new Dictionary<ulong, Pending>();
            public readonly LinkedList<ulong> RetryQueue = new LinkedList<ulong>();
            public LinkedListNode<ulong> RetryCursor;
        }

        private sealed class QueuedEvent
        {
            public TransportEvent Event;
            public Peer Peer;
        }

        private readonly TransportOptions _options;
        private readonly byte[] _receive;
        private readonly Dictionary<ulong, Peer> _peers = new Dictionary<ulong, Peer>();
        private readonly Dictionary<IPEndPoint, Peer> _endpoints = new Dictionary<IPEndPoint, Peer>();
        private readonly LinkedList<Peer> _maintenance = new LinkedList<Peer>();
        private readonly Queue<QueuedEvent> _events = new Queue<QueuedEvent>();
        private LinkedListNode<Peer> _maintenanceCursor;
        private Socket _socket;
        private HMACSHA256 _cookies;
        private ulong _nextPeerId;
        private bool _connecting;
        private bool _disposed;
        private bool _polling;
        private IPEndPoint _remote;
        private byte[] _nonce;
        private byte[] _cookie;
        private byte[] _connectionData;
        private double _connectStarted;
        private double _nextHandshake;

        public UdpGameTransport(TransportOptions options = null)
        {
            _options = (options ?? new TransportOptions()).Snapshot();
            _options.ValidateUdp();
            Budget = PacketBudget.FromOptions(_options);
            // One extra byte detects oversize datagrams on platforms that truncate instead of throwing.
            _receive = new byte[_options.MaxPacketSize + 1];
        }

        public TransportType Type => TransportType.Udp;
        public bool IsServer { get; private set; }
        public bool IsRunning => _socket != null;
        public ulong LocalPeerId { get; private set; }
        public IPEndPoint LocalEndPoint => _socket?.LocalEndPoint as IPEndPoint;
        public int MaxPayloadSize => _options.MaxPacketSize - HeaderSize;
        public PacketBudget Budget { get; }
        public event Action<TransportEvent> EventReceived;

        public void StartServer(IPEndPoint endpoint)
        {
            EnsureCanStart(endpoint);
            Open(endpoint, true);
            IsServer = true;
            _cookies = new HMACSHA256(RandomBytes(32));
        }

        public void StartClient(IPEndPoint endpoint, byte[] connectionData = null)
        {
            EnsureCanStart(endpoint);
            if (endpoint.Port == 0) throw new ArgumentOutOfRangeException(nameof(endpoint), "A remote port is required.");
            if ((connectionData?.Length ?? 0) > _options.MaxConnectionDataSize)
                throw new ArgumentOutOfRangeException(nameof(connectionData));
            Open(new IPEndPoint(endpoint.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0), false);
            IsServer = false;
            _remote = CopyEndpoint(endpoint);
            _nonce = RandomBytes(16);
            _connectionData = connectionData == null ? Array.Empty<byte>() : (byte[])connectionData.Clone();
            _connecting = true;
            _connectStarted = Now;
            _nextHandshake = 0;
        }

        /// <summary>Receives at most maxEvents datagrams, dispatches at most maxEvents callbacks,
        /// and services at most maxEvents peers and maxEvents retransmissions. Pump frequently.</summary>
        public void Poll(int maxEvents = 256)
        {
            if (maxEvents < 1) throw new ArgumentOutOfRangeException(nameof(maxEvents));
            if (_disposed) throw new ObjectDisposedException(nameof(UdpGameTransport));
            if (_polling) throw new InvalidOperationException("Poll cannot be called recursively.");
            if (_socket == null) return;
            _polling = true;
            try
            {
                int callbacks = 0;
                Dispatch(ref callbacks, maxEvents);
                if (_socket == null || callbacks == maxEvents) return;
                double now = Now;
                if (_connecting)
                {
                    if (now - _connectStarted >= _options.ConnectTimeout.TotalSeconds)
                    {
                        _connecting = false;
                        Enqueue(new TransportEvent(TransportEventType.Disconnected, 0, reason: "Connection timed out"));
                    }
                    else if (now >= _nextHandshake)
                    {
                        SendHandshake();
                        _nextHandshake = now + _options.RetransmitInterval.TotalSeconds;
                    }
                }
                Maintain(now, maxEvents);
                Dispatch(ref callbacks, maxEvents);
                for (int count = 0; count < maxEvents && callbacks < maxEvents && _socket != null; count++)
                {
                    // Polling thousands of mostly idle clients must not throw a WouldBlock
                    // exception for every client and every simulation frame.
                    if (!_socket.Poll(0, SelectMode.SelectRead)) break;
                    EndPoint source = new IPEndPoint(_socket.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
                    int length;
                    try { length = _socket.ReceiveFrom(_receive, 0, _receive.Length, SocketFlags.None, ref source); }
                    catch (SocketException ex) when (Transient(ex.SocketErrorCode)) { break; }
                    catch (SocketException ex) when (ex.SocketErrorCode == SocketError.MessageSize || ex.SocketErrorCode == SocketError.ConnectionReset) { continue; }
                    if (length <= _options.MaxPacketSize)
                        Receive((IPEndPoint)source, length, now);
                    Dispatch(ref callbacks, maxEvents);
                }
            }
            finally { _polling = false; }
        }

        public bool Send(ulong peerId, ArraySegment<byte> payload, DeliveryMode delivery = DeliveryMode.ReliableUnordered, byte channel = 0)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(UdpGameTransport));
            if (payload.Array == null && payload.Count != 0) throw new ArgumentException("Invalid payload.", nameof(payload));
            if (payload.Count > MaxPayloadSize) throw new ArgumentOutOfRangeException(nameof(payload), "Fragment application data to fit MaxPayloadSize.");
            if (channel >= _options.Channels) throw new ArgumentOutOfRangeException(nameof(channel));
            if ((byte)delivery > (byte)DeliveryMode.ReliableUnordered) throw new ArgumentOutOfRangeException(nameof(delivery));
            if (!_peers.TryGetValue(peerId, out var peer)) return false;
            if (channel >= peer.Channels.Length || payload.Count > peer.PacketSize - HeaderSize) return false;
            var state = peer.Channels[channel];
            bool reliable = delivery == DeliveryMode.ReliableOrdered || delivery == DeliveryMode.ReliableUnordered;
            if (reliable && (peer.Pending.Count >= _options.MaxPendingReliablePerPeer ||
                peer.PendingBytes + HeaderSize + payload.Count > _options.MaxQueuedBytesPerPeer)) return false;
            uint sequence = delivery == DeliveryMode.ReliableOrdered ? state.NextSendReliable : delivery == DeliveryMode.ReliableUnordered ? state.NextSendUnordered : delivery == DeliveryMode.UnreliableSequenced ? state.NextSendSequenced : 0;
            byte[] packet = Packet(Kind.Data, peer.Id, peer.Token, channel, delivery, sequence, payload);
            double now = Now;
            if (reliable)
            {
                // Queue before sending, so accepting reliable data always retains ownership until acknowledged.
                ulong key = Key(channel, sequence, delivery);
                peer.Pending.Add(key, new Pending { Packet = packet, SentAt = now, Node = peer.RetryQueue.AddLast(key) });
                peer.PendingBytes += packet.Length;
                if (delivery == DeliveryMode.ReliableOrdered) state.NextSendReliable++;
                else state.NextSendUnordered++;
                TrySend(peer.Endpoint, packet);
            }
            else
            {
                if (!TrySend(peer.Endpoint, packet)) return false;
                if (delivery == DeliveryMode.UnreliableSequenced) state.NextSendSequenced++;
            }
            peer.LastSend = now;
            return true;
        }

        public void Disconnect(ulong peerId, string reason = "Disconnected")
        {
            if (_peers.TryGetValue(peerId, out var peer))
            {
                byte[] text = Encoding.UTF8.GetBytes(reason ?? "Disconnected");
                if (text.Length > 128) Array.Resize(ref text, 128);
                TrySend(peer.Endpoint, Packet(Kind.Disconnect, peer.Id, peer.Token, payload: new ArraySegment<byte>(text)));
                Remove(peer, reason ?? "Disconnected");
            }
            else if (!IsServer && peerId == 0 && _connecting)
            {
                _connecting = false;
                Enqueue(new TransportEvent(TransportEventType.Disconnected, 0, reason: reason));
            }
        }

        public void Stop()
        {
            // Best-effort notification. A lost disconnect is resolved by the remote peer's timeout.
            if (_socket != null)
            {
                foreach (var peer in _peers.Values)
                    TrySend(peer.Endpoint, Packet(Kind.Disconnect, peer.Id, peer.Token));
                _socket.Dispose();
                _socket = null;
            }
            _cookies?.Dispose();
            _cookies = null;
            _peers.Clear();
            _endpoints.Clear();
            _maintenance.Clear();
            _maintenanceCursor = null;
            _events.Clear();
            _connecting = false;
            _cookie = null;
            _nonce = null;
            _remote = null;
            _connectionData = null;
            LocalPeerId = 0;
            IsServer = false;
        }

        public void Dispose() { if (_disposed) return; Stop(); _disposed = true; }

        private void Receive(IPEndPoint endpoint, int length, double now)
        {
            if (length < HeaderSize || Read32(0) != Magic || _receive[4] != WireVersion || Read16(36) != length - HeaderSize || Read16(38) != 0) return;
            var kind = (Kind)_receive[5];
            byte channel = _receive[6];
            var delivery = (DeliveryMode)_receive[7];
            ulong id = Read64(8);
            int payloadLength = length - HeaderSize;
            if (kind == Kind.Hello && IsServer)
            {
                if (id != 0 || payloadLength != 32 || channel != 0 || delivery != 0) return;
                int packetSize = Read16(HeaderSize);
                int channels = _receive[HeaderSize + 2];
                int framePayload = Read16(HeaderSize + 3);
                if (packetSize < 256 || packetSize > 1400 || channels < 1 || channels > 32) return;
                var nonce = Slice(16, 16);
                var cookie = Cookie(endpoint, nonce, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), packetSize, channels, framePayload);
                TrySend(endpoint, Packet(Kind.Challenge, 0, nonce, payload: new ArraySegment<byte>(cookie)));
                return;
            }
            if (!IsServer && _connecting && endpoint.Equals(_remote))
            {
                if (kind == Kind.Challenge && id == 0 && payloadLength == CookieSize && TokenEquals(_nonce))
                {
                    _cookie = Slice(HeaderSize, CookieSize);
                    _nextHandshake = 0;
                    return;
                }
                if (kind == Kind.Reject && id == 0 && TokenEquals(_nonce) && payloadLength <= 128)
                {
                    _connecting = false;
                    Enqueue(new TransportEvent(TransportEventType.Disconnected, 0, reason: Encoding.UTF8.GetString(_receive, HeaderSize, payloadLength)));
                    return;
                }
                if (kind == Kind.Welcome && id != 0 && payloadLength == 21 && NonceEquals(HeaderSize, _nonce))
                {
                    int packetSize = Read16(HeaderSize + 16);
                    int channels = _receive[HeaderSize + 18];
                    if (packetSize != _options.MaxPacketSize || channels != _options.Channels || Read16(HeaderSize + 19) != Budget.MaxFramePayloadBytes) return;
                    var peer = AddPeer(id, endpoint, Slice(16, 16), _nonce, packetSize, channels, now);
                    _connecting = false;
                    _cookie = null;
                    LocalPeerId = id;
                    Enqueue(new TransportEvent(TransportEventType.Connected, 0), peer);
                    return;
                }
                return;
            }
            if (kind == Kind.Connect && IsServer)
            {
                if (id != 0 || payloadLength < CookieSize + SettingsSize || payloadLength > CookieSize + SettingsSize + _options.MaxConnectionDataSize || channel != 0 || delivery != 0) return;
                int packetSize = Read16(HeaderSize + CookieSize);
                int channels = _receive[HeaderSize + CookieSize + 2];
                int framePayload = Read16(HeaderSize + CookieSize + 3);
                if (packetSize < 256 || packetSize > 1400 || channels < 1 || channels > 32) return;
                byte[] nonce = Slice(16, 16);
                long timestamp = (long)Read64(HeaderSize);
                long age = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - timestamp;
                if (age < -2 || age > 30) return;
                byte[] expected = Cookie(endpoint, nonce, timestamp, packetSize, channels, framePayload);
                if (!CryptographicOperations.FixedTimeEquals(expected.AsSpan(), _receive.AsSpan(HeaderSize, CookieSize))) return;
                if (packetSize != _options.MaxPacketSize || channels != _options.Channels || framePayload != Budget.MaxFramePayloadBytes)
                {
                    Reject(endpoint, nonce, "Transport packet size, channel count and frame budget must match the server");
                    return;
                }
                if (_endpoints.TryGetValue(endpoint, out var existing))
                {
                    if (CryptographicOperations.FixedTimeEquals(existing.Nonce, nonce)) Welcome(existing);
                    else Reject(endpoint, nonce, "Endpoint already connected");
                    return;
                }
                if (_peers.Count >= _options.MaxPeers) { Reject(endpoint, nonce, "Server is full"); return; }
                var connectionData = Slice(HeaderSize + CookieSize + SettingsSize, payloadLength - CookieSize - SettingsSize);
                bool accepted;
                var admittingSocket = _socket;
                try { accepted = _options.ConnectionValidator?.Invoke(endpoint, connectionData) ?? true; }
                catch { accepted = false; }
                // The validator is application code and may stop/restart the transport.
                if (_socket != admittingSocket || !IsServer) return;
                if (!accepted) { Reject(endpoint, nonce, "Connection rejected"); return; }
                do { _nextPeerId++; } while (_nextPeerId == 0 || _peers.ContainsKey(_nextPeerId));
                var peer = AddPeer(_nextPeerId, endpoint, RandomBytes(16), nonce, Math.Min(packetSize, _options.MaxPacketSize), Math.Min(channels, _options.Channels), now);
                Welcome(peer);
                Enqueue(new TransportEvent(TransportEventType.Connected, peer.Id, connectionData), peer);
                return;
            }

            ulong publicId = IsServer ? id : 0;
            if (!_peers.TryGetValue(publicId, out var active) || active.Id != id || !active.Endpoint.Equals(endpoint) || !TokenEquals(active.Token)) return;
            if (length > active.PacketSize) return;
            if (kind == Kind.Data)
            {
                if (channel >= active.Channels.Length || (byte)delivery > (byte)DeliveryMode.ReliableUnordered) return;
                ReceiveData(active, channel, delivery, Read32(32), payloadLength);
            }
            else if (kind == Kind.Ack)
            {
                if (payloadLength != 0 || channel >= active.Channels.Length || (delivery != DeliveryMode.ReliableOrdered && delivery != DeliveryMode.ReliableUnordered)) return;
                ulong key = Key(channel, Read32(32), delivery);
                if (active.Pending.TryGetValue(key, out var pending))
                {
                    active.Pending.Remove(key);
                    if (active.RetryCursor == pending.Node) active.RetryCursor = pending.Node.Next ?? active.RetryQueue.First;
                    active.RetryQueue.Remove(pending.Node);
                    if (active.RetryQueue.Count == 0) active.RetryCursor = null;
                    active.PendingBytes -= pending.Packet.Length;
                }
            }
            else if (kind == Kind.Disconnect)
            {
                if (payloadLength > 128) return;
                Remove(active, payloadLength == 0 ? "Remote disconnected" : Encoding.UTF8.GetString(_receive, HeaderSize, payloadLength));
                return;
            }
            else if (kind == Kind.Heartbeat)
            {
                if (payloadLength != 0) return;
            }
            else return;
            active.LastReceive = now;
        }

        private void ReceiveData(Peer peer, byte channel, DeliveryMode delivery, uint sequence, int payloadLength)
        {
            var state = peer.Channels[channel];
            ulong publicId = IsServer ? peer.Id : 0;
            if (delivery == DeliveryMode.ReliableUnordered)
            {
                int distance = unchecked((int)(sequence - state.NextReceiveUnordered));
                if (distance < 0 || state.ReceivedUnordered.Contains(sequence))
                { Acknowledge(peer, channel, sequence, delivery); return; }
                // The dedupe window stores sequence numbers only, never reordered payloads.
                // Packets beyond its bounded window remain unacknowledged for retransmission.
                if (distance >= _options.MaxPendingReliablePerPeer) return;
                if (distance == 0)
                {
                    state.NextReceiveUnordered++;
                    while (state.ReceivedUnordered.Remove(state.NextReceiveUnordered)) state.NextReceiveUnordered++;
                }
                else state.ReceivedUnordered.Add(sequence);
                Acknowledge(peer, channel, sequence, delivery);
                Enqueue(new TransportEvent(TransportEventType.Data, publicId, Slice(HeaderSize, payloadLength), delivery, channel), peer);
                return;
            }
            if (delivery == DeliveryMode.ReliableOrdered)
            {
                int distance = unchecked((int)(sequence - state.NextReceiveReliable));
                if (distance < 0) { Acknowledge(peer, channel, sequence); return; }
                if (distance >= _options.MaxPendingReliablePerPeer) return;
                if (state.Reordered.ContainsKey(sequence)) { Acknowledge(peer, channel, sequence); return; }
                if (peer.BufferedPackets >= _options.MaxPendingReliablePerPeer || peer.BufferedBytes + payloadLength > _options.MaxQueuedBytesPerPeer) return;
                // Leave a slot for the missing in-order packet. Without this reservation,
                // independent channels can fill their shared budget with gaps and deadlock.
                if (distance > 0 && (peer.BufferedPackets >= _options.MaxPendingReliablePerPeer - 1 ||
                    peer.BufferedBytes + payloadLength > _options.MaxQueuedBytesPerPeer - (peer.PacketSize - HeaderSize))) return;
                var payload = Slice(HeaderSize, payloadLength);
                peer.BufferedBytes += payloadLength;
                peer.BufferedPackets++;
                state.Reordered.Add(sequence, payload);
                Acknowledge(peer, channel, sequence);
                while (state.Reordered.TryGetValue(state.NextReceiveReliable, out var ready))
                {
                    state.Reordered.Remove(state.NextReceiveReliable++);
                    Enqueue(new TransportEvent(TransportEventType.Data, publicId, ready, delivery, channel), peer);
                }
                return;
            }
            if (delivery == DeliveryMode.UnreliableSequenced)
            {
                if (state.HasReceiveSequenced && unchecked((int)(sequence - state.LastReceiveSequenced)) <= 0) return;
                state.HasReceiveSequenced = true;
                state.LastReceiveSequenced = sequence;
            }
            Enqueue(new TransportEvent(TransportEventType.Data, publicId, Slice(HeaderSize, payloadLength), delivery, channel));
        }

        private void Acknowledge(Peer peer, byte channel, uint sequence, DeliveryMode delivery = DeliveryMode.ReliableOrdered)
        {
            TrySend(peer.Endpoint, Packet(Kind.Ack, peer.Id, peer.Token, channel, delivery, sequence));
        }

        private void Maintain(double now, int budget)
        {
            int peers = Math.Min(_maintenance.Count, budget);
            int sends = 0;
            for (int i = 0; i < peers && _maintenance.Count > 0; i++)
            {
                var node = _maintenanceCursor ?? _maintenance.First;
                var peer = node.Value;
                _maintenanceCursor = node.Next ?? _maintenance.First;
                if (now - peer.LastReceive >= _options.PeerTimeout.TotalSeconds) { Remove(peer, "Peer timed out"); continue; }
                int attempts = Math.Min(8, peer.RetryQueue.Count);
                bool removed = false;
                for (int j = 0; j < attempts; j++)
                {
                    var retryNode = peer.RetryCursor ?? peer.RetryQueue.First;
                    ulong key = retryNode.Value;
                    peer.RetryCursor = retryNode.Next ?? peer.RetryQueue.First;
                    var pending = peer.Pending[key];
                    if (sends >= budget || now - pending.SentAt < _options.RetransmitInterval.TotalSeconds) continue;
                    if (pending.Retransmits >= _options.MaxRetransmits) { Remove(peer, "Reliable delivery timed out"); removed = true; break; }
                    pending.Retransmits++;
                    pending.SentAt = now;
                    if (TrySend(peer.Endpoint, pending.Packet))
                    {
                        peer.LastSend = now;
                    }
                    sends++;
                }
                if (!removed && now - peer.LastSend >= _options.HeartbeatInterval.TotalSeconds && sends < budget)
                {
                    TrySend(peer.Endpoint, Packet(Kind.Heartbeat, peer.Id, peer.Token));
                    peer.LastSend = now;
                    sends++;
                }
            }
        }

        private void Dispatch(ref int count, int limit)
        {
            while (count < limit && _events.Count != 0 && _socket != null)
            {
                var queued = _events.Dequeue();
                if (queued.Peer != null && queued.Event.Type == TransportEventType.Data && queued.Event.Delivery == DeliveryMode.ReliableOrdered)
                {
                    queued.Peer.BufferedBytes -= queued.Event.Payload.Length;
                    queued.Peer.BufferedPackets--;
                }
                if (queued.Peer != null && !queued.Peer.Active && queued.Event.Type != TransportEventType.Disconnected) continue;
                count++;
                EventReceived?.Invoke(queued.Event);
            }
        }

        private Peer AddPeer(ulong id, IPEndPoint endpoint, byte[] token, byte[] nonce, int packetSize, int channels, double now)
        {
            var peer = new Peer { Id = id, Endpoint = CopyEndpoint(endpoint), Token = token, Nonce = nonce,
                PacketSize = packetSize, Channels = new ChannelState[channels], LastReceive = now, LastSend = now };
            for (int i = 0; i < channels; i++) peer.Channels[i] = new ChannelState();
            _peers.Add(IsServer ? id : 0, peer);
            _endpoints.Add(peer.Endpoint, peer);
            peer.Node = _maintenance.AddLast(peer);
            return peer;
        }

        private void Remove(Peer peer, string reason)
        {
            ulong publicId = IsServer ? peer.Id : 0;
            if (!_peers.Remove(publicId)) return;
            peer.Active = false;
            _endpoints.Remove(peer.Endpoint);
            if (_maintenanceCursor == peer.Node) _maintenanceCursor = peer.Node.Next ?? _maintenance.First;
            _maintenance.Remove(peer.Node);
            if (_maintenance.Count == 0) _maintenanceCursor = null;
            if (!IsServer) LocalPeerId = 0;
            Enqueue(new TransportEvent(TransportEventType.Disconnected, publicId, reason: reason));
        }

        private void Enqueue(TransportEvent value, Peer peer = null) => _events.Enqueue(new QueuedEvent { Event = value, Peer = peer });

        private void Welcome(Peer peer)
        {
            var payload = new byte[21];
            Buffer.BlockCopy(peer.Nonce, 0, payload, 0, 16);
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(16), (ushort)peer.PacketSize);
            payload[18] = (byte)peer.Channels.Length;
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(19), (ushort)Budget.MaxFramePayloadBytes);
            TrySend(peer.Endpoint, Packet(Kind.Welcome, peer.Id, peer.Token, payload: new ArraySegment<byte>(payload)));
        }

        private void Reject(IPEndPoint endpoint, byte[] nonce, string reason) =>
            TrySend(endpoint, Packet(Kind.Reject, 0, nonce, payload: new ArraySegment<byte>(Encoding.UTF8.GetBytes(reason))));

        private void SendHandshake()
        {
            int offset = _cookie == null ? 0 : CookieSize;
            var payload = new byte[_cookie == null ? 32 : CookieSize + SettingsSize + _connectionData.Length];
            if (_cookie != null) { Buffer.BlockCopy(_cookie, 0, payload, 0, CookieSize); Buffer.BlockCopy(_connectionData, 0, payload, CookieSize + SettingsSize, _connectionData.Length); }
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(offset), (ushort)_options.MaxPacketSize);
            payload[offset + 2] = (byte)_options.Channels;
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(offset + 3), (ushort)Budget.MaxFramePayloadBytes);
            TrySend(_remote, Packet(_cookie == null ? Kind.Hello : Kind.Connect, 0, _nonce, payload: new ArraySegment<byte>(payload)));
        }

        private byte[] Cookie(IPEndPoint endpoint, byte[] nonce, long timestamp, int packetSize, int channels, int framePayload)
        {
            byte[] address = Encoding.UTF8.GetBytes(endpoint.ToString());
            var input = new byte[address.Length + 29];
            Buffer.BlockCopy(address, 0, input, 0, address.Length);
            Buffer.BlockCopy(nonce, 0, input, address.Length, 16);
            BinaryPrimitives.WriteInt64LittleEndian(input.AsSpan(address.Length + 16), timestamp);
            BinaryPrimitives.WriteUInt16LittleEndian(input.AsSpan(address.Length + 24), (ushort)packetSize);
            input[address.Length + 26] = (byte)channels;
            BinaryPrimitives.WriteUInt16LittleEndian(input.AsSpan(address.Length + 27), (ushort)framePayload);
            byte[] hash = _cookies.ComputeHash(input);
            var result = new byte[CookieSize];
            BinaryPrimitives.WriteInt64LittleEndian(result, timestamp);
            Buffer.BlockCopy(hash, 0, result, 8, 16);
            return result;
        }

        private static byte[] Packet(Kind kind, ulong id, byte[] token, byte channel = 0, DeliveryMode delivery = 0, uint sequence = 0, ArraySegment<byte> payload = default)
        {
            var bytes = new byte[HeaderSize + payload.Count];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, Magic);
            bytes[4] = WireVersion;
            bytes[5] = (byte)kind;
            bytes[6] = channel;
            bytes[7] = (byte)delivery;
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), id);
            if (token != null) Buffer.BlockCopy(token, 0, bytes, 16, 16);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(32), sequence);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(36), checked((ushort)payload.Count));
            if (payload.Count != 0) Buffer.BlockCopy(payload.Array, payload.Offset, bytes, HeaderSize, payload.Count);
            return bytes;
        }

        private bool TrySend(IPEndPoint endpoint, byte[] packet)
        {
            if (_socket == null) return false;
            try { return _socket.SendTo(packet, endpoint) == packet.Length; }
            catch (SocketException ex) when (Transient(ex.SocketErrorCode) || ex.SocketErrorCode == SocketError.ConnectionReset || ex.SocketErrorCode == SocketError.NetworkUnreachable || ex.SocketErrorCode == SocketError.HostUnreachable || ex.SocketErrorCode == SocketError.MessageSize) { return false; }
        }

        private void Open(IPEndPoint endpoint, bool server)
        {
            var socket = new Socket(endpoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                socket.Blocking = false;
                if (endpoint.AddressFamily == AddressFamily.InterNetwork) socket.DontFragment = true;
                // A dedicated server has one socket for all peers. Client simulations should not
                // reserve multi-megabyte kernel buffers per player.
                socket.ReceiveBufferSize = server ? 8 * 1024 * 1024 : 64 * 1024;
                socket.SendBufferSize = server ? 1024 * 1024 : 64 * 1024;
                socket.Bind(endpoint);
                _socket = socket;
            }
            catch { socket.Dispose(); throw; }
        }

        private void EnsureCanStart(IPEndPoint endpoint)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(UdpGameTransport));
            if (endpoint == null) throw new ArgumentNullException(nameof(endpoint));
            if (_socket != null) throw new InvalidOperationException("Stop the running transport before starting it again.");
            if (endpoint.AddressFamily != AddressFamily.InterNetwork && endpoint.AddressFamily != AddressFamily.InterNetworkV6)
                throw new NotSupportedException("Only IPv4 and IPv6 endpoints are supported.");
            if (endpoint.AddressFamily == AddressFamily.InterNetworkV6 && _options.IpHeaderBytes < 40)
                throw new ArgumentException("IPv6 requires at least 40 reserved IP header bytes.", nameof(endpoint));
        }

        private bool TokenEquals(byte[] value) => CryptographicOperations.FixedTimeEquals(value, _receive.AsSpan(16, 16));
        private bool NonceEquals(int offset, byte[] value) => CryptographicOperations.FixedTimeEquals(value, _receive.AsSpan(offset, 16));
        private byte[] Slice(int offset, int count) { var result = new byte[count]; Buffer.BlockCopy(_receive, offset, result, 0, count); return result; }
        private ushort Read16(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(_receive.AsSpan(offset));
        private uint Read32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(_receive.AsSpan(offset));
        private ulong Read64(int offset) => BinaryPrimitives.ReadUInt64LittleEndian(_receive.AsSpan(offset));
        private static ulong Key(byte channel, uint sequence, DeliveryMode delivery) => ((ulong)delivery << 40) | ((ulong)channel << 32) | sequence;
        private static IPEndPoint CopyEndpoint(IPEndPoint endpoint) => new IPEndPoint(endpoint.Address, endpoint.Port);
        private static bool Transient(SocketError error) => error == SocketError.WouldBlock || error == SocketError.IOPending || error == SocketError.NoBufferSpaceAvailable || error == SocketError.TryAgain;
        private static byte[] RandomBytes(int size) { var bytes = new byte[size]; using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes); return bytes; }
        private static double Now => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;
    }
}
