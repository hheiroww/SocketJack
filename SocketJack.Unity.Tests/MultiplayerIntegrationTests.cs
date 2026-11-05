using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Multiplayer.Transport;

namespace SocketJack.Multiplayer.Tests
{
    [TestClass]
    public class MultiplayerIntegrationTests
    {
        [DataTestMethod]
        [DataRow(TransportType.Udp)]
        [DataRow(TransportType.Tcp)]
        public void RealSocketsReplicateOwnedInputLateJoinRoomsAndDisconnect(TransportType type)
        {
            using var server = new NetworkServer(GameTransport.Create(type));
            using var owner = new NetworkClient(GameTransport.Create(type));
            using var observer = new NetworkClient(GameTransport.Create(type));
            server.Start(new IPEndPoint(IPAddress.Loopback, 0));
            owner.Connect(server.Transport.LocalEndPoint);
            Pump(() => owner.IsConnected, server, owner);
            var obj = server.Spawn(1, owner.LocalPeerId, state: new byte[] { 1 });
            Pump(() => owner.Objects.ContainsKey(obj.Id), server, owner);
            int inputs = 0;
            server.InputReceived += input => { inputs++; server.SetState(input.ObjectId, input.Payload); };
            Assert.IsTrue(owner.SendInput(obj.Id, new byte[] { 2 }, DeliveryMode.ReliableOrdered));
            Pump(() => inputs == 1 && owner.Objects[obj.Id].State[0] == 2, server, owner);
            observer.Connect(server.Transport.LocalEndPoint);
            Pump(() => observer.IsConnected && observer.Objects.ContainsKey(obj.Id), server, owner, observer);
            CollectionAssert.AreEqual(new byte[] { 2 }, observer.Objects[obj.Id].State);
            Assert.IsFalse(observer.SendInput(obj.Id, new byte[] { 255 }));
            server.SetPeerRoom(observer.LocalPeerId, 10);
            Pump(() => observer.Objects.Count == 0, server, owner, observer);
            server.SetState(obj.Id, new byte[] { 3 });
            Pump(() => owner.Objects[obj.Id].State[0] == 3, server, owner, observer);
            Assert.AreEqual(0, observer.Objects.Count);
            server.SetPeerRoom(observer.LocalPeerId, 0);
            Pump(() => observer.Objects.ContainsKey(obj.Id) && observer.Objects[obj.Id].State[0] == 3, server, owner, observer);
            owner.Stop();
            Pump(() => server.Peers.Count == 1 && !server.Objects.ContainsKey(obj.Id) && !observer.Objects.ContainsKey(obj.Id), server, observer);
            Assert.AreEqual(1, inputs);
            int messages = 0;
            observer.MessageReceived += message => { if (message.MessageId == 99 && message.Payload.SequenceEqual(new byte[] { 42 })) messages++; };
            Assert.IsTrue(server.SendMessage(observer.LocalPeerId, 99, new byte[] { 42 }));
            Pump(() => messages == 1, server, observer);
        }

        private static void Pump(Func<bool> completed, NetworkServer server, params NetworkClient[] clients)
        {
            var timeout = Stopwatch.StartNew();
            while (!completed() && timeout.Elapsed < TimeSpan.FromSeconds(10))
            {
                server.Update(1d / 60); foreach (var client in clients) client.Update(1d / 60);
                Thread.Sleep(1);
            }
            Assert.IsTrue(completed(), "Multiplayer operation timed out.");
        }
    }
}
