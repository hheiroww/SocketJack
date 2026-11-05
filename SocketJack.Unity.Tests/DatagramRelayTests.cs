using System;
using System.Diagnostics;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Multiplayer.Transport;
namespace SocketJack.Multiplayer.Tests
{
    [TestClass]
    public sealed class DatagramRelayTests
    {
        [TestMethod]
        public async Task ReliableUdpAndLargeObjectsRetainIndependentPeersThroughRelay()
        {
            using var relay = new DatagramRelayServer(); relay.Start(new IPEndPoint(IPAddress.Loopback, 0));
            using var server = new NetworkServer(new UdpGameTransport(DatagramRelayGameTransport.ReserveOverhead()), new NetworkSessionOptions { MaxStateBytes = 8192, MaxMessageBytes = 8192 });
            server.Start(new IPEndPoint(IPAddress.Loopback, 0));
            using var host = new DatagramRelayHostBridge(relay.CreateAllocation("game", true), relay.LocalEndPoint, server.Transport.LocalEndPoint);
            await host.StartAsync();
            using var ab = new DatagramRelayClientBridge(relay.CreateAllocation("game", false), relay.LocalEndPoint);
            using var bb = new DatagramRelayClientBridge(relay.CreateAllocation("game", false), relay.LocalEndPoint);
            await ab.StartAsync(); await bb.StartAsync();
            using var a = new NetworkClient(new DatagramRelayGameTransport(ab), new NetworkSessionOptions { MaxStateBytes = 8192, MaxMessageBytes = 8192 });
            using var b = new NetworkClient(new DatagramRelayGameTransport(bb), new NetworkSessionOptions { MaxStateBytes = 8192, MaxMessageBytes = 8192 });
            a.Connect(new IPEndPoint(IPAddress.Loopback, 1)); b.Connect(new IPEndPoint(IPAddress.Loopback, 1));
            Pump(() => a.IsConnected && b.IsConnected, server, a, b);
            Assert.AreNotEqual(a.LocalPeerId, b.LocalPeerId); Assert.AreEqual(2, host.ConnectedGateways);
            var data = new byte[6000]; for (int i = 0; i < data.Length; i++) data[i] = (byte)(i % 251);
            var obj = server.Spawn(1, a.LocalPeerId, 0, data, false);
            Pump(() => a.Objects.ContainsKey(obj.Id) && b.Objects.ContainsKey(obj.Id), server, a, b);
            CollectionAssert.AreEqual(data, b.Objects[obj.Id].State);
            var changed = new byte[6100]; changed[6099] = 77; Assert.IsTrue(a.SendOwnerState(obj.Id, changed));
            Pump(() => b.Objects[obj.Id].State.Length == 6100 && b.Objects[obj.Id].State[6099] == 77, server, a, b);
            Assert.IsFalse(b.SendOwnerState(obj.Id, new byte[] { 1 }));
            Assert.AreEqual(48, ((ObjectFrameTransport)a.Transport).Budget.ExtraOverheadBytes);
            await ab.StopAsync(); await bb.StopAsync(); await host.StopAsync(); await relay.StopAsync();
        }
        [TestMethod]
        public async Task AdvertisedNativePayloadFitsExactRelayMtuAndRejectsNextByte()
        {
            using var relay = new DatagramRelayServer(); relay.Start(new IPEndPoint(IPAddress.Loopback, 0));
            using var server = new UdpGameTransport(DatagramRelayGameTransport.ReserveOverhead()); server.StartServer(new IPEndPoint(IPAddress.Loopback, 0));
            using var host = new DatagramRelayHostBridge(relay.CreateAllocation("budget", true), relay.LocalEndPoint, server.LocalEndPoint); await host.StartAsync();
            using var bridge = new DatagramRelayClientBridge(relay.CreateAllocation("budget", false), relay.LocalEndPoint); await bridge.StartAsync();
            using var client = new DatagramRelayGameTransport(bridge); int received = 0; ulong peer = 0;
            server.EventReceived += value => { if (value.Type == TransportEventType.Connected) peer = value.PeerId; if (value.Type == TransportEventType.Data) received = value.Payload.Length; };
            client.StartClient(new IPEndPoint(IPAddress.Loopback, 1));
            void Step() { server.Poll(); client.Poll(); Thread.Sleep(1); }
            Until(() => peer != 0 && client.LocalPeerId != 0, Step);
            Assert.AreEqual(862, client.MaxPayloadSize); Assert.AreEqual(998, client.Budget.GetPacketBytes(client.MaxPayloadSize));
            Assert.IsTrue(client.Send(0, new ArraySegment<byte>(new byte[client.MaxPayloadSize]), DeliveryMode.ReliableUnordered));
            Until(() => received == client.MaxPayloadSize, Step);
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => client.Send(0, new ArraySegment<byte>(new byte[client.MaxPayloadSize + 1])));
            await bridge.StopAsync(); await host.StopAsync(); await relay.StopAsync();
        }
        [TestMethod]
        public async Task AllocationRenewalRevocationAndRestartAreExplicit()
        {
            using var relay = new DatagramRelayServer(); relay.Start(new IPEndPoint(IPAddress.Loopback, 0));
            var host = relay.CreateAllocation("expiry", true, TimeSpan.FromSeconds(1));
            var client = relay.CreateAllocation("expiry", false, TimeSpan.FromMilliseconds(100));
            var renewed = relay.Renew(client.AllocationId, TimeSpan.FromSeconds(1));
            CollectionAssert.AreEqual(client.Secret, renewed.Secret); Assert.IsTrue(renewed.ExpiresUtc > client.ExpiresUtc);
            relay.Revoke(client.AllocationId); Assert.AreEqual(1, relay.AllocationCount);
            Assert.ThrowsException<InvalidOperationException>(() => relay.Renew(client.AllocationId));
            relay.CloseSession("expiry"); Assert.AreEqual(0, relay.AllocationCount);
            relay.Dispose(); Assert.ThrowsException<InvalidOperationException>(() => relay.Start(new IPEndPoint(IPAddress.Loopback, 0)));
            await relay.StopAsync(); relay.Start(new IPEndPoint(IPAddress.Loopback, 0)); Assert.IsTrue(relay.IsRunning); await relay.StopAsync();
        }
        private static void Pump(Func<bool> done, NetworkServer server, params NetworkClient[] clients)
            => Until(done, () => { server.Update(.01); foreach (var client in clients) client.Update(.01); Thread.Sleep(1); });
        private static void Until(Func<bool> done, Action step)
        { var clock = Stopwatch.StartNew(); while (!done() && clock.Elapsed < TimeSpan.FromSeconds(8)) step(); Assert.IsTrue(done(), "Actual UDP relay operation timed out."); }
    }
}
