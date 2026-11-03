using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using SocketJack.Multiplayer.Serialization;

namespace SocketJack.Multiplayer.Transport
{
    public sealed class ObjectFrameOptions
    {
        public int MaxMessageBytes { get; set; } = 65535;
        public int MaxQueuedMessagesPerPeer { get; set; } = 128;
        public int MaxQueuedBytesPerPeer { get; set; } = 1024 * 1024;
        public int MaxReassemblyBytesPerPeer { get; set; } = 1024 * 1024;
        public int MaxConcurrentObjectsPerPeer { get; set; } = 256;
        public int MaxOrderedMessagesPerChannel { get; set; } = 128;
        public int MaxQueuedEventsPerPeer { get; set; } = 1024;
        public int MaxPeers { get; set; } = 4096;
        public int MaxFramesPerPoll { get; set; } = 256;
        public TimeSpan ReassemblyTimeout { get; set; } = TimeSpan.FromSeconds(30);
        internal ObjectFrameOptions Snapshot()
        {
            if (MaxMessageBytes < 1 || MaxMessageBytes > 1024 * 1024 || MaxQueuedMessagesPerPeer < 1 ||
                MaxQueuedBytesPerPeer < MaxMessageBytes || MaxReassemblyBytesPerPeer < MaxMessageBytes || MaxConcurrentObjectsPerPeer < 1 ||
                MaxOrderedMessagesPerChannel < 1 || MaxQueuedEventsPerPeer < 1 || MaxPeers < 1 || MaxPeers > 65535 || MaxFramesPerPoll < 1 ||
                ReassemblyTimeout <= TimeSpan.Zero || double.IsInfinity(ReassemblyTimeout.TotalSeconds))
                throw new ArgumentOutOfRangeException(nameof(ObjectFrameOptions));
            return (ObjectFrameOptions)MemberwiseClone();
        }
    }

    /// <summary>Automatically splits logical messages only when one frame cannot hold them.
    /// All UDP/IP object frames remain below 999 bytes under the configured PacketBudget.
    /// Multipart messages use reliable unordered fragments; the original logical delivery contract
    /// is restored after complete reconstruction. A successful Send atomically accepts the whole
    /// logical message into bounded storage, even if the native transport is currently backpressured.</summary>
    public sealed class ObjectFrameTransport : IGameTransport
    {
        public const int HeaderSize = 28;
        private const uint Magic = 0x464f4a53; // SJOF in little endian
        private sealed class Outgoing { internal byte[][] Frames; internal int Index; internal DeliveryMode Delivery; internal byte Channel; }
        private sealed class QueuedEvent { internal TransportEvent Event; internal Peer Peer; }
        private sealed class Channel
        {
            internal uint NextSendOrdered = 1, NextReceiveOrdered = 1, NextSendSequenced = 1, LatestSequenced;
            internal bool HasSequenced;
            internal double GapStarted;
            internal readonly Dictionary<uint, TransportEvent> Ordered = new Dictionary<uint, TransportEvent>();
        }
        private sealed class Peer
        {
            internal ulong Id;
            internal ObjectRebuilder Rebuilder;
            internal int OutBytes, OrderedBytes;
            internal int EventCount, EventBytes;
            internal readonly Queue<Outgoing> Out = new Queue<Outgoing>();
            internal readonly Dictionary<byte, Channel> Channels = new Dictionary<byte, Channel>();
        }
        private readonly IGameTransport _inner;
        private readonly ObjectFrameOptions _options;
        private readonly Dictionary<ulong, Peer> _peers = new Dictionary<ulong, Peer>();
        private readonly List<Peer> _schedule = new List<Peer>();
        private readonly Queue<QueuedEvent> _events = new Queue<QueuedEvent>();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private int _cursor, _maintenanceCursor;
        private ulong _nextId;
        private bool _disposed, _polling;
        public IGameTransport InnerTransport => _inner;
        public PacketBudget Budget { get; }
        public int MaxFramePayloadSize { get; }
        public int MaxFragmentDataSize => MaxFramePayloadSize - HeaderSize;
        public int MaxPayloadSize => _options.MaxMessageBytes;
        public TransportType Type => _inner.Type;
        public bool IsServer => _inner.IsServer;
        public bool IsRunning => _inner.IsRunning;
        public ulong LocalPeerId => _inner.LocalPeerId;
        public IPEndPoint LocalEndPoint => _inner.LocalEndPoint;
        public event Action<TransportEvent> EventReceived;

        public ObjectFrameTransport(IGameTransport inner, ObjectFrameOptions options = null, PacketBudget budget = null)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            if (inner is ObjectFrameTransport) throw new ArgumentException("Transport is already object framed.", nameof(inner));
            if (inner.IsRunning) throw new InvalidOperationException("Wrap the transport before starting it.");
            _options = (options ?? new ObjectFrameOptions()).Snapshot();
            Budget = budget ?? (inner as UdpGameTransport)?.Budget ?? (inner as TcpGameTransport)?.Budget ?? PacketBudget.Default;
            MaxFramePayloadSize = Math.Min(inner.MaxPayloadSize, Budget.MaxFramePayloadBytes);
            if (MaxFramePayloadSize <= HeaderSize) throw new ArgumentException("Transport leaves no fragment payload capacity.", nameof(inner));
            _inner.EventReceived += OnNativeEvent;
        }

        public static IGameTransport Wrap(IGameTransport transport)
        { if (transport == null) throw new ArgumentNullException(nameof(transport)); return transport is ObjectFrameTransport ? transport : new ObjectFrameTransport(transport); }
        public void StartServer(IPEndPoint endpoint) { EnsureStart(); _inner.StartServer(endpoint); }
        public void StartClient(IPEndPoint endpoint, byte[] connectionData = null) { EnsureStart(); _inner.StartClient(endpoint, connectionData); }
        public void Poll(int maxEvents = 256)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ObjectFrameTransport));
            if (_polling) throw new InvalidOperationException("Poll cannot be called recursively.");
            if (maxEvents < 1) throw new ArgumentOutOfRangeException(nameof(maxEvents));
            if (!IsRunning) return;
            _polling = true;
            try
            {
                int callbacks = 0;
                Dispatch(ref callbacks, maxEvents);
                if (!IsRunning || callbacks == maxEvents) return;
                _inner.Poll(maxEvents - callbacks);
                Flush(Math.Min(maxEvents, _options.MaxFramesPerPoll));
                double now = _clock.Elapsed.TotalSeconds;
                int maintenance = Math.Min(maxEvents, _schedule.Count);
                while (_schedule.Count > 0 && maintenance-- > 0)
                {
                    if (_maintenanceCursor >= _schedule.Count) _maintenanceCursor = 0;
                    var peer = _schedule[_maintenanceCursor++];
                    peer.Rebuilder.Prune();
                    foreach (var channel in peer.Channels.Values)
                        if (channel.Ordered.Count > 0 && now - channel.GapStarted > _options.ReassemblyTimeout.TotalSeconds)
                        { Disconnect(peer.Id, "Ordered object reconstruction timed out"); break; }
                }
                Dispatch(ref callbacks, maxEvents);
            }
            finally { _polling = false; }
        }

        public bool Send(ulong peerId, ArraySegment<byte> payload, DeliveryMode delivery = DeliveryMode.ReliableUnordered, byte channel = 0)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ObjectFrameTransport));
            if (payload.Array == null && payload.Count != 0) throw new ArgumentException("Invalid payload.", nameof(payload));
            if (payload.Count > MaxPayloadSize) throw new ArgumentOutOfRangeException(nameof(payload), "Logical object exceeds MaxPayloadSize.");
            if ((byte)delivery > (byte)DeliveryMode.ReliableUnordered || channel >= 32) throw new ArgumentOutOfRangeException(nameof(delivery));
            if (!IsRunning || !_peers.TryGetValue(peerId, out var peer)) return false;
            int count = Math.Max(1, (payload.Count + MaxFragmentDataSize - 1) / MaxFragmentDataSize);
            int bytes = checked(payload.Count + count * HeaderSize);
            if (count > ushort.MaxValue || peer.Out.Count >= _options.MaxQueuedMessagesPerPeer || bytes > _options.MaxQueuedBytesPerPeer - peer.OutBytes) return false;
            var state = GetChannel(peer, channel);
            uint sequence = delivery == DeliveryMode.ReliableOrdered ? state.NextSendOrdered : delivery == DeliveryMode.UnreliableSequenced ? state.NextSendSequenced : 0;
            ulong id = checked(++_nextId);
            var frames = new byte[count][];
            for (int i = 0; i < count; i++)
            {
                int offset = i * MaxFragmentDataSize, length = Math.Min(MaxFragmentDataSize, payload.Count - offset);
                var frame = new byte[HeaderSize + length];
                Write32(frame, 0, Magic); frame[4] = 1; frame[5] = (byte)delivery; frame[6] = channel;
                Write64(frame, 8, id); Write32(frame, 16, sequence); Write16(frame, 20, (ushort)i); Write16(frame, 22, (ushort)count); Write32(frame, 24, (uint)payload.Count);
                if (length > 0) Buffer.BlockCopy(payload.Array, payload.Offset + offset, frame, HeaderSize, length);
                frames[i] = frame;
            }
            if (delivery == DeliveryMode.ReliableOrdered) state.NextSendOrdered = unchecked(sequence + 1);
            if (delivery == DeliveryMode.UnreliableSequenced) state.NextSendSequenced = unchecked(sequence + 1);
            peer.Out.Enqueue(new Outgoing { Frames = frames, Delivery = count > 1 ? DeliveryMode.ReliableUnordered : delivery, Channel = channel }); peer.OutBytes += bytes;
            TryFlushPeer(peer);
            return true;
        }

        public void Disconnect(ulong peerId, string reason = "Disconnected")
        { RemovePeer(peerId); _inner.Disconnect(peerId, reason); }
        public void Stop() { _inner.Stop(); _peers.Clear(); _schedule.Clear(); _events.Clear(); _cursor = _maintenanceCursor = 0; _nextId = 0; }
        public void Dispose() { if (_disposed) return; Stop(); _inner.EventReceived -= OnNativeEvent; _inner.Dispose(); _disposed = true; }
        private void EnsureStart()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ObjectFrameTransport));
            if (IsRunning) throw new InvalidOperationException("Stop before restarting the transport.");
            _peers.Clear(); _schedule.Clear(); _events.Clear(); _cursor = _maintenanceCursor = 0; _nextId = 0;
        }

        private void OnNativeEvent(TransportEvent ev)
        {
            if (ev.Type == TransportEventType.Connected)
            {
                if (!_peers.ContainsKey(ev.PeerId))
                {
                    if (_peers.Count >= _options.MaxPeers) { _inner.Disconnect(ev.PeerId, "Object transport capacity reached"); return; }
                    var peer = new Peer { Id = ev.PeerId, Rebuilder = new ObjectRebuilder(_options.MaxConcurrentObjectsPerPeer, ushort.MaxValue,
                        _options.ReassemblyTimeout.TotalSeconds, false, 1024, () => _clock.Elapsed.TotalSeconds) };
                    _peers.Add(ev.PeerId, peer); _schedule.Add(peer);
                }
                QueueEvent(ev, _peers[ev.PeerId]); return;
            }
            if (ev.Type == TransportEventType.Disconnected) { RemovePeer(ev.PeerId); QueueEvent(ev); return; }
            if (!_peers.TryGetValue(ev.PeerId, out var sender)) return;
            TransportEvent complete; uint sequence;
            try { complete = ReadFrame(sender, ev, out sequence); }
            catch (InvalidDataException) { Disconnect(ev.PeerId, "Invalid object frame"); return; }
            if (complete != null) DispatchComplete(sender, complete, sequence);
        }

        private TransportEvent ReadFrame(Peer sender, TransportEvent ev, out uint sequence)
        {
            var frame = ev.Payload; sequence = 0;
            if (frame.Length < HeaderSize || frame.Length > MaxFramePayloadSize || Read32(frame, 0) != Magic || frame[4] != 1 || frame[7] != 0 ||
                frame[5] > (byte)DeliveryMode.ReliableUnordered || frame[6] != ev.Channel || frame[6] >= 32) throw new InvalidDataException("Invalid frame header.");
            var delivery = (DeliveryMode)frame[5]; ulong id = Read64(frame, 8); sequence = Read32(frame, 16);
            int index = Read16(frame, 20), count = Read16(frame, 22); uint declared = Read32(frame, 24);
            int length = frame.Length - HeaderSize;
            if (id == 0 || count < 1 || index >= count || declared > MaxPayloadSize || count > Math.Max(1, declared) ||
                (count > 1 && (length == 0 || declared <= length)) || (count == 1 && (index != 0 || declared != length)) ||
                ev.Delivery != (count > 1 ? DeliveryMode.ReliableUnordered : delivery)) throw new InvalidDataException("Invalid fragment metadata.");
            var data = new byte[length]; Buffer.BlockCopy(frame, HeaderSize, data, 0, length);
            if (count > 1)
            {
                data = sender.Rebuilder.Add(id.ToString(System.Globalization.CultureInfo.InvariantCulture), count, index, data,
                    _options.MaxReassemblyBytesPerPeer, (int)declared, frame[5] + ":" + frame[6] + ":" + sequence);
                if (data == null) return null;
            }
            return new TransportEvent(TransportEventType.Data, sender.Id, data, delivery, ev.Channel);
        }

        private void DispatchComplete(Peer peer, TransportEvent ev, uint sequence)
        {
            var channel = GetChannel(peer, ev.Channel);
            if (ev.Delivery == DeliveryMode.UnreliableSequenced)
            {
                if (channel.HasSequenced && !Newer(sequence, channel.LatestSequenced)) return;
                channel.HasSequenced = true; channel.LatestSequenced = sequence;
            }
            if (ev.Delivery != DeliveryMode.ReliableOrdered) { QueueEvent(ev, peer); return; }
            if (sequence != channel.NextReceiveOrdered)
            {
                if (!Newer(sequence, channel.NextReceiveOrdered)) return;
                if (channel.Ordered.ContainsKey(sequence)) return;
                if (channel.Ordered.Count >= _options.MaxOrderedMessagesPerChannel || ev.Payload.Length > _options.MaxReassemblyBytesPerPeer - peer.OrderedBytes)
                { Disconnect(peer.Id, "Ordered object buffer exceeded"); return; }
                if (channel.Ordered.Count == 0) channel.GapStarted = _clock.Elapsed.TotalSeconds;
                channel.Ordered.Add(sequence, ev); peer.OrderedBytes += ev.Payload.Length; return;
            }
            channel.NextReceiveOrdered = unchecked(channel.NextReceiveOrdered + 1);
            QueueEvent(ev, peer);
            while (_peers.TryGetValue(peer.Id, out var current) && ReferenceEquals(current, peer) && channel.Ordered.TryGetValue(channel.NextReceiveOrdered, out var pending))
            {
                channel.Ordered.Remove(channel.NextReceiveOrdered); peer.OrderedBytes -= pending.Payload.Length;
                channel.NextReceiveOrdered = unchecked(channel.NextReceiveOrdered + 1); QueueEvent(pending, peer);
            }
            if (channel.Ordered.Count > 0) channel.GapStarted = _clock.Elapsed.TotalSeconds;
        }

        private void QueueEvent(TransportEvent ev, Peer peer = null)
        {
            if (peer != null)
            {
                if (peer.EventCount >= _options.MaxQueuedEventsPerPeer || ev.Payload.Length > _options.MaxReassemblyBytesPerPeer - peer.EventBytes)
                { Disconnect(peer.Id, "Completed object queue exceeded"); return; }
                peer.EventCount++; peer.EventBytes += ev.Payload.Length;
            }
            _events.Enqueue(new QueuedEvent { Event = ev, Peer = peer });
        }
        private void Dispatch(ref int callbacks, int maximum)
        {
            while (_events.Count > 0 && callbacks < maximum)
            {
                var queued = _events.Dequeue();
                if (queued.Peer != null)
                {
                    queued.Peer.EventCount--; queued.Peer.EventBytes -= queued.Event.Payload.Length;
                    if (!_peers.TryGetValue(queued.Peer.Id, out var current) || !ReferenceEquals(current, queued.Peer)) continue;
                }
                callbacks++; EventReceived?.Invoke(queued.Event);
            }
        }

        private void Flush(int budget)
        {
            int idle = 0;
            while (budget > 0 && _schedule.Count > 0 && idle < _schedule.Count)
            {
                if (_cursor >= _schedule.Count) _cursor = 0;
                var peer = _schedule[_cursor++];
                if (peer.Out.Count == 0) { idle++; continue; }
                if (!TryFlushPeer(peer)) { idle++; continue; }
                budget--; idle = 0;
            }
        }
        private bool TryFlushPeer(Peer peer)
        {
            if (peer.Out.Count == 0) return false;
            var message = peer.Out.Peek(); var frame = message.Frames[message.Index];
            if (!_inner.Send(peer.Id, new ArraySegment<byte>(frame), message.Delivery, message.Channel)) return false;
            message.Frames[message.Index] = null; // release accepted frame storage in step with the byte budget
            peer.OutBytes -= frame.Length; message.Index++;
            peer.Out.Dequeue();
            if (message.Index < message.Frames.Length) peer.Out.Enqueue(message);
            return true;
        }
        private void RemovePeer(ulong id)
        {
            if (!_peers.TryGetValue(id, out var peer)) return;
            _peers.Remove(id); int index = _schedule.IndexOf(peer);
            if (index >= 0) { _schedule.RemoveAt(index); if (index < _cursor) _cursor--; if (index < _maintenanceCursor) _maintenanceCursor--; }
        }
        private static Channel GetChannel(Peer peer, byte id)
        { if (!peer.Channels.TryGetValue(id, out var channel)) peer.Channels.Add(id, channel = new Channel()); return channel; }
        private static bool Newer(uint value, uint previous) => unchecked((int)(value - previous)) > 0;
        private static ushort Read16(byte[] bytes, int at) => (ushort)(bytes[at] | bytes[at + 1] << 8);
        private static uint Read32(byte[] bytes, int at) => (uint)(bytes[at] | bytes[at + 1] << 8 | bytes[at + 2] << 16 | bytes[at + 3] << 24);
        private static ulong Read64(byte[] bytes, int at) => Read32(bytes, at) | ((ulong)Read32(bytes, at + 4) << 32);
        private static void Write16(byte[] bytes, int at, ushort value) { bytes[at] = (byte)value; bytes[at + 1] = (byte)(value >> 8); }
        private static void Write32(byte[] bytes, int at, uint value) { for (int i = 0; i < 4; i++) bytes[at + i] = (byte)(value >> (i * 8)); }
        private static void Write64(byte[] bytes, int at, ulong value) { Write32(bytes, at, (uint)value); Write32(bytes, at + 4, (uint)(value >> 32)); }
    }
}
