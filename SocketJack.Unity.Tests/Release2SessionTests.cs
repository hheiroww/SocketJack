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
    public class Release2SessionTests
    {
        [DataTestMethod, DataRow(TransportType.Udp), DataRow(TransportType.Tcp)]
        public void CompatibilityHandshakeRejectsMismatchedGameBeforeAdmission(TransportType type)
        {
            using var server = new NetworkServer(GameTransport.Create(type), new NetworkSessionOptions { CompatibilityId = "game-a" });
            using var client = new NetworkClient(GameTransport.Create(type), new NetworkSessionOptions { CompatibilityId = "game-b" });
            int admissions = 0; bool disconnected = false;
            server.PeerConnected += _ => admissions++;
            client.Disconnected += _ => disconnected = true;
            server.Start(new IPEndPoint(IPAddress.Loopback, 0)); client.Connect(server.Transport.LocalEndPoint);
            Pump(() => disconnected, server, client);
            Assert.AreEqual(0, admissions); Assert.IsFalse(client.IsConnected); Assert.AreEqual(0, client.Objects.Count);
        }

        [DataTestMethod, DataRow(TransportType.Udp), DataRow(TransportType.Tcp)]
        public void OwnershipTransferRevokesOldEpochEvenAfterTransferBack(TransportType type)
        {
            using var server = new NetworkServer(GameTransport.Create(type), new NetworkSessionOptions { CompatibilityId = "game-a" });
            using var a = new NetworkClient(GameTransport.Create(type), new NetworkSessionOptions { CompatibilityId = "game-a" });
            using var b = new NetworkClient(GameTransport.Create(type), new NetworkSessionOptions { CompatibilityId = "game-a" });
            server.Start(new IPEndPoint(IPAddress.Loopback, 0)); a.Connect(server.Transport.LocalEndPoint); b.Connect(server.Transport.LocalEndPoint);
            Pump(() => a.IsConnected && b.IsConnected, server, a, b);
            var obj = server.Spawn(1, a.LocalPeerId, 0, new byte[] { 1 }, false);
            Pump(() => a.Objects.ContainsKey(obj.Id) && b.Objects.ContainsKey(obj.Id), server, a, b);
            int ownershipNotifications = 0; a.OwnershipChanged += (_, __) => ownershipNotifications++;
            server.TransferOwnership(obj.Id, b.LocalPeerId, false);
            Pump(() => a.Objects[obj.Id].AuthorityEpoch == 2 && b.Objects[obj.Id].AuthorityEpoch == 2, server, a, b);
            Assert.IsFalse(a.SendOwnerState(obj.Id, new byte[] { 9 }));
            server.TransferOwnership(obj.Id, a.LocalPeerId, false);
            Pump(() => a.Objects[obj.Id].AuthorityEpoch == 3 && b.Objects[obj.Id].AuthorityEpoch == 3, server, a, b);
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write((byte)3); writer.Write((byte)8); writer.Write(obj.Id); writer.Write((uint)1); writer.Write(a.Objects[obj.Id].ViewId);
            writer.Write((uint)999); writer.Write((ushort)1); writer.Write((byte)99);
            Assert.IsTrue(a.Transport.Send(0, new ArraySegment<byte>(stream.ToArray()), DeliveryMode.ReliableUnordered, 2));
            for (int i = 0; i < 20; i++) { server.Update(.02); a.Update(.02); b.Update(.02); Thread.Sleep(1); }
            Assert.AreEqual((byte)1, obj.State[0]); Assert.IsTrue(a.IsConnected); Assert.AreEqual(2, ownershipNotifications);
            Assert.IsTrue(a.SendOwnerState(obj.Id, new byte[] { 2 }));
            Pump(() => obj.State[0] == 2, server, a, b);
        }

        private static void Pump(Func<bool> done, NetworkServer server, params NetworkClient[] clients)
        {
            var clock = Stopwatch.StartNew();
            while (!done() && clock.Elapsed < TimeSpan.FromSeconds(5))
            { server.Update(.01); foreach (var client in clients) client.Update(.01); Thread.Sleep(1); }
            Assert.IsTrue(done(), "Session operation timed out.");
        }
    }
}
