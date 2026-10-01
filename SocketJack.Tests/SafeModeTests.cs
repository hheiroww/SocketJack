using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Net;
using SocketJack.Serialization;
using System.Net;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using JsonWire = SocketJack.Serialization.Json.JsonSerializer;

namespace SocketJack.Tests;

[TestClass]
public sealed class SafeModeTests {
    public sealed class Message { public int Number { get; set; } }
    public sealed class Unapproved { public static int Created; public Unapproved() { Interlocked.Increment(ref Created); } public string Value { get; set; } }
    public sealed class Container { public Unapproved Child { get; set; } }
    static NetworkOptions Options(bool binary = false) {
        var options = new NetworkOptions { UsePeerToPeer = false, Logging = false, EnablePatternCache = false, AutoReconnect = false };
        if (binary) options.Serializer = new BinarySerializer();
        options.VerifiedAssemblies.Add(typeof(Message).Assembly, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(Message).Assembly.Location))));
        options.Authorization.AnonymousMessageTypes.Add(typeof(Message));
        options.Authorization.AnonymousMessageTypes.Add(typeof(int));
        options.Authorization.AnonymousMessageTypes.Add(typeof(string));
        return options;
    }
    static int Port() { var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); listener.Start(); int p = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return p; }

    [TestMethod]
    public void DefaultConnectionsHaveIndependentAllowlistsPinsAndPolicies() {
        var original = NetworkOptions.DefaultOptions;
        try {
            NetworkOptions.DefaultOptions = Options();
            NetworkOptions.DefaultOptions.Authorization.AuthorizeMessage = _ => true;
            var first = NetworkOptions.NewDefault(); var second = NetworkOptions.NewDefault();
            first.Whitelist.Add(typeof(Message));
            first.Authorization.AnonymousMessageTypes.Add(typeof(Container));
            first.Authorization.RequireAuthentication = false;
            first.EndpointSecurity.Enabled = false;
            Assert.IsFalse(second.Whitelist.Contains(typeof(Message)));
            Assert.IsFalse(second.Authorization.AnonymousMessageTypes.Contains(typeof(Container)));
            Assert.IsTrue(second.Authorization.RequireAuthentication);
            Assert.IsTrue(second.EndpointSecurity.Enabled);
            Assert.AreNotSame(first.VerifiedAssemblies, second.VerifiedAssemblies);
            Assert.IsNotNull(second.Authorization.AuthorizeMessage);
            second.VerifiedAssemblies.Verify(typeof(Message).Assembly);
        } finally { NetworkOptions.DefaultOptions = original; }
    }

    [TestMethod]
    public void SafeModeDefaultsOnAndRequiresTrustedLocalDllPin() {
        Assert.IsTrue(new NetworkOptions().SafeMode);
        var options = new NetworkOptions(); options.Whitelist.Add(typeof(Message));
        Assert.ThrowsException<SecurityException>(() => SafeModeHandshake.Create(options));
        Assert.ThrowsException<SecurityException>(() => options.VerifiedAssemblies.Add(typeof(Message).Assembly, new string('0', 64)));
        Assert.IsFalse(new TypeList(new[] { typeof(Dictionary<string, string>) }).Contains(typeof(Dictionary<string, Unapproved>)));
    }
    [DataTestMethod][DataRow(false)][DataRow(true)]
    public async Task MatchingTcpHandshakeAllowsTypedMessages(bool binary) {
        int port = Port(); using var server = new SocketJack.Net.TcpServer(Options(binary), port);
        using var client = new SocketJack.Net.TcpClient(Options(binary));
        var got = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.RegisterCallback<Message>(e => got.TrySetResult(e.Object.Number));
        client.Options.Whitelist.Add(typeof(Message));
        Assert.IsTrue(server.Listen()); Assert.IsTrue(await client.Connect("127.0.0.1", port));
        client.Send(new Message { Number = 42 });
        Assert.AreEqual(42, await got.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }
    [DataTestMethod][DataRow("md5")][DataRow("sha256")][DataRow("missing")][DataRow("duplicate")][DataRow("legacy")][DataRow("oversize")]
    public async Task InvalidTcpHandshakeClosesBeforeCallbacksAndRevealsNoHash(string attack) {
        int port = Port(); var options = Options(); using var server = new SocketJack.Net.TcpServer(options, port);
        int connected = 0, messages = 0; server.ClientConnected += _ => Interlocked.Increment(ref connected);
        server.RegisterCallback<int>(_ => Interlocked.Increment(ref messages)); Assert.IsTrue(server.Listen());
        byte[] hello = SafeModeHandshake.Create(options);
        var claims = JsonNode.Parse(hello.AsSpan(5))!.AsArray();
        if (attack == "md5") claims[0]!["Md5"] = new string('0', 32);
        if (attack == "sha256") claims[0]!["Sha256"] = new string('0', 64);
        if (attack == "missing") claims.RemoveAt(0);
        if (attack == "duplicate") claims[1] = claims[0]!.DeepClone();
        hello = Encoding.UTF8.GetBytes("SJSM1" + claims.ToJsonString());
        if (attack == "legacy") hello = options.Serializer.Serialize(new Wrapper(42, server));
        byte[] frame = SafeModeHandshake.Frame(hello);
        if (attack == "oversize") frame = Encoding.ASCII.GetBytes("000000000100000");
        using var raw = new System.Net.Sockets.TcpClient(); await raw.ConnectAsync(IPAddress.Loopback, port);
        await raw.GetStream().WriteAsync(frame);
        var reply = new byte[1024];
        int read = -1;
        try { read = await raw.GetStream().ReadAsync(reply).AsTask().WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (IOException) { read = 0; }
        Assert.AreEqual(0, read, "Invalid clients must be closed without disclosing expected hashes.");
        Assert.AreEqual(0, connected); Assert.AreEqual(0, messages);
    }
    [TestMethod]
    public async Task ValidRawClientReceivesOnlyFixedAcceptanceMarker() {
        int port = Port(); var options = Options(); using var server = new SocketJack.Net.TcpServer(options, port); Assert.IsTrue(server.Listen());
        using var raw = new System.Net.Sockets.TcpClient(); await raw.ConnectAsync(IPAddress.Loopback, port);
        await raw.GetStream().WriteAsync(SafeModeHandshake.Frame(SafeModeHandshake.Create(options)));
        var reply = new byte[5]; await raw.GetStream().ReadExactlyAsync(reply);
        CollectionAssert.AreEqual(SafeModeHandshake.Accepted, reply);
        Assert.AreEqual(0, raw.Available);
    }
    [TestMethod]
    public void JsonRedirectCannotConstructUnapprovedOrBlacklistedType() {
        var options = Options(); using var socket = new SocketJack.Net.UdpClient(options);
        options.Blacklist.Add(typeof(Unapproved)); Unapproved.Created = 0;
        byte[] data = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { Type = typeof(Unapproved).AssemblyQualifiedName, Value = new { Value = "harmless" }, Recipient = "#ALL#" });
        Assert.ThrowsException<TypeNotAllowedException>(() => options.Serializer.DeserializeRedirect(socket, data));
        Assert.AreEqual(0, Unapproved.Created);
    }
    [TestMethod]
    public void NestedUnapprovedTypeAndMalformedPropertyAreRejected() {
        var options = Options(); using var socket = new SocketJack.Net.UdpClient(options);
        options.Whitelist.Add(typeof(Container)); options.Whitelist.Add(typeof(Message)); Unapproved.Created = 0;
        byte[] data = Encoding.UTF8.GetBytes("{\"Type\":\"" + typeof(Container).FullName + "\",\"value\":{\"Child\":{\"Value\":\"test\"}}}");
        Assert.ThrowsException<TypeNotAllowedException>(() => options.Serializer.Deserialize(data).Unwrap(socket));
        Assert.AreEqual(0, Unapproved.Created);
        data = Encoding.UTF8.GetBytes("{\"Type\":\"" + typeof(Message).FullName + "\",\"value\":{\"Number\":\"wrong-type\"}}");
        Assert.ThrowsException<Exception>(() => options.Serializer.Deserialize(data).Unwrap(socket));
    }
    [DataTestMethod][DataRow(false)][DataRow(true)]
    public async Task MatchingUdpHandshakeAllowsMessages(bool reliable) {
        int port = Port(); var so = Options(); var co = Options();
        if (reliable) so.UdpMode = co.UdpMode = UdpMode.UDP_Reliable;
        using var server = new SocketJack.Net.UdpServer(so, port); using var client = new SocketJack.Net.UdpClient(co);
        var got = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.RegisterCallback<int>(e => got.TrySetResult(e.Object)); Assert.IsTrue(server.Listen());
        Assert.IsTrue(await client.Connect("127.0.0.1", port)); client.Send(27);
        Assert.AreEqual(27, await got.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }
    [TestMethod]
    public async Task ExplicitUnsafeModeRetainsLegacyTcp() {
        int port = Port(); var so = Options(); so.SafeMode = false; var co = Options(); co.SafeMode = false;
        using var server = new SocketJack.Net.TcpServer(so, port); using var client = new SocketJack.Net.TcpClient(co);
        Assert.IsTrue(server.Listen()); Assert.IsTrue(await client.Connect("127.0.0.1", port));
    }
    [TestMethod]
    public async Task MatchingMutableTcpHandshakeAllowsMessages() {
        int port = Port(); using var server = new MutableTcpServer(Options(), port); using var client = new SocketJack.Net.TcpClient(Options());
        var got = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.RegisterCallback<int>(e => got.TrySetResult(e.Object)); Assert.IsTrue(server.Listen());
        Assert.IsTrue(await client.Connect("127.0.0.1", port)); client.Send(35);
        Assert.AreEqual(35, await got.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }
    [TestMethod]
    public async Task MatchingWebSocketUpgradeAllowsMessages() {
        int port = Port(); using var server = new SocketJack.Net.WebSockets.WebSocketServer(port) { Options = Options() };
        using var client = new SocketJack.Net.WebSockets.WebSocketClient(Options());
        var got = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.RegisterCallback<int>(e => got.TrySetResult(e.Object)); Assert.IsTrue(server.Listen());
        Assert.IsTrue(await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/")));
        client.Send(36); Assert.AreEqual(36, await got.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }
    [TestMethod]
    public async Task InvalidWebSocketUpgradeNeverAnnouncesClient() {
        int port = Port(); using var server = new SocketJack.Net.WebSockets.WebSocketServer(port) { Options = Options() };
        int connected = 0; server.ClientConnected += _ => Interlocked.Increment(ref connected); Assert.IsTrue(server.Listen());
        using var raw = new System.Net.Sockets.TcpClient(); await raw.ConnectAsync(IPAddress.Loopback, port);
        await raw.GetStream().WriteAsync(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: localhost\r\nConnection: Upgrade\r\nUpgrade: websocket\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n\r\n"));
        var reply = new byte[1024]; int n = await raw.GetStream().ReadAsync(reply).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.AreEqual(0, n); Assert.AreEqual(0, connected);
    }
    [TestMethod]
    public async Task InvalidOrdinaryUdpHandshakeNeverCreatesClient() {
        int port = Port(); using var server = new SocketJack.Net.UdpServer(Options(), port);
        int connected = 0; server.ClientConnected += _ => Interlocked.Increment(ref connected); Assert.IsTrue(server.Listen());
        using var raw = new System.Net.Sockets.UdpClient();
        byte[] request = Encoding.ASCII.GetBytes("SJSM1[]"); await raw.SendAsync(request, request.Length, new IPEndPoint(IPAddress.Loopback, port));
        var reply = await raw.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.AreEqual("SJNO1", Encoding.ASCII.GetString(reply.Buffer)); Assert.AreEqual(0, connected); Assert.AreEqual(0, server.Clients.Count);
    }
    [TestMethod]
    public void BoundedDecompressionRejectsExpansionAboveLimit() {
        var options = Options(); options.MaximumBufferSize = 1024;
        byte[] bomb = options.CompressionAlgorithm.Compress(new byte[32768]);
        Assert.ThrowsException<InvalidDataException>(() => UdpReliableObjects.Decompress(bomb, options));
    }

    [DataTestMethod][DataRow(false)][DataRow(true)]
    public async Task TypedHttpRequiresValidManifestBeforeHandler(bool valid) {
        int port = Port(); var options = Options(); using var server = new HttpServer(options, port);
        int invoked = 0; server.Map<int>("POST", "/typed", (_, body, _, _) => { Interlocked.Increment(ref invoked); return "ok"; });
        Assert.IsTrue(server.Listen());
        using var raw = new System.Net.Sockets.TcpClient(); await raw.ConnectAsync(IPAddress.Loopback, port);
        byte[] body = options.Serializer.Serialize(new Wrapper(5, server));
        string claim = valid ? SafeModeHandshake.HeaderName + ": " + Convert.ToBase64String(SafeModeHandshake.Create(options)) + "\r\n" : "";
        string headers = "POST /typed HTTP/1.1\r\nHost: localhost\r\nContent-Type: application/json\r\nContent-Length: " + body.Length + "\r\n" + claim + "Connection: close\r\n\r\n";
        await raw.GetStream().WriteAsync(Encoding.ASCII.GetBytes(headers).Concat(body).ToArray());
        var response = new byte[8192]; int n = 0;
        try { n = await raw.GetStream().ReadAsync(response).AsTask().WaitAsync(TimeSpan.FromSeconds(5)); } catch (IOException) { }
        if (valid) { Assert.IsTrue(n > 0); Assert.AreEqual(1, invoked); }
        else { Assert.AreEqual(0, n); Assert.AreEqual(0, invoked); }
    }
    [TestMethod]
    public void ChangingAllowlistRequiresNewHandshake() {
        var options = Options(); using var socket = new SocketJack.Net.UdpClient(options);
        var connection = new NetworkConnection(socket, null);
        SafeModeHandshake.MarkVerified(connection, options);
        options.Whitelist.Add(typeof(Message));
        Assert.ThrowsException<SecurityException>(() => SafeModeHandshake.RequireVerified(connection, options));
    }

    [TestMethod]
    public async Task InvalidReliableUdpManifestClosesBeforeClientEvent() {
        int port = Port(); var options = Options(); options.UdpMode = UdpMode.UDP_Reliable;
        using var server = new SocketJack.Net.UdpServer(options, port);
        int connected = 0; server.ClientConnected += _ => Interlocked.Increment(ref connected); Assert.IsTrue(server.Listen());
        using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Dgram, System.Net.Sockets.ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var rejected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var transport = new UdpReliableTransport(socket, false, options, _ => {}, (_, _, _) => {}, (_, _) => rejected.TrySetResult(true));
        transport.Start(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var peer = await transport.Connect(new IPEndPoint(IPAddress.Loopback, port), timeout.Token);
        byte[] hello = SafeModeHandshake.Create(options); var claims = JsonNode.Parse(hello.AsSpan(5))!.AsArray(); claims[0]!["Md5"] = new string('0', 32);
        await peer.Send(Encoding.UTF8.GetBytes("SJSM1" + claims.ToJsonString()), 0, false, false, timeout.Token);
        Assert.IsTrue(await rejected.Task.WaitAsync(TimeSpan.FromSeconds(3))); Assert.AreEqual(0, connected); Assert.AreEqual(0, server.Clients.Count);
    }
    [TestMethod]
    public async Task LargeWebSocketFrameHonorsNetworkByteOrder() {
        int port = Port(); using var server = new SocketJack.Net.WebSockets.WebSocketServer(port) { Options = Options() };
        using var client = new SocketJack.Net.WebSockets.WebSocketClient(Options());
        var got = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.RegisterCallback<string>(e => got.TrySetResult(e.Object.Length)); Assert.IsTrue(server.Listen());
        Assert.IsTrue(await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/")));
        client.Send(new string('x', 100000)); Assert.AreEqual(100000, await got.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

}
