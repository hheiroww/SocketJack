using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SocketJack.Multiplayer.Transport;

namespace SocketJack.Multiplayer
{
    /// <summary>Reserved member messages use application channel 3 and reliable unordered delivery.
    /// Per-member revisions and monotonic view tokens reject stale independent updates.</summary>
    public static class NetworkMemberProtocol
    {
        public const ushort ClientMessageId = 60003;
        public const ushort ServerMessageId = 60004;
    }

    internal sealed class MemberPacket
    {
        internal byte Kind;
        internal ulong ObjectId, View, Schema;
        internal int Room;
        internal ushort Member, Count;
        internal uint Sequence;
        internal uint Epoch;
        internal byte[] Value = Array.Empty<byte>();
        internal byte[] Encode()
        {
            using (var stream = new MemoryStream(44 + Value.Length))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((byte)2); writer.Write(Kind); writer.Write(ObjectId); writer.Write(Room); writer.Write(Epoch);
                writer.Write(View); writer.Write(Schema); writer.Write(Member); writer.Write(Count);
                writer.Write(Sequence); writer.Write((ushort)Value.Length); writer.Write(Value); return stream.ToArray();
            }
        }
        internal static MemberPacket Decode(NetworkMessage message)
        {
            if (message.Channel != 3 || message.Delivery != DeliveryMode.ReliableUnordered || message.Payload == null ||
                message.Payload.Length < 44 || message.Payload.Length > 44 + NetworkMemberSyncOptions.AbsoluteMaxMemberBytes)
                throw new InvalidDataException("Invalid member sync envelope.");
            using (var stream = new MemoryStream(message.Payload, false))
            using (var reader = new BinaryReader(stream))
            {
                if (reader.ReadByte() != 2) throw new InvalidDataException("Unsupported member sync protocol.");
                var packet = new MemberPacket { Kind = reader.ReadByte(), ObjectId = reader.ReadUInt64(), Room = reader.ReadInt32(),
                    Epoch = reader.ReadUInt32(),
                    View = reader.ReadUInt64(), Schema = reader.ReadUInt64(), Member = reader.ReadUInt16(),
                    Count = reader.ReadUInt16(), Sequence = reader.ReadUInt32() };
                int size = reader.ReadUInt16();
                if (packet.ObjectId == 0 || packet.Epoch == 0 || packet.View == 0 || size > NetworkMemberSyncOptions.AbsoluteMaxMemberBytes || size != stream.Length - stream.Position)
                    throw new InvalidDataException("Invalid member sync bounds.");
                packet.Value = reader.ReadBytes(size); return packet;
            }
        }
    }

    /// <summary>Server canonical member cache and room-scoped subscriptions. Pump after NetworkServer.Update.
    /// Latest unsent state is coalesced and retried when transport capacity returns.</summary>
    public sealed class NetworkMemberServer : IDisposable
    {
        private sealed class MemberState
        {
            internal byte[] Value;
            internal uint Revision = 1, OwnerSequence;
            internal bool HasOwnerSequence;
            internal double NextCapture;
        }
        private sealed class ObjectState
        {
            internal ulong Id;
            internal NetworkMemberSchema Schema;
            internal MemberState[] Members;
            internal int ScheduleIndex, CaptureCursor;
        }
        private sealed class Subscription
        {
            internal ulong Peer, Object, View;
            internal int Room, Cursor;
            internal uint[] Sent;
            internal int ScheduleIndex;
            internal uint Epoch;
        }
        private readonly NetworkServer server;
        private readonly NetworkMemberSyncOptions options;
        private readonly Dictionary<ulong, ObjectState> objects = new Dictionary<ulong, ObjectState>();
        private readonly Dictionary<ulong, Dictionary<ulong, Subscription>> subscriptions = new Dictionary<ulong, Dictionary<ulong, Subscription>>();
        private readonly List<ObjectState> captureSchedule = new List<ObjectState>();
        private readonly List<Subscription> sendSchedule = new List<Subscription>();
        private int canonicalBytes, sendCursor, captureCursor, memberCount, subscriptionMembers;
        private double now;
        private bool disposed;
        public event Action<ulong, Exception> BindingError;
        /// <summary>Optional semantic validation after bounded decoding and server-owned authority checks.</summary>
        public Func<NetworkObject, ushort, object, bool> OwnerValueValidator { get; set; }
        /// <summary>A host can skip setting its own owner's shared scene instance while still caching canonical bytes.</summary>
        public Func<NetworkObject, bool> ApplyOwnerValue { get; set; }

        public NetworkMemberServer(NetworkServer server, NetworkMemberSyncOptions options = null)
        {
            this.server = server ?? throw new ArgumentNullException(nameof(server)); this.options = (options ?? new NetworkMemberSyncOptions()).Snapshot();
            server.MessageReceived += OnMessage; server.PeerDisconnected += OnDisconnected; server.OwnershipChanged += OnOwnershipChanged;
        }
        public void RegisterObject(ulong objectId, NetworkMemberSchema schema)
        {
            if (disposed) throw new ObjectDisposedException(nameof(NetworkMemberServer));
            if (!server.Objects.ContainsKey(objectId)) throw new ArgumentException("Register after the server spawns this object.");
            if (objects.ContainsKey(objectId) || objects.Count >= options.MaxObjects) throw new InvalidOperationException("Member object capacity or duplicate registration.");
            if (schema == null) throw new ArgumentNullException(nameof(schema));
            var state = new ObjectState { Id = objectId, Schema = schema, Members = new MemberState[schema.Members.Count] };
            int bytes = 0;
            for (int i = 0; i < state.Members.Length; i++) { var value = schema.Capture(i); bytes = checked(bytes + value.Length); state.Members[i] = new MemberState { Value = value, NextCapture = now + schema.Members[i].SendInterval }; }
            if (bytes > options.MaxCanonicalBytes - canonicalBytes) throw new InvalidOperationException("Member canonical byte budget exceeded.");
            canonicalBytes += bytes; objects.Add(objectId, state); state.ScheduleIndex = captureSchedule.Count;
            captureSchedule.Add(state); memberCount += state.Members.Length;
        }
        public void RemoveObject(ulong objectId)
        {
            if (!objects.TryGetValue(objectId, out var state)) return;
            foreach (var member in state.Members) canonicalBytes -= member.Value.Length;
            memberCount -= state.Members.Length; objects.Remove(objectId);
            var last = captureSchedule[captureSchedule.Count - 1]; captureSchedule[state.ScheduleIndex] = last; last.ScheduleIndex = state.ScheduleIndex;
            captureSchedule.RemoveAt(captureSchedule.Count - 1);
            // Subscriptions are lazily revoked within the bounded send scan; no all-peer sweep on despawn.
        }
        public void Update(double deltaSeconds)
        {
            SessionWire.ValidateDelta(deltaSeconds); if (disposed) return; now += deltaSeconds;
            int captures = Math.Min(options.MaxCaptureChecksPerUpdate, memberCount);
            while (captures-- > 0 && captureSchedule.Count > 0)
            {
                if (captureCursor >= captureSchedule.Count) captureCursor = 0;
                var state = captureSchedule[captureCursor++];
                if (!server.Objects.TryGetValue(state.Id, out var obj)) { RemoveObject(state.Id); continue; }
                int i = state.CaptureCursor; state.CaptureCursor = (i + 1) % state.Members.Length;
                var binding = state.Schema.Members[i]; var member = state.Members[i];
                if ((binding.Authority == NetworkMemberAuthority.Owner && obj.OwnerId != 0) || now < member.NextCapture) continue;
                member.NextCapture = now + binding.SendInterval;
                try
                {
                    var bytes = state.Schema.Capture(i);
                    if (disposed || !server.IsRunning || !objects.TryGetValue(state.Id, out var current) || !ReferenceEquals(current, state) ||
                        !server.Objects.TryGetValue(state.Id, out var currentObject) || !ReferenceEquals(currentObject, obj)) continue;
                    SetCanonical(state, i, bytes, !binding.OnlyWhenChanged);
                }
                catch (Exception error) { BindingError?.Invoke(state.Id, error); RemoveObject(state.Id); }
            }
            int budget = options.MaxSendsPerUpdate, visits = 0;
            int maxVisits = Math.Min(options.MaxScansPerUpdate, subscriptionMembers);
            while (budget > 0 && sendSchedule.Count > 0 && visits++ < maxVisits)
            {
                if (sendCursor >= sendSchedule.Count) sendCursor = 0;
                var subscription = sendSchedule[sendCursor++];
                if (!objects.TryGetValue(subscription.Object, out var state) || !server.Objects.TryGetValue(subscription.Object, out var obj) ||
                    !server.CanObserve(subscription.Peer, subscription.Object) || obj.AuthorityEpoch != subscription.Epoch || obj.RoomId != subscription.Room || server.GetPeerRoom(subscription.Peer) != subscription.Room)
                { RemoveSubscription(subscription); continue; }
                int index = subscription.Cursor; subscription.Cursor = (index + 1) % state.Members.Length;
                var member = state.Members[index];
                if (subscription.Sent[index] == member.Revision) continue;
                budget--;
                var packet = new MemberPacket { Kind = 3, ObjectId = state.Id, Room = subscription.Room, Epoch = obj.AuthorityEpoch, View = subscription.View,
                    Schema = state.Schema.Fingerprint, Member = state.Schema.Members[index].Id, Count = (ushort)state.Members.Length,
                    Sequence = member.Revision, Value = member.Value };
                if (server.SendMessage(subscription.Peer, NetworkMemberProtocol.ServerMessageId, packet.Encode(), DeliveryMode.ReliableUnordered))
                    subscription.Sent[index] = member.Revision;
            }
        }
        private void SetCanonical(ObjectState state, int index, byte[] value, bool force = false)
        {
            var member = state.Members[index]; if (!force && member.Value.SequenceEqual(value)) return;
            if (value.Length - member.Value.Length > options.MaxCanonicalBytes - canonicalBytes) throw new InvalidOperationException("Member canonical byte budget exceeded.");
            canonicalBytes += value.Length - member.Value.Length; member.Value = value;
            member.Revision = unchecked(member.Revision + 1); if (member.Revision == 0) member.Revision = 1;
        }
        private void OnMessage(NetworkMessage message)
        {
            if (disposed || message.MessageId != NetworkMemberProtocol.ClientMessageId) return;
            if (!server.IsRunning || !server.Peers.Contains(message.PeerId)) return;
            MemberPacket packet;
            try { packet = MemberPacket.Decode(message); }
            catch (Exception error) when (error is InvalidDataException || error is EndOfStreamException) { server.Disconnect(message.PeerId, "Invalid member sync packet"); return; }
            if (!server.Objects.TryGetValue(packet.ObjectId, out var obj) ||
                !server.CanObserve(message.PeerId, packet.ObjectId) || obj.AuthorityEpoch != packet.Epoch || obj.RoomId != packet.Room || server.GetPeerRoom(message.PeerId) != packet.Room)
            { return; } // Despawn/room changes can revoke an otherwise valid in-flight request/proposal.
            if (!objects.TryGetValue(packet.ObjectId, out var state))
            { server.Disconnect(message.PeerId, "Member schema missing on visible server object"); return; }
            if (packet.Schema != state.Schema.Fingerprint) { server.Disconnect(message.PeerId, "Member schema or serializer mismatch"); return; }
            if (packet.Kind == 1)
            {
                if (packet.Member != 0 || packet.Count != 0 || packet.Sequence != 0 || packet.Value.Length != 0)
                { server.Disconnect(message.PeerId, "Invalid member baseline request"); return; }
                if (!subscriptions.TryGetValue(message.PeerId, out var byObject)) subscriptions.Add(message.PeerId, byObject = new Dictionary<ulong, Subscription>());
                if (!byObject.ContainsKey(packet.ObjectId) && (byObject.Count >= options.MaxSubscriptionsPerPeer || sendSchedule.Count >= options.MaxSubscriptions))
                { server.Disconnect(message.PeerId, "Member subscription capacity exceeded"); return; }
                if (byObject.TryGetValue(packet.ObjectId, out var existing))
                {
                    if (existing.View >= packet.View) return;
                    RemoveSubscription(existing);
                    if (!subscriptions.ContainsKey(message.PeerId)) subscriptions.Add(message.PeerId, byObject);
                }
                if (obj.OwnerId == message.PeerId) foreach (var ownerMember in state.Members) ownerMember.HasOwnerSequence = false;
                var added = new Subscription { Peer = message.PeerId, Object = packet.ObjectId, View = packet.View,
                    Room = packet.Room, Epoch = packet.Epoch, Sent = new uint[state.Members.Length], ScheduleIndex = sendSchedule.Count };
                byObject[packet.ObjectId] = added; sendSchedule.Add(added); subscriptionMembers += added.Sent.Length;
                return;
            }
            if (packet.Kind != 2 || packet.Count != 0 || packet.Sequence == 0 || !state.Schema.TryIndex(packet.Member, out int index) ||
                state.Schema.Members[index].Authority != NetworkMemberAuthority.Owner || obj.OwnerId != message.PeerId)
            { server.Disconnect(message.PeerId, "Member authority denied"); return; }
            if (!subscriptions.TryGetValue(message.PeerId, out var peers) || !peers.TryGetValue(packet.ObjectId, out var subscription) || subscription.View != packet.View)
                return; // A previous view was revoked; its late proposals do not affect the current view.
            var member = state.Members[index];
            if (member.HasOwnerSequence && !SessionWire.Newer(packet.Sequence, member.OwnerSequence)) return;
            if (packet.Value.Length - member.Value.Length > options.MaxCanonicalBytes - canonicalBytes)
            { server.Disconnect(message.PeerId, "Member canonical byte budget exceeded"); return; }
            object value;
            try { value = state.Schema.Decode(index, packet.Value); }
            catch (Exception) { server.Disconnect(message.PeerId, "Invalid serialized member value"); return; }
            if (!StillAuthorized(message.PeerId, packet, state, obj, subscription)) return;
            try
            {
                if (OwnerValueValidator != null && !OwnerValueValidator(obj, packet.Member, value))
                { if (server.Peers.Contains(message.PeerId)) server.Disconnect(message.PeerId, "Member game validation denied"); return; }
                if (!StillAuthorized(message.PeerId, packet, state, obj, subscription)) return;
                bool apply = ApplyOwnerValue == null || ApplyOwnerValue(obj);
                if (!StillAuthorized(message.PeerId, packet, state, obj, subscription)) return;
                if (apply) state.Schema.Members[index].SetValue(value);
                if (!StillAuthorized(message.PeerId, packet, state, obj, subscription)) return;
                SetCanonical(state, index, (byte[])packet.Value.Clone());
                member.HasOwnerSequence = true; member.OwnerSequence = packet.Sequence;
            }
            catch (Exception error) { BindingError?.Invoke(state.Id, error); if (server.Peers.Contains(message.PeerId)) server.Disconnect(message.PeerId, "Member application failed"); }
        }
        private bool StillAuthorized(ulong peer, MemberPacket packet, ObjectState state, NetworkObject obj, Subscription subscription)
            => !disposed && server.IsRunning && server.CanObserve(peer, packet.ObjectId) && server.GetPeerRoom(peer) == packet.Room &&
                objects.TryGetValue(packet.ObjectId, out var current) && ReferenceEquals(current, state) &&
                server.Objects.TryGetValue(packet.ObjectId, out var currentObject) && ReferenceEquals(currentObject, obj) && currentObject.AuthorityEpoch == packet.Epoch && currentObject.RoomId == packet.Room && currentObject.OwnerId == peer &&
                subscriptions.TryGetValue(peer, out var values) && values.TryGetValue(packet.ObjectId, out var active) && ReferenceEquals(active, subscription) && active.View == packet.View;
        private void RemoveSubscription(Subscription value)
        {
            if (!subscriptions.TryGetValue(value.Peer, out var values) || !values.Remove(value.Object)) return;
            subscriptionMembers -= value.Sent.Length;
            if (values.Count == 0) subscriptions.Remove(value.Peer);
            var last = sendSchedule[sendSchedule.Count - 1]; sendSchedule[value.ScheduleIndex] = last; last.ScheduleIndex = value.ScheduleIndex;
            sendSchedule.RemoveAt(sendSchedule.Count - 1);
        }
        private void OnDisconnected(ulong peer, string reason)
        { if (subscriptions.TryGetValue(peer, out var values)) foreach (var value in values.Values.ToArray()) RemoveSubscription(value); }
        private void OnOwnershipChanged(NetworkObject obj, ulong previousOwner)
        {
            if (objects.TryGetValue(obj.Id, out var state)) foreach (var member in state.Members) member.HasOwnerSequence = false;
        }
        public void Dispose()
        { if (disposed) return; disposed = true; server.MessageReceived -= OnMessage; server.PeerDisconnected -= OnDisconnected; server.OwnershipChanged -= OnOwnershipChanged; objects.Clear(); subscriptions.Clear(); captureSchedule.Clear(); sendSchedule.Clear(); canonicalBytes = memberCount = subscriptionMembers = 0; }
    }

    /// <summary>Client view-bound member baselines and owner proposals. Register only after object spawn.
    /// All requests/proposals share a token bucket, reserving session capacity for ordinary gameplay input.</summary>
    public sealed class NetworkMemberClient : IDisposable
    {
        private sealed class MemberState
        {
            internal byte[] LastSent;
            internal uint Revision, Proposal;
            internal bool Received;
            internal double NextCapture;
        }
        private sealed class ObjectState
        {
            internal ulong Id, View;
            internal NetworkMemberSchema Schema;
            internal MemberState[] Members;
            internal bool Requested;
            internal int Received, Cursor, ScheduleIndex;
        }
        private readonly NetworkClient client;
        private readonly NetworkMemberSyncOptions options;
        private readonly Dictionary<ulong, ObjectState> objects = new Dictionary<ulong, ObjectState>();
        private readonly List<ObjectState> schedule = new List<ObjectState>();
        private ulong nextView;
        private double now, tokens;
        private int cursor, memberCount, retainedBytes;
        private bool disposed;
        public event Action<ulong, Exception> BindingError;
        public event Action<ulong> BaselineReady;
        public event Action<ulong, ushort, object> MemberChanged;
        /// <summary>A host consumes protocol bookkeeping without applying old echoes to its shared server scene.</summary>
        public bool ApplyValues { get; set; } = true;
        public NetworkMemberClient(NetworkClient client, NetworkMemberSyncOptions options = null)
        {
            this.client = client ?? throw new ArgumentNullException(nameof(client)); this.options = (options ?? new NetworkMemberSyncOptions()).Snapshot();
            tokens = this.options.ClientBurstMessages; client.MessageReceived += OnMessage; client.ObjectDespawned += RemoveObject; client.Disconnected += OnDisconnected; client.OwnershipChanged += OnOwnershipChanged;
        }
        public void RegisterObject(ulong objectId, NetworkMemberSchema schema)
        {
            if (disposed) throw new ObjectDisposedException(nameof(NetworkMemberClient));
            if (!client.Objects.ContainsKey(objectId)) throw new ArgumentException("Register after the client receives this object's spawn.");
            if (objects.ContainsKey(objectId) || objects.Count >= options.MaxObjects) throw new InvalidOperationException("Member object capacity or duplicate registration.");
            if (schema == null) throw new ArgumentNullException(nameof(schema));
            var members = new MemberState[schema.Members.Count]; for (int i = 0; i < members.Length; i++) members[i] = new MemberState();
            nextView++; if (nextView == 0) nextView++;
            var state = new ObjectState { Id = objectId, View = nextView, Schema = schema, Members = members, ScheduleIndex = schedule.Count };
            objects.Add(objectId, state); schedule.Add(state); memberCount += state.Members.Length;
        }
        public bool IsReady(ulong objectId) => objects.TryGetValue(objectId, out var state) && state.Received == state.Members.Length;
        public bool IsRegistered(ulong objectId) => objects.ContainsKey(objectId);
        public void RemoveObject(ulong objectId)
        {
            if (!objects.TryGetValue(objectId, out var state)) return;
            foreach (var member in state.Members) retainedBytes -= member.LastSent == null ? 0 : member.LastSent.Length;
            objects.Remove(objectId); memberCount -= state.Members.Length;
            var last = schedule[schedule.Count - 1]; schedule[state.ScheduleIndex] = last; last.ScheduleIndex = state.ScheduleIndex;
            schedule.RemoveAt(schedule.Count - 1);
        }
        public void Update(double deltaSeconds)
        {
            SessionWire.ValidateDelta(deltaSeconds); if (disposed || !client.IsConnected) return; now += deltaSeconds;
            tokens = Math.Min(options.ClientBurstMessages, tokens + deltaSeconds * options.MaxClientMessagesPerSecond);
            int budget = options.MaxSendsPerUpdate, visits = 0;
            int maxVisits = Math.Min(options.MaxScansPerUpdate, memberCount);
            while (budget > 0 && tokens >= 1 && schedule.Count > 0 && visits++ < maxVisits)
            {
                if (cursor >= schedule.Count) cursor = 0;
                var state = schedule[cursor++];
                if (!client.Objects.TryGetValue(state.Id, out var obj)) { RemoveObject(state.Id); continue; }
                if (!state.Requested)
                {
                    budget--;
                    var packet = new MemberPacket { Kind = 1, ObjectId = state.Id, Room = obj.RoomId, Epoch = obj.AuthorityEpoch, View = state.View, Schema = state.Schema.Fingerprint };
                    if (client.SendMessage(NetworkMemberProtocol.ClientMessageId, packet.Encode(), DeliveryMode.ReliableUnordered)) { state.Requested = true; tokens--; }
                    continue;
                }
                if (state.Received != state.Members.Length || obj.OwnerId != client.LocalPeerId) continue;
                int index = state.Cursor; state.Cursor = (index + 1) % state.Members.Length;
                var member = state.Members[index]; var binding = state.Schema.Members[index];
                if (binding.Authority != NetworkMemberAuthority.Owner || now < member.NextCapture) continue;
                byte[] bytes;
                try { bytes = state.Schema.Capture(index); }
                catch (Exception error) { BindingError?.Invoke(state.Id, error); RemoveObject(state.Id); continue; }
                if (!IsCurrent(state, obj)) continue;
                if (binding.OnlyWhenChanged && member.LastSent != null && member.LastSent.SequenceEqual(bytes)) { member.NextCapture = now + binding.SendInterval; continue; }
                uint proposal = unchecked(member.Proposal + 1); if (proposal == 0) proposal = 1;
                var update = new MemberPacket { Kind = 2, ObjectId = state.Id, Room = obj.RoomId, Epoch = obj.AuthorityEpoch, View = state.View,
                    Schema = state.Schema.Fingerprint, Member = binding.Id, Sequence = proposal, Value = bytes };
                if (!CanRetain(member, bytes)) { FailCapacity(state.Id); return; }
                budget--;
                if (client.SendMessage(NetworkMemberProtocol.ClientMessageId, update.Encode(), DeliveryMode.ReliableUnordered) && IsCurrent(state, obj))
                { tokens--; member.Proposal = proposal; Retain(member, bytes); member.NextCapture = now + binding.SendInterval; }
            }
        }
        private void OnMessage(NetworkMessage message)
        {
            if (disposed || message.MessageId != NetworkMemberProtocol.ServerMessageId) return;
            MemberPacket packet;
            try { packet = MemberPacket.Decode(message); }
            catch (Exception error) when (error is InvalidDataException || error is EndOfStreamException) { client.Stop(); return; }
            if (packet.Kind != 3 || packet.Sequence == 0) { client.Stop(); return; }
            // Application messages may precede lifecycle messages on another channel. A later registration requests a full baseline.
            if (!objects.TryGetValue(packet.ObjectId, out var state) || packet.View != state.View || !client.Objects.TryGetValue(packet.ObjectId, out var obj) || packet.Epoch != obj.AuthorityEpoch || packet.Room != obj.RoomId) return;
            if (packet.Schema != state.Schema.Fingerprint || packet.Count != state.Members.Length || !state.Schema.TryIndex(packet.Member, out int index))
            { BindingError?.Invoke(state.Id, new InvalidDataException("Member schema or serializer mismatch.")); client.Stop(); return; }
            var member = state.Members[index]; if (member.Received && !SessionWire.Newer(packet.Sequence, member.Revision)) return;
            object value;
            try { value = state.Schema.Decode(index, packet.Value); }
            catch (Exception error) { BindingError?.Invoke(state.Id, error); client.Stop(); return; }
            if (!IsCurrent(state, obj)) return;
            bool readyBefore = state.Received == state.Members.Length;
            try
            {
                bool ownerEcho = state.Received == state.Members.Length && obj.OwnerId == client.LocalPeerId && state.Schema.Members[index].Authority == NetworkMemberAuthority.Owner;
                bool retain = !member.Received && obj.OwnerId == client.LocalPeerId && state.Schema.Members[index].Authority == NetworkMemberAuthority.Owner;
                if (retain && !CanRetain(member, packet.Value)) { FailCapacity(state.Id); return; }
                if (ApplyValues && !ownerEcho) state.Schema.Members[index].SetValue(value);
                if (!IsCurrent(state, obj)) return;
                if (!member.Received) { member.Received = true; state.Received++; if (retain) Retain(member, (byte[])packet.Value.Clone()); }
                member.Revision = packet.Sequence;
            }
            catch (Exception error) { BindingError?.Invoke(state.Id, error); client.Stop(); }
            if (IsCurrent(state, obj))
            {
                MemberChanged?.Invoke(state.Id, packet.Member, value);
                if (!readyBefore && state.Received == state.Members.Length) BaselineReady?.Invoke(state.Id);
            }
        }
        private bool CanRetain(MemberState member, byte[] bytes) => bytes.Length - (member.LastSent == null ? 0 : member.LastSent.Length) <= options.MaxCanonicalBytes - retainedBytes;
        private bool IsCurrent(ObjectState state, NetworkObject obj)
            => !disposed && client.IsConnected && objects.TryGetValue(state.Id, out var current) && ReferenceEquals(current, state) &&
                client.Objects.TryGetValue(state.Id, out var currentObject) && ReferenceEquals(currentObject, obj);
        private void Retain(MemberState member, byte[] bytes) { retainedBytes += bytes.Length - (member.LastSent == null ? 0 : member.LastSent.Length); member.LastSent = bytes; }
        private void FailCapacity(ulong objectId) { BindingError?.Invoke(objectId, new InvalidOperationException("Client member byte budget exceeded.")); client.Stop(); }
        private void OnDisconnected(string reason) { objects.Clear(); schedule.Clear(); memberCount = retainedBytes = 0; tokens = options.ClientBurstMessages; }
        private void OnOwnershipChanged(NetworkObject obj, ulong previousOwner)
        {
            if (!objects.TryGetValue(obj.Id, out var state)) return;
            var schema = state.Schema; RemoveObject(obj.Id); RegisterObject(obj.Id, schema);
        }
        public void Dispose()
        { if (disposed) return; disposed = true; client.MessageReceived -= OnMessage; client.ObjectDespawned -= RemoveObject; client.Disconnected -= OnDisconnected; client.OwnershipChanged -= OnOwnershipChanged; objects.Clear(); schedule.Clear(); memberCount = retainedBytes = 0; }
    }
}
