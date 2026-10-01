using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Net;
using SocketJack.Net.Database;
using SocketJack.Net.P2P;
using SocketJack.Serialization;
using System.Net;
using System.Net.Security;
using System.Reflection;
using System.Security;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace SocketJack.Tests;

[TestClass]
public sealed class AuthorizationTests {
    public sealed class Login { public string Token { get; set; } public bool IsAdmin { get; set; } }
    public sealed class Data { public static int Setters; public int Value { get => 0; set => Interlocked.Increment(ref Setters); } }
    [AdministrativeMessage]
    public sealed class Admin { public static int Setters; public int Value { get => 0; set => Interlocked.Increment(ref Setters); } }
    public sealed class NestedAdmin { public Admin Command { get; set; } }
    public sealed class TypeHolder { public Type RuntimeType { get; set; } }
    public sealed class ReadOnlyGadget { public TypeHolder Child { get; } }
    public static int Invocations;
    public static int DangerousMethod() => Interlocked.Increment(ref Invocations);
    static NetworkOptions Options(bool binary = false) {
        var options = new NetworkOptions { UsePeerToPeer = false, AutoReconnect = false, EnablePatternCache = false };
        if (binary) options.Serializer = new BinarySerializer();
        options.VerifiedAssemblies.Add(typeof(Login).Assembly, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(Login).Assembly.Location))));
        foreach (var type in new[] { typeof(Login), typeof(Data), typeof(Admin), typeof(NestedAdmin), typeof(TypeHolder), typeof(ReadOnlyGadget) }) options.Whitelist.Add(type);
        options.Authorization.AnonymousMessageTypes.Add(typeof(Login));
        return options;
    }
    static int Port() { using var s = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); s.Start(); return ((IPEndPoint)s.LocalEndpoint).Port; }
    static X509Certificate2 Certificate() {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return new X509Certificate2(cert.Export(X509ContentType.Pfx));
    }
    sealed class RawClient : IDisposable {
        readonly System.Net.Sockets.TcpClient client = new();
        internal Stream Stream;
        internal static async Task<RawClient> Connect(int port, NetworkOptions options, X509Certificate2 certificate = null) {
            var raw = new RawClient(); await raw.client.ConnectAsync(IPAddress.Loopback, port); raw.Stream = raw.client.GetStream();
            if (certificate != null) {
                var ssl = new SslStream(raw.Stream, false, (_, cert, _, _) => cert != null && cert.GetCertHashString() == certificate.GetCertHashString());
                await ssl.AuthenticateAsClientAsync("localhost"); raw.Stream = ssl;
            }
            if (options.SafeMode) {
                await raw.Stream.WriteAsync(SafeModeHandshake.Frame(SafeModeHandshake.Create(options)));
                var ack = new byte[5]; await raw.Stream.ReadExactlyAsync(ack).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                CollectionAssert.AreEqual(SafeModeHandshake.Accepted, ack);
            }
            return raw;
        }
        internal async Task Send(NetworkOptions options, Type type, object value) => await Stream.WriteAsync(SafeModeHandshake.Frame(options.Serializer.Serialize(new Wrapper { Type = type.FullName, value = value })));
        internal async Task Closed() {
            int read;
            try { read = await Stream.ReadAsync(new byte[1024]).AsTask().WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (IOException) { read = 0; }
            Assert.AreEqual(0, read, "Unauthorized client must be disconnected.");
        }
        public void Dispose() { Stream?.Dispose(); client.Dispose(); }
    }
    static object Fields(bool binary, string name, object value) => new { Value = value };

    [DataTestMethod][DataRow(false, true)][DataRow(true, true)][DataRow(false, false)]
    public async Task MatchingDllDoesNotAuthorizePayloadOrInvokeSetter(bool binary, bool safeMode) {
        var options = Options(binary); options.SafeMode = safeMode; int port = Port();
        using var server = new SocketJack.Net.TcpServer(options, port); int callbacks = 0; Data.Setters = 0;
        server.RegisterCallback<Data>(_ => Interlocked.Increment(ref callbacks)); Assert.IsTrue(server.Listen());
        using var client = await RawClient.Connect(port, options);
        await client.Send(options, typeof(Data), Fields(binary, "Value", 1)); await client.Closed();
        Assert.AreEqual(0, callbacks); Assert.AreEqual(0, Data.Setters);
    }
    [DataTestMethod][DataRow(false)][DataRow(true)]
    public async Task ForgedLoginAdminFlagDoesNotGrantAdministrativeRole(bool binary) {
        var options = Options(binary); options.UseSsl = true; using var cert = Certificate(); int port = Port();
        using var server = new SocketJack.Net.TcpServer(options, port) { SslCertificate = cert };
        var loggedIn = new TaskCompletionSource<NetworkConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
        options.Authorization.AuthorizeMessage = _ => true; int callbacks = 0; Admin.Setters = 0;
        server.RegisterCallback<Login>(e => {
            if (e.Object.Token != "fixture-user-token") return;
            e.Connection.SetAuthenticatedPrincipal(new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "user") }, "fixture")), DateTimeOffset.UtcNow.AddMinutes(1));
            loggedIn.TrySetResult(e.Connection);
        });
        server.RegisterCallback<Admin>(_ => Interlocked.Increment(ref callbacks)); Assert.IsTrue(server.Listen());
        using var client = await RawClient.Connect(port, options, cert);
        await client.Send(options, typeof(Login), new { Token = "fixture-user-token", IsAdmin = true });
        var connection = await loggedIn.Task.WaitAsync(TimeSpan.FromSeconds(3)); Assert.IsTrue(connection.IsAuthenticated);
        Assert.IsFalse(connection.AuthenticatedPrincipal.IsInRole("Administrator"));
        await client.Send(options, typeof(Admin), Fields(binary, "Value", 1)); await client.Closed();
        Assert.AreEqual(0, Admin.Setters); Assert.AreEqual(0, callbacks);
    }
    [TestMethod]
    public async Task ExplicitAdminApprovalWorksAndRevocationClosesNextMessage() {
        var options = Options(); options.UseSsl = true; using var cert = Certificate(); int port = Port();
        using var server = new SocketJack.Net.TcpServer(options, port) { SslCertificate = cert };
        var received = new TaskCompletionSource<NetworkConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
        options.Authorization.AuthorizeMessage = c => c.MessageType == typeof(Login) || c.Principal?.IsInRole("Administrator") == true;
        server.RegisterCallback<Login>(e => {
            if (e.Object.Token == "fixture-admin-token") e.Connection.SetAuthenticatedPrincipal(new ClaimsPrincipal(new ClaimsIdentity(new[] {
                new Claim(ClaimTypes.Name, "admin"), new Claim(ClaimTypes.Role, "Administrator") }, "fixture")), DateTimeOffset.UtcNow.AddMinutes(1));
        });
        int callbacks = 0; server.RegisterCallback<Admin>(e => { Interlocked.Increment(ref callbacks); received.TrySetResult(e.Connection); });
        Assert.IsTrue(server.Listen()); using var client = await RawClient.Connect(port, options, cert);
        await client.Send(options, typeof(Login), new { Token = "fixture-admin-token" });
        // Wait for the server-owned identity, not merely receipt of the DLL handshake.
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!server.Clients.Values.Any(c => c.IsAuthenticated) && DateTime.UtcNow < deadline) await Task.Delay(10);
        await client.Send(options, typeof(Admin), new { Value = 1 });
        var connection = await received.Task.WaitAsync(TimeSpan.FromSeconds(3)); Assert.AreEqual(1, callbacks);
        var copy = connection.AuthenticatedPrincipal; ((ClaimsIdentity)copy.Identity).RemoveClaim(copy.FindFirst(ClaimTypes.Role));
        Assert.IsTrue(connection.AuthenticatedPrincipal.IsInRole("Administrator"), "Callers must not mutate the stored identity through a getter.");
        connection.ClearAuthentication(); await client.Send(options, typeof(Admin), new { Value = 2 }); await client.Closed();
        Assert.AreEqual(1, callbacks);
    }
    [DataTestMethod][DataRow(false)][DataRow(true)]
    public async Task RedirectCannotActivateAdminPayloadEvenWithMatchingDll(bool binary) {
        var options = Options(binary); int port = Port(); using var server = new SocketJack.Net.TcpServer(options, port);
        options.Authorization.RequireAuthentication = false; options.Authorization.AuthorizePeerRedirect = _ => true;
        Admin.Setters = 0; Assert.IsTrue(server.Listen()); using var client = await RawClient.Connect(port, options);
        await client.Send(options, typeof(PeerRedirect), new Dictionary<string, object> {
            ["Type"] = typeof(Admin).FullName, ["Sender"] = "admin", ["Recipient"] = "#ALL#",
            ["Value"] = new Wrapper { Type = typeof(Admin).FullName, value = Fields(binary, "Value", 1) }
        });
        await client.Closed(); Assert.AreEqual(0, Admin.Setters);
    }
    [TestMethod]
    public async Task AnonymousEnvelopeCannotHideNestedAdmin() {
        var options = Options(); options.Authorization.AnonymousMessageTypes.Add(typeof(NestedAdmin)); int port = Port();
        using var server = new SocketJack.Net.TcpServer(options, port); Admin.Setters = 0;
        Assert.IsTrue(server.Listen()); using var client = await RawClient.Connect(port, options);
        await client.Send(options, typeof(NestedAdmin), new { Command = new { Value = 1 } }); await client.Closed(); Assert.AreEqual(0, Admin.Setters);
    }
    [TestMethod]
    public void SegmentIdsAreIsolatedAndDuplicatesOrInvalidIndicesFailClosed() {
        var first = new InboundSegmentBuffer(); var second = new InboundSegmentBuffer();
        Assert.IsNull(first.Add(new Segment("same", new byte[] { 1 }, 1, 2), 100));
        Assert.IsNull(second.Add(new Segment("same", new byte[] { 9 }, 2, 2), 100));
        CollectionAssert.AreEqual(new byte[] { 1, 2 }, first.Add(new Segment("same", new byte[] { 2 }, 2, 2), 100));
        Assert.ThrowsException<SecurityException>(() => second.Add(new Segment("same", new byte[] { 8 }, 2, 2), 100));
        Assert.ThrowsException<SecurityException>(() => second.Add(new Segment("bad", new byte[] { 8 }, 0, 1), 100));
    }
    [TestMethod]
    public void DangerousTypesCannotHideBehindReadOnlyProperties() {
        using var socket = new SocketJack.Net.UdpClient(Options());
        socket.Options.Whitelist.Add(typeof(Type));
        Assert.ThrowsException<TypeNotAllowedException>(() => DeserializationTypePolicy.Validate(typeof(ReadOnlyGadget), socket));
    }
    [TestMethod]
    public async Task ArbitraryHttpBodyCannotInvokeCallbackBeforeRequestGate() {
        var options = Options(); options.Authorization.AnonymousMessageTypes.Add(typeof(Data));
        int port = Port(); using var server = new SocketJack.Net.HttpServer(options, port);
        int calls = 0; server.RegisterCallback<Data>(_ => Interlocked.Increment(ref calls)); server.RequestGate = (_, _) => "blocked";
        Assert.IsTrue(server.Listen()); using var http = new System.Net.Http.HttpClient();
        using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, $"http://127.0.0.1:{port}/anything");
        req.Headers.TryAddWithoutValidation(SafeModeHandshake.HeaderName, Convert.ToBase64String(SafeModeHandshake.Create(options)));
        req.Content = new System.Net.Http.ByteArrayContent(options.Serializer.Serialize(new Wrapper { Type = typeof(Data).FullName, value = new { Value = 1 } }));
        using var response = await http.SendAsync(req); Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode); Assert.AreEqual(0, calls);
    }
    [TestMethod]
    public void ReflectedAdminEndpointCannotExecuteClrMethod() {
        using var server = new MutableTcpServer(Options(), Port());
        var panel = new SqlAdminPanel(server);
        var request = new HttpRequest { Context = new HttpContext() };
        var endpointType = typeof(SqlAdminPanel).GetNestedType("ApiEndpointDef", BindingFlags.NonPublic);
        var endpoint = Activator.CreateInstance(endpointType, true);
        endpointType.GetField("HandlerTypeName").SetValue(endpoint, typeof(AuthorizationTests).FullName);
        endpointType.GetField("HandlerMethodName").SetValue(endpoint, nameof(DangerousMethod));
        Invocations = 0;
        var method = typeof(SqlAdminPanel).GetMethod("ExecuteReflectedEndpoint", BindingFlags.Instance | BindingFlags.NonPublic);
        method.Invoke(panel, new object[] { request, endpoint, new Dictionary<string, string>() });
        Assert.AreEqual(403, request.Context.StatusCodeNumber); Assert.AreEqual(0, Invocations);
    }
    [DataTestMethod][DataRow(false)][DataRow(true)]
    public async Task AuthenticatedRedirectChecksRecipientAndRejectsNestedAdminBeforeActivation(bool binary) {
        var options = Options(binary); options.UseSsl = true;
        using var cert = Certificate(); int port = Port();
        using var server = new SocketJack.Net.TcpServer(options, port) { SslCertificate = cert };
        var loggedIn = new TaskCompletionSource<NetworkConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.RegisterCallback<Login>(e => {
            if (e.Object.Token != "fixture-admin-token") return;
            e.Connection.SetAuthenticatedPrincipal(new ClaimsPrincipal(new ClaimsIdentity(new[] {
                new Claim(ClaimTypes.Name, "admin"), new Claim(ClaimTypes.Role, "Administrator") }, "fixture")), DateTimeOffset.UtcNow.AddMinutes(1));
            loggedIn.TrySetResult(e.Connection);
        });
        options.Authorization.AuthorizeMessage = _ => true;
        options.Authorization.AuthorizePeerRedirect = c => c.Recipient == "allowed-peer";
        Assert.IsTrue(server.Listen()); using var client = await RawClient.Connect(port, options, cert);
        await client.Send(options, typeof(Login), new { Token = "fixture-admin-token" });
        var connection = await loggedIn.Task.WaitAsync(TimeSpan.FromSeconds(3));
        // Enable routing after login so this fixture isolates decoded redirect policy.
        options.UsePeerToPeer = true;
        byte[] Encode(Type type, object value, string recipient) => options.Serializer.Serialize(new Wrapper {
            Type = typeof(PeerRedirect).FullName,
            value = new { Type = type.FullName, Sender = "forged-admin", Recipient = recipient, Value = value }
        });
        Data.Setters = 0;
        Assert.ThrowsException<SecurityException>(() => InboundMessageDecoder.Read(server, connection, Encode(typeof(Data), new { Value = 1 }, "forbidden-peer")));
        Assert.AreEqual(0, Data.Setters);
        var valid = (PeerRedirect)InboundMessageDecoder.Read(server, connection, Encode(typeof(Data), new { Value = 1 }, "allowed-peer"));
        InboundMessageSecurity.BeforeDispatch(server, connection, valid, typeof(PeerRedirect));
        Assert.AreEqual(connection.ID.ToString(), valid.Sender);
        Assert.AreEqual(1, Data.Setters);
        Admin.Setters = 0;
        Assert.ThrowsException<SecurityException>(() => InboundMessageDecoder.Read(server, connection, Encode(typeof(NestedAdmin), new { Command = new { Value = 1 } }, "allowed-peer")));
        Assert.AreEqual(0, Admin.Setters);
    }
    [TestMethod]
    public async Task TypedHttpRouteRejectsDifferentAllowedDtoBeforeSetter() {
        var options = Options(); options.Authorization.AnonymousMessageTypes.Add(typeof(Login)); options.Authorization.AnonymousMessageTypes.Add(typeof(Data));
        int port = Port(); using var server = new SocketJack.Net.HttpServer(options, port);
        int calls = 0; Data.Setters = 0;
        server.Map<Login>("POST", "/login", (_, _, _, _) => { calls++; return "ok"; });
        Assert.IsTrue(server.Listen()); using var http = new System.Net.Http.HttpClient();
        using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, $"http://127.0.0.1:{port}/login");
        request.Headers.TryAddWithoutValidation(SafeModeHandshake.HeaderName, Convert.ToBase64String(SafeModeHandshake.Create(options)));
        request.Content = new System.Net.Http.ByteArrayContent(options.Serializer.Serialize(new Wrapper { Type = typeof(Data).FullName, value = new { Value = 1 } }));
        try { using var response = await http.SendAsync(request); Assert.IsFalse(response.IsSuccessStatusCode); }
        catch (System.Net.Http.HttpRequestException) { }
        Assert.AreEqual(0, calls); Assert.AreEqual(0, Data.Setters);
    }

}
