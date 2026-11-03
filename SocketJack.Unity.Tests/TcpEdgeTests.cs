using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Unity.Transport;

namespace SocketJack.Unity.Tests
{
    [TestClass]
    public class TcpEdgeTests
    {
        private static IPEndPoint Any => new IPEndPoint(IPAddress.Loopback, 0);

        [TestMethod]
        public void TcpPartialHandshakeTimesOutWithoutAdmissionOrConnectedEvent()
        {
            int admitted = 0, connected = 0;
            var options = new TransportOptions { ConnectTimeout = TimeSpan.FromMilliseconds(100), ConnectionValidator = (_, __) => { admitted++; return true; } };
            using var server = new TcpGameTransport(options);
            server.EventReceived += e => { if (e.Type == TransportEventType.Connected) connected++; };
            server.StartServer(Any);
            using var slow = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            slow.Connect(server.LocalEndPoint);
            server.Poll();
            Assert.AreEqual(1, server.PeerCount);
            slow.Send(new byte[] { 3, 0 }); // Partial length field; no body is allocated from this value.
            Pump(() => server.PeerCount == 0, server);
            Assert.AreEqual(0, admitted);
            Assert.AreEqual(0, connected);
        }

        [TestMethod]
        public void TcpDataBeforeHandshakeAndZeroLengthFramesNeverAdmit()
        {
            int admitted = 0;
            using var server = new TcpGameTransport(new TransportOptions { ConnectionValidator = (_, __) => { admitted++; return true; } });
            server.StartServer(Any);
            foreach (var frame in new[] { new byte[] { 0, 0, 0, 0 }, new byte[] { 3, 0, 0, 0, 3, 0, 2 } })
            {
                using var attacker = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                attacker.Connect(server.LocalEndPoint);
                server.Poll();
                attacker.Send(frame);
                Pump(() => server.PeerCount == 0, server);
            }
            Assert.AreEqual(0, admitted);
        }

        [TestMethod]
        public void TcpValidatorMayThrowOrStopTransportWithoutLeakingAcceptedSocket()
        {
            using (var server = new TcpGameTransport(new TransportOptions { ConnectionValidator = (_, __) => throw new InvalidOperationException("Admission failed") }))
            using (var client = new TcpGameTransport())
            {
                server.StartServer(Any);
                client.StartClient(server.LocalEndPoint);
                Pump(() => !client.IsRunning, server, client);
                Assert.AreEqual(0, server.PeerCount);
            }
            TcpGameTransport stopping = null;
            using (stopping = new TcpGameTransport(new TransportOptions { ConnectionValidator = (_, __) => { stopping.Stop(); return true; } }))
            using (var client = new TcpGameTransport())
            {
                int connected = 0;
                stopping.EventReceived += e => { if (e.Type == TransportEventType.Connected) connected++; };
                stopping.StartServer(Any);
                client.StartClient(stopping.LocalEndPoint);
                Pump(() => !stopping.IsRunning && !client.IsRunning, stopping, client);
                Assert.AreEqual(0, stopping.PeerCount);
                Assert.AreEqual(0, connected);
            }
        }

        [TestMethod]
        public void TcpRejectedSendNeverAppearsAndAcceptedPayloadIsOwnedUntilFlush()
        {
            using var server = new TcpGameTransport();
            using var client = new TcpGameTransport(new TransportOptions { MaxPendingReliablePerPeer = 1 });
            var received = new List<byte[]>();
            server.EventReceived += e => { if (e.Type == TransportEventType.Data) received.Add(e.Payload); };
            server.StartServer(Any);
            client.StartClient(server.LocalEndPoint);
            Pump(() => client.LocalPeerId != 0, server, client);
            var original = new byte[] { 1, 2 };
            Assert.IsTrue(client.Send(0, new ArraySegment<byte>(original)));
            original[0] = 9;
            Assert.IsFalse(client.Send(0, new ArraySegment<byte>(new byte[] { 3 })));
            Pump(() => received.Count == 1, server, client);
            Assert.IsTrue(client.Send(0, new ArraySegment<byte>(new byte[] { 4 })));
            Pump(() => received.Count == 2, server, client);
            CollectionAssert.AreEqual(new byte[] { 1, 2 }, received[0]);
            CollectionAssert.AreEqual(new byte[] { 4 }, received[1]);
        }

        [TestMethod]
        public void TcpDataCallbackCanStopAndReconnectTheClient()
        {
            using var server = new TcpGameTransport();
            using var client = new TcpGameTransport();
            server.StartServer(Any);
            server.EventReceived += e => { if (e.Type == TransportEventType.Data) server.Send(e.PeerId, new ArraySegment<byte>(e.Payload)); };
            int reconnects = 0;
            ulong firstId = 0;
            client.EventReceived += e =>
            {
                if (e.Type != TransportEventType.Data) return;
                reconnects++;
                client.Stop();
                client.StartClient(server.LocalEndPoint);
            };
            client.StartClient(server.LocalEndPoint);
            Pump(() => client.LocalPeerId != 0, server, client);
            firstId = client.LocalPeerId;
            Assert.IsTrue(client.Send(0, new ArraySegment<byte>(new byte[] { 7 })));
            Pump(() => reconnects == 1 && client.LocalPeerId != 0 && client.LocalPeerId != firstId, server, client);
            Assert.AreEqual(1, server.PeerCount);
        }

        [TestMethod]
        public void TcpRejectsRecursivePollFromCallback()
        {
            using var server = new TcpGameTransport();
            using var client = new TcpGameTransport();
            int checkedCallbacks = 0;
            server.EventReceived += e =>
            {
                if (e.Type != TransportEventType.Connected) return;
                Assert.ThrowsException<InvalidOperationException>(() => server.Poll());
                checkedCallbacks++;
            };
            server.StartServer(Any);
            client.StartClient(server.LocalEndPoint);
            Pump(() => client.LocalPeerId != 0, server, client);
            Assert.AreEqual(1, checkedCallbacks);
        }

        [TestMethod]
        public void TcpIncompatibleConfigurationNeverReportsConnected()
        {
            using var server = new TcpGameTransport();
            using var client = new TcpGameTransport(new TransportOptions { Channels = 2 });
            int connected = 0;
            server.EventReceived += e => { if (e.Type == TransportEventType.Connected) connected++; };
            client.EventReceived += e => { if (e.Type == TransportEventType.Connected) connected++; };
            server.StartServer(Any);
            client.StartClient(server.LocalEndPoint);
            Pump(() => !client.IsRunning, server, client);
            Assert.AreEqual(0, connected);
            Assert.AreEqual(0, server.PeerCount);
        }

        [TestMethod]
        public void TcpIncompatibleEffectiveFrameBudgetsNeverReportConnected()
        {
            using var server = new TcpGameTransport();
            using var client = new TcpGameTransport(new TransportOptions { ExtraPacketOverheadBytes = 32 });
            int connected = 0;
            server.EventReceived += e => { if (e.Type == TransportEventType.Connected) connected++; };
            client.EventReceived += e => { if (e.Type == TransportEventType.Connected) connected++; };
            server.StartServer(Any);
            client.StartClient(server.LocalEndPoint);
            Pump(() => !client.IsRunning, server, client);
            Assert.AreEqual(0, connected);
            Assert.AreEqual(0, server.PeerCount);
        }

        [TestMethod]
        public void TcpDefaultReliabilityPreservesUnorderedContractMetadataOverItsOrderedStream()
        {
            using var server = new TcpGameTransport();
            using var client = new TcpGameTransport();
            var received = new List<TransportEvent>();
            server.EventReceived += e => { if (e.Type == TransportEventType.Data) received.Add(e); };
            server.StartServer(Any);
            client.StartClient(server.LocalEndPoint);
            Pump(() => client.LocalPeerId != 0, server, client);
            IGameTransport throughInterface = client;
            Assert.IsTrue(throughInterface.Send(0, new ArraySegment<byte>(new byte[] { 1 })));
            Assert.IsTrue(client.Send(0, new ArraySegment<byte>(new byte[] { 2 })));
            Pump(() => received.Count == 2, server, client);
            Assert.AreEqual(DeliveryMode.ReliableUnordered, received[0].Delivery);
            Assert.AreEqual(DeliveryMode.ReliableUnordered, received[1].Delivery);
            // TCP necessarily preserves byte-stream order even for the unordered API request.
            CollectionAssert.AreEqual(new byte[] { 1 }, received[0].Payload);
            CollectionAssert.AreEqual(new byte[] { 2 }, received[1].Payload);
        }

        [TestMethod]
        public void TcpVersionOneHandshakeCannotEstablishVersionTwoSession()
        {
            using var server = new TcpGameTransport();
            int admitted = 0;
            server.EventReceived += e => { if (e.Type == TransportEventType.Connected) admitted++; };
            server.StartServer(Any);
            using var oldClient = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            oldClient.Connect(server.LocalEndPoint);
            server.Poll();
            // Previous handshake: body kind/channel/delivery, SJG1, maxpacket1200, channels4.
            oldClient.Send(new byte[] { 10, 0, 0, 0, 1, 0, 2, 0x53, 0x4a, 0x47, 1, 0xb0, 4, 4 });
            Pump(() => server.PeerCount == 0, server);
            Assert.AreEqual(0, admitted);
        }

        private static void Pump(Func<bool> done, params IGameTransport[] transports)
        {
            var watch = Stopwatch.StartNew();
            while (!done() && watch.Elapsed < TimeSpan.FromSeconds(5))
            {
                foreach (var transport in transports) transport.Poll();
                Thread.Sleep(1);
            }
            Assert.IsTrue(done(), "TCP operation did not complete within five seconds.");
        }
    }
}
