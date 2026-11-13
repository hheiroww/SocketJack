using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Net;

namespace SocketJack.Tests;

[TestClass]
public sealed class ApplicationWebSocketTests {
    public sealed record Note(string Text);

    [TestMethod]
    public async Task TypedRecordsAndMediaAreScopedToTheAuthenticatedSubject() {
        string root = Path.Combine(Path.GetTempPath(), "SocketJack-AppSocket-" + Guid.NewGuid().ToString("N"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        CancellationToken cancellation = timeout.Token;
        using var backend = new ApplicationWebSocket(new ApplicationWebSocketOptions {
            StorageDirectory = root,
            AllowedOrigins = new[] { "http://localhost:5091" },
            Authenticate = (username, password, _) => ValueTask.FromResult(password == "test-password" ? username : null)
        });
        backend.RegisterCollection<Note>("notes");
        backend.Register<Note, Note>("test.echo", (_, input, _) => ValueTask.FromResult(input));
        string frontend = new string('a', 64);
        string original = await backend.SaveDraftAsync(new ApplicationRevision {
            FrontendSha256 = frontend,
            Routes = new[] { new ApplicationRouteDefinition { Name = "app.echo", Operation = "test.echo" } }
        }, cancellation);
        backend.ActivateDraft(null, original, frontend);
        Assert.IsFalse(backend.CanUpgrade("null", false));
        Assert.IsFalse(backend.CanUpgrade("http://localhost:5092", false));
        StringAssert.Contains(backend.GenerateManifest(), "\"notes\"");
        StringAssert.Contains(backend.GenerateClientDeclarations(), "\"test.echo\"");

        var alice = StartSocket(backend, cancellation);
        var bob = StartSocket(backend, cancellation);
        using var aliceClient = alice.Client;
        using var bobClient = bob.Client;
        Assert.AreEqual("unauthenticated", Code(await Invoke(aliceClient, "records.list", new { collection = "notes" }, cancellation)));
        Assert.IsTrue(Ok(await Invoke(aliceClient, "session.signIn", new { username = "alice", password = "test-password" }, cancellation)));
        Assert.IsTrue(Ok(await Invoke(bobClient, "session.signIn", new { username = "bob", password = "test-password" }, cancellation)));
        Assert.AreEqual("first", Value(await Invoke(aliceClient, "app.echo", new Note("first"), cancellation)).GetProperty("text").GetString());
        string changed = await backend.SaveDraftAsync(new ApplicationRevision {
            FrontendSha256 = frontend,
            Routes = new[] { new ApplicationRouteDefinition { Name = "app.echo", Operation = "test.echo", InputProperty = "note" } }
        }, cancellation);
        Assert.ThrowsException<InvalidOperationException>(() => backend.ActivateDraft(original, changed, new string('b', 64)));
        backend.ActivateDraft(original, changed, frontend);
        Assert.AreEqual("invalidInput", Code(await Invoke(aliceClient, "app.echo", new Note("missing"), cancellation)));
        Assert.AreEqual("second", Value(await Invoke(aliceClient, "app.echo", new { note = new Note("second") }, cancellation)).GetProperty("text").GetString());
        backend.Rollback(changed);
        Assert.AreEqual("restored", Value(await Invoke(aliceClient, "app.echo", new Note("restored"), cancellation)).GetProperty("text").GetString());
        var note = Value(await Invoke(aliceClient, "records.create", new { collection = "notes", data = new Note("Alice only") }, cancellation));
        string id = note.GetProperty("id").GetString();
        Assert.AreEqual("notFound", Code(await Invoke(bobClient, "records.get", new { collection = "notes", id }, cancellation)));
        Assert.AreEqual(0, Value(await Invoke(bobClient, "records.list", new { collection = "notes" }, cancellation))
            .GetProperty("records").GetArrayLength());
        Assert.AreEqual("conflict", Code(await Invoke(aliceClient, "records.update",
            new { collection = "notes", id, revision = 99, data = new Note("Conflict") }, cancellation)));

        byte[] content = { 11, 22, 33, 44 };
        string sha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        var upload = Value(await Invoke(aliceClient, "media.begin",
            new { filename = "demo.html", contentType = "text/html", length = content.Length, sha256 }, cancellation));
        string mediaId = upload.GetProperty("id").GetString();
        Assert.IsTrue(Ok(await Invoke(aliceClient, "media.append", new { id = mediaId, offset = 0, sha256, data = content }, cancellation)));
        Assert.IsTrue(Ok(await Invoke(aliceClient, "media.complete", new { id = mediaId }, cancellation)));
        Assert.AreEqual("notFound", Code(await Invoke(bobClient, "media.read", new { id = mediaId, offset = 0 }, cancellation)));
        var media = Value(await Invoke(aliceClient, "media.read", new { id = mediaId, offset = 1, maximumBytes = 2 }, cancellation));
        Assert.AreEqual("application/octet-stream", media.GetProperty("contentType").GetString());
        var buffer = new byte[2];
        var received = await aliceClient.ReceiveAsync(new ArraySegment<byte>(buffer), cancellation);
        Assert.AreEqual(WebSocketMessageType.Binary, received.MessageType);
        CollectionAssert.AreEqual(new byte[] { 22, 33 }, buffer);

        backend.RevokeSessions("alice");
        Assert.AreEqual("unauthenticated", Code(await Invoke(aliceClient, "profile.get", null, cancellation)));
        Assert.IsTrue(Ok(await Invoke(bobClient, "profile.get", null, cancellation)));
        await aliceClient.CloseAsync(WebSocketCloseStatus.NormalClosure, "", cancellation);
        await bobClient.CloseAsync(WebSocketCloseStatus.NormalClosure, "", cancellation);
        await Task.WhenAll(alice.Server, bob.Server);
    }

    private static (WebSocket Client, Task Server) StartSocket(ApplicationWebSocket backend, CancellationToken cancellation) {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try {
            var client = new System.Net.Sockets.TcpClient();
            client.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            var accepted = listener.AcceptTcpClient();
            var serverSocket = WebSocket.CreateFromStream(accepted.GetStream(), true, ApplicationWebSocket.Subprotocol, TimeSpan.Zero);
            var clientSocket = WebSocket.CreateFromStream(client.GetStream(), false, ApplicationWebSocket.Subprotocol, TimeSpan.Zero);
            return (clientSocket, Serve());
            async Task Serve() {
                using (accepted)
                using (serverSocket)
                    await backend.RunAsync(serverSocket, "http://localhost:5091", false, cancellation);
            }
        } finally {
            listener.Stop();
        }
    }

    private static async Task<JsonElement> Invoke(WebSocket socket, string operation, object input, CancellationToken cancellation) {
        string id = Guid.NewGuid().ToString("N");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { id, operation, input }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellation);
        var output = new byte[128 * 1024];
        var result = await socket.ReceiveAsync(new ArraySegment<byte>(output), cancellation);
        Assert.IsTrue(result.EndOfMessage);
        using var parsed = JsonDocument.Parse(output.AsMemory(0, result.Count));
        Assert.AreEqual(id, parsed.RootElement.GetProperty("id").GetString());
        return parsed.RootElement.Clone();
    }

    private static bool Ok(JsonElement value) => value.GetProperty("ok").GetBoolean();
    private static JsonElement Value(JsonElement value) => value.GetProperty("value");
    private static string Code(JsonElement value) => value.GetProperty("error").GetProperty("code").GetString();
}
