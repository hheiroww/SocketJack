using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Multiplayer.Serialization;
using SocketJack.Multiplayer.Transport;

namespace SocketJack.Multiplayer.Tests
{
    [TestClass]
    public class MemberSyncTests
    {
        private sealed class Values
        {
            public int Health = 10;
            public string Cosmetic = "gray";
            public byte[] Blob = Array.Empty<byte>();
            public int[] Slots = new int[64];
        }
        private static NetworkMemberSchema Mixed(Values value) => Mixed(value, new SocketJackBinaryFormatter());
        private static NetworkMemberSchema Mixed(Values value, INetworkSerializer serializer) => new NetworkMemberSchema(serializer, new[]
        {
            new NetworkMemberBinding(1, "game.health", typeof(int), () => value.Health, v => value.Health = (int)v, NetworkMemberAuthority.Server, .016),
            new NetworkMemberBinding(2, "game.cosmetic", typeof(string), () => value.Cosmetic, v => value.Cosmetic = (string)v, NetworkMemberAuthority.Owner, .016)
        });

        [TestMethod]
        public void MixedMemberAuthorityIsIndependentOfPhysicsAndLateJoinGetsCurrentValues()
        {
            using var h = new Harness(Mixed); var owner = h.AddPeer(); var observer = h.AddPeer(); h.Pump();
            var obj = h.Spawn(owner.Game.LocalPeerId); h.Pump();
            Assert.IsTrue(obj.ServerAuthority); Assert.IsTrue(owner.Members.IsReady(obj.Id));
            h.Values[obj.Id].Health = 25; owner.Values[obj.Id].Health = 999; owner.Values[obj.Id].Cosmetic = "red"; h.Pump();
            Assert.AreEqual(25, owner.Values[obj.Id].Health); Assert.AreEqual(25, observer.Values[obj.Id].Health);
            Assert.AreEqual("red", h.Values[obj.Id].Cosmetic); Assert.AreEqual("red", observer.Values[obj.Id].Cosmetic);
            CollectionAssert.AreEqual(new byte[] { 42 }, observer.Game.Objects[obj.Id].State, "Member sync must not replace physics/transform payloads.");
            var late = h.AddPeer(); h.Pump(); Assert.IsTrue(late.Members.IsReady(obj.Id));
            Assert.AreEqual(25, late.Values[obj.Id].Health); Assert.AreEqual("red", late.Values[obj.Id].Cosmetic);
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void RejectsNonownerAndOwnerWritingServerMember(bool ownerAttacksServerField)
        {
            using var h = new Harness(Mixed); var owner = h.AddPeer(); var other = h.AddPeer(); h.Pump();
            var obj = h.Spawn(owner.Game.LocalPeerId); h.Pump(); var attacker = ownerAttacksServerField ? owner : other;
            ushort member = ownerAttacksServerField ? (ushort)1 : (ushort)2;
            var codec = new SocketJackBinaryFormatter(); byte[] bytes = ownerAttacksServerField ? codec.Serialize(900, typeof(int)) : codec.Serialize("forged", typeof(string));
            attacker.Game.SendMessage(NetworkMemberProtocol.ClientMessageId,
                Packet(2, obj.Id, 0, 1, Mixed(h.Values[obj.Id]).Fingerprint, member, 0, 1, bytes), DeliveryMode.ReliableUnordered);
            h.Pump(); Assert.IsFalse(h.Server.Peers.Contains(attacker.Game.LocalPeerId));
            Assert.AreEqual(10, h.Values[obj.Id].Health); Assert.AreEqual("gray", h.Values[obj.Id].Cosmetic);
        }

        [TestMethod]
        public void DelayedVisibilityTrafficAndRoomReentryDoNotDisconnectOrRetainOldOwnerSequence()
        {
            using var h = new Harness(Mixed); var owner = h.AddPeer(); h.Pump(); var obj = h.Spawn(owner.Game.LocalPeerId); h.Pump();
            for (int i = 0; i < 40; i++) { owner.Values[obj.Id].Cosmetic = "before" + i; h.Pump(2); }
            owner.Values[obj.Id].Cosmetic = "in-flight"; owner.Members.Update(.05); // proposal waits in the server's native event queue
            h.Server.SetPeerRoom(owner.Game.LocalPeerId, 9); h.Server.SetObjectRoom(obj.Id, 9); h.Pump();
            Assert.IsTrue(owner.Game.IsConnected); Assert.IsTrue(owner.Members.IsReady(obj.Id));
            owner.Values[obj.Id].Cosmetic = "first-in-new-view"; h.Pump();
            Assert.AreEqual("first-in-new-view", h.Values[obj.Id].Cosmetic, "New view proposal1 must not be blocked by the previous view's proposal sequence.");
            owner.Values[obj.Id].Cosmetic = "despawn-race"; owner.Members.Update(.05);
            h.Server.Despawn(obj.Id); h.ServerMembers.RemoveObject(obj.Id); h.Pump();
            Assert.IsTrue(owner.Game.IsConnected); Assert.IsFalse(owner.Game.Objects.ContainsKey(obj.Id));
        }

        [TestMethod]
        public void StateBeforeSpawnIsDiscardedAndReliableBackpressureEventuallySendsLatestValue()
        {
            using var h = new Harness(Mixed); var peer = h.AddPeer(); h.Pump(); var obj = h.Spawn();
            h.Server.SendMessage(peer.Game.LocalPeerId, NetworkMemberProtocol.ServerMessageId,
                Packet(3, obj.Id, 0, 1, Mixed(h.Values[obj.Id]).Fingerprint, 1, 2, 900,
                    new SocketJackBinaryFormatter().Serialize(999, typeof(int))), DeliveryMode.ReliableUnordered);
            peer.Transport.ReverseIncoming(); h.Pump(); Assert.AreEqual(10, peer.Values[obj.Id].Health);
            h.Hub.Server.BlockReliable = true;
            for (int i = 0; i < 30; i++) { h.Values[obj.Id].Health = i; h.Pump(1); }
            Assert.IsTrue(peer.Game.IsConnected); Assert.AreNotEqual(29, peer.Values[obj.Id].Health);
            h.Hub.Server.BlockReliable = false; h.Pump(40); Assert.AreEqual(29, peer.Values[obj.Id].Health);
        }

        [TestMethod]
        public void HostBookkeepingNeverRollsBackSharedServerSceneValue()
        {
            using var h = new Harness(Mixed); var host = h.AddPeer(); h.Pump(); var obj = h.Spawn(host.Game.LocalPeerId);
            h.Server.Update(.05); // flush the reliable spawn before registering its client member view
            host.Game.Update(.05); // receive spawn and register normal client schema
            host.Members.RemoveObject(obj.Id); host.Members.ApplyValues = false;
            host.Members.RegisterObject(obj.Id, Mixed(h.Values[obj.Id])); host.Members.Update(.05);
            h.Server.Update(.05); h.ServerMembers.Update(.05); // Health10 baseline is now queued for host
            h.Values[obj.Id].Health = 20; host.Game.Update(.05);
            Assert.AreEqual(20, h.Values[obj.Id].Health, "Old host baseline cannot write over the authoritative scene instance.");
            Assert.IsTrue(host.Members.IsReady(obj.Id));
        }

        [TestMethod]
        public void CanonicalBudgetFailureHappensBeforeOwnerSetterAndClientRetainedCacheIsBounded()
        {
            NetworkMemberSchema Large(Values value) => new NetworkMemberSchema(new SocketJackBinaryFormatter(), new[]
            {
                new NetworkMemberBinding(1, "blob", typeof(byte[]), () => value.Blob, v => value.Blob = (byte[])v, NetworkMemberAuthority.Owner, .016, true, 8192),
                new NetworkMemberBinding(2, "cosmetic", typeof(string), () => value.Cosmetic, v => value.Cosmetic = (string)v, NetworkMemberAuthority.Server, .016, true, 8192)
            });
            using var h = new Harness(Large, new NetworkMemberSyncOptions { MaxCanonicalBytes = 8192 });
            var owner = h.AddPeer(); h.Pump();
            var initial = new Values { Blob = new byte[4000], Cosmetic = new string('a', 4000) };
            var obj = h.Spawn(owner.Game.LocalPeerId, initial); h.Pump(); owner.Values[obj.Id].Blob = new byte[6000]; h.Pump();
            Assert.AreEqual(4000, initial.Blob.Length, "A rejected quota change must not invoke the owner setter.");
            Assert.IsFalse(owner.Game.IsConnected);

            using var bounded = new Harness(v => new NetworkMemberSchema(new SocketJackBinaryFormatter(), new[]
                { new NetworkMemberBinding(1, "blob", typeof(byte[]), () => v.Blob, x => v.Blob = (byte[])x, NetworkMemberAuthority.Owner, .016, true, 8192) }));
            var smallClient = bounded.AddPeer(new NetworkMemberSyncOptions { MaxCanonicalBytes = 8192 }); bounded.Pump();
            bounded.Spawn(smallClient.Game.LocalPeerId, new Values { Blob = new byte[5000] });
            bounded.Spawn(smallClient.Game.LocalPeerId, new Values { Blob = new byte[5000] }); bounded.Pump();
            Assert.IsFalse(smallClient.Game.IsConnected, "Locally owned comparison bytes must respect the aggregate client cap.");
        }

        [TestMethod]
        public void GetterAndValidatorRevocationCannotApplyOrRecacheStaleState()
        {
            using var h = new Harness(Mixed); var owner = h.AddPeer(); h.Pump(); var obj = h.Spawn(owner.Game.LocalPeerId); h.Pump();
            h.ServerMembers.OwnerValueValidator = (state, id, value) => { h.Server.Despawn(state.Id); h.ServerMembers.RemoveObject(state.Id); return true; };
            owner.Values[obj.Id].Cosmetic = "revoked"; h.Pump(); Assert.AreEqual("gray", h.Values[obj.Id].Cosmetic);
            Assert.IsTrue(owner.Game.IsConnected);
        }

        [TestMethod]
        public void VisibleObjectWithoutServerMemberSchemaRejectsMismatchedClient()
        {
            using var h = new Harness(Mixed); var peer = h.AddPeer(); h.Pump(); string reason = null;
            peer.Game.Disconnected += text => reason = text;
            h.Server.Spawn(1); // deliberately omit server member registration while client prefab expects it
            h.Pump(); Assert.IsFalse(peer.Game.IsConnected); StringAssert.Contains(reason, "Member schema");
        }

        [TestMethod]
        public void WrongCustomDecoderResultCannotReachValidatorOrSetter()
        {
            var original = new Values(); var codec = new HookedSerializer { WrongStringResult = true };
            using var h = new Harness(v => Mixed(v, ReferenceEquals(v, original) ? codec : new SocketJackBinaryFormatter()));
            var owner = h.AddPeer(); h.Pump(); var obj = h.Spawn(owner.Game.LocalPeerId, original); h.Pump();
            bool validatorCalled = false; h.ServerMembers.OwnerValueValidator = (_, __, ___) => { validatorCalled = true; return true; };
            owner.Values[obj.Id].Cosmetic = "bad-type"; h.Pump();
            Assert.IsFalse(validatorCalled); Assert.AreEqual("gray", original.Cosmetic); Assert.IsFalse(owner.Game.IsConnected);
        }

        [TestMethod]
        public void ClientDecoderRevocationCannotApplyOldSetterOrRestoreRemovedBookkeeping()
        {
            var original = new Values { Health = 35 }; var codec = new HookedSerializer();
            using var h = new Harness(v => Mixed(v, ReferenceEquals(v, original) ? new SocketJackBinaryFormatter() : codec));
            var peer = h.AddPeer(); h.Pump(); var obj = h.Spawn(0, original);
            h.Server.Update(.05); peer.Game.Update(.05); // client schema exists, but its baseline has not been requested yet
            codec.OnDecode = () => peer.Members.RemoveObject(obj.Id);
            peer.Members.Update(.05); h.Server.Update(.05); h.ServerMembers.Update(.05); peer.Game.Update(.05);
            Assert.AreEqual(10, peer.Values[obj.Id].Health); Assert.IsFalse(peer.Members.IsReady(obj.Id));
            Assert.IsTrue(peer.Game.IsConnected);
        }

        [TestMethod]
        public void SixtyFourChangedOwnerMembersStayWithinDefaultInboundRateAndRemainFair()
        {
            NetworkMemberSchema Slots(Values value) => new NetworkMemberSchema(new SocketJackBinaryFormatter(), Enumerable.Range(0, 64).Select(index =>
                new NetworkMemberBinding((ushort)(index + 1), "slot" + index, typeof(int), () => value.Slots[index], x => value.Slots[index] = (int)x,
                    NetworkMemberAuthority.Owner, .016)));
            using var h = new Harness(Slots); var owner = h.AddPeer(); h.Pump(); var obj = h.Spawn(owner.Game.LocalPeerId); h.Pump(40);
            Assert.IsTrue(owner.Members.IsReady(obj.Id));
            for (int frame = 1; frame <= 100; frame++)
            {
                for (int i = 0; i < 64; i++) owner.Values[obj.Id].Slots[i] = frame;
                Assert.IsTrue(owner.Game.SendInput(obj.Id, new byte[] { 1 })); h.Pump(1);
            }
            Assert.IsTrue(owner.Game.IsConnected); Assert.IsTrue(h.Values[obj.Id].Slots.All(x => x > 80), "Every selected member must make progress under the global traffic budget.");
        }

        private sealed class HookedSerializer : INetworkSerializer
        {
            private readonly SocketJackBinaryFormatter inner = new SocketJackBinaryFormatter();
            internal bool WrongStringResult; internal Action OnDecode;
            public string ProtocolId => inner.ProtocolId;
            public bool CanSerialize(Type type) => inner.CanSerialize(type);
            public byte[] Serialize(object value, Type type, int maxBytes = 8192) => inner.Serialize(value, type, maxBytes);
            public object Deserialize(byte[] bytes, Type type, int maxBytes = 8192)
            { var value = inner.Deserialize(bytes, type, maxBytes); OnDecode?.Invoke(); return WrongStringResult && type == typeof(string) ? (object)123 : value; }
        }

        [TestMethod]
        public void ReflectionSelectionExcludesUnsafeAccessorsAndSchemaIgnoresSchedulingPrecision()
        {
            var descriptors = NetworkMemberAccessors.GetEligibleMembers(typeof(AccessorFixture));
            Assert.IsTrue(descriptors.Any(x => x.Name == "PrivateProperty")); Assert.IsTrue(descriptors.Any(x => x.Name == "PrivateField"));
            foreach (string excluded in new[] { "StaticField", "ReadOnlyField", "ReadOnlyProperty", "Item", "InitOnly" })
                Assert.IsFalse(descriptors.Any(x => x.Name == excluded), excluded);
            Assert.IsFalse(descriptors.Any(x => x.Name.Contains("k__BackingField")));
            NetworkMemberSchema Schema(double interval, bool changed) => new NetworkMemberSchema(new SocketJackBinaryFormatter(), new[]
                { new NetworkMemberBinding(1, "value", typeof(int), () => 1, _ => { }, sendInterval: interval, onlyWhenChanged: changed) });
            Assert.AreEqual(Schema(.1, true).Fingerprint, Schema((double).1f, false).Fingerprint);
            Assert.IsFalse(NetworkMemberAccessors.GetTypeKey(typeof(Dictionary<string, int[]>)).Contains("Version="));
            Assert.ThrowsException<NotSupportedException>(() => new NetworkMemberSchema(new SocketJackBinaryFormatter(), new[]
                { new NetworkMemberBinding(1, "list", typeof(List<int>), () => new List<int>(), _ => { }) }));
        }

        private sealed class AccessorFixture
        {
            public static int StaticField = 0;
            public readonly int ReadOnlyField = 1;
            public int ReadOnlyProperty => 1;
            public int InitOnly { get; init; }
            private int PrivateField = 1;
            private int PrivateProperty { get; set; }
            public int this[int index] { get => index; set { } }
            public int ReadPrivate() => PrivateField + PrivateProperty;
        }
        private static byte[] Packet(byte kind, ulong obj, int room, ulong view, ulong schema, ushort member, ushort count, uint revision, byte[] value)
        {
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write((byte)2); writer.Write(kind); writer.Write(obj); writer.Write(room); writer.Write((uint)1); writer.Write(view); writer.Write(schema);
            writer.Write(member); writer.Write(count); writer.Write(revision); writer.Write((ushort)value.Length); writer.Write(value); return stream.ToArray();
        }
        private sealed class Peer : IDisposable
        {
            internal NetworkClient Game; internal NetworkMemberClient Members; internal MemoryTransport Transport;
            internal readonly Dictionary<ulong, Values> Values = new Dictionary<ulong, Values>();
            public void Dispose() { Members.Dispose(); Game.Dispose(); }
        }
        private sealed class Harness : IDisposable
        {
            internal readonly MemoryHub Hub = new MemoryHub();
            internal readonly NetworkServer Server; internal readonly NetworkMemberServer ServerMembers;
            internal readonly Dictionary<ulong, Values> Values = new Dictionary<ulong, Values>();
            private readonly List<Peer> peers = new List<Peer>(); private readonly Func<Values, NetworkMemberSchema> schema;
            internal Harness(Func<Values, NetworkMemberSchema> schema, NetworkMemberSyncOptions limits = null)
            {
                this.schema = schema;
                Server = new NetworkServer(new ObjectFrameTransport(Hub.Server, new ObjectFrameOptions { MaxQueuedMessagesPerPeer = 4 }),
                    new NetworkSessionOptions { MaxMessageBytes = 8232 });
                ServerMembers = new NetworkMemberServer(Server, limits); Server.Start(Hub.Endpoint);
            }
            internal Peer AddPeer(NetworkMemberSyncOptions limits = null)
            {
                var peer = new Peer { Transport = Hub.NewClient() };
                peer.Game = new NetworkClient(peer.Transport, new NetworkSessionOptions { MaxMessageBytes = 8232 });
                peer.Members = new NetworkMemberClient(peer.Game, limits);
                peer.Game.ObjectSpawned += obj => { var value = new Values(); peer.Values[obj.Id] = value; peer.Members.RegisterObject(obj.Id, schema(value)); };
                peer.Game.ObjectDespawned += id => peer.Values.Remove(id); peers.Add(peer); peer.Game.Connect(Hub.Endpoint); return peer;
            }
            internal NetworkObject Spawn(ulong owner = 0, Values value = null)
            { var obj = Server.Spawn(1, owner, 0, new byte[] { 42 }, true); Values[obj.Id] = value ?? new Values(); ServerMembers.RegisterObject(obj.Id, schema(Values[obj.Id])); return obj; }
            internal void Pump(int count = 20)
            { for (int i = 0; i < count; i++) { Server.Update(.05, 4096); ServerMembers.Update(.05); foreach (var peer in peers) { peer.Game.Update(.05, 4096); peer.Members.Update(.05); } } }
            public void Dispose() { foreach (var peer in peers) peer.Dispose(); ServerMembers.Dispose(); Server.Dispose(); }
        }
        private sealed class MemoryHub
        {
            internal readonly IPEndPoint Endpoint = new IPEndPoint(IPAddress.Loopback, 1);
            internal readonly Dictionary<ulong, MemoryTransport> Clients = new Dictionary<ulong, MemoryTransport>();
            internal readonly MemoryTransport Server; internal ulong Next;
            internal MemoryHub() { Server = new MemoryTransport(this); }
            internal MemoryTransport NewClient() => new MemoryTransport(this);
        }
        private sealed class MemoryTransport : IGameTransport
        {
            private readonly MemoryHub hub; internal readonly Queue<TransportEvent> Incoming = new Queue<TransportEvent>(); internal bool BlockReliable;
            public TransportType Type => TransportType.Custom; public bool IsServer { get; private set; } public bool IsRunning { get; private set; }
            public ulong LocalPeerId { get; private set; } public IPEndPoint LocalEndPoint => hub.Endpoint; public int MaxPayloadSize => 1160;
            public event Action<TransportEvent> EventReceived;
            internal MemoryTransport(MemoryHub hub) { this.hub = hub; }
            public void StartServer(IPEndPoint endpoint) { IsServer = IsRunning = true; }
            public void StartClient(IPEndPoint endpoint, byte[] connectionData = null)
            { IsRunning = true; LocalPeerId = ++hub.Next; hub.Clients.Add(LocalPeerId, this); Incoming.Enqueue(new TransportEvent(TransportEventType.Connected, 0)); hub.Server.Incoming.Enqueue(new TransportEvent(TransportEventType.Connected, LocalPeerId)); }
            public void Poll(int maxEvents = 256) { for (int i = 0; i < maxEvents && Incoming.Count > 0; i++) EventReceived?.Invoke(Incoming.Dequeue()); }
            public bool Send(ulong peerId, ArraySegment<byte> payload, DeliveryMode delivery = DeliveryMode.ReliableUnordered, byte channel = 0)
            {
                if (!IsRunning || (BlockReliable && (delivery == DeliveryMode.ReliableOrdered || delivery == DeliveryMode.ReliableUnordered))) return false;
                if (IsServer) { if (!hub.Clients.TryGetValue(peerId, out var peer)) return false; peer.Incoming.Enqueue(new TransportEvent(TransportEventType.Data, 0, payload.ToArray(), delivery, channel)); }
                else hub.Server.Incoming.Enqueue(new TransportEvent(TransportEventType.Data, LocalPeerId, payload.ToArray(), delivery, channel)); return true;
            }
            public void Disconnect(ulong peerId, string reason = "Disconnected")
            { ulong id = IsServer ? peerId : LocalPeerId; if (hub.Clients.TryGetValue(id, out var peer)) { hub.Clients.Remove(id); peer.Incoming.Enqueue(new TransportEvent(TransportEventType.Disconnected, 0, reason: reason)); hub.Server.Incoming.Enqueue(new TransportEvent(TransportEventType.Disconnected, id, reason: reason)); } }
            public void Stop() { if (IsRunning && !IsServer && hub.Clients.Remove(LocalPeerId)) hub.Server.Incoming.Enqueue(new TransportEvent(TransportEventType.Disconnected, LocalPeerId)); IsRunning = false; Incoming.Clear(); }
            public void Dispose() => Stop();
            internal void ReverseIncoming() { var packets = Incoming.Reverse().ToArray(); Incoming.Clear(); foreach (var packet in packets) Incoming.Enqueue(packet); }
        }
    }
}
