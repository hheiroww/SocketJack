using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Multiplayer.Transport;

namespace SocketJack.Multiplayer.Tests
{
    [TestClass]
    public class TcpTransportTests
    {
        [TestMethod]
        public void ConnectRoundTripChannelsAndReconnect()
        {
            using var server = new TcpGameTransport();
            using var client = new TcpGameTransport();
            var serverEvents = new List<TransportEvent>();
            var clientEvents = new List<TransportEvent>();
            server.EventReceived += serverEvents.Add; client.EventReceived += clientEvents.Add;
            server.StartServer(new IPEndPoint(IPAddress.Loopback, 0));
            client.StartClient(server.LocalEndPoint);
            Pump(server, client, () => client.LocalPeerId != 0);
            ulong firstId = client.LocalPeerId;
            Assert.IsTrue(server.Send(firstId, new ArraySegment<byte>(new byte[] { 8, 9, 10 }, 1, 2), DeliveryMode.ReliableOrdered, 2));
            Assert.IsTrue(client.Send(0, new ArraySegment<byte>(new byte[] { 1, 2 }), DeliveryMode.UnreliableSequenced, 1));
            Pump(server, client, () => serverEvents.Exists(e => e.Type == TransportEventType.Data) && clientEvents.Exists(e => e.Type == TransportEventType.Data));
            var received = clientEvents.Find(e => e.Type == TransportEventType.Data);
            CollectionAssert.AreEqual(new byte[] { 9, 10 }, received.Payload);
            Assert.AreEqual((byte)2, received.Channel);
            client.Stop();
            Pump(server, null, () => server.PeerCount == 0);
            client.StartClient(server.LocalEndPoint);
            Pump(server, client, () => client.LocalPeerId != 0);
            Assert.AreNotEqual(firstId, client.LocalPeerId);
        }

        [TestMethod]
        public void AdmissionAndSendBackpressureAreExplicit()
        {
            using var server = new TcpGameTransport(new TransportOptions { ConnectionValidator = (_, data) => data.Length == 1 && data[0] == 42 });
            using var denied = new TcpGameTransport();
            server.StartServer(new IPEndPoint(IPAddress.Loopback, 0));
            denied.StartClient(server.LocalEndPoint, new byte[] { 0 });
            Pump(server, denied, () => !denied.IsRunning);
            Assert.AreEqual(0, server.PeerCount);
            using var client = new TcpGameTransport(new TransportOptions { MaxPendingReliablePerPeer = 1 });
            client.StartClient(server.LocalEndPoint, new byte[] { 42 });
            Pump(server, client, () => client.LocalPeerId != 0);
            Assert.IsTrue(client.Send(0, new ArraySegment<byte>(new byte[] { 1 })));
            Assert.IsFalse(client.Send(0, new ArraySegment<byte>(new byte[] { 2 })));
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => client.Send(0, new ArraySegment<byte>(new byte[client.MaxPayloadSize + 1])));
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => client.Send(0, default, channel: 255));
        }

        [TestMethod]
        public void OversizedAndPartialFramesAreBoundedAndDoNotConnect()
        {
            using var server = new TcpGameTransport();
            server.StartServer(new IPEndPoint(IPAddress.Loopback, 0));
            using var attacker = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            attacker.Connect(server.LocalEndPoint);
            server.Poll();
            attacker.Send(new byte[] { 255, 255 });
            server.Poll();
            Assert.AreEqual(1, server.PeerCount);
            attacker.Send(new byte[] { 255, 127 });
            Pump(server, null, () => server.PeerCount == 0);
        }

        [TestMethod]
        public void PollFairnessServicesPeersPastBudget()
        {
            using var server = new TcpGameTransport();
            server.StartServer(new IPEndPoint(IPAddress.Loopback, 0));
            var clients = new List<TcpGameTransport>();
            int connected = 0;
            server.EventReceived += e => { if (e.Type == TransportEventType.Connected) connected++; };
            try
            {
                for (int i = 0; i < 40; i++) { var client = new TcpGameTransport(); clients.Add(client); client.StartClient(server.LocalEndPoint); }
                var watch = Stopwatch.StartNew();
                while (watch.Elapsed < TimeSpan.FromSeconds(10) && connected < clients.Count)
                { server.Poll(3); foreach (var client in clients) client.Poll(3); Thread.Sleep(1); }
                Assert.AreEqual(clients.Count, connected);
            }
            finally { foreach (var client in clients) client.Dispose(); }
        }

        private static void Pump(IGameTransport server, IGameTransport client, Func<bool> complete)
        {
            var watch = Stopwatch.StartNew();
            while (!complete() && watch.Elapsed < TimeSpan.FromSeconds(5)) { server.Poll(); client?.Poll(); Thread.Sleep(1); }
            Assert.IsTrue(complete(), "Transport operation timed out.");
        }
    }
}
