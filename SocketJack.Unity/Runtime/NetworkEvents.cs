using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SocketJack.Multiplayer.Serialization;
using SocketJack.Multiplayer.Transport;

namespace SocketJack.Multiplayer
{
    public enum NetworkEventAudience { Observers = 0, Owner = 1 }
    public sealed class NetworkEventContext
    {
        public ulong ObjectId { get; internal set; }
        public ulong SenderId { get; internal set; }
        public bool IsServerCommand { get; internal set; }
    }
    /// <summary>Explicit local typed event registration. Commands are permission-checked; events are transient,
    /// bounded, duplicate-filtered and never included in late-join baselines. Pump Update after session Update.</summary>
    public sealed class NetworkEventHub : IDisposable
    {
        public const ushort CommandMessageId = 60005, EventMessageId = 60006;
        private sealed class SequenceWindow
        {
            private uint newest; private ulong bits;
            internal bool Accept(uint value)
            {
                if (bits == 0) { newest = value; bits = 1; return true; }
                int delta = unchecked((int)(value - newest));
                if (delta > 0) { bits = delta >= 64 ? 1 : (bits << delta) | 1; newest = value; return true; }
                if (delta <= -64) return false;
                ulong mask = 1UL << -delta; if ((bits & mask) != 0) return false; bits |= mask; return true;
            }
        }
        private sealed class Registration
        {
            internal Type Type;
            internal INetworkSerializer Codec;
            internal Action<NetworkEventContext, object> Handler;
            internal Func<ulong, object, bool> Validator;
            internal bool RequireOwner, Echo;
            internal int MaxBytes;
            internal uint Epoch;
            internal uint NextSend;
            internal readonly Dictionary<ulong, SequenceWindow> Windows = new Dictionary<ulong, SequenceWindow>();
        }
        private sealed class Pending
        {
            internal ulong Object, View; internal uint Epoch; internal int Room; internal byte[] Bytes;
        }
        private NetworkServer server;
        private NetworkClient client;
        private readonly Dictionary<(ulong, ushort), Registration> registrations = new Dictionary<(ulong, ushort), Registration>();
        private readonly Dictionary<ulong, Queue<Pending>> pending = new Dictionary<ulong, Queue<Pending>>();
        private int dedupeEntries;
        private bool disposed;
        public int MaxRegistrations { get; } = 65536;
        public int MaxPendingPerPeer { get; } = 128;
        public int MaxDedupeEntries { get; } = 65536;
        public NetworkEventHub(NetworkServer server = null, NetworkClient client = null) { AttachServer(server); AttachClient(client); }
        public void AttachServer(NetworkServer value)
        {
            if (server == value) return;
            if (server != null) { server.MessageReceived -= OnCommand; server.PeerDisconnected -= OnDisconnected; }
            server = value;
            if (server != null) { server.MessageReceived += OnCommand; server.PeerDisconnected += OnDisconnected; }
        }
        public void AttachClient(NetworkClient value)
        {
            if (client == value) return;
            if (client != null) client.MessageReceived -= OnEvent;
            client = value; if (client != null) client.MessageReceived += OnEvent;
        }
        public void Register<T>(ulong objectId, ushort eventId, INetworkSerializer codec, Action<NetworkEventContext, T> handler,
            bool requireOwnership = true, Func<ulong, T, bool> validator = null, int maxBytes = 256, bool echoCommands = false)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            Register(objectId, eventId, typeof(T), codec, (context, value) => handler(context, (T)value), requireOwnership,
                validator == null ? (Func<ulong, object, bool>)null : (peer, value) => validator(peer, (T)value), maxBytes, echoCommands);
        }
        public void Register(ulong objectId, ushort eventId, Type type, INetworkSerializer codec, Action<NetworkEventContext, object> handler,
            bool requireOwnership = true, Func<ulong, object, bool> validator = null, int maxBytes = 256, bool echoCommands = false)
        {
            if (disposed) throw new ObjectDisposedException(nameof(NetworkEventHub));
            if (objectId == 0 || eventId == 0 || type == null || codec == null || handler == null || !codec.CanSerialize(type) || maxBytes < 1 || maxBytes > 8192)
                throw new ArgumentException("Invalid explicitly registered network event.");
            var key = (objectId, eventId);
            if (registrations.ContainsKey(key) || registrations.Count >= MaxRegistrations) throw new InvalidOperationException("Duplicate event or event capacity exceeded.");
            registrations.Add(key, new Registration { Type = type, Codec = codec, Handler = handler, RequireOwner = requireOwnership, Validator = validator, MaxBytes = maxBytes, Echo = echoCommands });
        }
        public void Unregister(ulong objectId, ushort eventId)
        { if (registrations.TryGetValue((objectId, eventId), out var registration)) { dedupeEntries -= registration.Windows.Count; registrations.Remove((objectId, eventId)); } }
        public bool SendCommand<T>(ulong objectId, ushort eventId, T value) => SendCommand(objectId, eventId, (object)value);
        public bool SendCommand(ulong objectId, ushort eventId, object value)
        {
            if (disposed || client == null || !client.IsConnected || !registrations.TryGetValue((objectId, eventId), out var registration) ||
                !client.Objects.TryGetValue(objectId, out var obj) || (registration.RequireOwner && obj.OwnerId != client.LocalPeerId)) return false;
            byte[] packet = Encode(obj, eventId, value, registration, obj.ViewId, unchecked(++registration.NextSend));
            return client.SendMessage(CommandMessageId, packet, DeliveryMode.ReliableUnordered);
        }
        public bool Emit<T>(ulong objectId, ushort eventId, T value, NetworkEventAudience audience = NetworkEventAudience.Observers)
            => Emit(objectId, eventId, (object)value, audience);
        public bool Emit(ulong objectId, ushort eventId, object value, NetworkEventAudience audience = NetworkEventAudience.Observers)
        {
            if (disposed || server == null || !server.IsRunning || !registrations.TryGetValue((objectId, eventId), out var registration) || !server.Objects.TryGetValue(objectId, out var obj)) return false;
            if (!Enum.IsDefined(typeof(NetworkEventAudience), audience)) throw new ArgumentOutOfRangeException(nameof(audience));
            uint eventSequence = unchecked(++registration.NextSend);
            foreach (ulong peer in server.Peers.ToArray())
            {
                if (!server.CanObserve(peer, objectId) || (audience == NetworkEventAudience.Owner && peer != obj.OwnerId)) continue;
                ulong view = server.GetViewId(peer, objectId);
                var packet = Encode(obj, eventId, value, registration, view, eventSequence);
                if (!pending.TryGetValue(peer, out var queue))
                {
                    if (server.SendMessage(peer, EventMessageId, packet, DeliveryMode.ReliableUnordered)) continue;
                    pending.Add(peer, queue = new Queue<Pending>());
                }
                if (queue.Count >= MaxPendingPerPeer || queue.Sum(item => item.Bytes.Length) + packet.Length > 65536) { server.Disconnect(peer, "Network event queue exceeded"); pending.Remove(peer); continue; }
                queue.Enqueue(new Pending { Object = objectId, View = view, Epoch = obj.AuthorityEpoch, Room = obj.RoomId, Bytes = packet });
            }
            return true;
        }
        private byte[] Encode(NetworkObject obj, ushort id, object value, Registration registration, ulong view, uint sequence)
        {
            byte[] bytes = registration.Codec.Serialize(value, registration.Type, registration.MaxBytes);
            if (bytes == null || bytes.Length > registration.MaxBytes) throw new InvalidOperationException("Event codec exceeded its bound.");
            using (var stream = new MemoryStream(29 + bytes.Length)) using (var writer = new BinaryWriter(stream))
            {
                writer.Write((byte)1); writer.Write(obj.Id); writer.Write(obj.AuthorityEpoch); writer.Write(view); writer.Write(id); writer.Write(sequence);
                writer.Write((ushort)bytes.Length); writer.Write(bytes); return stream.ToArray();
            }
        }
        private void OnCommand(NetworkMessage message) { if (message.MessageId == CommandMessageId) Receive(message, true); }
        private void OnEvent(NetworkMessage message) { if (message.MessageId == EventMessageId) Receive(message, false); }
        private void Receive(NetworkMessage message, bool command)
        {
            if (disposed) return;
            if (message.Channel != 3 || message.Delivery != DeliveryMode.ReliableUnordered || message.Payload.Length < 29 || message.Payload.Length > 8221) { Deny(message.PeerId, command); return; }
            ulong objectId, view; uint epoch, eventSequence; ushort eventId; byte[] bytes;
            try
            {
                using (var stream = new MemoryStream(message.Payload, false)) using (var reader = new BinaryReader(stream))
                {
                    if (reader.ReadByte() != 1) throw new InvalidDataException();
                    objectId = reader.ReadUInt64(); epoch = reader.ReadUInt32(); view = reader.ReadUInt64(); eventId = reader.ReadUInt16(); eventSequence = reader.ReadUInt32();
                    bytes = SessionWire.Bytes(reader, 8192); SessionWire.End(reader);
                }
            }
            catch (IOException) { Deny(message.PeerId, command); return; }
            NetworkObject obj;
            if (command)
            {
                if (server == null || !server.Objects.TryGetValue(objectId, out obj) || !server.CanObserve(message.PeerId, objectId)) return;
            }
            else if (client == null || !client.Objects.TryGetValue(objectId, out obj)) return;
            if (epoch != obj.AuthorityEpoch || view == 0 || (command ? server.GetViewId(message.PeerId, objectId) : obj.ViewId) != view) return;
            if (!registrations.TryGetValue((objectId, eventId), out var registration)) { Deny(message.PeerId, command); return; }
            if (bytes.Length > registration.MaxBytes || (command && registration.RequireOwner && obj.OwnerId != message.PeerId)) { Deny(message.PeerId, command); return; }
            object value;
            try { value = registration.Codec.Deserialize(bytes, registration.Type, registration.MaxBytes); }
            catch (Exception) { Deny(message.PeerId, command); return; }
            if (value == null ? registration.Type.IsValueType && Nullable.GetUnderlyingType(registration.Type) == null : !registration.Type.IsInstanceOfType(value))
            { Deny(message.PeerId, command); return; }
            if (registration.Epoch != epoch) { dedupeEntries -= registration.Windows.Count; registration.Windows.Clear(); registration.Epoch = epoch; }
            ulong sender = command ? message.PeerId : 0;
            if (!registration.Windows.TryGetValue(sender, out var window))
            {
                if (registration.Windows.Count >= 4096 || dedupeEntries >= MaxDedupeEntries) { Deny(sender, command); return; }
                registration.Windows.Add(sender, window = new SequenceWindow());
                dedupeEntries++;
            }
            if (!window.Accept(eventSequence)) return;
            if (command && registration.Validator != null && !registration.Validator(sender, value)) return;
            if (command && (!server.Objects.TryGetValue(objectId, out var current) || !ReferenceEquals(current, obj) || current.AuthorityEpoch != epoch || server.GetViewId(sender, objectId) != view)) return;
            registration.Handler(new NetworkEventContext { ObjectId = objectId, SenderId = sender, IsServerCommand = command }, value);
            if (command && registration.Echo) Emit(objectId, eventId, value);
        }
        public void Update()
        {
            if (disposed || server == null || !server.IsRunning) return;
            int budget = 256;
            foreach (ulong peer in pending.Keys.ToArray())
            {
                if (!pending.TryGetValue(peer, out var queue)) continue;
                int perPeer = 16;
                while (budget > 0 && perPeer-- > 0 && queue.Count > 0)
                {
                    budget--; var item = queue.Peek();
                    if (server.GetViewId(peer, item.Object) != item.View || !server.Objects.TryGetValue(item.Object, out var obj) || obj.AuthorityEpoch != item.Epoch || obj.RoomId != item.Room) { queue.Dequeue(); continue; }
                    if (!server.SendMessage(peer, EventMessageId, item.Bytes, DeliveryMode.ReliableUnordered)) break;
                    queue.Dequeue();
                }
                if (queue.Count == 0) pending.Remove(peer);
            }
        }
        private void Deny(ulong peer, bool command) { if (command) server?.Disconnect(peer, "Invalid network event or event authority"); else client?.Stop(); }
        private void OnDisconnected(ulong peer, string reason) { pending.Remove(peer); foreach (var registration in registrations.Values) if (registration.Windows.Remove(peer)) dedupeEntries--; }
        public void Dispose()
        {
            if (disposed) return; disposed = true; AttachServer(null); AttachClient(null); registrations.Clear(); pending.Clear();
        }
    }
}
