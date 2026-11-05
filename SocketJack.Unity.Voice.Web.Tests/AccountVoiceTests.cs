using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Net.WebSockets;
using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Multiplayer.Web;
using SocketJack.Multiplayer.Transport;

namespace SocketJack.Multiplayer.Voice.Web.Tests
{
    [TestClass]
    public sealed class AccountVoiceTests
    {
        private static string Stage(string name)
        {
            string root = Environment.GetEnvironmentVariable("SOCKETJACK_VOICE_TEST_OUTPUT") ?? Path.Combine(Path.GetTempPath(), "SocketJack.Voice.Acceptance");
            string directory = Path.Combine(root, name); Directory.CreateDirectory(directory); return Path.Combine(directory, Guid.NewGuid().ToString("N") + ".sqlite");
        }
        private static string Password() => Guid.NewGuid().ToString("N") + "aA7!";
        private static async Task Until(Func<bool> condition)
        { using var timeout = new CancellationTokenSource(10000); while (!condition()) await Task.Delay(10, timeout.Token); }
        private static Uri Origin(SocketJackWebServer server) => new Uri("http://127.0.0.1:" + server.LocalEndPoint.Port + "/");
        private static byte[] Audio(int frame = 0) => new OpusVoiceCodec().Encode(Enumerable.Range(0, 320).Select(i => (float)(.2 * Math.Sin(2 * Math.PI * 440 * (frame * 320 + i) / 16000))).ToArray());
        [TestMethod]
        public async Task AuthenticatedBinaryVoiceEnforcesGroupsIgnorePresenceAndImmediateSessionRevocation()
        {
            using var store = new SqliteAccountStore(Stage("permissions")); await using var server = new SocketJackWebServer(store, store, new SocketJackWebOptions { ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0) });
            using var relay = new AccountVoiceRelay(server); server.Start();
            using var alice = new SocketJackWebClient(Origin(server)); using var bob = new SocketJackWebClient(Origin(server)); using var cara = new SocketJackWebClient(Origin(server));
            await alice.RegisterAsync("alice", Password(), "Alice"); await bob.RegisterAsync("bob", Password(), "Bob"); await cara.RegisterAsync("cara", Password(), "Cara");
            using var a = new AccountVoiceClient(alice); using var b = new AccountVoiceClient(bob); using var c = new AccountVoiceClient(cara);
            await a.ConnectAsync(); await b.ConnectAsync(); await c.ConnectAsync();
            var received = new ConcurrentQueue<AccountVoiceFrame>(); b.FrameReceived += received.Enqueue; var third = new ConcurrentQueue<AccountVoiceFrame>(); c.FrameReceived += third.Enqueue;
            Assert.IsTrue(a.SendEncoded(Audio(), VoiceChannel.Global)); await Until(() => received.Count == 1 && third.Count == 1);
            Assert.AreEqual(alice.User.Id, received.Single().AccountId); var pcm = new float[320]; Assert.AreEqual(320, new OpusVoiceCodec().Decode(received.Single().EncodedAudio, pcm));
            var group = await alice.RequestAsync("groups/create", new { kind = "party", name = "Voice party" }); string groupId = group.GetProperty("id").GetString();
            var invitation = await alice.RequestAsync("groups/invite", new { groupId, targetId = bob.User.Id }); await bob.RequestAsync("groups/respond", new { invitationId = invitation.GetProperty("id").GetString(), accept = true });
            Assert.IsTrue(a.SendEncoded(Audio(1), VoiceChannel.Party, groupId)); await Until(() => received.Count == 2); Assert.AreEqual(1, third.Count);
            long before = relay.RejectedFrames; Assert.IsTrue(c.SendEncoded(Audio(), VoiceChannel.Party, groupId)); await Until(() => relay.RejectedFrames > before); Assert.AreEqual(2, received.Count);
            await bob.RequestAsync("chat/ignore", new { targetId = alice.User.Id, ignore = true }); Assert.IsTrue(a.SendEncoded(Audio(2), VoiceChannel.Global)); await Until(() => third.Count == 2); Assert.AreEqual(2, received.Count);
            await bob.RequestAsync("chat/ignore", new { targetId = alice.User.Id, ignore = false });
            before = relay.RejectedFrames; Assert.IsTrue(a.SendEncoded(Audio(3), VoiceChannel.Proximity)); await Until(() => relay.RejectedFrames > before);
            server.Presence.UpdateGamePresence(alice.User, "world", 0, Vector3.Zero); server.Presence.UpdateGamePresence(bob.User, "world", 0, new Vector3(3, 0, 0)); server.Presence.UpdateGamePresence(cara.User, "world", 1, Vector3.Zero);
            Assert.IsTrue(a.SendEncoded(Audio(4), VoiceChannel.Proximity)); await Until(() => received.Count == 3); Assert.AreEqual(2, third.Count);
            var history = await bob.GetHistoryAsync("global"); Assert.AreEqual(0, history.Count, "Voice must not create text chat history.");
            await bob.LogoutAsync(); await Until(() => !b.IsConnected); Assert.IsTrue(a.IsConnected && c.IsConnected);
            await a.StopAsync(); await b.StopAsync(); await c.StopAsync();
        }
        [TestMethod]
        public async Task ReconnectingSenderGetsANewCanonicalSessionEvenWhenItsStreamCounterRestarts()
        {
            using var store = new SqliteAccountStore(Stage("reconnect")); await using var server = new SocketJackWebServer(store, store, new SocketJackWebOptions { ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0) }); using var relay = new AccountVoiceRelay(server); server.Start();
            using var alice = new SocketJackWebClient(Origin(server)); using var bob = new SocketJackWebClient(Origin(server)); await alice.RegisterAsync("alice", Password(), "Alice"); await bob.RegisterAsync("bob", Password(), "Bob");
            using var first = new AccountVoiceClient(alice); using var receiver = new AccountVoiceClient(bob); await first.ConnectAsync(); await receiver.ConnectAsync(); var frames = new ConcurrentQueue<AccountVoiceFrame>(); receiver.FrameReceived += frames.Enqueue;
            for (int i = 0; i < 3; i++) { first.ResetStream(); Assert.IsTrue(first.SendEncoded(Audio(i), VoiceChannel.Global)); int count = i + 1; await Until(() => frames.Count == count); }
            var previous = frames.Last(); await first.StopAsync(); using var replacement = new AccountVoiceClient(alice); await replacement.ConnectAsync(); Assert.IsTrue(replacement.SendEncoded(Audio(), VoiceChannel.Global)); await Until(() => frames.Count == 4);
            var current = frames.Last(); Assert.AreEqual(previous.AccountId, current.AccountId); Assert.AreNotEqual(previous.SessionId, current.SessionId); Assert.IsTrue(current.StreamId < previous.StreamId); await replacement.StopAsync(); await receiver.StopAsync();
        }
        [TestMethod]
        public async Task DelayedOldConnectorCannotCloseANewerVoiceConnection()
        {
            using var store = new SqliteAccountStore(Stage("delayed-connect")); await using var server = new SocketJackWebServer(store, store, new SocketJackWebOptions { ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0) }); using var relay = new AccountVoiceRelay(server); server.Start();
            using var alice = new SocketJackWebClient(Origin(server)); using var bob = new SocketJackWebClient(Origin(server)); await alice.RegisterAsync("alice", Password(), "Alice"); await bob.RegisterAsync("bob", Password(), "Bob");
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); int attempts = 0;
            async Task<WebSocket> Connect(Uri endpoint, CancellationToken token)
            {
                var socket = new ClientWebSocket(); await socket.ConnectAsync(endpoint, CancellationToken.None);
                if (Interlocked.Increment(ref attempts) == 1) { entered.TrySetResult(true); await release.Task; } return socket;
            }
            using var sender = new AccountVoiceClient(alice) { SocketConnector = Connect }; using var receiver = new AccountVoiceClient(bob); await receiver.ConnectAsync();
            Task old = sender.ConnectAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); await sender.StopAsync(); await sender.ConnectAsync(); Assert.IsTrue(sender.IsConnected);
            release.TrySetResult(true); await Assert.ThrowsExceptionAsync<OperationCanceledException>(async () => await old); Assert.IsTrue(sender.IsConnected);
            int received = 0; receiver.FrameReceived += _ => Interlocked.Increment(ref received); Assert.IsTrue(sender.SendEncoded(Audio(), VoiceChannel.Global)); await Until(() => received == 1); Assert.IsTrue(sender.IsConnected); await sender.StopAsync(); await receiver.StopAsync();
        }
        [TestMethod]
        public async Task GlobalAndCommunityVoiceCrossAnEnrolledLeafThroughCanonicalHub()
        {
            using var hubStore = new SqliteAccountStore(Stage("hub")); await using var hub = new SocketJackWebServer(hubStore, hubStore, new SocketJackWebOptions { ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0), NodeId = "hub" });
            using var hubVoice = new AccountVoiceRelay(hub); hub.Start(); using var http = new HttpClient { BaseAddress = Origin(hub) };
            var admin = await Call(http, "bootstrap", new { code = hub.BootstrapCode, username = "operator", password = Password() });
            var grant = await Call(http, "nodes/issue-link", new { }, admin.GetProperty("token").GetString());
            using var leafStore = new SqliteAccountStore(Stage("leaf")); await using var leaf = new SocketJackWebServer(leafStore, leafStore, new SocketJackWebOptions { ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0), NodeId = "leaf", HubEndPoint = Origin(hub), EnrollmentToken = grant.GetProperty("linkToken").GetString() });
            using var leafVoice = new AccountVoiceRelay(leaf); await leaf.StartAsync(); Assert.IsTrue(leaf.IsLinkedToHub);
            using var alice = new SocketJackWebClient(Origin(hub)); using var bob = new SocketJackWebClient(Origin(leaf));
            await alice.RegisterAsync("alice", Password(), "Alice"); await bob.RegisterAsync("bob", Password(), "Bob"); using var a = new AccountVoiceClient(alice); using var b = new AccountVoiceClient(bob);
            await a.ConnectAsync(); await b.ConnectAsync(); var received = new ConcurrentQueue<AccountVoiceFrame>(); b.FrameReceived += received.Enqueue;
            Assert.IsTrue(a.SendEncoded(Audio(), VoiceChannel.Global)); await Until(() => received.Count == 1); Assert.AreEqual(alice.User.Id, received.Single().AccountId);
            var community = await alice.RequestAsync("communities/create", new { name = "Voice Community" }); string communityId = community.GetProperty("id").GetString();
            var invitation = await alice.RequestAsync("communities/invite", new { communityId, targetId = bob.User.Id }); await bob.RequestAsync("communities/respond", new { invitationId = invitation.GetProperty("id").GetString(), accept = true });
            Assert.IsTrue(a.SendEncoded(Audio(1), VoiceChannel.Community, communityId)); await Until(() => received.Count == 2); Assert.AreEqual(0, leafVoice.Connections); Assert.AreEqual(2, hubVoice.Connections);
            await a.StopAsync(); await b.StopAsync();
        }
        [DataTestMethod]
        [DataRow(false)] [DataRow(true)]
        public async Task AuthenticatedGameVoiceOnLeafUsesCanonicalPermissionsAndAnOwnerThreadCache(bool tcp)
        {
            using var hubStore = new SqliteAccountStore(Stage("game-hub")); await using var hub = new SocketJackWebServer(hubStore, hubStore, new SocketJackWebOptions { ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0), NodeId = "hub" });
            hub.Start(); using var http = new HttpClient { BaseAddress = Origin(hub) }; var admin = await Call(http, "bootstrap", new { code = hub.BootstrapCode, username = "operator", password = Password() });
            var grant = await Call(http, "nodes/issue-link", new { }, admin.GetProperty("token").GetString());
            using var leafStore = new SqliteAccountStore(Stage("game-leaf")); await using var leaf = new SocketJackWebServer(leafStore, leafStore, new SocketJackWebOptions { ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0), NodeId = "leaf", HubEndPoint = Origin(hub), EnrollmentToken = grant.GetProperty("linkToken").GetString() }); await leaf.StartAsync();
            using var alice = new SocketJackWebClient(Origin(hub)); using var bob = new SocketJackWebClient(Origin(leaf)); using var outsider = new SocketJackWebClient(Origin(hub));
            await alice.RegisterAsync("alice", Password(), "Alice"); await bob.RegisterAsync("bob", Password(), "Bob"); await outsider.RegisterAsync("outsider", Password(), "Outsider");
            var community = await alice.RequestAsync("communities/create", new { name = "Game voice community" }); string communityId = community.GetProperty("id").GetString();
            var invitation = await alice.RequestAsync("communities/invite", new { communityId, targetId = bob.User.Id }); await bob.RequestAsync("communities/respond", new { invitationId = invitation.GetProperty("id").GetString(), accept = true });
            byte[] aTicket = await alice.GetGameConnectionDataAsync("leaf"), bTicket = await bob.GetGameConnectionDataAsync("leaf");
            leaf.Presence.UpdateGamePresence(alice.User, "world", 0, Vector3.Zero); leaf.Presence.UpdateGamePresence(bob.User, "world", 0, Vector3.One);
            leaf.PublishNodePresence(new FederationPresenceUpdate { Players = new System.Collections.Generic.List<FederationPresence> {
                new FederationPresence { AccountId = alice.User.Id, WorldId = "world", RoomId = 0 }, new FederationPresence { AccountId = bob.User.Id, WorldId = "world", RoomId = 0, X = 1 } } });
            await Until(() => hub.Presence.TryGetPresence(alice.User.Id, out var first) && first.HasGamePresence && hub.Presence.TryGetPresence(bob.User.Id, out var second) && second.HasGamePresence);
            CollectionAssert.AreEquivalent(new[] { alice.User.Id, bob.User.Id }, await leaf.ResolveVoiceAudienceAsync(alice.User.Id, SocketJack.Multiplayer.Chat.ChatChannel.Community, communityId));
            await Assert.ThrowsExceptionAsync<WebAccountException>(() => leaf.ResolveVoiceAudienceAsync(outsider.User.Id, SocketJack.Multiplayer.Chat.ChatChannel.Global));
            await Task.Run(() =>
            {
                int ownerThread = Thread.CurrentThread.ManagedThreadId;
                var configuration = new TransportOptions { ConnectionValidator = leaf.ValidateGameConnection };
                IGameTransport Create(bool server = false) => tcp ? (IGameTransport)new TcpGameTransport(server ? configuration : null) : new UdpGameTransport(server ? configuration : null);
                using var game = new NetworkServer(Create(true)); using var a = new NetworkClient(Create()); using var b = new NetworkClient(Create());
                var mappings = new System.Collections.Generic.Dictionary<ulong, WebAccount>(); game.PeerConnected += peer => mappings[peer] = leaf.BindAcceptedGameConnection(game.GetConnectionData(peer));
                game.Start(new IPEndPoint(IPAddress.Loopback, 0)); a.Connect(game.Transport.LocalEndPoint, aTicket); b.Connect(game.Transport.LocalEndPoint, bTicket);
                using var membership = new WebVoiceMembershipProvider(() => { Assert.AreEqual(ownerThread, Thread.CurrentThread.ManagedThreadId); return leaf; }, peer => { Assert.AreEqual(ownerThread, Thread.CurrentThread.ManagedThreadId); return mappings.TryGetValue(peer, out var account) ? account : null; }, () => game.Peers);
                using var relay = new NetworkVoiceServer(game, membership); using var sender = new NetworkVoiceClient(a); using var receiver = new NetworkVoiceClient(b);
                int received = 0; receiver.FrameReceived += packet => { Assert.AreEqual(alice.User.Id, packet.AccountId); received++; };
                var clock = Stopwatch.StartNew(); void Pump() { relay.Update(clock.Elapsed.TotalSeconds); game.Update(.002); a.Update(.002); b.Update(.002); Thread.Sleep(2); }
                void Wait(Func<bool> condition) { double until = clock.Elapsed.TotalSeconds + 8; while (!condition() && clock.Elapsed.TotalSeconds < until) Pump(); Assert.IsTrue(condition(), "Leaf game voice condition timed out."); }
                Wait(() => a.IsConnected && b.IsConnected); var obj = game.Spawn(1, a.LocalPeerId); Wait(() => a.Objects.ContainsKey(obj.Id) && b.Objects.ContainsKey(obj.Id));
                double next = 0; Wait(() => { if (clock.Elapsed.TotalSeconds >= next) { Assert.IsTrue(sender.SendEncoded(obj.Id, Audio(), VoiceChannel.Community, communityId)); next = clock.Elapsed.TotalSeconds + .04; } return received > 0; });
                Assert.IsTrue(relay.ForwardedFrames > 0); Assert.IsTrue(membership.TryGetParticipant(a.LocalPeerId, out var speaker)); membership.Invalidate(); Assert.AreEqual(0, membership.ResolveAudience(speaker, VoiceChannel.Community, communityId, "").Count());
            });
        }
        [TestMethod]
        public async Task RealTlsVoiceUsesThePinnedTestCertificateWithoutDisablingDefaultProductionValidation()
        {
            using var rsa = RSA.Create(2048); var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1); var names = new SubjectAlternativeNameBuilder(); names.AddDnsName("localhost"); names.AddIpAddress(IPAddress.Loopback); request.CertificateExtensions.Add(names.Build());
            using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1)); using var certificate = new X509Certificate2(generated.Export(X509ContentType.Pfx));
            using var store = new SqliteAccountStore(Stage("tls")); await using var server = new SocketJackWebServer(store, store, new SocketJackWebOptions { ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0), Certificate = certificate }); using var relay = new AccountVoiceRelay(server); server.Start();
            var uri = new Uri("https://localhost:" + server.LocalEndPoint.Port + "/"); using var provider = new LocalAccountProvider(store); var first = await provider.RegisterAsync("alice", Password(), "Alice"); var second = await provider.RegisterAsync("bob", Password(), "Bob");
            using var aAccount = new SocketJackWebClient(uri); using var bAccount = new SocketJackWebClient(uri);
            async Task<string> Http(Uri target, string method, string body, string bearer, CancellationToken token)
            { using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert?.Thumbprint == certificate.Thumbprint }; using var client = new HttpClient(handler); using var message = new HttpRequestMessage(new HttpMethod(method), target); if (body != null) message.Content = new StringContent(body, Encoding.UTF8, "application/json"); if (bearer != null) message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer); using var response = await client.SendAsync(message, token); return await response.Content.ReadAsStringAsync(token); }
            aAccount.HttpRequestOverride = Http; bAccount.HttpRequestOverride = Http; await aAccount.ResumeAsync(first.Token); await bAccount.ResumeAsync(second.Token);
            async Task<WebSocket> Connect(Uri target, CancellationToken token) { var socket = new ClientWebSocket(); socket.Options.RemoteCertificateValidationCallback = (_, cert, _, _) => cert?.GetCertHashString() == certificate.Thumbprint; await socket.ConnectAsync(target, token); return socket; }
            using var a = new AccountVoiceClient(aAccount) { SocketConnector = Connect }; using var b = new AccountVoiceClient(bAccount) { SocketConnector = Connect }; await a.ConnectAsync(); await b.ConnectAsync();
            int received = 0; b.FrameReceived += _ => Interlocked.Increment(ref received); Assert.IsTrue(a.SendEncoded(Audio(), VoiceChannel.Global)); await Until(() => received == 1); await a.StopAsync(); await b.StopAsync();
        }
        private static async Task<JsonElement> Call(HttpClient client, string operation, object body, string bearer = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/" + operation); request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"); if (bearer != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            using var response = await client.SendAsync(request); var result = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync()); Assert.IsTrue(result.GetProperty("ok").GetBoolean()); return result.GetProperty("data").Clone();
        }
    }
}
