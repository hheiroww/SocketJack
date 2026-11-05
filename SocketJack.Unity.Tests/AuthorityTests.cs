using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Multiplayer.Transport;

namespace SocketJack.Multiplayer.Tests
{
    [TestClass]
    public class AuthorityTests
    {
        [DataTestMethod]
        [DataRow(TransportType.Udp)]
        [DataRow(TransportType.Tcp)]
        public void ExplicitOwnerAuthorityReplicatesAndRejectsOtherOwners(TransportType type)
        {
            using var server = new NetworkServer(GameTransport.Create(type), new NetworkSessionOptions { ServerAuthority = false });
            using var owner = new NetworkClient(GameTransport.Create(type));
            using var observer = new NetworkClient(GameTransport.Create(type));
            server.Start(new IPEndPoint(IPAddress.Loopback, 0));
            var world = server.Spawn(1, state: new byte[] { 0 });
            Assert.IsTrue(world.ServerAuthority, "Unowned objects always retain server authority.");
            owner.Connect(server.Transport.LocalEndPoint); observer.Connect(server.Transport.LocalEndPoint);
            Pump(() => owner.IsConnected && observer.IsConnected, server, owner, observer);
            var obj = server.Spawn(2, owner.LocalPeerId, state: new byte[] { 1 });
            Pump(() => owner.Objects.ContainsKey(obj.Id) && observer.Objects.ContainsKey(obj.Id), server, owner, observer);
            Assert.IsFalse(obj.ServerAuthority); Assert.IsFalse(owner.Objects[obj.Id].ServerAuthority);
            int changes = 0; server.ObjectChanged += _ => changes++;
            server.OwnerStateValidator = (_, bytes) => bytes.Length == 1 && bytes[0] < 50;
            Assert.IsTrue(owner.SendOwnerState(obj.Id, new byte[] { 7 }));
            Pump(() => obj.State[0] == 7 && observer.Objects[obj.Id].State[0] == 7, server, owner, observer);
            Assert.AreEqual(1, changes);
            Assert.IsFalse(observer.SendOwnerState(obj.Id, new byte[] { 8 }));
            Assert.IsTrue(observer.Transport.Send(0, new ArraySegment<byte>(OwnerPacket(obj.Id, 10, 8, observer.Objects[obj.Id].ViewId)), DeliveryMode.ReliableUnordered, 2));
            Pump(() => !observer.IsConnected, server, owner, observer);
            Assert.AreEqual((byte)7, obj.State[0]); Assert.IsTrue(owner.IsConnected);
        }

        [DataTestMethod]
        [DataRow(TransportType.Udp)]
        [DataRow(TransportType.Tcp)]
        public void DedicatedDefaultRejectsClientCanonicalWrites(TransportType type)
        {
            using var server = new NetworkServer(GameTransport.Create(type));
            using var client = new NetworkClient(GameTransport.Create(type));
            server.Start(new IPEndPoint(IPAddress.Loopback, 0)); client.Connect(server.Transport.LocalEndPoint);
            Pump(() => client.IsConnected, server, client);
            var obj = server.Spawn(1, client.LocalPeerId, state: new byte[] { 1 });
            Pump(() => client.Objects.ContainsKey(obj.Id), server, client);
            Assert.IsTrue(client.Objects[obj.Id].ServerAuthority);
            Assert.IsFalse(client.SendOwnerState(obj.Id, new byte[] { 2 }));
            Assert.IsTrue(client.Transport.Send(0, new ArraySegment<byte>(OwnerPacket(obj.Id, 1, 2)), DeliveryMode.ReliableUnordered, 2));
            Pump(() => !client.IsConnected, server, client);
            Assert.AreEqual((byte)1, obj.State[0]); Assert.AreEqual(0, server.Peers.Count);
        }

        [TestMethod]
        public void UnorderedOwnerStateUsesSequencesAndValidationFailsClosed()
        {
            using var server = new NetworkServer(GameTransport.Create(TransportType.Udp));
            using var client = new NetworkClient(GameTransport.Create(TransportType.Udp));
            server.Start(new IPEndPoint(IPAddress.Loopback, 0)); client.Connect(server.Transport.LocalEndPoint);
            Pump(() => client.IsConnected, server, client);
            var obj = server.Spawn(1, client.LocalPeerId, roomId: 0, state: new byte[] { 0 }, serverAuthority: false);
            Pump(() => client.Objects.ContainsKey(obj.Id), server, client);
            server.OwnerStateValidator = (_, bytes) => bytes.Length == 1 && bytes[0] < 10;
            client.Transport.Send(0, new ArraySegment<byte>(OwnerPacket(obj.Id, 20, 2)), DeliveryMode.ReliableUnordered, 2);
            client.Transport.Send(0, new ArraySegment<byte>(OwnerPacket(obj.Id, 19, 1)), DeliveryMode.ReliableUnordered, 2);
            Pump(() => obj.State[0] == 2, server, client);
            for (int i = 0; i < 10; i++) { server.Update(.02); client.Update(.02); Thread.Sleep(1); }
            Assert.AreEqual((byte)2, obj.State[0]);
            client.Transport.Send(0, new ArraySegment<byte>(OwnerPacket(obj.Id, 21, 99)), DeliveryMode.ReliableUnordered, 2);
            Pump(() => !client.IsConnected, server, client);
            Assert.AreEqual((byte)2, obj.State[0]);
        }

        private static byte[] OwnerPacket(ulong objectId, uint sequence, byte value, ulong view = 1)
        {
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write((byte)3); writer.Write((byte)8); writer.Write(objectId); writer.Write((uint)1); writer.Write(view); writer.Write(sequence);
            writer.Write((ushort)1); writer.Write(value); return stream.ToArray();
        }
        private static void Pump(Func<bool> complete, NetworkServer server, params NetworkClient[] clients)
        {
            var elapsed = Stopwatch.StartNew();
            while (!complete() && elapsed.Elapsed < TimeSpan.FromSeconds(8))
            { server.Update(.02); foreach (var client in clients) client.Update(.02); Thread.Sleep(1); }
            Assert.IsTrue(complete(), "Authority operation timed out.");
        }
    }
}
