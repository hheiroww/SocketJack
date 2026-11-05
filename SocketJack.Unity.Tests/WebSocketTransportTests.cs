using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Multiplayer.Transport;

namespace SocketJack.Multiplayer.Tests
{
    [TestClass]
    public sealed class WebSocketTransportTests
    {
        [TestMethod]
        public void RealWebSocketsPreserveDeliveryMetadataMainThreadAdmissionAndReconnect()
        {
            int ownerThread = Environment.CurrentManagedThreadId;
            var options = new TransportOptions { ConnectionValidator = (endpoint, bytes) => { Assert.AreEqual(ownerThread, Environment.CurrentManagedThreadId); return bytes.SequenceEqual(new byte[] { 42 }); }, HeartbeatInterval = TimeSpan.FromMilliseconds(20), PeerTimeout = TimeSpan.FromSeconds(2) };
            using var server = new WebSocketGameTransport(options);
            using var client = new WebSocketGameTransport(new TransportOptions { HeartbeatInterval = TimeSpan.FromMilliseconds(20), PeerTimeout = TimeSpan.FromSeconds(2) });
            var received = new List<TransportEvent>(); server.EventReceived += value => { Assert.AreEqual(ownerThread, Environment.CurrentManagedThreadId); received.Add(value); };
            server.StartServer(new IPEndPoint(IPAddress.Loopback, 0)); client.StartClient(server.LocalEndPoint, new byte[] { 42 });
            Pump(server, client, () => client.LocalPeerId != 0); ulong first = client.LocalPeerId;
            foreach (var delivery in new[] { DeliveryMode.Unreliable, DeliveryMode.UnreliableSequenced, DeliveryMode.ReliableOrdered, DeliveryMode.ReliableUnordered })
                Assert.IsTrue(client.Send(0, new ArraySegment<byte>(new byte[] { (byte)delivery, 9 }), delivery, 3));
            Pump(server, client, () => received.Count(e => e.Type == TransportEventType.Data) == 4);
            foreach (var delivery in new[] { DeliveryMode.Unreliable, DeliveryMode.UnreliableSequenced, DeliveryMode.ReliableOrdered, DeliveryMode.ReliableUnordered })
                Assert.IsTrue(received.Any(e => e.Type == TransportEventType.Data && e.Delivery == delivery && e.Channel == 3 && e.Payload.SequenceEqual(new byte[] { (byte)delivery, 9 })));
            Pump(server, client, () => client.TryGetPeerStatistics(0, out double rtt, out _) && rtt > 0);
            Assert.IsTrue(client.BytesSent > 0 && client.BytesReceived > 0);
            client.Stop(); Pump(server, null, () => server.PeerCount == 0);
            client.StartClient(server.LocalEndPoint, new byte[] { 42 }); Pump(server, client, () => client.LocalPeerId != 0);
            Assert.AreNotEqual(first, client.LocalPeerId);
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => client.Send(0, new ArraySegment<byte>(new byte[client.MaxPayloadSize + 1])));
        }

        [TestMethod]
        public void AdmissionAndSettingsMismatchFailBeforeConnection()
        {
            using var server = new WebSocketGameTransport(new TransportOptions { ConnectionValidator = (endpoint, data) => false });
            using var denied = new WebSocketGameTransport(); server.StartServer(new IPEndPoint(IPAddress.Loopback, 0));
            denied.StartClient(server.LocalEndPoint); Pump(server, denied, () => !denied.IsRunning);
            Assert.AreEqual(0, server.PeerCount);
            using var otherServer = new WebSocketGameTransport(); using var mismatch = new WebSocketGameTransport(new TransportOptions { Channels = 5 });
            otherServer.StartServer(new IPEndPoint(IPAddress.Loopback, 0)); mismatch.StartClient(otherServer.LocalEndPoint);
            Pump(otherServer, mismatch, () => !mismatch.IsRunning); Assert.AreEqual(0, otherServer.PeerCount);
        }

        [TestMethod]
        public void LargeSessionStatesAndLateJoinWorkOverSelectedWebSocketTransport()
        {
            var options = new NetworkSessionOptions { MaxStateBytes = 8192, MaxMessageBytes = 8192, SnapshotRate = 1 };
            using var server = new NetworkServer(new WebSocketGameTransport(), options);
            using var client = new NetworkClient(new WebSocketGameTransport(), options);
            using var late = new NetworkClient(new WebSocketGameTransport(), options);
            server.Start(new IPEndPoint(IPAddress.Loopback, 0)); client.Connect(server.Transport.LocalEndPoint);
            void PumpSession(Func<bool> done)
            { var watch = Stopwatch.StartNew(); while (!done() && watch.Elapsed < TimeSpan.FromSeconds(8)) { server.Update(.01); client.Update(.01); late.Update(.01); Thread.Sleep(1); } Assert.IsTrue(done()); }
            PumpSession(() => client.IsConnected);
            byte[] baseline = Enumerable.Range(0, 6000).Select(i => (byte)(i * 13)).ToArray(); var obj = server.Spawn(1, client.LocalPeerId, state: baseline);
            PumpSession(() => client.Objects.ContainsKey(obj.Id)); CollectionAssert.AreEqual(baseline, client.Objects[obj.Id].State);
            byte[] changed = Enumerable.Range(0, 6100).Select(i => (byte)(i * 17)).ToArray(); server.SetState(obj.Id, changed);
            PumpSession(() => client.Objects[obj.Id].State.Length == changed.Length);
            late.Connect(server.Transport.LocalEndPoint); PumpSession(() => late.Objects.ContainsKey(obj.Id)); CollectionAssert.AreEqual(changed, late.Objects[obj.Id].State);
            Assert.AreEqual((TransportType)2, server.Transport.Type);
        }

        [TestMethod]
        public async Task CallerOwnedListenerAndTlsRemainUsableWhenGameStops()
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder(); names.AddDnsName("localhost"); names.AddIpAddress(IPAddress.Loopback); request.CertificateExtensions.Add(names.Build());
            using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
            // Schannel on supported Windows hosts requires importing the test key through PFX.
            using var certificate = new X509Certificate2(generated.Export(X509ContentType.Pfx));
            await using var listener = new WebSocketListener(new WebSocketListenerOptions { Certificate = certificate });
            using var server = new WebSocketGameTransport(listener: listener);
            listener.UpgradeAuthorizer = (incoming, token) => Task.FromResult(incoming.Path == "/ws/game");
            listener.WebSocketAccepted = server.AcceptWebSocketAsync; listener.HttpHandler = (incoming, token) => Task.FromResult(WebSocketHttpResponse.Text("website"));
            listener.Start(new IPEndPoint(IPAddress.Loopback, 0)); server.StartServer(new IPEndPoint(IPAddress.Loopback, 0));
            using var client = new WebSocketGameTransport(clientUri: new Uri("wss://localhost:" + listener.LocalEndPoint.Port + "/ws/game"));
            Exception connectionError = null;
            Exception serverError = null; listener.HandlerError += error => serverError = error;
            client.ClientConnector = async (uri, token) => { var socket = new ClientWebSocket(); socket.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true; try { await socket.ConnectAsync(uri, token); return socket; } catch (Exception error) { connectionError = error; throw; } };
            client.StartClient(listener.LocalEndPoint);
            var clock = Stopwatch.StartNew();
            while (client.LocalPeerId == 0 && client.IsRunning && clock.Elapsed < TimeSpan.FromSeconds(8)) { server.Poll(); client.Poll(); await Task.Delay(1); }
            Assert.AreNotEqual(0UL, client.LocalPeerId, serverError?.ToString() ?? connectionError?.ToString() ?? "TLS game handshake timed out.");
            server.Stop(); Assert.IsTrue(listener.IsRunning, "Game transport must never stop a caller-owned website listener.");
            Pump(null, client, () => !client.IsRunning); await listener.StopAsync(); Assert.AreEqual(0, listener.ConnectionCount);
        }

        [TestMethod]
        public void ReliableBackpressureDoesNotAcceptOrDeliverRejectedData()
        {
            using var server = new WebSocketGameTransport(); using var client = new WebSocketGameTransport(new TransportOptions { MaxPendingReliablePerPeer = 1 });
            BlockingSocket blocked = null;
            client.ClientConnector = async (uri, token) => { var real = new ClientWebSocket(); await real.ConnectAsync(uri, token); return blocked = new BlockingSocket(real); };
            int data = 0; server.EventReceived += value => { if (value.Type == TransportEventType.Data) { Assert.AreEqual(1, value.Payload[0]); data++; } };
            server.StartServer(new IPEndPoint(IPAddress.Loopback, 0)); client.StartClient(server.LocalEndPoint); Pump(server, client, () => client.LocalPeerId != 0);
            blocked.Pause = true;
            Assert.IsTrue(client.Send(0, new ArraySegment<byte>(new byte[] { 1 })));
            Pump(server, client, () => blocked.Blocked.Task.IsCompleted);
            Assert.IsFalse(client.Send(0, new ArraySegment<byte>(new byte[] { 2 })));
            blocked.Release.TrySetResult(true); Pump(server, client, () => data == 1);
            for (int i = 0; i < 5; i++) { server.Poll(); client.Poll(); Thread.Sleep(1); }
            Assert.AreEqual(1, data); Assert.AreEqual(1L, client.SendsRejected);
        }
        private sealed class BlockingSocket : WebSocket
        {
            private readonly WebSocket inner;
            public bool Pause;
            public readonly TaskCompletionSource<bool> Blocked = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal BlockingSocket(WebSocket inner) { this.inner = inner; }
            public override WebSocketCloseStatus? CloseStatus => inner.CloseStatus;
            public override string CloseStatusDescription => inner.CloseStatusDescription;
            public override WebSocketState State => inner.State;
            public override string SubProtocol => inner.SubProtocol;
            public override void Abort() => inner.Abort();
            public override Task CloseAsync(WebSocketCloseStatus closeStatus, string description, CancellationToken token) => inner.CloseAsync(closeStatus, description, token);
            public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string description, CancellationToken token) => inner.CloseOutputAsync(closeStatus, description, token);
            public override void Dispose() { Release.TrySetResult(true); inner.Dispose(); }
            public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken token) => inner.ReceiveAsync(buffer, token);
            public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken token)
            { if (Pause && buffer.Count > 1 && buffer.Array[buffer.Offset + 1] == 3) { Blocked.TrySetResult(true); await Release.Task.WaitAsync(token); } await inner.SendAsync(buffer, type, end, token); }
        }
        private static void Pump(IGameTransport server, IGameTransport client, Func<bool> ready)
        { var watch = Stopwatch.StartNew(); while (!ready() && watch.Elapsed < TimeSpan.FromSeconds(8)) { server?.Poll(); client?.Poll(); Thread.Sleep(1); } Assert.IsTrue(ready(), "WebSocket condition timed out."); }
    }
}
