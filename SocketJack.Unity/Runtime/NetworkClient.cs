using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using SocketJack.Multiplayer.Transport;

namespace SocketJack.Multiplayer
{
    /// <summary>Client view of authoritative objects. All callbacks run on the thread calling Update.</summary>
    public sealed class NetworkClient : IDisposable
    {
        private readonly IGameTransport _transport;
        private readonly NetworkSessionOptions _options;
        private readonly Dictionary<ulong, NetworkObject> _objects = new Dictionary<ulong, NetworkObject>();
        private readonly ReadOnlyDictionary<ulong, NetworkObject> _objectView;
        private readonly Queue<TransportEvent> _beforeWelcome = new Queue<TransportEvent>();
        private int _beforeWelcomeBytes;
        private bool _disposed, _updating, _connectionActive;
        private uint _connectionGeneration;
        private uint _nextOwnerStateSequence;

        public bool IsRunning => _transport.IsRunning && !_transport.IsServer;
        public bool IsConnected { get; private set; }
        public ulong LocalPeerId { get; private set; }
        public uint ServerTick { get; private set; }
        public IReadOnlyDictionary<ulong, NetworkObject> Objects => _objectView;
        public IGameTransport Transport => _transport;
        public event Action Connected;
        public event Action<string> Disconnected;
        public event Action<NetworkObject> ObjectSpawned;
        public event Action<NetworkObject> ObjectChanged;
        public event Action<ulong> ObjectDespawned;
        public event Action<NetworkMessage> MessageReceived;

        public NetworkClient(IGameTransport transport, NetworkSessionOptions options = null)
        {
            _transport = ObjectFrameTransport.Wrap(transport ?? throw new ArgumentNullException(nameof(transport)));
            _options = (options ?? new NetworkSessionOptions()).Snapshot(_transport);
            _objectView = new ReadOnlyDictionary<ulong, NetworkObject>(_objects);
            _transport.EventReceived += OnTransportEvent;
        }

        public void Connect(IPEndPoint endpoint, byte[] connectionData = null)
        {
            ThrowIfDisposed();
            if (_transport.IsRunning) throw new InvalidOperationException("Transport is already running.");
            _objects.Clear(); LocalPeerId = 0; ServerTick = 0; IsConnected = false; _nextOwnerStateSequence = 0;
            _connectionActive = true; _connectionGeneration = unchecked(_connectionGeneration + 1);
            try { _transport.StartClient(endpoint, connectionData); }
            catch { _connectionActive = false; throw; }
        }

        public void Update(double deltaSeconds, int maxEvents = 256)
        {
            ThrowIfDisposed(); SessionWire.ValidateDelta(deltaSeconds);
            if (_updating) throw new InvalidOperationException("Update cannot be called recursively.");
            if (!IsRunning) return;
            _updating = true;
            try { _transport.Poll(maxEvents); }
            finally { _updating = false; }
        }

        /// <summary>Sends player intent. Server input handlers must still validate game rules, movement speed and cooldowns.</summary>
        public bool SendInput(ulong objectId, byte[] payload, DeliveryMode delivery = DeliveryMode.ReliableUnordered)
        {
            ThrowIfDisposed();
            var bytes = SessionWire.Copy(payload, _options.MaxMessageBytes, nameof(payload));
            if (!IsConnected || !_objects.TryGetValue(objectId, out var obj) || obj.OwnerId != LocalPeerId) return false;
            var packet = SessionWire.Write(SessionWire.Kind.Input, w => { w.Write(objectId); SessionWire.Bytes(w, bytes); });
            return _transport.Send(0, new ArraySegment<byte>(packet), delivery, 2);
        }

        /// <summary>Sends canonical state only for an owned object whose authority was explicitly
        /// assigned to its owner by the server. Dedicated-server objects reject this operation.</summary>
        public bool SendOwnerState(ulong objectId, byte[] state, DeliveryMode delivery = DeliveryMode.ReliableUnordered)
        {
            ThrowIfDisposed();
            var bytes = SessionWire.Copy(state, _options.MaxStateBytes, nameof(state));
            if (!IsConnected || !_objects.TryGetValue(objectId, out var obj) || obj.OwnerId != LocalPeerId || obj.ServerAuthority) return false;
            uint sequence = unchecked(_nextOwnerStateSequence + 1);
            var packet = SessionWire.Write(SessionWire.Kind.OwnerState, w => { w.Write(objectId); w.Write(sequence); SessionWire.Bytes(w, bytes); });
            if (!_transport.Send(0, new ArraySegment<byte>(packet), delivery, 2)) return false;
            _nextOwnerStateSequence = sequence;
            return true;
        }

        /// <summary>Returns false on backpressure. Retry reliable application messages after a later Update.</summary>
        public bool SendMessage(ushort messageId, byte[] payload, DeliveryMode delivery = DeliveryMode.ReliableUnordered, byte channel = 3)
        {
            ThrowIfDisposed();
            if (channel < 3) throw new ArgumentOutOfRangeException(nameof(channel), "Channels 0, 1 and 2 are reserved for session traffic.");
            var bytes = SessionWire.Copy(payload, _options.MaxMessageBytes, nameof(payload));
            if (!IsConnected) return false;
            var packet = SessionWire.Write(SessionWire.Kind.Message, w => { w.Write(messageId); SessionWire.Bytes(w, bytes); });
            return _transport.Send(0, new ArraySegment<byte>(packet), delivery, channel);
        }

        public void Stop()
        {
            if (_disposed) return;
            EndConnection("Stopped");
        }

        public void Dispose()
        {
            if (_disposed) return;
            Stop(); _transport.EventReceived -= OnTransportEvent; _transport.Dispose(); _disposed = true;
        }

        private void OnTransportEvent(TransportEvent ev)
        {
            if (ev.Type == TransportEventType.Connected) return; // session Welcome completes connection
            if (ev.Type == TransportEventType.Disconnected)
            { EndConnection(ev.Reason); return; }
            if (ev.PeerId != 0) { InvalidPacket(); return; }
            Action callback = null;
            try
            {
                using (var reader = SessionWire.Read(ev.Payload, _transport.MaxPayloadSize, out var kind))
                {
                    if (kind != SessionWire.Kind.State && kind != SessionWire.Kind.Message &&
                        (ev.Delivery != DeliveryMode.ReliableOrdered || ev.Channel != 0)) throw new InvalidDataException();
                    switch (kind)
                    {
                        case SessionWire.Kind.Welcome:
                            if (IsConnected) throw new InvalidDataException();
                            ulong id = reader.ReadUInt64(); uint tick = reader.ReadUInt32(); SessionWire.End(reader);
                            if (id == 0 || id != _transport.LocalPeerId) throw new InvalidDataException();
                            LocalPeerId = id; ServerTick = tick; IsConnected = true;
                            callback = () =>
                            {
                                Connected?.Invoke();
                                while (IsConnected && _beforeWelcome.Count > 0)
                                {
                                    var pending = _beforeWelcome.Dequeue(); _beforeWelcomeBytes -= pending.Payload.Length;
                                    OnTransportEvent(pending);
                                }
                            };
                            break;
                        case SessionWire.Kind.Spawn:
                            RequireConnected(); callback = ReadSpawn(reader); break;
                        case SessionWire.Kind.Despawn:
                            RequireConnected(); ulong removed = reader.ReadUInt64(); SessionWire.End(reader);
                            if (_objects.Remove(removed)) callback = () => ObjectDespawned?.Invoke(removed);
                            break;
                        case SessionWire.Kind.ClearView:
                            RequireConnected(); SessionWire.End(reader);
                            var removedIds = new List<ulong>(_objects.Keys); _objects.Clear();
                            callback = () => { foreach (ulong removedId in removedIds) ObjectDespawned?.Invoke(removedId); };
                            break;
                        case SessionWire.Kind.State:
                            if (ev.Channel != 1 || ev.Delivery != DeliveryMode.UnreliableSequenced) throw new InvalidDataException();
                            ulong objectId = reader.ReadUInt64(); uint revision = reader.ReadUInt32(); uint stateTick = reader.ReadUInt32();
                            var state = SessionWire.Bytes(reader, _options.MaxStateBytes); SessionWire.End(reader);
                            if (!IsConnected) break; // state may overtake reliable Welcome/spawn across channels
                            AdvanceTick(stateTick);
                            if (_objects.TryGetValue(objectId, out var obj) && SessionWire.Newer(revision, obj.Revision))
                            { obj.StateData = state; obj.Revision = revision; obj.Tick = stateTick; callback = () => ObjectChanged?.Invoke(obj); }
                            break;
                        case SessionWire.Kind.Message:
                            if (ev.Channel < 3) throw new InvalidDataException();
                            ushort messageId = reader.ReadUInt16(); var message = SessionWire.Bytes(reader, _options.MaxMessageBytes); SessionWire.End(reader);
                            if (!IsConnected)
                            {
                                if (_beforeWelcome.Count >= _options.MaxQueuedMessagesPerPeer || ev.Payload.Length > _options.MaxQueuedBytesPerPeer - _beforeWelcomeBytes)
                                    throw new InvalidDataException("Pre-Welcome queue overflow.");
                                _beforeWelcome.Enqueue(ev); _beforeWelcomeBytes += ev.Payload.Length; break;
                            }
                            callback = () => MessageReceived?.Invoke(new NetworkMessage { PeerId = 0, MessageId = messageId, Payload = message, Delivery = ev.Delivery, Channel = ev.Channel });
                            break;
                        default: throw new InvalidDataException();
                    }
                }
            }
            catch (Exception error) when (error is InvalidDataException || error is EndOfStreamException)
            { InvalidPacket(); return; }
            callback?.Invoke();
        }

        private Action ReadSpawn(BinaryReader reader)
        {
            var obj = new NetworkObject { Id = reader.ReadUInt64(), PrefabId = reader.ReadUInt32(), OwnerId = reader.ReadUInt64(),
                RoomId = reader.ReadInt32(), Revision = reader.ReadUInt32(), Tick = reader.ReadUInt32(), ServerAuthority = reader.ReadBoolean(), StateData = SessionWire.Bytes(reader, _options.MaxStateBytes) };
            SessionWire.End(reader);
            if (obj.Id == 0) throw new InvalidDataException();
            AdvanceTick(obj.Tick);
            if (_objects.TryGetValue(obj.Id, out var previous))
            {
                if (previous.PrefabId != obj.PrefabId || previous.OwnerId != obj.OwnerId || previous.RoomId != obj.RoomId || previous.ServerAuthority != obj.ServerAuthority) throw new InvalidDataException();
                if (SessionWire.Newer(obj.Revision, previous.Revision))
                { previous.StateData = obj.StateData; previous.Revision = obj.Revision; previous.Tick = obj.Tick; return () => ObjectChanged?.Invoke(previous); }
                return null;
            }
            if (_objects.Count >= _options.MaxObjects) throw new InvalidDataException("Client object capacity exceeded.");
            _objects.Add(obj.Id, obj); return () => ObjectSpawned?.Invoke(obj);
        }

        private void AdvanceTick(uint tick) { if (SessionWire.Newer(tick, ServerTick)) ServerTick = tick; }
        private void RequireConnected() { if (!IsConnected) throw new InvalidDataException("Missing Welcome."); }
        private void InvalidPacket() { EndConnection("Invalid session packet"); }
        private void EndConnection(string reason)
        {
            uint generation = _connectionGeneration;
            bool notify = _connectionActive; _connectionActive = false;
            _transport.Stop(); ClearView();
            if (notify && generation == _connectionGeneration) Disconnected?.Invoke(reason);
        }
        private void ClearView()
        {
            IsConnected = false; LocalPeerId = 0; _beforeWelcome.Clear(); _beforeWelcomeBytes = 0;
            var ids = new List<ulong>(_objects.Keys); _objects.Clear();
            foreach (ulong id in ids) ObjectDespawned?.Invoke(id);
        }
        private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(NetworkClient)); }
    }
}
