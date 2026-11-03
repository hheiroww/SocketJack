using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Unity;
using SocketJack.Unity.Transport;

namespace SocketJack.Unity.Tests
{
    [TestClass]
    public class SessionTests
    {
        [TestMethod]
        public void ReplicatesLateJoinOwnedInputsAndDisconnectCleanup()
        {
            var hub = new MemoryHub();
            using var server = new NetworkServer(hub.Server);
            server.Start(new IPEndPoint(IPAddress.Loopback, 1));
            var world = server.Spawn(7, state: new byte[] { 1, 2 });
            using var a = new NetworkClient(hub.NewClient()); a.Connect(hub.Endpoint); Pump(server, a);
            Assert.IsTrue(a.IsConnected); Assert.AreEqual(1, a.Objects.Count);
            CollectionAssert.AreEqual(new byte[] { 1, 2 }, a.Objects[world.Id].State);
            var owned = server.Spawn(8, a.LocalPeerId, state: new byte[] { 3 }); Pump(server, a);
            int inputs = 0;
            server.InputReceived += input => { inputs++; Assert.AreEqual(owned.Id, input.ObjectId); server.SetState(input.ObjectId, input.Payload); };
            Assert.IsFalse(a.SendInput(world.Id, new byte[] { 9 }));
            Assert.IsTrue(a.SendInput(owned.Id, new byte[] { 4 })); Pump(server, a, 8);
            Assert.AreEqual(1, inputs); CollectionAssert.AreEqual(new byte[] { 4 }, a.Objects[owned.Id].State);
            using var late = new NetworkClient(hub.NewClient()); late.Connect(hub.Endpoint); Pump(server, late);
            Assert.AreEqual(2, late.Objects.Count); CollectionAssert.AreEqual(new byte[] { 4 }, late.Objects[owned.Id].State);
            a.Stop(); Pump(server, late);
            Assert.IsFalse(server.Objects.ContainsKey(owned.Id)); Assert.IsFalse(late.Objects.ContainsKey(owned.Id));
            Assert.IsTrue(server.Objects.ContainsKey(world.Id));
        }

        [TestMethod]
        public void RoomInterestTransitionsDespawnAndSupplyNewBaselines()
        {
            var hub = new MemoryHub(); using var server = new NetworkServer(hub.Server);
            server.Start(hub.Endpoint); var publicObject = server.Spawn(1, roomId: 0); var privateObject = server.Spawn(2, roomId: 10);
            using var client = new NetworkClient(hub.NewClient()); client.Connect(hub.Endpoint); Pump(server, client);
            Assert.IsTrue(client.Objects.ContainsKey(publicObject.Id)); Assert.IsFalse(client.Objects.ContainsKey(privateObject.Id));
            server.SetPeerRoom(client.LocalPeerId, 10); Pump(server, client);
            Assert.IsFalse(client.Objects.ContainsKey(publicObject.Id)); Assert.IsTrue(client.Objects.ContainsKey(privateObject.Id));
            server.SetObjectRoom(publicObject.Id, 10); Pump(server, client);
            Assert.AreEqual(2, client.Objects.Count);
            server.SetState(publicObject.Id, new byte[] { 10 }); Pump(server, client, 8);
            CollectionAssert.AreEqual(new byte[] { 10 }, client.Objects[publicObject.Id].State);
        }

        [TestMethod]
        public void ForgedInputAndServerOnlyMessagesDisconnectAttacker()
        {
            var hub = new MemoryHub(); using var server = new NetworkServer(hub.Server); server.Start(hub.Endpoint);
            var obj = server.Spawn(1); var transport = hub.NewClient(); using var client = new NetworkClient(transport);
            client.Connect(hub.Endpoint); Pump(server, client); ulong id = client.LocalPeerId;
            int inputs = 0; server.InputReceived += _ => inputs++;
            var forged = Packet(5, w => { w.Write(obj.Id); w.Write((ushort)0); });
            transport.Send(0, new ArraySegment<byte>(Frame(forged, DeliveryMode.UnreliableSequenced, 2)), DeliveryMode.UnreliableSequenced, 2); server.Update(0);
            Assert.AreEqual(0, inputs); Assert.IsFalse(server.Peers.Contains(id));
            using var next = new NetworkClient(hub.NewClient()); next.Connect(hub.Endpoint); Pump(server, next); id = next.LocalPeerId;
            ((MemoryTransport)((ObjectFrameTransport)next.Transport).InnerTransport).Send(0,
                new ArraySegment<byte>(Frame(Packet(3, w => w.Write(obj.Id)), DeliveryMode.ReliableOrdered, 0)), DeliveryMode.ReliableOrdered, 0);
            server.Update(0); Assert.IsFalse(server.Peers.Contains(id)); Assert.IsTrue(server.Objects.ContainsKey(obj.Id));
        }

        [TestMethod]
        public void ReliableBackpressureRetriesAndOverflowDisconnects()
        {
            var hub = new MemoryHub(); using var server = new NetworkServer(hub.Server,
                new NetworkSessionOptions { MaxQueuedMessagesPerPeer = 3 });
            server.Start(hub.Endpoint); using var client = new NetworkClient(hub.NewClient()); client.Connect(hub.Endpoint); Pump(server, client);
            hub.Server.BlockReliable = true;
            var obj = server.Spawn(1); Pump(server, client); Assert.AreEqual(0, client.Objects.Count);
            hub.Server.BlockReliable = false; Pump(server, client); Assert.IsTrue(client.Objects.ContainsKey(obj.Id));
            ulong id = client.LocalPeerId; hub.Server.BlockReliable = true;
            for (int i = 0; i < 4; i++) server.Spawn(2);
            Assert.IsFalse(server.Peers.Contains(id));
        }

        [TestMethod]
        public void LostSnapshotRepairsAndStaleStateCannotResurrectDespawnedObject()
        {
            var hub = new MemoryHub(); using var server = new NetworkServer(hub.Server); server.Start(hub.Endpoint);
            var obj = server.Spawn(1, state: new byte[] { 1 }); var transport = hub.NewClient();
            using var client = new NetworkClient(transport); client.Connect(hub.Endpoint); Pump(server, client);
            hub.Server.DropStates = true; server.SetState(obj.Id, new byte[] { 2 }); Pump(server, client, 8);
            CollectionAssert.AreEqual(new byte[] { 1 }, client.Objects[obj.Id].State);
            hub.Server.DropStates = false; Pump(server, client, 8); CollectionAssert.AreEqual(new byte[] { 2 }, client.Objects[obj.Id].State);
            var stale = Packet(4, w => { w.Write(obj.Id); w.Write((uint)1); w.Write((uint)0); w.Write((ushort)1); w.Write((byte)99); });
            transport.Incoming.Enqueue(new TransportEvent(TransportEventType.Data, 0, Frame(stale, DeliveryMode.UnreliableSequenced, 1), DeliveryMode.UnreliableSequenced, 1)); client.Update(0);
            CollectionAssert.AreEqual(new byte[] { 2 }, client.Objects[obj.Id].State);
            server.Despawn(obj.Id); Pump(server, client);
            transport.Incoming.Enqueue(new TransportEvent(TransportEventType.Data, 0, Frame(stale, DeliveryMode.UnreliableSequenced, 1), DeliveryMode.UnreliableSequenced, 1)); client.Update(0);
            Assert.AreEqual(0, client.Objects.Count);
        }

        [TestMethod]
        public void MalformedPayloadAndRateLimitAreBounded()
        {
            var hub = new MemoryHub(); using var server = new NetworkServer(hub.Server, new NetworkSessionOptions { MaxInboundMessagesPerPeerPerSecond = 2 });
            server.Start(hub.Endpoint); using var client = new NetworkClient(hub.NewClient()); client.Connect(hub.Endpoint); Pump(server, client);
            ulong id = client.LocalPeerId;
            for (int i = 0; i < 3; i++) client.SendMessage(1, Array.Empty<byte>());
            server.Update(0); Assert.IsFalse(server.Peers.Contains(id));
            var transport = hub.NewClient(); using var next = new NetworkClient(transport); next.Connect(hub.Endpoint); Pump(server, next); id = next.LocalPeerId;
            transport.Send(0, new ArraySegment<byte>(Frame(new byte[] { 1, 6, 0, 0, 255, 255 }, DeliveryMode.ReliableOrdered, 3)), DeliveryMode.ReliableOrdered, 3);
            server.Update(0); Assert.IsFalse(server.Peers.Contains(id));
        }

        [TestMethod]
        public void TwoThousandPeersReceiveOnlyTheirRoomsWithinConfiguredSnapshotBudget()
        {
            var hub = new MemoryHub(); using var server = new NetworkServer(hub.Server, new NetworkSessionOptions { MaxStateUpdatesPerTick = 2 });
            server.Start(hub.Endpoint); server.PeerConnected += id => server.SetPeerRoom(id, (int)(id % 100));
            for (int room = 0; room < 100; room++) for (int i = 0; i < 5; i++) server.Spawn((uint)i, roomId: room);
            var peers = new List<MemoryTransport>();
            for (int i = 0; i < 2000; i++) { var transport = hub.NewClient(); transport.StartClient(hub.Endpoint); peers.Add(transport); }
            for (int i = 0; i < 10; i++) server.Update(0, 4096);
            Assert.AreEqual(2000, server.Peers.Count);
            foreach (var peer in peers)
            {
                var spawnPackets = peer.Incoming.Where(e => e.Type == TransportEventType.Data && e.Payload.Length > 30 && e.Payload[29] == 2).ToArray();
                Assert.IsTrue(spawnPackets.Length >= 5);
                foreach (var packet in spawnPackets) Assert.AreEqual((int)(peer.LocalPeerId % 100), BitConverter.ToInt32(packet.Payload, 50));
                peer.Incoming.Clear();
            }
            server.Update(.1, 4096);
            foreach (var peer in peers) Assert.AreEqual(2, peer.Incoming.Count(e => e.Type == TransportEventType.Data && e.Payload[29] == 4));
            Assert.AreEqual(4000, peers.Sum(p => p.Incoming.Count));
        }

        [TestMethod]
        public void CustomMessagesPreserveIdsAndChannelsAndReportBackpressure()
        {
            var hub = new MemoryHub(); using var server = new NetworkServer(hub.Server); server.Start(hub.Endpoint);
            using var client = new NetworkClient(hub.NewClient()); client.Connect(hub.Endpoint); Pump(server, client);
            NetworkMessage received = null; server.MessageReceived += message => received = message;
            Assert.IsTrue(client.SendMessage(42, new byte[] { 1, 2 })); server.Update(0);
            Assert.AreEqual((ushort)42, received.MessageId); Assert.AreEqual((byte)3, received.Channel);
            hub.Server.BlockReliable = true; Assert.IsTrue(server.SendMessage(client.LocalPeerId, 2, new byte[0]));
            bool rejected = false; for (int i = 0; i < 200; i++) if (!server.SendMessage(client.LocalPeerId, 2, new byte[0])) rejected = true;
            Assert.IsTrue(rejected); hub.Server.BlockReliable = false; Pump(server, client, 8);
            NetworkMessage answer = null; client.MessageReceived += message => answer = message;
            Assert.IsTrue(server.SendMessage(client.LocalPeerId, 3, new byte[] { 4 })); client.Update(0);
            Assert.AreEqual((ushort)3, answer.MessageId); CollectionAssert.AreEqual(new byte[] { 4 }, answer.Payload);
        }

        [TestMethod]
        public void StateAndCustomMessageOvertakingWelcomeDoNotBreakConnection()
        {
            var hub = new MemoryHub(); using var server = new NetworkServer(hub.Server); server.Start(hub.Endpoint);
            var transport = hub.NewClient(); using var client = new NetworkClient(transport); client.Connect(hub.Endpoint);
            int messages = 0; client.MessageReceived += _ => messages++;
            var state = Packet(4, w => { w.Write((ulong)1); w.Write((uint)9); w.Write((uint)9); w.Write((ushort)0); });
            var custom = Packet(6, w => { w.Write((ushort)7); w.Write((ushort)0); });
            transport.Incoming.Enqueue(new TransportEvent(TransportEventType.Data, 0, Frame(state, DeliveryMode.UnreliableSequenced, 1), DeliveryMode.UnreliableSequenced, 1));
            transport.Incoming.Enqueue(new TransportEvent(TransportEventType.Data, 0, Frame(custom, DeliveryMode.ReliableOrdered, 3), DeliveryMode.ReliableOrdered, 3));
            client.Update(0); Assert.AreEqual(0, messages); Assert.IsFalse(client.IsConnected);
            Pump(server, client); Assert.IsTrue(client.IsConnected); Assert.AreEqual(1, messages);
        }

        [TestMethod]
        public void ApplicationCallbackIOExceptionPropagatesWithoutProtocolDisconnect()
        {
            var hub = new MemoryHub(); using var server = new NetworkServer(hub.Server); server.Start(hub.Endpoint);
            using var client = new NetworkClient(hub.NewClient()); client.Connect(hub.Endpoint);
            client.Connected += () => throw new IOException("Application failure");
            server.Update(0); Assert.ThrowsException<IOException>(() => client.Update(0));
            Assert.IsTrue(client.IsConnected); ulong id = client.LocalPeerId;
            server.MessageReceived += _ => throw new InvalidDataException("Application failure");
            client.SendMessage(1, new byte[0]); Assert.ThrowsException<InvalidDataException>(() => server.Update(0));
            Assert.IsTrue(server.Peers.Contains(id));
        }

        [TestMethod]
        public void DisconnectNotifiesOnceAndAllowsReuseOfClientSession()
        {
            var hub = new MemoryHub(); using var server = new NetworkServer(hub.Server); server.Start(hub.Endpoint);
            using var client = new NetworkClient(hub.NewClient()); client.Connect(hub.Endpoint); Pump(server, client);
            int disconnects = 0; client.Disconnected += _ => disconnects++;
            server.Disconnect(client.LocalPeerId, "Test"); client.Update(0); Assert.AreEqual(1, disconnects); Assert.IsFalse(client.IsRunning);
            client.Stop(); Assert.AreEqual(1, disconnects);
            client.Connect(hub.Endpoint); Pump(server, client); Assert.IsTrue(client.IsConnected);
        }

        [TestMethod]
        public void LargeLateJoinAndRoomChangeStreamThroughTinyReliableQueues()
        {
            var hub = new MemoryHub();
            using var server = new NetworkServer(hub.Server, new NetworkSessionOptions { MaxQueuedMessagesPerPeer = 8, MaxQueuedBytesPerPeer = 2400 });
            server.Start(hub.Endpoint);
            for (int i = 0; i < 300; i++) server.Spawn(1, roomId: 0, state: new byte[1024]);
            for (int i = 0; i < 200; i++) server.Spawn(2, roomId: 10, state: new byte[1024]);
            using var client = new NetworkClient(hub.NewClient()); client.Connect(hub.Endpoint); Pump(server, client, 180);
            Assert.IsTrue(client.IsConnected); Assert.AreEqual(300, client.Objects.Count);
            server.SetPeerRoom(client.LocalPeerId, 10); Pump(server, client, 130);
            Assert.IsTrue(client.IsConnected); Assert.AreEqual(200, client.Objects.Count);
            Assert.IsTrue(client.Objects.Values.All(o => o.RoomId == 10));
        }

        [TestMethod]
        public void OverflowingOwnerCannotPublishSpawnAfterItsCleanupDespawn()
        {
            var hub = new MemoryHub(); using var server = new NetworkServer(new ObjectFrameTransport(hub.Server,
                new ObjectFrameOptions { MaxQueuedMessagesPerPeer = 1 }), new NetworkSessionOptions { MaxQueuedMessagesPerPeer = 2 });
            server.Start(hub.Endpoint); using var owner = new NetworkClient(hub.NewClient()); owner.Connect(hub.Endpoint); Pump(server, owner);
            using var observer = new NetworkClient(hub.NewClient()); observer.Connect(hub.Endpoint); Pump(server, observer);
            ulong id = owner.LocalPeerId; hub.Server.BlockedPeers.Add(id);
            server.Spawn(1, id); Pump(server, observer);
            server.Spawn(1, id); Pump(server, observer);
            server.Spawn(1, id); Pump(server, observer);
            server.Spawn(1, id); Pump(server, observer);
            Assert.IsFalse(server.Peers.Contains(id)); Assert.AreEqual(0, server.Objects.Count); Assert.AreEqual(0, observer.Objects.Count);
        }

        [TestMethod]
        public void MalformedPacketDoesNotStopReconnectFromSynchronousDisconnectHandler()
        {
            var hub = new MemoryHub(); using var server = new NetworkServer(hub.Server); server.Start(hub.Endpoint);
            var transport = hub.NewClient(); transport.SynchronousDisconnect = true;
            using var client = new NetworkClient(transport); client.Connect(hub.Endpoint); Pump(server, client);
            int disconnects = 0;
            client.Disconnected += _ => { if (++disconnects == 1) client.Connect(hub.Endpoint); };
            transport.Incoming.Enqueue(new TransportEvent(TransportEventType.Data, 0, Frame(new byte[] { 255, 255 }, DeliveryMode.ReliableUnordered, 0)));
            client.Update(0);
            Assert.AreEqual(1, disconnects); Assert.IsTrue(client.IsRunning);
            Pump(server, client); Assert.IsTrue(client.IsConnected);
            Assert.AreEqual(1, disconnects);
        }

        [TestMethod]
        public void ReconnectFromViewCleanupSuppressesStaleDisconnectedNotification()
        {
            var hub = new MemoryHub(); using var server = new NetworkServer(hub.Server); server.Start(hub.Endpoint); server.Spawn(1);
            var transport = hub.NewClient(); using var client = new NetworkClient(transport); client.Connect(hub.Endpoint); Pump(server, client);
            int disconnects = 0, cleanupCallbacks = 0;
            client.Disconnected += _ => disconnects++;
            client.ObjectDespawned += _ => { if (++cleanupCallbacks == 1) client.Connect(hub.Endpoint); };
            transport.Incoming.Enqueue(new TransportEvent(TransportEventType.Data, 0, Frame(new byte[] { 255, 255 }, DeliveryMode.ReliableUnordered, 0)));
            client.Update(0);
            Assert.AreEqual(1, cleanupCallbacks); Assert.AreEqual(0, disconnects); Assert.IsTrue(client.IsRunning);
            Pump(server, client); Assert.IsTrue(client.IsConnected); Assert.AreEqual(1, client.Objects.Count);
        }

        private static byte[] Packet(byte kind, Action<BinaryWriter> fields)
        { using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream); writer.Write((byte)1); writer.Write(kind); fields(writer); return stream.ToArray(); }
        private static byte[] Frame(byte[] payload, DeliveryMode mode, byte channel)
        {
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write((uint)0x464f4a53); writer.Write((byte)1); writer.Write((byte)mode); writer.Write(channel); writer.Write((byte)0);
            writer.Write((ulong)900000); writer.Write(mode == DeliveryMode.ReliableOrdered ? (uint)1 : (uint)900000);
            writer.Write((ushort)0); writer.Write((ushort)1); writer.Write(payload.Length); writer.Write(payload); return stream.ToArray();
        }
        private static void Pump(NetworkServer server, NetworkClient client, int count = 4)
        { for (int i = 0; i < count; i++) { server.Update(1d / 30); client.Update(1d / 30); } }

        private sealed class MemoryHub
        {
            internal readonly IPEndPoint Endpoint = new IPEndPoint(IPAddress.Loopback, 1);
            internal readonly Dictionary<ulong, MemoryTransport> Clients = new Dictionary<ulong, MemoryTransport>();
            internal readonly MemoryTransport Server;
            internal ulong NextId;
            internal MemoryHub() { Server = new MemoryTransport(this); }
            internal MemoryTransport NewClient() => new MemoryTransport(this);
        }
        private sealed class MemoryTransport : IGameTransport
        {
            private readonly MemoryHub _hub;
            internal readonly Queue<TransportEvent> Incoming = new Queue<TransportEvent>();
            internal bool BlockReliable, DropStates;
            internal bool SynchronousDisconnect;
            internal readonly HashSet<ulong> BlockedPeers = new HashSet<ulong>();
            public TransportType Type => TransportType.Custom;
            public bool IsServer { get; private set; }
            public bool IsRunning { get; private set; }
            public ulong LocalPeerId { get; private set; }
            public IPEndPoint LocalEndPoint => _hub.Endpoint;
            public int MaxPayloadSize => 1160;
            public event Action<TransportEvent> EventReceived;
            internal MemoryTransport(MemoryHub hub) { _hub = hub; }
            public void StartServer(IPEndPoint endpoint) { IsServer = true; IsRunning = true; }
            public void StartClient(IPEndPoint endpoint, byte[] connectionData = null)
            {
                IsRunning = true; LocalPeerId = ++_hub.NextId; _hub.Clients.Add(LocalPeerId, this);
                Incoming.Enqueue(new TransportEvent(TransportEventType.Connected, 0));
                _hub.Server.Incoming.Enqueue(new TransportEvent(TransportEventType.Connected, LocalPeerId));
            }
            public void Poll(int maxEvents = 256)
            { for (int i = 0; i < maxEvents && Incoming.Count > 0; i++) EventReceived?.Invoke(Incoming.Dequeue()); }
            public bool Send(ulong peerId, ArraySegment<byte> payload, DeliveryMode delivery = DeliveryMode.ReliableOrdered, byte channel = 0)
            {
                if (!IsRunning || ((delivery == DeliveryMode.ReliableOrdered || delivery == DeliveryMode.ReliableUnordered) && (BlockReliable || BlockedPeers.Contains(peerId)))) return false;
                if (DropStates && delivery == DeliveryMode.UnreliableSequenced) return true;
                if (IsServer)
                {
                    if (!_hub.Clients.TryGetValue(peerId, out var target)) return false;
                    target.Incoming.Enqueue(new TransportEvent(TransportEventType.Data, 0, payload.ToArray(), delivery, channel));
                }
                else _hub.Server.Incoming.Enqueue(new TransportEvent(TransportEventType.Data, LocalPeerId, payload.ToArray(), delivery, channel));
                return true;
            }
            public void Disconnect(ulong peerId, string reason = "Disconnected")
            {
                ulong id = IsServer ? peerId : LocalPeerId;
                if (!_hub.Clients.TryGetValue(id, out var target)) return;
                _hub.Clients.Remove(id);
                var disconnected = new TransportEvent(TransportEventType.Disconnected, 0, reason: reason);
                if (target.SynchronousDisconnect) target.EventReceived?.Invoke(disconnected);
                else target.Incoming.Enqueue(disconnected);
                _hub.Server.Incoming.Enqueue(new TransportEvent(TransportEventType.Disconnected, id, reason: reason));
            }
            public void Stop()
            {
                if (IsRunning && !IsServer && _hub.Clients.Remove(LocalPeerId))
                    _hub.Server.Incoming.Enqueue(new TransportEvent(TransportEventType.Disconnected, LocalPeerId, reason: "Stopped"));
                IsRunning = false; Incoming.Clear();
            }
            public void Dispose() => Stop();
        }
    }
}
