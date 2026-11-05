using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace SocketJack.Multiplayer.Transport
{
    /// <summary>Bounded, manually pumped TCP transport. All delivery modes use TCP's reliable
    /// stream and can incur head-of-line blocking. Traffic is plaintext; use a trusted network
    /// or a separately secured tunnel when confidentiality is required.</summary>
    public sealed class TcpGameTransport : IGameTransport, ITransportMetricsSource
    {
        private sealed class Frame { internal byte[] Bytes; internal int Offset; }
        private sealed class Peer
        {
            internal Socket Socket;
            internal ulong Id;
            internal bool Connecting, Ready;
            internal double Started, LastReceive, LastSend;
            internal readonly Queue<Frame> Out = new Queue<Frame>();
            internal int QueuedBytes;
            internal readonly byte[] Length = new byte[4];
            internal int LengthRead, BodyRead;
            internal byte[] Body;
        }
        private readonly TransportOptions options;
        private readonly Dictionary<ulong, Peer> peers = new Dictionary<ulong, Peer>();
        private readonly List<Peer> schedule = new List<Peer>();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private Socket listener;
        private ulong nextId = 1;
        private int cursor;
        private byte[] connectionData;
        private bool disposed, polling;
        private long bytesSent, bytesReceived, framesSent, framesReceived, invalidPackets, sendRejected;
        public TransportType Type => TransportType.Tcp;
        public bool IsServer { get; private set; }
        public bool IsRunning { get; private set; }
        public ulong LocalPeerId { get; private set; }
        public IPEndPoint LocalEndPoint { get; private set; }
        public int MaxPayloadSize => options.MaxPacketSize - 16;
        public PacketBudget Budget { get; }
        public int PeerCount => peers.Count;
        public event Action<TransportEvent> EventReceived;
        public TcpGameTransport(TransportOptions options = null)
        {
            this.options = (options ?? new TransportOptions()).Snapshot();
            Budget = new PacketBudget(this.options.PathMtu, this.options.IpHeaderBytes == 20 ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6,
                this.options.ExtraPacketOverheadBytes, this.options.MaxPacketSize);
        }

        public void StartServer(IPEndPoint endpoint)
        {
            EnsureStart(endpoint);
            Socket socket = NewSocket(endpoint.AddressFamily);
            try { socket.Bind(endpoint); socket.Listen(Math.Min(options.MaxPeers, 512)); }
            catch { socket.Dispose(); throw; }
            listener = socket; IsServer = true; IsRunning = true; LocalPeerId = 0;
            LocalEndPoint = (IPEndPoint)socket.LocalEndPoint;
        }
        public void StartClient(IPEndPoint endpoint, byte[] connectionData = null)
        {
            EnsureStart(endpoint);
            if ((connectionData?.Length ?? 0) > options.MaxConnectionDataSize) throw new ArgumentException("Connection data exceeds the configured limit.", nameof(connectionData));
            this.connectionData = connectionData == null ? Array.Empty<byte>() : (byte[])connectionData.Clone();
            var socket = NewSocket(endpoint.AddressFamily);
            var peer = new Peer { Id = 0, Socket = socket, Started = Now, LastReceive = Now, LastSend = Now, Body = new byte[options.MaxPacketSize], Connecting = true };
            try
            {
                socket.Bind(new IPEndPoint(endpoint.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0));
                try { socket.Connect(endpoint); peer.Connecting = false; }
                catch (SocketException ex) when (WouldBlock(ex)) { }
                peers.Add(0, peer); schedule.Add(peer); IsServer = false; IsRunning = true; LocalPeerId = 0;
                LocalEndPoint = (IPEndPoint)socket.LocalEndPoint;
                if (!peer.Connecting) SendHello(peer);
            }
            catch { socket.Dispose(); peers.Clear(); schedule.Clear(); throw; }
        }
        public void Poll(int maxEvents = 256)
        {
            if (disposed) throw new ObjectDisposedException(nameof(TcpGameTransport));
            if (polling) throw new InvalidOperationException("Poll cannot be called recursively.");
            polling = true;
            try { PollCore(maxEvents); }
            finally { polling = false; }
        }
        private void PollCore(int maxEvents)
        {
            if (maxEvents < 1) throw new ArgumentOutOfRangeException(nameof(maxEvents));
            if (!IsRunning) return;
            for (int accepted = 0; IsServer && listener != null && accepted < maxEvents; accepted++)
            {
                Socket socket;
                try { socket = listener.Accept(); }
                catch (SocketException ex) when (WouldBlock(ex)) { break; }
                if (peers.Count >= options.MaxPeers) { socket.Dispose(); continue; }
                socket.Blocking = false; socket.NoDelay = true;
                var peer = new Peer { Socket = socket, Id = nextId++, Started = Now, LastReceive = Now, LastSend = Now, Body = new byte[options.MaxPacketSize] };
                peers.Add(peer.Id, peer); schedule.Add(peer);
            }
            int count = Math.Min(schedule.Count, maxEvents);
            while (IsRunning && schedule.Count > 0 && count-- > 0)
            {
                if (cursor >= schedule.Count) cursor = 0;
                Peer peer = schedule[cursor++];
                if (Now - (peer.Ready ? peer.LastReceive : peer.Started) > (peer.Ready ? options.PeerTimeout : options.ConnectTimeout).TotalSeconds)
                { Remove(peer, "Timed out"); continue; }
                if (peer.Connecting)
                {
                    try
                    {
                        if (!peer.Socket.Poll(0, SelectMode.SelectWrite) && !peer.Socket.Poll(0, SelectMode.SelectError)) continue;
                        if ((int)peer.Socket.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Error) != 0) { Remove(peer, "Connection failed"); continue; }
                        peer.Connecting = false; SendHello(peer);
                    }
                    catch (SocketException) { Remove(peer, "Connection failed"); continue; }
                }
                if (peer.Ready && Now - peer.LastSend > options.HeartbeatInterval.TotalSeconds) Enqueue(peer, 4, Array.Empty<byte>(), DeliveryMode.ReliableOrdered, 0);
                if (!Flush(peer)) { Remove(peer, "Connection closed"); continue; }
                byte[] frame;
                // An idle peer is normal. Avoid allocating a WouldBlock exception for every
                // connection on every frame when thousands of players are connected.
                try { if (!peer.Socket.Poll(0, SelectMode.SelectRead)) continue; }
                catch (SocketException) { Remove(peer, "Connection closed"); continue; }
                try { frame = ReadFrame(peer); }
                catch (IOException) { Remove(peer, "Invalid frame"); continue; }
                catch (SocketException) { Remove(peer, "Connection closed"); continue; }
                if (frame != null) ProcessFrame(peer, frame);
            }
        }
        public bool Send(ulong peerId, ArraySegment<byte> payload, DeliveryMode delivery = DeliveryMode.ReliableUnordered, byte channel = 0)
        {
            if (disposed) throw new ObjectDisposedException(nameof(TcpGameTransport));
            ValidatePayload(payload, delivery, channel);
            if (!IsRunning || !peers.TryGetValue(peerId, out Peer peer) || !peer.Ready) { sendRejected++; return false; }
            bool accepted = Enqueue(peer, 3, Copy(payload), delivery, channel); if (!accepted) sendRejected++; return accepted;
        }
        public void Disconnect(ulong peerId, string reason = "Disconnected")
        {
            if (peers.TryGetValue(peerId, out Peer peer)) Remove(peer, reason);
        }
        public void Stop()
        {
            IsRunning = false; listener?.Dispose(); listener = null;
            foreach (Peer peer in schedule) peer.Socket.Dispose();
            peers.Clear(); schedule.Clear(); cursor = 0; LocalPeerId = 0; LocalEndPoint = null;
        }
        public void Dispose() { Stop(); disposed = true; }
        private double Now => clock.Elapsed.TotalSeconds;
        private void EnsureStart(IPEndPoint endpoint)
        {
            if (disposed) throw new ObjectDisposedException(nameof(TcpGameTransport));
            if (IsRunning) throw new InvalidOperationException("Stop the transport before restarting it.");
            if (endpoint == null) throw new ArgumentNullException(nameof(endpoint));
        }
        private static Socket NewSocket(AddressFamily family) => new Socket(family, SocketType.Stream, ProtocolType.Tcp) { Blocking = false, NoDelay = true };
        private static bool WouldBlock(SocketException ex) => ex.SocketErrorCode == SocketError.WouldBlock || ex.SocketErrorCode == SocketError.IOPending || ex.SocketErrorCode == SocketError.InProgress || ex.SocketErrorCode == SocketError.AlreadyInProgress || ex.SocketErrorCode == SocketError.NoBufferSpaceAvailable;
        private void SendHello(Peer peer)
        {
            var hello = new byte[9 + connectionData.Length];
            hello[0] = 0x53; hello[1] = 0x4a; hello[2] = 0x47; hello[3] = 2;
            hello[4] = (byte)options.MaxPacketSize; hello[5] = (byte)(options.MaxPacketSize >> 8); hello[6] = (byte)options.Channels;
            hello[7] = (byte)Budget.MaxFramePayloadBytes; hello[8] = (byte)(Budget.MaxFramePayloadBytes >> 8);
            Buffer.BlockCopy(connectionData, 0, hello, 9, connectionData.Length);
            Enqueue(peer, 1, hello, DeliveryMode.ReliableOrdered, 0);
        }
        private bool Enqueue(Peer peer, byte kind, byte[] payload, DeliveryMode delivery, byte channel)
        {
            int bodyLength = payload.Length + 3;
            if (peer.Out.Count >= options.MaxPendingReliablePerPeer || peer.QueuedBytes + bodyLength + 4 > options.MaxQueuedBytesPerPeer) return false;
            var frame = new byte[bodyLength + 4]; Write32(frame, 0, (uint)bodyLength);
            frame[4] = kind; frame[5] = channel; frame[6] = (byte)delivery;
            Buffer.BlockCopy(payload, 0, frame, 7, payload.Length);
            peer.Out.Enqueue(new Frame { Bytes = frame }); peer.QueuedBytes += frame.Length;
            return true;
        }
        private bool Flush(Peer peer)
        {
            if (peer.Out.Count == 0) return true;
            Frame frame = peer.Out.Peek();
            try
            {
                int count = peer.Socket.Send(frame.Bytes, frame.Offset, frame.Bytes.Length - frame.Offset, SocketFlags.None);
                if (count == 0) return false;
                bytesSent += count;
                frame.Offset += count; peer.QueuedBytes -= count; peer.LastSend = Now;
                if (frame.Offset == frame.Bytes.Length) { peer.Out.Dequeue(); framesSent++; }
                return true;
            }
            catch (SocketException ex) when (WouldBlock(ex)) { return true; }
            catch (SocketException) { return false; }
        }
        // At most one frame per peer per pump, with exact framing and bounded allocations.
        private byte[] ReadFrame(Peer peer)
        {
            try
            {
                if (peer.LengthRead < 4)
                {
                    int n = peer.Socket.Receive(peer.Length, peer.LengthRead, 4 - peer.LengthRead, SocketFlags.None);
                    if (n == 0) throw new IOException("Closed");
                    bytesReceived += n;
                    peer.LengthRead += n;
                    if (peer.LengthRead < 4) return null;
                }
                uint length = Read32(peer.Length, 0);
                if (length < 3 || length > options.MaxPacketSize) { invalidPackets++; throw new IOException("Invalid length"); }
                int received = peer.Socket.Receive(peer.Body, peer.BodyRead, (int)length - peer.BodyRead, SocketFlags.None);
                if (received == 0) throw new IOException("Closed");
                bytesReceived += received;
                peer.BodyRead += received;
                if (peer.BodyRead < length) return null;
                var result = new byte[length]; Buffer.BlockCopy(peer.Body, 0, result, 0, result.Length);
                peer.LengthRead = 0; peer.BodyRead = 0;
                framesReceived++;
                return result;
            }
            catch (SocketException ex) when (WouldBlock(ex)) { return null; }
        }
        private void ProcessFrame(Peer peer, byte[] frame)
        {
            byte kind = frame[0], channel = frame[1];
            if (channel >= options.Channels || frame[2] > (byte)DeliveryMode.ReliableUnordered) { invalidPackets++; Remove(peer, "Invalid frame"); return; }
            if (!peer.Ready)
            {
                if (IsServer && kind == 1 && frame.Length >= 12 && frame.Length - 12 <= options.MaxConnectionDataSize && frame[3] == 0x53 && frame[4] == 0x4a && frame[5] == 0x47 && frame[6] == 2)
                {
                    if ((frame[7] | frame[8] << 8) != options.MaxPacketSize || frame[9] != options.Channels || (frame[10] | frame[11] << 8) != Budget.MaxFramePayloadBytes) { Remove(peer, "Transport configuration mismatch"); return; }
                    var data = new byte[frame.Length - 12]; Buffer.BlockCopy(frame, 12, data, 0, data.Length);
                    bool allowed;
                    try { allowed = options.ConnectionValidator == null || options.ConnectionValidator((IPEndPoint)peer.Socket.RemoteEndPoint, data); }
                    catch { allowed = false; }
                    if (!IsRunning || !peers.TryGetValue(peer.Id, out var current) || current != peer) return;
                    if (!allowed) { Remove(peer, "Connection rejected"); return; }
                    var welcome = new byte[13]; Write32(welcome, 0, (uint)peer.Id); Write32(welcome, 4, (uint)(peer.Id >> 32));
                    welcome[8] = (byte)options.MaxPacketSize; welcome[9] = (byte)(options.MaxPacketSize >> 8); welcome[10] = (byte)options.Channels;
                    welcome[11] = (byte)Budget.MaxFramePayloadBytes; welcome[12] = (byte)(Budget.MaxFramePayloadBytes >> 8);
                    if (!Enqueue(peer, 2, welcome, DeliveryMode.ReliableOrdered, 0)) { Remove(peer, "Send queue full"); return; }
                    peer.Ready = true; peer.LastReceive = Now;
                    EventReceived?.Invoke(new TransportEvent(TransportEventType.Connected, peer.Id, data));
                }
                else if (!IsServer && kind == 2 && frame.Length == 16)
                {
                    if ((frame[11] | frame[12] << 8) != options.MaxPacketSize || frame[13] != options.Channels || (frame[14] | frame[15] << 8) != Budget.MaxFramePayloadBytes) { Remove(peer, "Transport configuration mismatch"); return; }
                    LocalPeerId = Read32(frame, 3) | ((ulong)Read32(frame, 7) << 32);
                    if (LocalPeerId == 0) { Remove(peer, "Invalid peer ID"); return; }
                    peer.Ready = true; peer.LastReceive = Now;
                    EventReceived?.Invoke(new TransportEvent(TransportEventType.Connected, 0));
                }
                else { invalidPackets++; Remove(peer, "Invalid handshake"); }
                return;
            }
            if (kind == 4 && frame.Length == 3) { peer.LastReceive = Now; return; }
            if (kind != 3 || frame.Length - 3 > MaxPayloadSize) { invalidPackets++; Remove(peer, "Invalid frame"); return; }
            peer.LastReceive = Now;
            var payload = new byte[frame.Length - 3]; Buffer.BlockCopy(frame, 3, payload, 0, payload.Length);
            EventReceived?.Invoke(new TransportEvent(TransportEventType.Data, peer.Id, payload, (DeliveryMode)frame[2], channel));
        }
        private void Remove(Peer peer, string reason)
        {
            if (!peers.Remove(peer.Id)) return;
            int index = schedule.IndexOf(peer);
            if (index >= 0) { schedule.RemoveAt(index); if (index < cursor) cursor--; }
            peer.Socket.Dispose();
            if (!IsServer) { IsRunning = false; LocalPeerId = 0; LocalEndPoint = null; }
            EventReceived?.Invoke(new TransportEvent(TransportEventType.Disconnected, peer.Id, reason: reason));
        }
        private void ValidatePayload(ArraySegment<byte> payload, DeliveryMode delivery, byte channel)
        {
            if (payload.Count > MaxPayloadSize) throw new ArgumentOutOfRangeException(nameof(payload), "Payload exceeds the transport MTU.");
            if (channel >= options.Channels) throw new ArgumentOutOfRangeException(nameof(channel));
            if (delivery < DeliveryMode.Unreliable || delivery > DeliveryMode.ReliableUnordered) throw new ArgumentOutOfRangeException(nameof(delivery));
        }
        private static byte[] Copy(ArraySegment<byte> value) { var bytes = new byte[value.Count]; if (value.Count > 0) Buffer.BlockCopy(value.Array, value.Offset, bytes, 0, value.Count); return bytes; }
        public TransportMetricsSnapshot GetTransportMetrics(int maxPeerDetails = 64)
        {
            TransportMetrics.ValidateDetailLimit(maxPeerDetails); var details = new List<PeerTransportMetrics>(); long queued = 0; int active = 0;
            foreach (var entry in peers)
            {
                queued += entry.Value.QueuedBytes; if (!entry.Value.Ready) continue; active++;
                if (details.Count < maxPeerDetails) details.Add(new PeerTransportMetrics(entry.Key, null, entry.Value.Out.Count, entry.Value.QueuedBytes));
            }
            return new TransportMetricsSnapshot(Type, bytesSent, bytesReceived, framesSent, framesReceived, 0, invalidPackets, sendRejected, active, queued, details);
        }
        private static void Write32(byte[] bytes, int offset, uint value) { for (int i = 0; i < 4; i++) bytes[offset + i] = (byte)(value >> (8 * i)); }
        private static uint Read32(byte[] bytes, int offset) => (uint)(bytes[offset] | bytes[offset + 1] << 8 | bytes[offset + 2] << 16 | bytes[offset + 3] << 24);
    }
}
