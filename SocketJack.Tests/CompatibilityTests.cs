using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Net;
using SocketJack.Serialization;
using System.Net;
using System.Reflection;
using System.Runtime.Loader;
using System.Security;

namespace SocketJack.Tests;

[TestClass]
public class CompatibilityTests {
    static Assembly Load(string version) => new AssemblyLoadContext("contracts-" + Guid.NewGuid(), true)
        .LoadFromAssemblyPath(Path.Combine(AppContext.BaseDirectory, "Contracts", version, "Compatibility.Contracts.dll"));
    static NetworkOptions Options(Type type, bool binary = false) {
        var options = new NetworkOptions { UsePeerToPeer = false, UseCompression = false, EnablePatternCache = false, AutoReconnect = false };
        if (binary) options.Serializer = new BinarySerializer();
        options.Whitelist.Add(type);
        foreach (var enumType in type.Assembly.GetTypes().Where(t => t.IsEnum)) options.Whitelist.Add(enumType);
        options.Authorization.AnonymousMessageTypes.Add(type);
        return options;
    }
    static int Port() { using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port; }

    [DataTestMethod][DataRow(false)][DataRow(true)]
    public async Task DifferentDllVersionsExchangeCompatibleMessagesAndBlockOnlyMissingFeature(bool binary) {
        var oldAssembly = Load("V1"); var newAssembly = Load("V2");
        var oldType = oldAssembly.GetType("Versioned.Message"); var newType = newAssembly.GetType("Versioned.Message");
        Assert.AreNotEqual(VerifiedAssemblyRegistry.Fingerprint(oldAssembly, false), VerifiedAssemblyRegistry.Fingerprint(newAssembly, false));
        int port = Port(); var so = Options(oldType, binary); var co = Options(newType, binary);
        var feature = newAssembly.GetType("Versioned.NewFeature"); co.Whitelist.Add(feature);
        using var server = new SocketJack.Net.TcpServer(so, port); using var client = new SocketJack.Net.TcpClient(co);
        var received = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        var echoed = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.OnReceive += (ref IReceivedEventArgs e) => { if (e.Obj.GetType() == oldType) received.TrySetResult(e.Obj); };
        client.OnReceive += (ref IReceivedEventArgs e) => { if (e.Obj.GetType() == newType) echoed.TrySetResult(e.Obj); };
        FeatureDisabledEventArgs disabled = null; co.FeatureDisabled += (_, e) => disabled = e;
        Assert.IsTrue(server.Listen()); Assert.IsTrue(await client.Connect("127.0.0.1", port));
        var value = Activator.CreateInstance(newType); newType.GetProperty("Number").SetValue(value, 42); newType.GetProperty("Note").SetValue(value, "optional field");
        client.Send(value);
        var actual = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(42, oldType.GetProperty("Number").GetValue(actual));
        server.SendBroadcast(actual);
        Assert.AreEqual(42, newType.GetProperty("Number").GetValue(await echoed.Task.WaitAsync(TimeSpan.FromSeconds(5))));
        Assert.IsFalse(client.Connection.TryUseFeature("New feature", feature));
        Assert.AreEqual("New feature", disabled.Feature);
        client.Send(Activator.CreateInstance(feature));
        Assert.IsTrue(client.Connected);
    }

    [TestMethod]
    public void ChangedFieldTypeIsDisabledButConnectionRemainsVerified() {
        Type oldType = Load("V1").GetType("Versioned.Message"), broken = Load("Broken").GetType("Versioned.Message");
        using var client = new SocketJack.Net.TcpClient(Options(oldType));
        var connection = new NetworkConnection(client, null);
        SafeModeHandshake.Validate(SafeModeHandshake.Create(Options(broken)), client.Options, connection);
        Assert.IsTrue(connection.SafeModeVerified);
        Assert.IsFalse(connection.Compatibility.Supports(oldType));
        Assert.IsTrue(connection.Compatibility.Supports(typeof(int)));
        Assert.ThrowsException<SecurityException>(() => InboundMessageSecurity.RequireType(client, connection, oldType));
    }

    [TestMethod]
    public void NoSilentDowngradeToLegacyAcknowledgement() {
        using var client = new SocketJack.Net.TcpClient(new NetworkOptions());
        Assert.ThrowsException<SecurityException>(() => SafeModeHandshake.AcceptReply(SafeModeHandshake.Accepted, client.Options, new NetworkConnection(client, null)));
    }

    [TestMethod]
    public void NewEnumOperationIsDisabledWithoutDisablingOlderOperations() {
        var oldType = Load("V1").GetType("Versioned.Message"); var newer = Load("V2").GetType("Versioned.Message");
        using var client = new SocketJack.Net.TcpClient(Options(newer));
        var connection = new NetworkConnection(client, null);
        SafeModeHandshake.Validate(SafeModeHandshake.Create(Options(oldType)), client.Options, connection);
        var message = Activator.CreateInstance(newer);
        Assert.IsTrue(connection.CanSend(message));
        FeatureDisabledEventArgs disabled = null; client.Options.FeatureDisabled += (_, e) => disabled = e;
        newer.GetProperty("Kind").SetValue(message, Enum.Parse(newer.Assembly.GetType("Versioned.ActionKind"), "NewAction"));
        Assert.IsFalse(connection.CanSend(message));
        Assert.IsNotNull(disabled);
        Assert.IsTrue(connection.Compatibility.Supports(newer));
    }

    [TestMethod]
    public void RequiredFieldsAndMalformedManifestsFailClosed() {
        var oldType = Load("V1").GetType("Versioned.Message"); var newer = Load("V2").GetType("Versioned.Message");
        var old = WireContracts.Create(Options(oldType)); var next = WireContracts.Create(Options(newer));
        next.Types[WireContracts.Name(newer)].Required = new[] { "Note" };
        Assert.IsFalse(WireContracts.Compare(old, next).Supports(oldType));
        next.Types[WireContracts.Name(newer)].Required = new[] { "missing" };
        Assert.ThrowsException<SecurityException>(() => WireContracts.Compare(old, next));
        using var client = new SocketJack.Net.TcpClient(Options(oldType));
        var duplicate = System.Text.Encoding.UTF8.GetBytes("SJSM2{\"Codec\":\"a\",\"Codec\":\"b\"}");
        Assert.ThrowsException<SecurityException>(() => SafeModeHandshake.Validate(duplicate, client.Options, new NetworkConnection(client, null)));
    }

    [TestMethod]
    public void GenericContractNamesResolveAcrossAssemblyVersionsWithoutLoadingPeerTypes() {
        var old = typeof(Dictionary<,>).MakeGenericType(typeof(string), Load("V1").GetType("Versioned.Message"));
        var newer = typeof(Dictionary<,>).MakeGenericType(typeof(string), Load("V2").GetType("Versioned.Message"));
        var types = new TypeList(new[] { newer });
        Assert.AreNotEqual(old.FullName, newer.FullName);
        Assert.AreEqual(newer, types.Resolve(old.FullName));
        Assert.IsNull(types.Resolve("Unregistered.Type, UnknownAssembly"));
    }

    [DataTestMethod][DataRow(false)][DataRow(true)]
    public async Task TypedHttpNegotiatesAndReportsDisabledContractWithoutInvokingHandler(bool incompatible) {
        int port = Port(); var options = new NetworkOptions { UsePeerToPeer = false, UseCompression = false, EnablePatternCache = false };
        options.Authorization.AnonymousMessageTypes.Add(typeof(int));
        using var server = new HttpServer(options, port);
        int calls = 0; FeatureDisabledEventArgs disabled = null;
        options.FeatureDisabled += (_, e) => disabled = e;
        server.Map<int>("POST", "/typed", (_, _, _, _) => { calls++; return "ok"; });
        Assert.IsTrue(server.Listen());
        var manifest = WireContracts.Create(options);
        if (incompatible) manifest.Types[typeof(int).FullName].Kind = "scalar:other";
        byte[] claim = System.Text.Encoding.ASCII.GetBytes("SJSM2").Concat(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(manifest)).ToArray();
        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, $"http://127.0.0.1:{port}/typed");
        request.Headers.TryAddWithoutValidation(SafeModeHandshake.HeaderName, Convert.ToBase64String(claim));
        request.Content = new System.Net.Http.ByteArrayContent(options.Serializer.Serialize(new Wrapper(5, server)));
        using var response = await http.SendAsync(request);
        Assert.AreEqual(incompatible ? HttpStatusCode.Conflict : HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(incompatible ? 0 : 1, calls);
        Assert.AreEqual(incompatible, disabled != null);
        Assert.IsTrue(response.Headers.Contains(SafeModeHandshake.CompatibilityHeader));
    }

    [DataTestMethod][DataRow("websocket")][DataRow("mutable")][DataRow("udp")][DataRow("reliable")]
    public async Task CompatibilityNegotiatesAcrossTransports(string transport) {
        var oldType = Load("V1").GetType("Versioned.Message"); var newer = Load("V2").GetType("Versioned.Message");
        var so = Options(oldType); var co = Options(newer); int port = Port();
        so.Authorization.AnonymousMessageTypes.Add(typeof(int)); co.Authorization.AnonymousMessageTypes.Add(typeof(int));
        var received = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var echoed = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (transport == "websocket") {
            using var server = new SocketJack.Net.WebSockets.WebSocketServer(port) { Options = so };
            using var client = new SocketJack.Net.WebSockets.WebSocketClient(co);
            server.RegisterCallback<int>(e => received.TrySetResult(e.Object)); client.RegisterCallback<int>(e => echoed.TrySetResult(e.Object));
            Assert.IsTrue(server.Listen()); Assert.IsTrue(await client.Connect("127.0.0.1", port));
            Assert.IsTrue(client.Connection.Compatibility.Supports(newer));
            client.Send(64); Assert.AreEqual(64, await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            server.SendBroadcast(65); Assert.AreEqual(65, await echoed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        } else if (transport == "mutable") {
            using var server = new MutableTcpServer(so, port); server.EnabledProtocols.Add(MutableTcpProtocols.SocketJack);
            using var client = new SocketJack.Net.TcpClient(co);
            server.RegisterCallback<int>(e => received.TrySetResult(e.Object)); client.RegisterCallback<int>(e => echoed.TrySetResult(e.Object));
            Assert.IsTrue(server.Listen()); Assert.IsTrue(await client.Connect("127.0.0.1", port));
            Assert.IsTrue(client.Connection.Compatibility.Supports(newer));
            client.Send(64); Assert.AreEqual(64, await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            server.SendBroadcast(65); Assert.AreEqual(65, await echoed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        } else {
            if (transport == "reliable") so.UdpMode = co.UdpMode = UdpMode.UDP_Reliable;
            using var server = new SocketJack.Net.UdpServer(so, port); using var client = new SocketJack.Net.UdpClient(co);
            server.RegisterCallback<int>(e => received.TrySetResult(e.Object)); client.RegisterCallback<int>(e => echoed.TrySetResult(e.Object));
            Assert.IsTrue(server.Listen()); Assert.IsTrue(await client.Connect("127.0.0.1", port));
            Assert.IsTrue(client.Connection.Compatibility.Supports(newer));
            client.Send(64); Assert.AreEqual(64, await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            server.SendBroadcast(65); Assert.AreEqual(65, await echoed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        }
    }
}
