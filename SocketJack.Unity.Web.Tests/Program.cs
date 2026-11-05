using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SocketJack.Multiplayer.Web;
using SocketJack.Multiplayer.Transport;

static class Program
{
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    static int checks;
    static string Password() => "Sj!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    static void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL " + name); checks++; Console.WriteLine("PASS " + name); }
    static async Task Main(string[] args)
    {
        string stage = Argument(args, "--staging", Path.Combine(Path.GetTempPath(), "SocketJackWebAcceptance")); Directory.CreateDirectory(stage);
        string database = Path.Combine(stage, "accounts.sqlite");
        if (args.Contains("--serve")) { await Serve(stage, database, args); return; }
        using var store = new SqliteAccountStore(database);
        await using (var server = new SocketJackWebServer(store, store, new SocketJackWebOptions { ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0), ServerName = "SocketJack acceptance" }))
        {
            server.RegisterGameLink("item", id => id == "sword", _ => "Trusted sword"); server.Start();
            var origin = new Uri("http://127.0.0.1:" + server.LocalEndPoint.Port + "/"); using var http = new HttpClient { BaseAddress = origin };
            var website = await http.GetAsync("/"); string html = await website.Content.ReadAsStringAsync();
            Check(website.IsSuccessStatusCode && html.Contains("Communities") && html.Contains("/app.js") && website.Headers.Contains("Content-Security-Policy"), "embedded website and security headers");
            Check((await http.GetByteArrayAsync("/logo.png")).Length > 1000 && (await http.GetStringAsync("/app.css")).Contains("border-radius:0"), "bundled logo and square theme");
            Check(!(await Call(http, "diagnostics")).GetProperty("ok").GetBoolean(), "diagnostics requires authentication");
            string adminPassword = Password(), alicePassword = Password(), bobPassword = Password();
            var bootstrap = await Call(http, "bootstrap", new { code = server.BootstrapCode, username = "operator", password = adminPassword, displayName = "Operator" });
            Check(bootstrap.GetProperty("ok").GetBoolean(), "one-time loopback operator bootstrap"); string admin = Data(bootstrap).GetProperty("token").GetString();
            Check(!(await Call(http, "bootstrap", new { code = server.BootstrapCode, username = "another_operator", password = adminPassword })).GetProperty("ok").GetBoolean(), "bootstrap cannot create another operator");
            var alice = Data(await Call(http, "register", new { username = "alice", password = alicePassword, displayName = "Alice" }));
            var bob = Data(await Call(http, "register", new { username = "bob", password = bobPassword, displayName = "Bob" }));
            string at = alice.GetProperty("token").GetString(), bt = bob.GetProperty("token").GetString(); string aid = alice.GetProperty("user").GetProperty("id").GetString(), bid = bob.GetProperty("user").GetProperty("id").GetString();
            Check(at.Length > 32 && bt.Length > 32 && aid != bid, "account registration and independent sessions");
            var duplicate = await Call(http, "register", new { username = "ALICE", password = Password() }); Check(!duplicate.GetProperty("ok").GetBoolean(), "case-normalized duplicate usernames rejected");
            var wrong = await Call(http, "login", new { username = "alice", password = Password() }); Check(!wrong.GetProperty("ok").GetBoolean(), "wrong password rejected");
            Check(!(await Call(http, "diagnostics", null, at)).GetProperty("ok").GetBoolean() && (await Call(http, "diagnostics", null, admin)).GetProperty("ok").GetBoolean(), "operator diagnostics permission");
            using var bobSocket = new ClientWebSocket(); await bobSocket.ConnectAsync(new Uri("ws://127.0.0.1:" + server.LocalEndPoint.Port + "/ws/api"), CancellationToken.None);
            await Send(bobSocket, new { id = "resume", op = "resume", data = new { token = bt } }); Check((await Receive(bobSocket)).GetProperty("ok").GetBoolean(), "WebSocket session resume");
            var global = Data(await Call(http, "chat/send", new { channel = "global", text = "Hello, shared world" }, at)); var live = await ReceiveEvent(bobSocket, "chat.message");
            Check(live.GetProperty("data").GetProperty("id").GetString() == global.GetProperty("id").GetString(), "live global chat delivered across authenticated clients");
            Check(!(await Call(http, "chat/send", new { channel = "combat", text = "Forged victory" }, at)).GetProperty("ok").GetBoolean(), "player cannot forge trusted combat events");
            var combat = server.PublishCombat("Trusted combat result", new[] { bid });
            var combatLive = await ReceiveEvent(bobSocket, "chat.message");
            Check(combatLive.GetProperty("data").GetProperty("id").GetString() == combat.Id.ToString() && combatLive.GetProperty("data").GetProperty("channel").GetString() == "combat", "trusted combat feed delivered live");
            Check(Data(await Call(http, "chat/history", new { channel = "combat" }, bt)).GetArrayLength() == 1 && Data(await Call(http, "chat/history", new { channel = "combat" }, at)).GetArrayLength() == 0, "trusted combat feed respects explicit recipients in history");
            var linked = Data(await Call(http, "chat/send", new { channel = "trade", text = "Selling [item:sword|Untrusted label]" }, at));
            Check(linked.GetProperty("links")[0].GetProperty("label").GetString() == "Trusted sword", "registered game-link label is server controlled");
            Check(!(await Call(http, "chat/send", new { channel = "global", text = "[item:missing]" }, at)).GetProperty("ok").GetBoolean(), "unknown game links rejected");
            var community = Data(await Call(http, "communities/create", new { name = "Builders", description = "Acceptance Community" }, at)); string cid = community.GetProperty("id").GetString();
            var invite = Data(await Call(http, "communities/invite", new { communityId = cid, targetUsername = "bob" }, at));
            Check((await Call(http, "communities/respond", new { invitationId = invite.GetProperty("id").GetString(), accept = true }, bt)).GetProperty("ok").GetBoolean(), "Community invitation acceptance");
            var communities = Data(await Call(http, "communities", null, bt)); Check(communities.GetProperty("communities")[0].GetProperty("members").GetArrayLength() == 2, "Community membership baseline");
            Check(!(await Call(http, "communities/role", new { communityId = cid, targetId = aid, role = "member" }, bt)).GetProperty("ok").GetBoolean(), "Community role escalation rejected");
            var group = Data(await Call(http, "groups/create", new { kind = "party" }, at)); string gid = group.GetProperty("id").GetString();
            var groupInvite = Data(await Call(http, "groups/invite", new { groupId = gid, targetUsername = "bob" }, at));
            Check((await Call(http, "groups/respond", new { invitationId = groupInvite.GetProperty("id").GetString(), accept = true }, bt)).GetProperty("ok").GetBoolean(), "party invitation acceptance");
            Check((await Call(http, "chat/send", new { channel = "party", scopeId = gid, text = "Party ready" }, bt)).GetProperty("ok").GetBoolean(), "member-authorized party chat");
            Check(!(await Call(http, "chat/send", new { channel = "party", scopeId = gid, text = "Not a member" }, admin)).GetProperty("ok").GetBoolean(), "nonmember party chat rejected");
            server.Presence.UpdateGamePresence(server.Accounts.FindById(aid), "world", 1, new System.Numerics.Vector3(0, 0, 0));
            server.Presence.UpdateGamePresence(server.Accounts.FindById(bid), "world", 1, new System.Numerics.Vector3(2, 0, 0));
            var nearby = Data(await Call(http, "chat/send", new { channel = "proximity", text = "Nearby only" }, at));
            var history = Data(await Call(http, "chat/history", new { channel = "proximity", limit = 100 }, bt));
            Check(history.EnumerateArray().Any(message => message.GetProperty("id").GetString() == nearby.GetProperty("id").GetString()), "proximity uses trusted game presence");
            Check(!(await Call(http, "chat/send", new { channel = "proximity", text = "No game presence" }, admin)).GetProperty("ok").GetBoolean(), "web-only account cannot spoof proximity presence");
            var ticket = Data(await Call(http, "game/ticket", new { }, at)).GetProperty("connectionData").GetString(); byte[] connectionData = Encoding.ASCII.GetBytes(ticket);
            Check(server.ValidateGameConnection(new IPEndPoint(IPAddress.Loopback, 1234), connectionData) && !server.ValidateGameConnection(new IPEndPoint(IPAddress.Loopback, 1234), connectionData) && server.BindAcceptedGameConnection(connectionData)?.Id == aid && server.BindAcceptedGameConnection(connectionData) == null, "short-lived game ticket admits and binds exactly once");
            Check((await Call(http, "chat/moderate", new { targetUsername = "bob", action = "mute", reason = "Acceptance" }, admin)).GetProperty("ok").GetBoolean(), "operator chat mute");
            Check(!(await Call(http, "chat/send", new { channel = "global", text = "Muted" }, bt)).GetProperty("ok").GetBoolean(), "muted account cannot send");
            await Call(http, "chat/moderate", new { targetUsername = "bob", action = "unmute" }, admin);
            using (var malicious = new HttpRequestMessage(HttpMethod.Get, "api/v1/status")) { malicious.Headers.Add("Origin", "https://untrusted.invalid"); Check((await http.SendAsync(malicious)).StatusCode == HttpStatusCode.Forbidden, "cross-origin HTTP requests rejected"); }
            await Call(http, "accounts/reset-password", new { targetUsername = "bob", password = Password() }, admin);
            Check(!(await Call(http, "me", null, bt)).GetProperty("ok").GetBoolean(), "password reset revokes sessions");
            using var portable = new SocketJackWebClient(origin); await portable.LoginAsync("alice", alicePassword); await portable.StartRealtimeAsync();
            await Task.WhenAll(portable.StartRealtimeAsync(), portable.StartRealtimeAsync()); Check(portable.IsLive, "overlapping realtime restarts do not cancel the new session's pending authentication");
            var delivered = new TaskCompletionSource<WebChatMessage>(TaskCreationOptions.RunContinuationsAsynchronously); portable.ChatMessageReceived += message => delivered.TrySetResult(message);
            var portableMessage = await portable.SendChatAsync("community", "Portable client", cid); Check(portableMessage.SenderId == aid && portable.IsLive, "portable HTTP and WebSocket client operates against real server");
            var buffered = await delivered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check(buffered.DeliveryEpoch == portable.SessionEpoch && !JsonSerializer.Serialize(buffered).Contains("DeliveryEpoch"), "live message carries receiving-session identity only locally");
            await portable.LogoutAsync(); Check(portable.User == null && !portable.IsLive, "portable logout clears live session");
            await portable.LoginAsync("alice", alicePassword);
            Check(buffered.DeliveryEpoch != portable.SessionEpoch, "buffered private message from earlier login is distinguishable after same-account sign-in");
            portable.PreserveSynchronizationContext = true; await portable.StartRealtimeAsync();
            var simultaneous = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => portable.LiveRequestAsync("me", new { })));
            Check(simultaneous.All(reply => reply.GetProperty("id").GetString() == aid), "inline continuation mode supports concurrent authenticated realtime requests");
            await portable.LogoutAsync();
            await server.StopAsync(); Check(!server.IsRunning, "web server stops cleanly"); server.Start(); Check(server.IsRunning, "web server restarts cleanly"); await server.StopAsync();
            var snapshot = store.Load(); Check(snapshot.Communities.Count == 1 && snapshot.Groups.Count == 1 && store.FindById(aid) != null, "SQLite social and account persistence");
        }
        bool rejectedUnsafe = false; try { await using var unsafeServer = new SocketJackWebServer(store, store, new SocketJackWebOptions { ListenEndPoint = new IPEndPoint(IPAddress.Any, 0) }); } catch (ArgumentException) { rejectedUnsafe = true; }
        Check(rejectedUnsafe, "network account hosting requires TLS");
        await TlsCheck(stage);
        await FederationCheck(stage);
        await RelayAndCustomSocketCheck(stage);
        File.WriteAllText(Path.Combine(stage, "acceptance-receipt.json"), JsonSerializer.Serialize(new { result = "pass", checks, checkedUtc = DateTime.UtcNow, database, boundary = "Real loopback HTTP/HTTPS/WebSocket/WSS and SQLite; no public deployment" }, Json));
        Console.WriteLine("WEB_ACCEPTANCE_PASS " + checks);
    }
    static async Task TlsCheck(string stage)
    {
        using var rsa = RSA.Create(2048); var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName("localhost"); names.AddIpAddress(IPAddress.Loopback); request.CertificateExtensions.Add(names.Build());
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        using var certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null);
        using var store = new SqliteAccountStore(Path.Combine(stage, "tls-accounts.sqlite"));
        await using var server = new SocketJackWebServer(store, store, new SocketJackWebOptions { ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0), Certificate = certificate }); server.Start();
        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, peer, _, _) => peer?.Thumbprint == certificate.Thumbprint };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://localhost:" + server.LocalEndPoint.Port + "/") };
        var login = Data(await Call(http, "register", new { username = "secure_user", password = Password(), displayName = "Secure" }));
        using var socket = new ClientWebSocket(); socket.Options.RemoteCertificateValidationCallback = (_, peer, _, _) => peer?.GetCertHashString() == certificate.Thumbprint;
        await socket.ConnectAsync(new Uri("wss://localhost:" + server.LocalEndPoint.Port + "/ws/api"), CancellationToken.None);
        await Send(socket, new { id = "tls", op = "resume", data = new { token = login.GetProperty("token").GetString() } });
        Check((await Receive(socket)).GetProperty("ok").GetBoolean(), "HTTPS registration and WSS authenticated session with certificate pinning");
    }
    static async Task FederationCheck(string stage)
    {
        using var hubStore = new SqliteAccountStore(Path.Combine(stage, "hub.sqlite"));
        await using var hub = new SocketJackWebServer(hubStore, hubStore, new SocketJackWebOptions { ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0), NodeId = "hub", ServerName = "Canonical hub" }); hub.Start();
        var hubUri = new Uri("http://127.0.0.1:" + hub.LocalEndPoint.Port + "/"); using var hubHttp = new HttpClient { BaseAddress = hubUri };
        var op = Data(await Call(hubHttp, "bootstrap", new { code = hub.BootstrapCode, username = "hub_operator", password = Password() })); string admin = op.GetProperty("token").GetString();
        var enrollment = Data(await Call(hubHttp, "nodes/issue-link", new { }, admin)); string linkToken = enrollment.GetProperty("linkToken").GetString();
        using var leafStore = new SqliteAccountStore(Path.Combine(stage, "leaf.sqlite"));
        await using var leaf = new SocketJackWebServer(leafStore, leafStore, new SocketJackWebOptions { ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0), NodeId = "world-two", ServerName = "Linked world", HubEndPoint = hubUri, EnrollmentToken = linkToken });
        await leaf.StartAsync(); Check(leaf.IsLinkedToHub, "linked server exchanges one-time enrollment and authenticates node WebSocket");
        Check(!(await Call(hubHttp, "nodes/enroll", new { nodeId = "unauthorized-copy", name = "Rejected", linkToken })).GetProperty("ok").GetBoolean(), "server enrollment token cannot be reused");
        var leafUri = new Uri("http://127.0.0.1:" + leaf.LocalEndPoint.Port + "/"); using var fromHub = new SocketJackWebClient(hubUri); using var fromLeaf = new SocketJackWebClient(leafUri);
        await fromHub.RegisterAsync("hub_player", Password(), "Hub player"); await fromLeaf.RegisterAsync("leaf_player", Password(), "Leaf player");
        await fromHub.StartRealtimeAsync(); await fromLeaf.StartRealtimeAsync();
        var received = new TaskCompletionSource<WebChatMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        fromLeaf.ChatMessageReceived += message => { if (message.Text == "Across linked servers") received.TrySetResult(message); };
        var sent = await fromHub.SendChatAsync("global", "Across linked servers"); var observed = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Check(observed.Id == sent.Id, "deployment-wide Global crosses hub and leaf WebSocket bridge");
        var accountOnHub = await fromHub.RequestAsync("communities/create", new { name = "Linked Builders" }); string communityId = accountOnHub.GetProperty("id").GetString();
        var invitation = await fromHub.RequestAsync("communities/invite", new { communityId, targetUsername = "leaf_player" });
        await fromLeaf.RequestAsync("communities/respond", new { invitationId = invitation.GetProperty("id").GetString(), accept = true });
        var baseline = await fromLeaf.RequestAsync("communities"); Check(baseline.GetProperty("communities")[0].GetProperty("members").GetArrayLength() == 2, "linked servers share canonical account and Community state");
        byte[] ticket = await fromLeaf.GetGameConnectionDataAsync("world-two");
        Check(leaf.ValidateGameConnection(new IPEndPoint(IPAddress.Loopback, 4321), ticket) && leaf.BindAcceptedGameConnection(ticket)?.Id == fromLeaf.User.Id, "hub waits for leaf ticket installation before game admission");
        leaf.PublishNodePresence(new FederationPresenceUpdate { Players = new System.Collections.Generic.List<FederationPresence> { new FederationPresence { AccountId = fromLeaf.User.Id, WorldId = "shared-world", RoomId = 1, X = 1, Y = 0, Z = 0 } } });
        for (int i = 0; i < 30 && (!hub.Presence.TryGetPresence(fromLeaf.User.Id, out var presence) || !presence.HasGamePresence); i++) await Task.Delay(100);
        Check(hub.Presence.TryGetPresence(fromLeaf.User.Id, out var gamePresence) && gamePresence.HasGamePresence && gamePresence.WorldId == "shared-world", "linked server publishes authoritative game presence");
        var leafAudience = await leaf.ResolveVoiceAudienceAsync(fromLeaf.User.Id, SocketJack.Multiplayer.Chat.ChatChannel.Community, communityId);
        Check(leafAudience.Length == 1 && leafAudience[0] == fromLeaf.User.Id, "native leaf voice resolves canonical Community permissions and limits delivery to its own present accounts");
        bool deniedOtherNode = false;
        try { await leaf.ResolveVoiceAudienceAsync(fromHub.User.Id, SocketJack.Multiplayer.Chat.ChatChannel.Global); }
        catch (WebAccountException) { deniedOtherNode = true; }
        Check(deniedOtherNode, "leaf cannot query native voice permissions for an account without its active game presence");
        await leaf.StopAsync(); await leaf.StartAsync(); Check(leaf.IsLinkedToHub, "linked server restarts from persisted node credentials");
        await Call(hubHttp, "nodes/revoke", new { nodeId = "world-two" }, admin);
        for (int i = 0; i < 30 && leaf.IsLinkedToHub; i++) await Task.Delay(100);
        Check(!leaf.IsLinkedToHub, "operator can revoke linked-server credentials");
    }
    static async Task RelayAndCustomSocketCheck(string stage)
    {
        using var store = new SqliteAccountStore(Path.Combine(stage, "relay.sqlite"));
        await using var server = new SocketJackWebServer(store, store, new SocketJackWebOptions { ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0), EnableRelay = true, NodeId = "relay" });
        string authenticatedId = null;
        server.RegisterAuthenticatedWebSocketHandler("/ws/voice-test", async (user, socket, token) =>
        {
            authenticatedId = user.Id; var bytes = new byte[100]; var message = await socket.ReceiveAsync(new ArraySegment<byte>(bytes), token);
            if (message.MessageType == WebSocketMessageType.Binary) await socket.SendAsync(new ArraySegment<byte>(bytes, 0, message.Count), WebSocketMessageType.Binary, true, token);
            await Task.Delay(Timeout.Infinite, token);
        });
        server.Start(); var origin = new Uri("http://127.0.0.1:" + server.LocalEndPoint.Port + "/"); using var http = new HttpClient { BaseAddress = origin };
        var host = Data(await Call(http, "register", new { username = "relay_host", password = Password() }));
        var client = Data(await Call(http, "register", new { username = "relay_client", password = Password() })); string ht = host.GetProperty("token").GetString(), ct = client.GetProperty("token").GetString();
        using var role = new ClientWebSocket(); await role.ConnectAsync(new Uri("ws://127.0.0.1:" + server.LocalEndPoint.Port + "/ws/voice-test"), CancellationToken.None);
        await Send(role, new { token = ht }); byte[] binary = { 3, 1, 4, 1, 5 }; await role.SendAsync(new ArraySegment<byte>(binary), WebSocketMessageType.Binary, true, CancellationToken.None);
        byte[] echo = new byte[100]; using (var timeout = new CancellationTokenSource(10000)) { var response = await role.ReceiveAsync(new ArraySegment<byte>(echo), timeout.Token); Check(response.MessageType == WebSocketMessageType.Binary && echo.Take(response.Count).SequenceEqual(binary) && authenticatedId == host.GetProperty("user").GetProperty("id").GetString(), "custom binary WebSocket role authenticates account before handing frames to voice"); }
        var room = Data(await Call(http, "relay/create", new { transport = "websocket", name = "Acceptance relay" }, ht)); string session = room.GetProperty("sessionId").GetString();
        var join = Data(await Call(http, "relay/join", new { sessionId = session }, ct)); var relayUri = new Uri("ws://127.0.0.1:" + server.LocalEndPoint.Port + "/ws/relay");
        using var gameServer = new WebSocketGameTransport(); gameServer.StartServer(new IPEndPoint(IPAddress.Loopback, 0));
        await using var tunnel = await WebSocketRelayHost.ConnectAsync(gameServer, relayUri, room.GetProperty("ticket").GetString());
        using var gameClient = new WebSocketGameTransport(clientUri: relayUri) { ClientConnector = WebSocketRelayService.CreateClientConnector(join.GetProperty("ticket").GetString()) };
        byte[] observed = null; gameServer.EventReceived += item => { if (item.Type == TransportEventType.Data) observed = item.Payload; };
        gameClient.StartClient(relayUri); await Pump(() => gameClient.LocalPeerId != 0, gameServer, gameClient);
        Check(gameClient.Send(0, new ArraySegment<byte>(binary), DeliveryMode.ReliableUnordered, 2), "account-issued WebSocket relay accepts game send"); await Pump(() => observed != null, gameServer, gameClient);
        Check(observed.SequenceEqual(binary), "account-authenticated WebSocket relay delivers native game data");
        using var replay = new WebSocketGameTransport(clientUri: relayUri) { ClientConnector = WebSocketRelayService.CreateClientConnector(join.GetProperty("ticket").GetString()) }; replay.StartClient(relayUri); await Pump(() => !replay.IsRunning, gameServer, gameClient, replay); Check(replay.LocalPeerId == 0, "relay join ticket cannot be reused");
        var udpRoom = Data(await Call(http, "relay/create", new { transport = "udp" }, ht)); string udpSession = udpRoom.GetProperty("sessionId").GetString();
        var udpJoin = Data(await Call(http, "relay/join", new { sessionId = udpSession }, ct));
        var relayEndpoint = new IPEndPoint(IPAddress.Parse(udpRoom.GetProperty("host").GetString()), udpRoom.GetProperty("port").GetInt32());
        using var udpServer = new UdpGameTransport(DatagramRelayGameTransport.ReserveOverhead()); udpServer.StartServer(new IPEndPoint(IPAddress.Loopback, 0));
        using var hostBridge = new DatagramRelayHostBridge(Allocation(udpRoom), relayEndpoint, udpServer.LocalEndPoint); await hostBridge.StartAsync();
        using var clientBridge = new DatagramRelayClientBridge(Allocation(udpJoin), relayEndpoint); await clientBridge.StartAsync();
        using var udpClient = new DatagramRelayGameTransport(clientBridge); byte[] udpObserved = null; udpServer.EventReceived += item => { if (item.Type == TransportEventType.Data) udpObserved = item.Payload; };
        udpClient.StartClient(relayEndpoint); await Pump(() => udpClient.LocalPeerId != 0, udpServer, udpClient);
        Check(udpClient.Send(0, new ArraySegment<byte>(binary)), "account-issued UDP relay accepts game send"); await Pump(() => udpObserved != null, udpServer, udpClient); Check(udpObserved.SequenceEqual(binary), "account-authenticated UDP relay delivers native game data");
        var renewed = Data(await Call(http, "relay/renew", new { sessionId = udpSession, allocationId = udpJoin.GetProperty("allocationId").GetString() }, ct));
        Check(renewed.GetProperty("allocationId").GetString() == udpJoin.GetProperty("allocationId").GetString() && renewed.GetProperty("secret").GetString() == udpJoin.GetProperty("secret").GetString(), "authenticated UDP allocation renewal preserves routing identity and key");
        Check(!(await Call(http, "relay/close", new { sessionId = session }, ct)).GetProperty("ok").GetBoolean(), "client cannot close another account's relay session");
        int before = server.RelayAllocationCount; await Call(http, "logout", new { }, ct); Check(server.RelayAllocationCount < before, "logout revokes owned UDP relay allocations");
        await Call(http, "relay/close", new { sessionId = session }, ht); await Pump(() => !gameClient.IsRunning, gameServer, gameClient); Check(!tunnel.IsConnected, "host closes authenticated relay session and active routes");
        await hostBridge.StopAsync(); await clientBridge.StopAsync();
    }
    static DatagramRelayAllocation Allocation(JsonElement data) => new(ulong.Parse(data.GetProperty("allocationId").GetString()), data.GetProperty("sessionId").GetString(), data.GetProperty("isHost").GetBoolean(), Convert.FromBase64String(data.GetProperty("secret").GetString()), data.GetProperty("expiresUtc").GetDateTime());
    static async Task Pump(Func<bool> completed, params IGameTransport[] transports)
    {
        using var timeout = new CancellationTokenSource(10000);
        while (!completed()) { timeout.Token.ThrowIfCancellationRequested(); foreach (var transport in transports) transport.Poll(); await Task.Delay(1, timeout.Token); }
    }
    static JsonElement Data(JsonElement value) { if (!value.GetProperty("ok").GetBoolean()) throw new Exception("API failure: " + value.GetProperty("error").GetString()); return value.GetProperty("data").Clone(); }
    static async Task<JsonElement> Call(HttpClient http, string operation, object data = null, string bearer = null)
    {
        using var request = new HttpRequestMessage(data == null ? HttpMethod.Get : HttpMethod.Post, "api/v1/" + operation);
        if (bearer != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (data != null) request.Content = new StringContent(JsonSerializer.Serialize(data, Json), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request); return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
    }
    static Task Send(ClientWebSocket socket, object value) => socket.SendAsync(new ArraySegment<byte>(JsonSerializer.SerializeToUtf8Bytes(value, Json)), WebSocketMessageType.Text, true, CancellationToken.None);
    static async Task<JsonElement> Receive(ClientWebSocket socket)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)); var bytes = new byte[65536]; using var memory = new MemoryStream(); WebSocketReceiveResult result;
        do { result = await socket.ReceiveAsync(new ArraySegment<byte>(bytes), timeout.Token); if (result.MessageType == WebSocketMessageType.Close) throw new Exception("Unexpected WebSocket close"); memory.Write(bytes, 0, result.Count); } while (!result.EndOfMessage);
        return JsonSerializer.Deserialize<JsonElement>(memory.ToArray());
    }
    static async Task<JsonElement> ReceiveEvent(ClientWebSocket socket, string name) { for (int i = 0; i < 20; i++) { var item = await Receive(socket); if (item.TryGetProperty("event", out var value) && value.GetString() == name) return item; } throw new Exception("Event not observed"); }
    static async Task Serve(string stage, string database, string[] args)
    {
        using var store = new SqliteAccountStore(database); await using var server = new SocketJackWebServer(store, store, new SocketJackWebOptions { ListenEndPoint = new IPEndPoint(IPAddress.Loopback, int.Parse(Argument(args, "--port", "7788"))), ServerName = "SocketJack · Second Release" });
        string password = Password(); var account = await server.Accounts.RegisterAsync("acceptance", password, "Acceptance Player");
        File.WriteAllText(Path.Combine(stage, "browser-fixture.private.json"), JsonSerializer.Serialize(new { username = "acceptance", password, bootstrapCode = server.BootstrapCode, endpoint = "http://127.0.0.1:" + server.LocalEndPoint?.Port }, Json));
        server.Start(); Console.WriteLine("BROWSER_HOST_READY " + server.LocalEndPoint.Port); await Task.Delay(Timeout.Infinite);
    }
    static string Argument(string[] args, string key, string fallback) { int index = Array.IndexOf(args, key); return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback; }
}
