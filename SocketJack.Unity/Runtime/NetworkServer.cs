using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net;
using SocketJack.Multiplayer.Transport;

namespace SocketJack.Multiplayer
{
    /// <summary>Single-threaded authoritative server with explicit room interest and bounded per-peer queues.
    /// Pump Update from a Unity Update method or a dedicated server loop. Objects are replicated only to
    /// peers in the same room. Game code validates input and calls SetState. Owner state updates
    /// require an explicit server-assigned owner-authority mode; server authority is the default.</summary>
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
            internal bool Ready;
            internal double HandshakeAge;
            internal byte[] ConnectionData = Array.Empty<byte>();
            internal readonly HashSet<ulong> Visible = new HashSet<ulong>();
            internal readonly List<ulong> VisibleList = new List<ulong>();
            internal ulong[] InterestCandidates;
            internal int InterestCursor;
            internal readonly Dictionary<ulong, ulong> Views = new Dictionary<ulong, ulong>();
            internal ulong NextView;
            internal int SnapshotEvery = 1, SnapshotCounter, StateLimit;
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
        private int _interestPeerCursor;
        public INetworkInterestProvider InterestProvider { get; set; }

        public bool IsRunning => _transport.IsRunning && _transport.IsServer;
        public uint Tick { get; private set; }
        public IReadOnlyDictionary<ulong, NetworkObject> Objects => _objectView;
        public IReadOnlyCollection<ulong> Peers => _peers.Keys;
        public IGameTransport Transport => _transport;
        public event Action<ulong> PeerConnected;
        public event Action<ulong, string> PeerDisconnected;
        public event Action<NetworkInput> InputReceived;
        public event Action<NetworkMessage> MessageReceived;
        public event Action<NetworkObject> ObjectChanged;
        public event Action<NetworkObject, ulong> OwnershipChanged;
        /// <summary>Optional game-specific validation for owner-authoritative state. Ownership,
        /// room, size, sequence and the server-assigned authority flag are always enforced first.</summary>
        public Func<NetworkObject, byte[], bool> OwnerStateValidator { get; set; }
        public event Action<uint, float> Ticked;

        public NetworkServer(IGameTransport transport, NetworkSessionOptions options = null)
        {
            _transport = ObjectFrameTransport.Wrap(transport ?? throw new ArgumentNullException(nameof(transport)));
            _options = (options ?? new NetworkSessionOptions()).Snapshot(_transport);
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
                foreach (var peer in _peers.Values.ToArray())
                {
                    peer.Tokens = Math.Min(_options.MaxInboundMessagesPerPeerPerSecond,
                        peer.Tokens + deltaSeconds * _options.MaxInboundMessagesPerPeerPerSecond);
                    if (!peer.Ready && (peer.HandshakeAge += deltaSeconds) >= 10)
                        Disconnect(peer.Id, "Session compatibility handshake timed out");
                }
                _transport.Poll(maxEvents);
                if (!IsRunning) return;
                RefreshInterest();
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
            => Spawn(prefabId, ownerId, roomId, state, null);

        /// <summary>Creates an object with explicit canonical state authority. Unowned objects remain server-authoritative.</summary>
        public NetworkObject Spawn(uint prefabId, ulong ownerId, int roomId, byte[] state, bool? serverAuthority)
        {
            RequireRunning();
            var payload = SessionWire.Copy(state, _options.MaxStateBytes, nameof(state));
            if (_objects.Count >= _options.MaxObjects || RoomObjects(roomId).Count >= _options.MaxObjectsPerRoom)
                throw new InvalidOperationException("Object capacity reached.");
            if (ownerId != 0 && (!_peers.TryGetValue(ownerId, out var owner) || !owner.Ready || owner.Room != roomId))
                throw new ArgumentException("The owner must be a connected peer in this room.", nameof(ownerId));
            var obj = new NetworkObject { Id = checked(++_nextId), PrefabId = prefabId, OwnerId = ownerId,
                RoomId = roomId, Revision = 1, Tick = Tick, StateData = payload,
                ServerAuthority = ownerId == 0 || (serverAuthority ?? _options.ServerAuthority) };
            _objects.Add(obj.Id, obj); RoomObjects(roomId).Add(obj.Id);
            if (ownerId != 0)
            {
                if (!_ownedObjects.TryGetValue(ownerId, out var owned)) _ownedObjects.Add(ownerId, owned = new HashSet<ulong>());
                owned.Add(obj.Id);
            }
            BroadcastReliable(roomId, null, obj, spawn: true);
            return obj;
        }

        public void SetState(ulong objectId, byte[] state)
        {
            RequireRunning();
            if (!_objects.TryGetValue(objectId, out var obj)) throw new KeyNotFoundException("Unknown network object.");
            var payload = SessionWire.Copy(state, _options.MaxStateBytes, nameof(state));
            if (obj.StateData.SequenceEqual(payload)) return;
            obj.StateData = payload; obj.Revision = unchecked(obj.Revision + 1); obj.Tick = Tick;
            ObjectChanged?.Invoke(obj);
        }

        public bool Despawn(ulong objectId)
        {
            RequireRunning();
            if (!_objects.TryGetValue(objectId, out var obj)) return false;
            _objects.Remove(objectId); RemoveRoomObject(obj.RoomId, objectId);
            RemoveOwnedObject(obj.OwnerId, objectId);
            BroadcastReliable(obj.RoomId, SessionWire.Despawn(objectId), obj, despawn: true);
            return true;
        }

        /// <summary>Server-only ownership change. Old input/state proposals are revoked, including after transfer back.</summary>
        public void TransferOwnership(ulong objectId, ulong ownerId, bool? serverAuthority = null)
        {
            RequireRunning();
            if (!_objects.TryGetValue(objectId, out var obj)) throw new KeyNotFoundException("Unknown network object.");
            if (ownerId != 0 && (!_peers.TryGetValue(ownerId, out var owner) || !owner.Ready || owner.Room != obj.RoomId))
                throw new ArgumentException("The new owner must be connected in the object's room.", nameof(ownerId));
            bool authority = ownerId == 0 || (serverAuthority ?? obj.ServerAuthority);
            if (obj.OwnerId == ownerId && obj.ServerAuthority == authority) return;
            if (obj.AuthorityEpoch == uint.MaxValue) throw new InvalidOperationException("Authority epoch exhausted; respawn this object.");
            ulong previous = obj.OwnerId;
            RemoveOwnedObject(previous, objectId);
            if (ownerId != 0)
            {
                if (!_ownedObjects.TryGetValue(ownerId, out var owned)) _ownedObjects.Add(ownerId, owned = new HashSet<ulong>());
                owned.Add(objectId);
            }
            obj.OwnerId = ownerId; obj.ServerAuthority = authority; obj.AuthorityEpoch++;
            obj.HasOwnerStateSequence = false; obj.LastOwnerStateSequence = 0;
            obj.Revision = unchecked(obj.Revision + 1); obj.Tick = Tick;
            BroadcastReliable(obj.RoomId, SessionWire.Ownership(obj), obj);
            OwnershipChanged?.Invoke(obj, previous);
            ObjectChanged?.Invoke(obj);
        }

        /// <summary>Moves this peer's interest to one room, despawning old views and sending a full new baseline.</summary>
        public void SetPeerRoom(ulong peerId, int roomId)
        {
            RequireRunning();
            if (!_peers.TryGetValue(peerId, out var peer)) throw new KeyNotFoundException("Unknown peer.");
            if (peer.Room == roomId) return;
            if (!Enqueue(peer, SessionWire.Write(SessionWire.Kind.ClearView, _ => { }))) return;
            peer.Visible.Clear(); peer.VisibleList.Clear(); peer.Views.Clear(); peer.InterestCandidates = null;
            RemoveRoomPeer(peer.Room, peerId); peer.Room = roomId; peer.StateCursor = 0; RoomPeers(roomId).Add(peerId);
            BeginBaseline(peer);
        }

        public int GetPeerRoom(ulong peerId)
        {
            if (!_peers.TryGetValue(peerId, out var peer)) throw new KeyNotFoundException("Unknown peer.");
            return peer.Room;
        }
        /// <summary>Copy of transport-validated admission data; available when PeerConnected is raised.</summary>
        public byte[] GetConnectionData(ulong peerId)
        {
            if (!_peers.TryGetValue(peerId, out var peer) || !peer.Ready) throw new KeyNotFoundException("Unknown admitted peer.");
            return (byte[])peer.ConnectionData.Clone();
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
            BroadcastReliable(obj.RoomId, SessionWire.Despawn(objectId), obj, despawn: true); RemoveRoomObject(obj.RoomId, objectId);
            obj.RoomId = roomId; obj.Revision = unchecked(obj.Revision + 1); obj.Tick = Tick;
            RoomObjects(roomId).Add(objectId); BroadcastReliable(roomId, null, obj, spawn: true);
        }

        /// <summary>Canonical observer permission used by state, members, relays and object events.</summary>
        public bool CanObserve(ulong peerId, ulong objectId)
            => _peers.TryGetValue(peerId, out var peer) && peer.Ready && _objects.TryGetValue(objectId, out var obj) &&
               peer.Room == obj.RoomId && peer.Visible.Contains(objectId) && (InterestProvider == null || InterestProvider.IsVisible(peerId, obj, true));
        public ulong GetViewId(ulong peerId, ulong objectId)
            => CanObserve(peerId, objectId) && _peers[peerId].Views.TryGetValue(objectId, out ulong view) ? view : 0;
        public long GetQueuedBytes(ulong peerId) => _peers.TryGetValue(peerId, out var peer) ? peer.QueuedBytes : 0;
        public void SetPeerReplication(ulong peerId, int snapshotEvery = 1, int maxStatesPerSnapshot = 0)
        {
            if (snapshotEvery < 1 || snapshotEvery > 8 || maxStatesPerSnapshot < 0 || maxStatesPerSnapshot > _options.MaxStateUpdatesPerTick) throw new ArgumentOutOfRangeException(nameof(snapshotEvery));
            if (!_peers.TryGetValue(peerId, out var peer)) throw new KeyNotFoundException("Unknown peer.");
            peer.SnapshotEvery = snapshotEvery; peer.StateLimit = maxStatesPerSnapshot;
        }

        public bool SendMessage(ulong peerId, ushort messageId, byte[] payload,
            DeliveryMode delivery = DeliveryMode.ReliableUnordered, byte channel = 3)
        {
            RequireRunning(); ValidateUserChannel(channel);
            var copy = SessionWire.Copy(payload, _options.MaxMessageBytes, nameof(payload));
            if (!_peers.TryGetValue(peerId, out var target) || !target.Ready) return false;
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
                if (ev.Payload.Length > 512) { _transport.Disconnect(ev.PeerId, "Admission data exceeds session limit"); return; }
                var peer = new Peer { Id = ev.PeerId, Tokens = _options.MaxInboundMessagesPerPeerPerSecond, ConnectionData = (byte[])ev.Payload.Clone() };
                _peers.Add(peer.Id, peer);
                if (_options.CompatibilityId.Length == 0) AcceptPeer(peer);
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
                        case SessionWire.Kind.Hello:
                            if (ev.Channel != 0 || ev.Delivery != DeliveryMode.ReliableOrdered) throw new InvalidDataException();
                            var compatibility = System.Text.Encoding.UTF8.GetString(SessionWire.Bytes(reader, 512)); SessionWire.End(reader);
                            if (!string.Equals(compatibility, _options.CompatibilityId, StringComparison.Ordinal))
                            { Disconnect(sender.Id, "Game, prefab, serializer or precision configuration mismatch"); return; }
                            if (!sender.Ready) AcceptPeer(sender);
                            break;
                        case SessionWire.Kind.Input:
                            if (!sender.Ready || ev.Channel != 2) throw new InvalidDataException();
                            ulong objectId = reader.ReadUInt64(); uint inputEpoch = reader.ReadUInt32(); ulong inputView = reader.ReadUInt64();
                            var input = SessionWire.Bytes(reader, _options.MaxMessageBytes); SessionWire.End(reader);
                            if (GetViewId(sender.Id, objectId) != inputView || inputView == 0) return;
                            if (_objects.TryGetValue(objectId, out var revokedInput) && inputEpoch != revokedInput.AuthorityEpoch) return;
                            if (!_objects.TryGetValue(objectId, out var obj) || obj.OwnerId != sender.Id || obj.RoomId != sender.Room)
                            { Disconnect(sender.Id, "Input authority denied"); return; }
                            callback = () => InputReceived?.Invoke(new NetworkInput { PeerId = sender.Id, ObjectId = objectId, Payload = input, Delivery = ev.Delivery });
                            break;
                        case SessionWire.Kind.Message:
                            if (!sender.Ready) throw new InvalidDataException();
                            ValidateWireChannel(ev.Channel);
                            ushort messageId = reader.ReadUInt16(); var message = SessionWire.Bytes(reader, _options.MaxMessageBytes); SessionWire.End(reader);
                            callback = () => MessageReceived?.Invoke(new NetworkMessage { PeerId = sender.Id, MessageId = messageId, Payload = message, Delivery = ev.Delivery, Channel = ev.Channel });
                            break;
                        case SessionWire.Kind.OwnerState:
                            if (!sender.Ready || ev.Channel != 2) throw new InvalidDataException();
                            ulong ownerObjectId = reader.ReadUInt64(); uint ownerEpoch = reader.ReadUInt32(); ulong ownerView = reader.ReadUInt64(); uint ownerSequence = reader.ReadUInt32();
                            var ownerState = SessionWire.Bytes(reader, _options.MaxStateBytes); SessionWire.End(reader);
                            if (GetViewId(sender.Id, ownerObjectId) != ownerView || ownerView == 0) return;
                            if (_objects.TryGetValue(ownerObjectId, out var revokedState) && ownerEpoch != revokedState.AuthorityEpoch) return;
                            if (!_objects.TryGetValue(ownerObjectId, out var ownerObject) || ownerObject.ServerAuthority ||
                                ownerObject.OwnerId != sender.Id || ownerObject.RoomId != sender.Room)
                            { Disconnect(sender.Id, "Owner state authority denied"); return; }
                            callback = () => ApplyOwnerState(sender.Id, ownerObject, ownerEpoch, ownerSequence, ownerState);
                            break;
                        default: throw new InvalidDataException();
                    }
                }
            }
            catch (Exception error) when (error is InvalidDataException || error is EndOfStreamException)
            { Disconnect(ev.PeerId, "Invalid session packet"); return; }
            callback?.Invoke();
        }

        private void AcceptPeer(Peer peer)
        {
            peer.Ready = true; RoomPeers(peer.Room).Add(peer.Id);
            Enqueue(peer, SessionWire.Write(SessionWire.Kind.Welcome, w =>
            { w.Write(peer.Id); w.Write(Tick); SessionWire.Bytes(w, System.Text.Encoding.UTF8.GetBytes(_options.CompatibilityId)); }));
            PeerConnected?.Invoke(peer.Id);
            if (_peers.ContainsKey(peer.Id)) BeginBaseline(peer);
        }

        private void ApplyOwnerState(ulong peerId, NetworkObject obj, uint epoch, uint sequence, byte[] state)
        {
            if (obj.HasOwnerStateSequence && !SessionWire.Newer(sequence, obj.LastOwnerStateSequence)) return;
            bool accepted;
            try { accepted = OwnerStateValidator == null || OwnerStateValidator(obj, state); }
            catch { accepted = false; }
            if (!IsRunning || !_peers.TryGetValue(peerId, out var currentPeer) || !_objects.TryGetValue(obj.Id, out var current) ||
                !ReferenceEquals(current, obj) || obj.AuthorityEpoch != epoch || obj.ServerAuthority || obj.OwnerId != peerId || obj.RoomId != currentPeer.Room) return;
            if (!accepted) { Disconnect(peerId, "Owner state validation denied"); return; }
            obj.HasOwnerStateSequence = true; obj.LastOwnerStateSequence = sequence;
            SetState(obj.Id, state);
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
                BroadcastReliable(obj.RoomId, SessionWire.Despawn(objectId), obj, despawn: true);
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
        private void BroadcastReliable(int room, byte[] packet, NetworkObject obj = null, bool spawn = false, bool despawn = false)
        {
            // Finish enqueueing this lifecycle event before disconnect cleanup can publish another event.
            // Otherwise an overflowing owner's cleanup could send Despawn before this Spawn to later observers.
            var overflow = new List<ulong>();
            foreach (ulong id in RoomPeers(room).ToArray())
                if (_peers.TryGetValue(id, out var peer))
                {
                    if (obj != null)
                    {
                        if (spawn && (peer.Visible.Contains(obj.Id) || peer.Visible.Count >= _options.MaxVisibleObjectsPerPeer ||
                            (InterestProvider != null && !InterestProvider.IsVisible(id, obj, false)))) continue;
                        if (!spawn && !peer.Visible.Contains(obj.Id)) continue;
                    }
                    ulong view = spawn ? checked(peer.NextView + 1) : 0;
                    if (!Enqueue(peer, spawn ? SessionWire.Spawn(obj, view) : packet, false)) { overflow.Add(id); continue; }
                    if (obj != null && spawn) { peer.NextView = view; peer.Views[obj.Id] = view; peer.Visible.Add(obj.Id); peer.VisibleList.Add(obj.Id); }
                    if (obj != null && despawn) { peer.Views.Remove(obj.Id); peer.Visible.Remove(obj.Id); peer.VisibleList.Remove(obj.Id); }
                }
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
            peer.Baseline = InterestProvider == null
                ? (_roomObjects.TryGetValue(peer.Room, out var objects) ? objects.ToArray() : Array.Empty<ulong>())
                : InterestProvider.QueryCandidates(peer.Id, peer.Room).Take(_options.MaxObjectsPerRoom).Distinct().ToArray();
            peer.BaselineCursor = 0;
        }

        private void FillBaseline(Peer peer)
        {
            for (int i = 0; i < _options.MaxReliableSendsPerUpdate && peer.BaselineCursor < peer.Baseline.Length; i++)
            {
                ulong id = peer.Baseline[peer.BaselineCursor];
                if (!_objects.TryGetValue(id, out var obj) || obj.RoomId != peer.Room || peer.Visible.Contains(id) ||
                    (InterestProvider != null && !InterestProvider.IsVisible(peer.Id, obj, false))) { peer.BaselineCursor++; continue; }
                if (peer.Visible.Count >= _options.MaxVisibleObjectsPerPeer) { peer.BaselineCursor = peer.Baseline.Length; break; }
                ulong view = checked(peer.NextView + 1);
                if (!Enqueue(peer, SessionWire.Spawn(obj, view), false)) break;
                peer.NextView = view; peer.Views[id] = view;
                peer.Visible.Add(id); peer.VisibleList.Add(id);
                peer.BaselineCursor++;
            }
            if (peer.BaselineCursor == peer.Baseline.Length) { peer.Baseline = Array.Empty<ulong>(); peer.BaselineCursor = 0; }
        }

        private void FlushReliable()
        {
            foreach (var peer in _peers.Values.ToArray())
            {
                if (!peer.Ready) continue;
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
                if (!peer.Ready) continue;
                if (peer.Reliable.Count > 0 || peer.Baseline.Length > 0) continue; // send lifecycle baselines first
                peer.SnapshotCounter = (peer.SnapshotCounter + 1) % peer.SnapshotEvery;
                if (peer.SnapshotCounter != 0) continue;
                var objects = peer.VisibleList;
                int count = Math.Min(objects.Count, peer.StateLimit == 0 ? _options.MaxStateUpdatesPerTick : peer.StateLimit);
                for (int i = 0; i < count && objects.Count > 0; i++)
                {
                    if (peer.StateCursor >= objects.Count) peer.StateCursor = 0;
                    if (_objects.TryGetValue(objects[peer.StateCursor++], out var obj) && CanObserve(peer.Id, obj.Id))
                    {
                        if (!packets.TryGetValue(obj.Id, out var packet)) packets.Add(obj.Id, packet = SessionWire.State(obj));
                        if (!_transport.Send(peer.Id, new ArraySegment<byte>(packet), DeliveryMode.UnreliableSequenced, 1)) break;
                    }
                }
            }
        }

        private void RefreshInterest()
        {
            if (InterestProvider == null || _peers.Count == 0) return;
            var peers = _peers.Values.ToArray(); int budget = _options.MaxInterestChecksPerUpdate;
            for (int visited = 0; visited < Math.Min(peers.Length, _options.MaxInterestPeersPerUpdate) && budget > 0; visited++)
            {
                if (_interestPeerCursor >= peers.Length) _interestPeerCursor = 0;
                var peer = peers[_interestPeerCursor++]; if (!peer.Ready) continue;
                // Revocation precedes admission. The list is bounded by MaxVisibleObjectsPerPeer.
                for (int i = peer.VisibleList.Count - 1; i >= 0 && budget > 0; i--)
                {
                    budget--; ulong id = peer.VisibleList[i];
                    if (_objects.TryGetValue(id, out var obj) && peer.Room == obj.RoomId && InterestProvider.IsVisible(peer.Id, obj, true)) continue;
                    if (!Enqueue(peer, SessionWire.Despawn(id))) break;
                    peer.Views.Remove(id); peer.Visible.Remove(id); peer.VisibleList.RemoveAt(i);
                }
                if (peer.InterestCandidates == null)
                {
                    peer.InterestCandidates = InterestProvider.QueryCandidates(peer.Id, peer.Room).Take(_options.MaxObjectsPerRoom).Distinct().ToArray();
                    peer.InterestCursor = 0;
                }
                while (budget-- > 0 && peer.InterestCursor < peer.InterestCandidates.Length)
                {
                    ulong id = peer.InterestCandidates[peer.InterestCursor++];
                    if (peer.Visible.Contains(id) || peer.Visible.Count >= _options.MaxVisibleObjectsPerPeer || !_objects.TryGetValue(id, out var obj) ||
                        obj.RoomId != peer.Room || !InterestProvider.IsVisible(peer.Id, obj, false)) continue;
                    ulong view = checked(peer.NextView + 1);
                    if (!Enqueue(peer, SessionWire.Spawn(obj, view))) { peer.InterestCursor--; break; }
                    peer.NextView = view; peer.Views[id] = view;
                    peer.Visible.Add(id); peer.VisibleList.Add(id);
                }
                if (peer.InterestCursor == peer.InterestCandidates.Length) peer.InterestCandidates = null;
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
