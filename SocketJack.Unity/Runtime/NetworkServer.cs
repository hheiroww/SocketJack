using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net;
using SocketJack.Unity.Transport;

namespace SocketJack.Unity
{
    /// <summary>Single-threaded authoritative server with explicit room interest and bounded per-peer queues.
    /// Pump Update from a Unity Update method or a dedicated server loop. Objects are replicated only to
    /// peers in the same room. Game code validates input and calls SetState; clients cannot write state.</summary>
    public sealed class NetworkServer : IDisposable
    {
        private sealed class Peer
        {
            internal ulong Id;
            internal int Room;
            internal readonly Queue<byte[]> Reliable = new Queue<byte[]>();
            internal int QueuedBytes;
            internal double Tokens;
            internal int StateCursor;
            internal ulong[] Baseline = Array.Empty<ulong>();
            internal int BaselineCursor;
        }
        private readonly IGameTransport _transport;
        private readonly NetworkSessionOptions _options;
        private readonly Dictionary<ulong, Peer> _peers = new Dictionary<ulong, Peer>();
        private readonly Dictionary<ulong, NetworkObject> _objects = new Dictionary<ulong, NetworkObject>();
        private readonly Dictionary<int, HashSet<ulong>> _roomPeers = new Dictionary<int, HashSet<ulong>>();
        private readonly Dictionary<int, List<ulong>> _roomObjects = new Dictionary<int, List<ulong>>();
        private readonly Dictionary<ulong, HashSet<ulong>> _ownedObjects = new Dictionary<ulong, HashSet<ulong>>();
        private readonly Queue<ulong> _overflowDisconnects = new Queue<ulong>();
        private readonly HashSet<ulong> _overflowPending = new HashSet<ulong>();
        private readonly ReadOnlyDictionary<ulong, NetworkObject> _objectView;
        private double _tickAccumulator, _snapshotAccumulator;
        private ulong _nextId;
        private bool _disposed, _updating, _drainingOverflow;

        public bool IsRunning => _transport.IsRunning && _transport.IsServer;
        public uint Tick { get; private set; }
        public IReadOnlyDictionary<ulong, NetworkObject> Objects => _objectView;
        public IReadOnlyCollection<ulong> Peers => _peers.Keys;
        public IGameTransport Transport => _transport;
        public event Action<ulong> PeerConnected;
        public event Action<ulong, string> PeerDisconnected;
        public event Action<NetworkInput> InputReceived;
        public event Action<NetworkMessage> MessageReceived;
        public event Action<uint, float> Ticked;

        public NetworkServer(IGameTransport transport, NetworkSessionOptions options = null)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _options = (options ?? new NetworkSessionOptions()).Snapshot(transport);
            _objectView = new ReadOnlyDictionary<ulong, NetworkObject>(_objects);
            _transport.EventReceived += OnTransportEvent;
        }

        public void Start(IPEndPoint endpoint)
        {
            ThrowIfDisposed();
            if (_transport.IsRunning) throw new InvalidOperationException("Transport is already running.");
            Tick = 0; _nextId = 0; _tickAccumulator = _snapshotAccumulator = 0;
            _transport.StartServer(endpoint);
        }

        /// <summary>Elapsed seconds from a monotonic clock. Catch-up is capped and excess simulation time is dropped.</summary>
        public void Update(double deltaSeconds, int maxEvents = 256)
        {
            ThrowIfDisposed(); SessionWire.ValidateDelta(deltaSeconds);
            if (_updating) throw new InvalidOperationException("Update cannot be called recursively.");
            if (!IsRunning) return;
            _updating = true;
            try
            {
                foreach (var peer in _peers.Values)
                    peer.Tokens = Math.Min(_options.MaxInboundMessagesPerPeerPerSecond,
                        peer.Tokens + deltaSeconds * _options.MaxInboundMessagesPerPeerPerSecond);
                _transport.Poll(maxEvents);
                if (!IsRunning) return;
                double step = 1d / _options.TickRate;
                _tickAccumulator = Math.Min(_tickAccumulator + deltaSeconds, step * _options.MaxCatchUpTicks);
                int count = 0;
                while (_tickAccumulator + 1e-10 >= step && count++ < _options.MaxCatchUpTicks && IsRunning)
                {
                    _tickAccumulator = Math.Max(0, _tickAccumulator - step);
                    Tick = unchecked(Tick + 1);
                    Ticked?.Invoke(Tick, (float)step);
                    _snapshotAccumulator += step;
                    if (_snapshotAccumulator + 1e-10 >= 1d / _options.SnapshotRate)
                    {
                        _snapshotAccumulator %= 1d / _options.SnapshotRate;
                        BroadcastStates();
                    }
                }
                FlushReliable();
            }
            finally { _updating = false; }
        }

        public NetworkObject Spawn(uint prefabId, ulong ownerId = 0, int roomId = 0, byte[] state = null)
        {
            RequireRunning();
            var payload = SessionWire.Copy(state, _options.MaxStateBytes, nameof(state));
            if (_objects.Count >= _options.MaxObjects || RoomObjects(roomId).Count >= _options.MaxObjectsPerRoom)
                throw new InvalidOperationException("Object capacity reached.");
            if (ownerId != 0 && (!_peers.TryGetValue(ownerId, out var owner) || owner.Room != roomId))
                throw new ArgumentException("The owner must be a connected peer in this room.", nameof(ownerId));
            var obj = new NetworkObject { Id = checked(++_nextId), PrefabId = prefabId, OwnerId = ownerId,
                RoomId = roomId, Revision = 1, Tick = Tick, StateData = payload };
            _objects.Add(obj.Id, obj); RoomObjects(roomId).Add(obj.Id);
            if (ownerId != 0)
            {
                if (!_ownedObjects.TryGetValue(ownerId, out var owned)) _ownedObjects.Add(ownerId, owned = new HashSet<ulong>());
                owned.Add(obj.Id);
            }
            BroadcastReliable(roomId, SessionWire.Spawn(obj));
            return obj;
        }

        public void SetState(ulong objectId, byte[] state)
        {
            RequireRunning();
            if (!_objects.TryGetValue(objectId, out var obj)) throw new KeyNotFoundException("Unknown network object.");
            var payload = SessionWire.Copy(state, _options.MaxStateBytes, nameof(state));
            if (obj.StateData.SequenceEqual(payload)) return;
            obj.StateData = payload; obj.Revision = unchecked(obj.Revision + 1); obj.Tick = Tick;
        }

        public bool Despawn(ulong objectId)
        {
            RequireRunning();
            if (!_objects.TryGetValue(objectId, out var obj)) return false;
            _objects.Remove(objectId); RemoveRoomObject(obj.RoomId, objectId);
            RemoveOwnedObject(obj.OwnerId, objectId);
            BroadcastReliable(obj.RoomId, SessionWire.Despawn(objectId));
            return true;
        }

        /// <summary>Moves this peer's interest to one room, despawning old views and sending a full new baseline.</summary>
        public void SetPeerRoom(ulong peerId, int roomId)
        {
            RequireRunning();
            if (!_peers.TryGetValue(peerId, out var peer)) throw new KeyNotFoundException("Unknown peer.");
            if (peer.Room == roomId) return;
            if (!Enqueue(peer, SessionWire.Write(SessionWire.Kind.ClearView, _ => { }))) return;
            RemoveRoomPeer(peer.Room, peerId); peer.Room = roomId; peer.StateCursor = 0; RoomPeers(roomId).Add(peerId);
            BeginBaseline(peer);
        }

        public int GetPeerRoom(ulong peerId)
        {
            if (!_peers.TryGetValue(peerId, out var peer)) throw new KeyNotFoundException("Unknown peer.");
            return peer.Room;
        }

        /// <summary>Moves an object between visibility rooms. Move its owning peer first when applicable.</summary>
        public void SetObjectRoom(ulong objectId, int roomId)
        {
            RequireRunning();
            if (!_objects.TryGetValue(objectId, out var obj)) throw new KeyNotFoundException("Unknown network object.");
            if (obj.RoomId == roomId) return;
            if (RoomObjects(roomId).Count >= _options.MaxObjectsPerRoom) throw new InvalidOperationException("Room object capacity reached.");
            if (obj.OwnerId != 0 && (!_peers.TryGetValue(obj.OwnerId, out var owner) || owner.Room != roomId))
                throw new InvalidOperationException("Move the owning peer to the destination room first.");
            BroadcastReliable(obj.RoomId, SessionWire.Despawn(objectId)); RemoveRoomObject(obj.RoomId, objectId);
            obj.RoomId = roomId; obj.Revision = unchecked(obj.Revision + 1); obj.Tick = Tick;
            RoomObjects(roomId).Add(objectId); BroadcastReliable(roomId, SessionWire.Spawn(obj));
        }

        public bool SendMessage(ulong peerId, ushort messageId, byte[] payload,
            DeliveryMode delivery = DeliveryMode.ReliableOrdered, byte channel = 3)
        {
            RequireRunning(); ValidateUserChannel(channel);
            var copy = SessionWire.Copy(payload, _options.MaxMessageBytes, nameof(payload));
            if (!_peers.ContainsKey(peerId)) return false;
            var packet = SessionWire.Write(SessionWire.Kind.Message, w => { w.Write(messageId); SessionWire.Bytes(w, copy); });
            return _transport.Send(peerId, new ArraySegment<byte>(packet), delivery, channel);
        }

        public void Disconnect(ulong peerId, string reason = "Disconnected")
        {
            if (_disposed) return;
            _transport.Disconnect(peerId, reason); RemovePeer(peerId, reason);
        }

        public void Stop()
        {
            if (_disposed) return;
            _transport.Stop();
            _peers.Clear(); _objects.Clear(); _roomObjects.Clear(); _roomPeers.Clear(); _ownedObjects.Clear();
            _overflowDisconnects.Clear(); _overflowPending.Clear();
            _tickAccumulator = _snapshotAccumulator = 0;
        }

        public void Dispose()
        {
            if (_disposed) return;
            Stop(); _transport.EventReceived -= OnTransportEvent; _transport.Dispose(); _disposed = true;
        }

        private void OnTransportEvent(TransportEvent ev)
        {
            if (ev.Type == TransportEventType.Connected)
            {
                if (_peers.ContainsKey(ev.PeerId)) return;
                if (ev.PeerId == 0 || _peers.Count >= _options.MaxPeers) { _transport.Disconnect(ev.PeerId, "Session capacity reached"); return; }
                var peer = new Peer { Id = ev.PeerId, Tokens = _options.MaxInboundMessagesPerPeerPerSecond };
                _peers.Add(peer.Id, peer); RoomPeers(0).Add(peer.Id);
                Enqueue(peer, SessionWire.Write(SessionWire.Kind.Welcome, w => { w.Write(peer.Id); w.Write(Tick); }));
                PeerConnected?.Invoke(peer.Id);
                if (!_peers.ContainsKey(peer.Id)) return;
                BeginBaseline(peer);
                return;
            }
            if (ev.Type == TransportEventType.Disconnected) { RemovePeer(ev.PeerId, ev.Reason); return; }
            if (!_peers.TryGetValue(ev.PeerId, out var sender)) return;
            if (--sender.Tokens < 0) { Disconnect(sender.Id, "Inbound message rate exceeded"); return; }
            Action callback = null;
            try
            {
                using (var reader = SessionWire.Read(ev.Payload, _transport.MaxPayloadSize, out var kind))
                {
                    switch (kind)
                    {
                        case SessionWire.Kind.Input:
                            if (ev.Channel != 2) throw new InvalidDataException();
                            ulong objectId = reader.ReadUInt64();
                            var input = SessionWire.Bytes(reader, _options.MaxMessageBytes); SessionWire.End(reader);
                            if (!_objects.TryGetValue(objectId, out var obj) || obj.OwnerId != sender.Id || obj.RoomId != sender.Room)
                            { Disconnect(sender.Id, "Input authority denied"); return; }
                            callback = () => InputReceived?.Invoke(new NetworkInput { PeerId = sender.Id, ObjectId = objectId, Payload = input, Delivery = ev.Delivery });
                            break;
                        case SessionWire.Kind.Message:
                            ValidateWireChannel(ev.Channel);
                            ushort messageId = reader.ReadUInt16(); var message = SessionWire.Bytes(reader, _options.MaxMessageBytes); SessionWire.End(reader);
                            callback = () => MessageReceived?.Invoke(new NetworkMessage { PeerId = sender.Id, MessageId = messageId, Payload = message, Delivery = ev.Delivery, Channel = ev.Channel });
                            break;
                        default: throw new InvalidDataException();
                    }
                }
            }
            catch (Exception error) when (error is InvalidDataException || error is EndOfStreamException)
            { Disconnect(ev.PeerId, "Invalid session packet"); return; }
            callback?.Invoke();
        }

        private void RemovePeer(ulong id, string reason)
        {
            if (!_peers.TryGetValue(id, out var peer)) return;
            _peers.Remove(id); RemoveRoomPeer(peer.Room, id);
            var owned = _ownedObjects.TryGetValue(id, out var ownerObjects) ? ownerObjects.ToArray() : Array.Empty<ulong>();
            _ownedObjects.Remove(id);
            foreach (ulong objectId in owned)
            {
                var obj = _objects[objectId]; _objects.Remove(objectId); RemoveRoomObject(obj.RoomId, objectId);
                BroadcastReliable(obj.RoomId, SessionWire.Despawn(objectId));
            }
            PeerDisconnected?.Invoke(id, reason);
        }

        private bool Enqueue(Peer peer, byte[] packet, bool disconnectOnOverflow = true)
        {
            if (!_peers.ContainsKey(peer.Id)) return false;
            if (peer.Reliable.Count >= _options.MaxQueuedMessagesPerPeer || packet.Length > _options.MaxQueuedBytesPerPeer - peer.QueuedBytes)
            { if (disconnectOnOverflow) { ScheduleOverflowDisconnect(peer.Id); DrainOverflowDisconnects(); } return false; }
            peer.Reliable.Enqueue(packet); peer.QueuedBytes += packet.Length; return true;
        }
        private void BroadcastReliable(int room, byte[] packet)
        {
            // Finish enqueueing this lifecycle event before disconnect cleanup can publish another event.
            // Otherwise an overflowing owner's cleanup could send Despawn before this Spawn to later observers.
            var overflow = new List<ulong>();
            foreach (ulong id in RoomPeers(room).ToArray())
                if (_peers.TryGetValue(id, out var peer) && !Enqueue(peer, packet, false)) overflow.Add(id);
            foreach (ulong id in overflow) ScheduleOverflowDisconnect(id);
            DrainOverflowDisconnects();
        }

        private void ScheduleOverflowDisconnect(ulong id)
        { if (_overflowPending.Add(id)) _overflowDisconnects.Enqueue(id); }
        private void DrainOverflowDisconnects()
        {
            if (_drainingOverflow) return;
            _drainingOverflow = true;
            try
            {
                while (_overflowDisconnects.Count > 0)
                {
                    ulong id = _overflowDisconnects.Dequeue(); _overflowPending.Remove(id);
                    Disconnect(id, "Reliable session queue overflow");
                }
            }
            finally { _drainingOverflow = false; }
        }

        private void BeginBaseline(Peer peer)
        {
            peer.Baseline = _roomObjects.TryGetValue(peer.Room, out var objects) ? objects.ToArray() : Array.Empty<ulong>();
            peer.BaselineCursor = 0;
        }

        private void FillBaseline(Peer peer)
        {
            for (int i = 0; i < _options.MaxReliableSendsPerUpdate && peer.BaselineCursor < peer.Baseline.Length; i++)
            {
                ulong id = peer.Baseline[peer.BaselineCursor];
                if (!_objects.TryGetValue(id, out var obj) || obj.RoomId != peer.Room) { peer.BaselineCursor++; continue; }
                if (!Enqueue(peer, SessionWire.Spawn(obj), false)) break;
                peer.BaselineCursor++;
            }
            if (peer.BaselineCursor == peer.Baseline.Length) { peer.Baseline = Array.Empty<ulong>(); peer.BaselineCursor = 0; }
        }

        private void FlushReliable()
        {
            foreach (var peer in _peers.Values.ToArray())
            {
                FillBaseline(peer);
                for (int i = 0; i < _options.MaxReliableSendsPerUpdate && peer.Reliable.Count > 0; i++)
                {
                    var packet = peer.Reliable.Peek();
                    if (!_transport.Send(peer.Id, new ArraySegment<byte>(packet), DeliveryMode.ReliableOrdered, 0)) break;
                    peer.Reliable.Dequeue(); peer.QueuedBytes -= packet.Length;
                }
            }
        }

        private void BroadcastStates()
        {
            var packets = new Dictionary<ulong, byte[]>();
            foreach (var peer in _peers.Values.ToArray())
            {
                if (peer.Reliable.Count > 0 || peer.Baseline.Length > 0) continue; // send lifecycle baselines first
                var objects = RoomObjects(peer.Room);
                int count = Math.Min(objects.Count, _options.MaxStateUpdatesPerTick);
                for (int i = 0; i < count && objects.Count > 0; i++)
                {
                    if (peer.StateCursor >= objects.Count) peer.StateCursor = 0;
                    if (_objects.TryGetValue(objects[peer.StateCursor++], out var obj))
                    {
                        if (!packets.TryGetValue(obj.Id, out var packet)) packets.Add(obj.Id, packet = SessionWire.State(obj));
                        if (!_transport.Send(peer.Id, new ArraySegment<byte>(packet), DeliveryMode.UnreliableSequenced, 1)) break;
                    }
                }
            }
        }

        private List<ulong> RoomObjects(int room)
        { if (!_roomObjects.TryGetValue(room, out var value)) _roomObjects.Add(room, value = new List<ulong>()); return value; }
        private HashSet<ulong> RoomPeers(int room)
        { if (!_roomPeers.TryGetValue(room, out var value)) _roomPeers.Add(room, value = new HashSet<ulong>()); return value; }
        private void RemoveRoomPeer(int room, ulong id)
        { if (_roomPeers.TryGetValue(room, out var peers)) { peers.Remove(id); if (peers.Count == 0) _roomPeers.Remove(room); } }
        private void RemoveRoomObject(int room, ulong id)
        { if (_roomObjects.TryGetValue(room, out var objects)) { objects.Remove(id); if (objects.Count == 0) _roomObjects.Remove(room); } }
        private void RemoveOwnedObject(ulong owner, ulong id)
        { if (_ownedObjects.TryGetValue(owner, out var objects)) { objects.Remove(id); if (objects.Count == 0) _ownedObjects.Remove(owner); } }
        private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(NetworkServer)); }
        private void RequireRunning() { ThrowIfDisposed(); if (!IsRunning) throw new InvalidOperationException("Server is not running."); }
        private static void ValidateUserChannel(byte channel) { if (channel < 3) throw new ArgumentOutOfRangeException(nameof(channel), "Channels 0, 1 and 2 are reserved for session traffic."); }
        private static void ValidateWireChannel(byte channel) { if (channel < 3) throw new InvalidDataException("Reserved channel."); }
    }
}
