using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Multiplayer.Transport;

namespace SocketJack.Multiplayer.Tests
{
    [TestClass]
    public sealed class TransportMetricsTests
    {
        [DataTestMethod]
        [DataRow(TransportType.Udp)]
        [DataRow(TransportType.Tcp)]
        [DataRow(TransportType.WebSocket)]
        public void CountersMeasureActualTrafficAndBoundedImmutablePeerSnapshots(TransportType type)
        {
            using var server = GameTransport.Create(type); using var client = GameTransport.Create(type);
            var serverSource = (ITransportMetricsSource)server; var clientSource = (ITransportMetricsSource)client;
            int echoed = 0;
            server.EventReceived += value => { if (value.Type == TransportEventType.Data) Assert.IsTrue(server.Send(value.PeerId, new ArraySegment<byte>(value.Payload), value.Delivery, value.Channel)); };
            client.EventReceived += value => { if (value.Type == TransportEventType.Data) { CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, value.Payload); echoed++; } };
            server.StartServer(new IPEndPoint(IPAddress.Loopback, 0)); client.StartClient(server.LocalEndPoint);
            Pump(() => client.LocalPeerId != 0, () => { server.Poll(); client.Poll(); });
            var before = clientSource.GetTransportMetrics();
            Assert.IsTrue(client.Send(0, new ArraySegment<byte>(new byte[] { 1, 2, 3 })));
            Pump(() => echoed == 1 && clientSource.GetTransportMetrics().QueuedBytes == 0, () => { server.Poll(); client.Poll(); });
            var after = clientSource.GetTransportMetrics();
            Assert.IsTrue(after.BytesSent > before.BytesSent && after.BytesReceived > before.BytesReceived);
            Assert.IsTrue(after.PacketsSent > before.PacketsSent && after.PacketsReceived > before.PacketsReceived);
            Assert.AreEqual(1, after.ConnectedPeers); Assert.AreEqual(1, after.Peers.Count);
            Assert.AreEqual(0, clientSource.GetTransportMetrics(0).Peers.Count);
            if (type == TransportType.Tcp) Assert.IsNull(after.Peers[0].RoundTripMilliseconds, "One-way TCP keepalives are not RTT samples.");
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => clientSource.GetTransportMetrics(4097));
            client.Stop();
            Assert.AreEqual(1, after.ConnectedPeers, "A published snapshot must not mutate when the transport changes.");
            Assert.AreEqual(0, clientSource.GetTransportMetrics().ConnectedPeers);
            Assert.IsTrue(clientSource.GetTransportMetrics().BytesSent >= after.BytesSent);
            using var wrapped = new ObjectFrameTransport(GameTransport.Create(type));
            Assert.IsTrue(ReferenceEquals(wrapped.InnerTransport, TransportMetrics.Source(wrapped)));
        }

        [TestMethod]
        public void UdpRetransmissionCountersAndKarnRttExcludeAmbiguousAcks()
        {
            var options = new TransportOptions { RetransmitInterval = TimeSpan.FromMilliseconds(30) };
            using var server = new UdpGameTransport(options); using var client = new UdpGameTransport(options);
            server.StartServer(new IPEndPoint(IPAddress.Loopback, 0));
            using var proxy = new DropFirstDataProxy(server.LocalEndPoint);
            int received = 0; server.EventReceived += value => { if (value.Type == TransportEventType.Data) received++; };
            client.StartClient(proxy.Endpoint);
            void Step() { proxy.Poll(); server.Poll(); proxy.Poll(); client.Poll(); }
            Pump(() => client.LocalPeerId != 0, Step);
            Assert.IsTrue(client.Send(0, new ArraySegment<byte>(new byte[] { 42 })));
            Pump(() => received == 1 && client.GetTransportMetrics().QueuedBytes == 0, Step);
            var retried = client.GetTransportMetrics();
            Assert.IsTrue(retried.ReliableRetransmissions >= 1); Assert.IsNull(retried.Peers[0].RoundTripMilliseconds);
            Assert.IsTrue(client.Send(0, new ArraySegment<byte>(new byte[] { 43 })));
            Pump(() => received == 2 && client.GetTransportMetrics().Peers[0].RoundTripMilliseconds.HasValue, Step);
            Assert.IsTrue(client.GetTransportMetrics().Peers[0].RoundTripMilliseconds.Value >= 0);
        }
        private sealed class DropFirstDataProxy : IDisposable
        {
            private readonly Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            private readonly IPEndPoint server;
            private IPEndPoint client;
            private bool dropped;
            public IPEndPoint Endpoint => (IPEndPoint)socket.LocalEndPoint;
            public DropFirstDataProxy(IPEndPoint server) { this.server = server; socket.Bind(new IPEndPoint(IPAddress.Loopback, 0)); socket.Blocking = false; }
            public void Poll()
            {
                byte[] bytes = new byte[2048];
                for (int work = 0; work < 128 && socket.Poll(0, SelectMode.SelectRead); work++)
                {
                    EndPoint from = new IPEndPoint(IPAddress.Any, 0); int count = socket.ReceiveFrom(bytes, ref from);
                    bool fromServer = from.Equals(server); if (!fromServer) client = (IPEndPoint)from;
                    if (!fromServer && !dropped && count > 6 && bytes[5] == 6) { dropped = true; continue; }
                    var target = fromServer ? client : server; if (target != null) socket.SendTo(bytes, 0, count, SocketFlags.None, target);
                }
            }
            public void Dispose() => socket.Dispose();
        }
        private static void Pump(Func<bool> ready, Action step)
        { var watch = Stopwatch.StartNew(); while (!ready() && watch.Elapsed < TimeSpan.FromSeconds(8)) { step(); Thread.Sleep(1); } Assert.IsTrue(ready(), "Transport metrics condition timed out."); }
    }
}
