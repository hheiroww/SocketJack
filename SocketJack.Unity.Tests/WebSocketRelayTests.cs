using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Multiplayer.Transport;

namespace SocketJack.Multiplayer.Tests
{
    [TestClass]
    public sealed class WebSocketRelayTests
    {
        [TestMethod]
        public async Task AuthenticatedRelayPreservesGameMessagesAndIsolatesSessions()
        {
            await using var fixture = new RelayFixture();
            using var serverA = new WebSocketGameTransport(); using var serverB = new WebSocketGameTransport();
            serverA.StartServer(new IPEndPoint(IPAddress.Loopback, 0)); serverB.StartServer(new IPEndPoint(IPAddress.Loopback, 0));
            await using var hostA = await WebSocketRelayHost.ConnectAsync(serverA, fixture.Uri, "host-a");
            await using var hostB = await WebSocketRelayHost.ConnectAsync(serverB, fixture.Uri, "host-b");
            using var clientA = fixture.Client("client-a"); using var clientB = fixture.Client("client-b");
            var a = new List<TransportEvent>(); var b = new List<TransportEvent>(); serverA.EventReceived += a.Add; serverB.EventReceived += b.Add;
            clientA.StartClient(fixture.Uri); clientB.StartClient(fixture.Uri);
            await Pump(() => clientA.LocalPeerId != 0 && clientB.LocalPeerId != 0, serverA, serverB, clientA, clientB);
            Assert.IsTrue(clientA.Send(0, new ArraySegment<byte>(new byte[] { 7 }), DeliveryMode.ReliableUnordered, 2));
            Assert.IsTrue(clientB.Send(0, new ArraySegment<byte>(new byte[] { 9 }), DeliveryMode.ReliableOrdered, 3));
            await Pump(() => a.Any(x => x.Type == TransportEventType.Data) && b.Any(x => x.Type == TransportEventType.Data), serverA, serverB, clientA, clientB);
            CollectionAssert.AreEqual(new byte[] { 7 }, a.Single(x => x.Type == TransportEventType.Data).Payload);
            CollectionAssert.AreEqual(new byte[] { 9 }, b.Single(x => x.Type == TransportEventType.Data).Payload);
            Assert.AreEqual(DeliveryMode.ReliableUnordered, a.Single(x => x.Type == TransportEventType.Data).Delivery);
            Assert.AreEqual((byte)2, a.Single(x => x.Type == TransportEventType.Data).Channel);
            Assert.AreEqual(2, fixture.Service.SessionCount); Assert.IsTrue(fixture.Service.BytesForwarded > 0);
            fixture.Service.RevokeSession("a"); await Pump(() => !clientA.IsRunning && !hostA.IsConnected, serverA, serverB, clientA, clientB);
            Assert.IsTrue(clientB.IsRunning); Assert.AreEqual(1, fixture.Service.SessionCount);
            Assert.IsTrue(clientB.Send(0, new ArraySegment<byte>(new byte[] { 10 })));
            await Pump(() => b.Count(x => x.Type == TransportEventType.Data) == 2, serverA, serverB, clientB);
        }

        [TestMethod]
        public async Task LargeStatesLateJoinAndHostDisconnectWorkThroughRelay()
        {
            await using var fixture = new RelayFixture();
            var options = new NetworkSessionOptions { MaxStateBytes = 8192, MaxMessageBytes = 8192, SnapshotRate = 1 };
            var native = new WebSocketGameTransport();
            using var server = new NetworkServer(native, options);
            using var owner = new NetworkClient(fixture.Client("client-a"), options);
            using var late = new NetworkClient(fixture.Client("client-a"), options);
            server.Start(new IPEndPoint(IPAddress.Loopback, 0));
            await using var host = await WebSocketRelayHost.ConnectAsync(native, fixture.Uri, "host-a");
            void Tick() { server.Update(.01); owner.Update(.01); late.Update(.01); }
            async Task Until(Func<bool> done)
            { var watch = Stopwatch.StartNew(); while (!done() && watch.Elapsed < TimeSpan.FromSeconds(8)) { Tick(); await Task.Delay(1); } Assert.IsTrue(done()); }
            owner.Connect(fixture.Listener.LocalEndPoint); await Until(() => owner.IsConnected);
            byte[] first = Enumerable.Range(0, 6000).Select(i => (byte)(i * 13)).ToArray();
            var obj = server.Spawn(1, owner.LocalPeerId, state: first); await Until(() => owner.Objects.ContainsKey(obj.Id));
            CollectionAssert.AreEqual(first, owner.Objects[obj.Id].State);
            byte[] next = Enumerable.Range(0, 6100).Select(i => (byte)(i * 17)).ToArray(); server.SetState(obj.Id, next);
            late.Connect(fixture.Listener.LocalEndPoint); await Until(() => late.Objects.ContainsKey(obj.Id) && owner.Objects[obj.Id].State.Length == next.Length);
            CollectionAssert.AreEqual(next, late.Objects[obj.Id].State); Assert.AreEqual(2, host.ClientCount);
            host.Dispose(); await Until(() => !owner.IsConnected && !late.IsConnected);
            await fixture.Service.StopAsync(); Assert.AreEqual(0, fixture.Service.ConnectionCount); Assert.AreEqual(0, fixture.Service.SessionCount);
            Assert.IsTrue(fixture.Listener.IsRunning, "Stopping a relay must not stop the caller-owned website listener.");
        }

        [TestMethod]
        public async Task MissingExpiredWrongRoleAndCapacityTicketsCannotAllocateOrReplaceHost()
        {
            await using var fixture = new RelayFixture(new WebSocketRelayOptions { MaxSessions = 1, MaxClientsPerSession = 1 });
            using var server = new WebSocketGameTransport(); server.StartServer(new IPEndPoint(IPAddress.Loopback, 0));
            await Assert.ThrowsExceptionAsync<WebSocketException>(async () => { await using var denied = await WebSocketRelayHost.ConnectAsync(server, fixture.Uri, "missing"); });
            await Assert.ThrowsExceptionAsync<WebSocketException>(async () => { await using var denied = await WebSocketRelayHost.ConnectAsync(server, fixture.Uri, "expired"); });
            Assert.AreEqual(0, fixture.Service.SessionCount);
            await using var host = await WebSocketRelayHost.ConnectAsync(server, fixture.Uri, "host-a");
            await Assert.ThrowsExceptionAsync<WebSocketException>(async () => { await using var denied = await WebSocketRelayHost.ConnectAsync(server, fixture.Uri, "host-a"); });
            await Assert.ThrowsExceptionAsync<WebSocketException>(async () => { await using var denied = await WebSocketRelayHost.ConnectAsync(server, fixture.Uri, "host-b"); });
            using var wrongSession = fixture.Client("client-b"); wrongSession.StartClient(fixture.Uri);
            await Pump(() => !wrongSession.IsRunning, server, wrongSession); Assert.AreEqual(1, fixture.Service.SessionCount);
            using var first = fixture.Client("client-a"); first.StartClient(fixture.Uri); await Pump(() => first.LocalPeerId != 0, server, first);
            using var overflow = fixture.Client("client-a"); overflow.StartClient(fixture.Uri); await Pump(() => !overflow.IsRunning, server, first, overflow);
            Assert.AreEqual(1, server.PeerCount); Assert.IsTrue(host.IsConnected); Assert.AreEqual(1, host.ClientCount);
            first.Stop(); await Pump(() => host.ClientCount == 0, server);
            using var reconnect = fixture.Client("client-a"); reconnect.StartClient(fixture.Uri); await Pump(() => reconnect.LocalPeerId != 0, server, reconnect);
            Assert.IsTrue(fixture.Authorizations >= 9, "Every attempt must pass account-ticket authorization.");
        }

        [TestMethod]
        public async Task ControlFramesConsumeTheSameBoundedConnectionRateBudget()
        {
            await using var fixture = new RelayFixture(new WebSocketRelayOptions { MaxMessageBytes = 256, ClientBytesPerSecond = 256 });
            using var server = new WebSocketGameTransport(); server.StartServer(new IPEndPoint(IPAddress.Loopback, 0));
            await using var host = await WebSocketRelayHost.ConnectAsync(server, fixture.Uri, "host-a");
            using var socket = new ClientWebSocket(); await socket.ConnectAsync(fixture.Uri, CancellationToken.None);
            byte[] ticket = Encoding.ASCII.GetBytes("client-a"); var join = new byte[4 + ticket.Length]; join[0] = join[1] = 1; join[2] = (byte)ticket.Length; Buffer.BlockCopy(ticket, 0, join, 4, ticket.Length);
            await socket.SendAsync(new ArraySegment<byte>(join), WebSocketMessageType.Binary, true, CancellationToken.None);
            await socket.ReceiveAsync(new ArraySegment<byte>(new byte[32]), CancellationToken.None);
            var ping = new byte[10]; ping[0] = 1; ping[1] = 6;
            try { for (int i = 0; i < 100; i++) await socket.SendAsync(new ArraySegment<byte>(ping), WebSocketMessageType.Binary, true, CancellationToken.None); }
            catch (WebSocketException) { }
            await Pump(() => fixture.Service.ConnectionCount == 1 && host.ClientCount == 0, server);
            Assert.IsTrue(host.IsConnected, "A rate-limited client must not evict the host.");
        }

        [TestMethod]
        public async Task SpoofedRoutesMalformedFramesAndCanceledJoinsReleaseCapacity()
        {
            await using var fixture = new RelayFixture();
            using var server = new WebSocketGameTransport(); server.StartServer(new IPEndPoint(IPAddress.Loopback, 0));
            await using var host = await WebSocketRelayHost.ConnectAsync(server, fixture.Uri, "host-a");
            foreach (var bad in new[] { new byte[] { 1 }, new byte[] { 2, 4, 0, 0, 0, 0, 0, 0, 0, 0, 42 }, new byte[] { 1, 4, 1, 0, 0, 0, 0, 0, 0, 0, 42 }, new byte[1600] })
            {
                using var socket = new ClientWebSocket(); await socket.ConnectAsync(fixture.Uri, CancellationToken.None);
                byte[] ticket = Encoding.ASCII.GetBytes("client-a"); var join = new byte[4 + ticket.Length]; join[0] = join[1] = 1; join[2] = (byte)ticket.Length; Buffer.BlockCopy(ticket, 0, join, 4, ticket.Length);
                await socket.SendAsync(new ArraySegment<byte>(join), WebSocketMessageType.Binary, true, CancellationToken.None);
                var buffer = new byte[32]; var ready = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None); Assert.AreEqual(10, ready.Count);
                await socket.SendAsync(new ArraySegment<byte>(bad), WebSocketMessageType.Binary, true, CancellationToken.None);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { var closed = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token); Assert.AreEqual(WebSocketMessageType.Close, closed.MessageType); }
                catch (WebSocketException) { }
                await Pump(() => fixture.Service.ConnectionCount == 1 && host.ClientCount == 0, server);
            }
            using (var pending = new ClientWebSocket())
            {
                await pending.ConnectAsync(fixture.Uri, CancellationToken.None);
                await fixture.Service.StopAsync(); Assert.AreEqual(0, fixture.Service.ConnectionCount);
            }
            Assert.IsFalse(host.IsConnected);
        }

        private static async Task Pump(Func<bool> done, params IGameTransport[] transports)
        {
            var watch = Stopwatch.StartNew(); while (!done() && watch.Elapsed < TimeSpan.FromSeconds(8)) { foreach (var transport in transports) transport.Poll(); await Task.Delay(1); }
            Assert.IsTrue(done(), "Relay loopback condition timed out.");
        }
        private sealed class RelayFixture : IAsyncDisposable
        {
            internal WebSocketRelayService Service { get; }
            internal WebSocketListener Listener { get; }
            internal Uri Uri => new Uri("ws://127.0.0.1:" + Listener.LocalEndPoint.Port + "/ws/relay");
            internal int Authorizations;
            internal RelayFixture(WebSocketRelayOptions options = null)
            {
                Service = new WebSocketRelayService(options); Listener = new WebSocketListener();
                Service.AuthorizeTicket = ticket =>
                {
                    Interlocked.Increment(ref Authorizations);
                    if (ticket == "expired") return new RelayAccess("a", WebSocketRelayRole.Host, DateTimeOffset.UtcNow.AddSeconds(-1));
                    string session = ticket.EndsWith("-a", StringComparison.Ordinal) ? "a" : ticket.EndsWith("-b", StringComparison.Ordinal) ? "b" : null;
                    if (session == null) return null;
                    if (ticket.StartsWith("host-", StringComparison.Ordinal)) return new RelayAccess(session, WebSocketRelayRole.Host, DateTimeOffset.UtcNow.AddMinutes(1));
                    if (ticket.StartsWith("client-", StringComparison.Ordinal)) return new RelayAccess(session, WebSocketRelayRole.Client, DateTimeOffset.UtcNow.AddMinutes(1));
                    return null;
                };
                Listener.UpgradeAuthorizer = (request, token) => Task.FromResult(request.Path == "/ws/relay");
                Listener.WebSocketAccepted = Service.HandleWebSocketAsync; Listener.Start(new IPEndPoint(IPAddress.Loopback, 0));
            }
            internal WebSocketGameTransport Client(string ticket)
            { var transport = new WebSocketGameTransport(clientUri: Uri); transport.ClientConnector = WebSocketRelayService.CreateClientConnector(ticket); return transport; }
            public async ValueTask DisposeAsync() { await Service.DisposeAsync(); await Listener.DisposeAsync(); }
        }
    }
}
