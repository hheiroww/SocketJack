using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Net;
using System.Net;
using System.Net.Http.Headers;

namespace SocketJack.Tests;

[TestClass]
public sealed class HttpStaticCachingTests {
    static int Port() { using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port; }
    static string DirectoryPath() { string path = Path.Combine(Path.GetTempPath(), "SocketJackStaticCache-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    static HttpServer Server(int port = 0) => new(new NetworkOptions { Logging = false, UsePeerToPeer = false, EnablePatternCache = true, PatternCachePromotionThreshold = 1 }, port);
    static async Task<HttpDispatchResult> Dispatch(HttpServer server, string path, Dictionary<string, string> headers = null) =>
        await server.DispatchMappedHttpRequestAsync(null, new HttpRequest { Method = "GET", Path = path, Headers = headers ?? new(StringComparer.OrdinalIgnoreCase) });

    [DataTestMethod]
    [DataRow("app.js")][DataRow("app.css")][DataRow("picture.svg")]
    public async Task StaticAssetsRevalidateAndContentChangesResetEvenPreservedTimestamps(string name) {
        string root = DirectoryPath();
        try {
            string path = Path.Combine(root, name); File.WriteAllText(path, "first-content"); DateTime original = File.GetLastWriteTimeUtc(path);
            int port = Port(); using var server = Server(port); server.MapDirectory("/assets", root); Assert.IsTrue(server.Listen());
            using var client = new System.Net.Http.HttpClient(); string url = $"http://127.0.0.1:{port}/assets/{name}";
            using var first = await client.GetAsync(url); Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);
            Assert.AreEqual("first-content", await first.Content.ReadAsStringAsync());
            string etag = first.Headers.ETag.ToString(); Assert.IsTrue(first.Headers.CacheControl.MustRevalidate); Assert.AreEqual(TimeSpan.Zero, first.Headers.CacheControl.MaxAge);
            using var sameRequest = new HttpRequestMessage(HttpMethod.Get, url); sameRequest.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Parse(etag));
            using var same = await client.SendAsync(sameRequest); Assert.AreEqual(HttpStatusCode.NotModified, same.StatusCode); Assert.AreEqual(0, (await same.Content.ReadAsByteArrayAsync()).Length);
            File.WriteAllText(path, "other-content"); File.SetLastWriteTimeUtc(path, original);
            using var changedRequest = new HttpRequestMessage(HttpMethod.Get, url); changedRequest.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Parse(etag)); changedRequest.Headers.IfModifiedSince = DateTimeOffset.UtcNow.AddDays(1);
            using var changed = await client.SendAsync(changedRequest); Assert.AreEqual(HttpStatusCode.OK, changed.StatusCode);
            Assert.AreEqual("other-content", await changed.Content.ReadAsStringAsync()); Assert.AreNotEqual(etag, changed.Headers.ETag.ToString());
        } finally { Directory.Delete(root, true); }
    }
    [TestMethod]
    public async Task GeneratedRoutesNeverBecomeCachedEvenWithStaticMimeAndGlobalCacheControl() {
        using var server = Server(); server.CacheControl = "public, max-age=86400";
        int calls = 0; server.Map("GET", "/generated.js", (_, request, _) => { calls++; return "same dynamic output"; });
        for (int i = 0; i < 4; i++) {
            var response = await Dispatch(server, "/generated.js", new(StringComparer.OrdinalIgnoreCase) { ["If-None-Match"] = "*" });
            Assert.AreEqual(200, response.StatusCode); Assert.AreEqual("no-store", response.Response.Headers["Cache-Control"]); Assert.IsFalse(response.Response.Headers.ContainsKey("ETag"));
        }
        Assert.AreEqual(4, calls);
    }
    [TestMethod]
    public async Task RenderedTemplatesAreReevaluatedAndNeverRetainedAsPages() {
        string root = DirectoryPath();
        try {
            string path = Path.Combine(root, "page.html"); File.WriteAllText(path, "Hello $User");
            using var server = Server(); string[][] variables = [["User", "first"]];
            server.RouteFile("GET", "/file", path, variables); server.RouteString("GET", "/string", "Hello $User", variables);
            Assert.AreEqual("Hello first", System.Text.Encoding.UTF8.GetString((await Dispatch(server, "/file")).BodyBytes));
            Assert.AreEqual("Hello first", System.Text.Encoding.UTF8.GetString((await Dispatch(server, "/string")).BodyBytes));
            variables[0][1] = "other";
            foreach (string route in new[] { "/file", "/string" }) {
                var result = await Dispatch(server, route); Assert.AreEqual("Hello other", System.Text.Encoding.UTF8.GetString(result.BodyBytes));
                Assert.AreEqual("no-store", result.Response.Headers["Cache-Control"]); Assert.IsFalse(result.Response.Headers.ContainsKey("ETag"));
            }
        } finally { Directory.Delete(root, true); }
    }
    [TestMethod]
    public async Task StreamingGeneratedContentCannotEnableCaching() {
        using var server = Server();
        server.MapStream("GET", "/stream", (_, _, output, token) => {
            output.SetHeader("Cache-Control", "public, max-age=86400");
            output.WriteLine("generated");
            return Task.CompletedTask;
        });
        string policy = null;
        await server.DispatchMappedHttpRequestAsync(null, new HttpRequest { Method = "GET", Path = "/stream" },
            new HttpStreamDispatchCallbacks { OnStart = response => policy = response.Headers["Cache-Control"], OnChunk = (_, _, _) => { } });
        StringAssert.Contains(policy, "no-store");
    }

    [TestMethod]
    public async Task HostedDispatchSupportsPrivateValidationDeletionAndNoStoreOptOut() {
        string root = DirectoryPath();
        try {
            string path = Path.Combine(root, "app.js"); File.WriteAllText(path, "static-content");
            using var server = Server(); server.MapDirectory("/assets", root);
            var first = await Dispatch(server, "/assets/app.js"); string etag = first.Response.Headers["ETag"];
            var same = await Dispatch(server, "/assets/app.js", new(StringComparer.OrdinalIgnoreCase) { ["If-None-Match"] = "W/" + etag, ["Cookie"] = "session=private" });
            Assert.AreEqual(304, same.StatusCode); StringAssert.StartsWith(same.Response.Headers["Cache-Control"], "private,");
            server.CacheControl = "no-store";
            var disabled = await Dispatch(server, "/assets/app.js", new(StringComparer.OrdinalIgnoreCase) { ["If-None-Match"] = etag });
            Assert.AreEqual(200, disabled.StatusCode); Assert.AreEqual("no-store", disabled.Response.Headers["Cache-Control"]); Assert.IsFalse(disabled.Response.Headers.ContainsKey("ETag"));
            server.CacheControl = null; File.Delete(path);
            var missing = await Dispatch(server, "/assets/app.js", new(StringComparer.OrdinalIgnoreCase) { ["If-None-Match"] = etag });
            Assert.AreEqual(404, missing.StatusCode); Assert.AreEqual("no-store", missing.Response.Headers["Cache-Control"]);
        } finally { Directory.Delete(root, true); }
    }
}
