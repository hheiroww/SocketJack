using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Net;
using System.Net;
using System.Net.Http.Headers;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SocketJack.Tests;

[TestClass]
public sealed class HttpStaticCachingTests {
    static int Port() { using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port; }
    static string DirectoryPath() { string path = Path.Combine(Path.GetTempPath(), "SocketJackStaticCache-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    static HttpServer Server(int port = 0) => new(new NetworkOptions { Logging = false, UsePeerToPeer = false, EnablePatternCache = true, PatternCachePromotionThreshold = 1 }, port);
    static async Task<HttpDispatchResult> Dispatch(HttpServer server, string path, Dictionary<string, string> headers = null) {
        var target = path.Split('?', 2);
        return await server.DispatchMappedHttpRequestAsync(null, new HttpRequest {
            Method = "GET", Path = target[0], QueryString = target.Length == 2 ? target[1] : null,
            Headers = headers ?? new(StringComparer.OrdinalIgnoreCase)
        });
    }

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

    [TestMethod]
    public async Task VersionedAssetsNegotiateVerifiedBytesAndOnlyPublicFingerprintGetsExpires() {
        string root = DirectoryPath();
        try {
            byte[] content = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(".button { color: teal; }\n", 200)));
            byte[] brotli = Encode(content, stream => new BrotliStream(stream, CompressionLevel.Optimal, leaveOpen: true));
            byte[] gzip = Encode(content, stream => new GZipStream(stream, CompressionLevel.Optimal, leaveOpen: true));
            File.WriteAllBytes(Path.Combine(root, "app.css"), content);
            File.WriteAllBytes(Path.Combine(root, "app.css.br"), brotli);
            File.WriteAllBytes(Path.Combine(root, "app.css.gz"), gzip);
            string manifestPath = Path.Combine(root, "manifest.json");
            WriteManifest(gzip);

            int port = Port();
            using var server = Server(port);
            server.Options.UseHttpCompression = true;
            server.StaticAssets = HttpStaticAssets.Load(root, manifestPath);
            server.MapDirectory("/assets", root);
            Assert.IsTrue(server.Listen());
            using var client = new System.Net.Http.HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.None });
            string query = server.StaticAssets.GetVersionQuery("app.css");
            string path = $"/assets/app.css?{query}";
            string url = $"http://127.0.0.1:{port}{path}";

            async Task<HttpResponseMessage> Send(HttpMethod method, string address, string coding, string etag = null) {
                using var request = new HttpRequestMessage(method, address);
                request.Headers.TryAddWithoutValidation("Accept-Encoding", coding);
                if (etag != null) request.Headers.TryAddWithoutValidation("If-None-Match", etag);
                return await client.SendAsync(request);
            }

            using var first = await Send(HttpMethod.Get, url, "br, gzip");
            Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);
            Assert.AreEqual("br", first.Content.Headers.ContentEncoding.Single());
            Assert.AreEqual(TimeSpan.FromDays(365), first.Headers.CacheControl.MaxAge);
            Assert.IsTrue(first.Headers.CacheControl.Extensions.Any(option => option.Name == "immutable"));
            Assert.IsTrue(first.Content.Headers.Expires > DateTimeOffset.UtcNow.AddDays(364));
            CollectionAssert.Contains(first.Headers.Vary.ToArray(), "Accept-Encoding");
            CollectionAssert.AreEqual(brotli, await first.Content.ReadAsByteArrayAsync());
            string brTag = first.Headers.ETag.ToString();

            using var negotiatedGzip = await Send(HttpMethod.Get, url, "br;q=0, gzip;q=1", brTag);
            Assert.AreEqual(HttpStatusCode.OK, negotiatedGzip.StatusCode);
            Assert.AreEqual("gzip", negotiatedGzip.Content.Headers.ContentEncoding.Single());
            CollectionAssert.AreEqual(gzip, await negotiatedGzip.Content.ReadAsByteArrayAsync());
            Assert.AreNotEqual(brTag, negotiatedGzip.Headers.ETag.ToString());

            using var head = await Send(HttpMethod.Head, url, "br");
            Assert.AreEqual((long)brotli.Length, head.Content.Headers.ContentLength);
            Assert.AreEqual(0, (await head.Content.ReadAsByteArrayAsync()).Length);
            using var unchanged = await Send(HttpMethod.Get, url, "br", brTag);
            Assert.AreEqual(HttpStatusCode.NotModified, unchanged.StatusCode);
            Assert.AreEqual(0, (await unchanged.Content.ReadAsByteArrayAsync()).Length);
            Assert.IsNotNull(unchanged.Content.Headers.Expires);

            using var identity = await Send(HttpMethod.Get, url, "identity, br;q=0.5");
            Assert.AreEqual(0, identity.Content.Headers.ContentEncoding.Count);
            CollectionAssert.AreEqual(content, await identity.Content.ReadAsByteArrayAsync());
            using var rejected = await Send(HttpMethod.Get, url, "br;q=0,gzip;q=0,identity;q=0");
            Assert.AreEqual(HttpStatusCode.NotAcceptable, rejected.StatusCode);
            Assert.IsTrue(rejected.Headers.CacheControl.NoStore);

            var privateResult = await Dispatch(server, path, new(StringComparer.OrdinalIgnoreCase) { ["Cookie"] = "session=fixture" });
            StringAssert.StartsWith(privateResult.Response.Headers["Cache-Control"], "private,");
            Assert.IsFalse(privateResult.Response.Headers.ContainsKey("Expires"));
            var stale = await Dispatch(server, "/assets/app.css?v=stale");
            Assert.AreEqual(200, stale.StatusCode);
            Assert.IsFalse(stale.Response.Headers.ContainsKey("Expires"));

            using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, url);
            rangeRequest.Headers.Range = new RangeHeaderValue(0, 3);
            using var range = await client.SendAsync(rangeRequest);
            Assert.AreEqual(HttpStatusCode.PartialContent, range.StatusCode);
            Assert.IsNull(range.Content.Headers.Expires);
            CollectionAssert.AreEqual(content[..4], await range.Content.ReadAsByteArrayAsync());

            using var streamed = new MemoryStream();
            var result = await server.DispatchMappedHttpRequestAsync(null,
                new HttpRequest { Method = "GET", Path = "/assets/app.css", QueryString = query, Headers = new(StringComparer.OrdinalIgnoreCase) { ["Accept-Encoding"] = "br" } },
                new HttpStreamDispatchCallbacks { OnChunk = (bytes, offset, length) => streamed.Write(bytes, offset, length) });
            Assert.IsTrue(result.Streamed);
            CollectionAssert.AreEqual(brotli, streamed.ToArray());

            byte[] wrongGzip = Encode(Encoding.UTF8.GetBytes("wrong representation"),
                stream => new GZipStream(stream, CompressionLevel.Optimal, leaveOpen: true));
            File.WriteAllBytes(Path.Combine(root, "app.css.gz"), wrongGzip);
            WriteManifest(wrongGzip);
            Assert.ThrowsException<InvalidDataException>(() => HttpStaticAssets.Load(root, manifestPath));

            void WriteManifest(byte[] gzipBytes) {
                File.WriteAllText(manifestPath, JsonSerializer.Serialize(new {
                    version = 1,
                    assets = new[] { new {
                        path = "app.css", length = content.Length, sha256 = Hash(content),
                        encodings = new {
                            br = new { path = "app.css.br", length = brotli.Length, sha256 = Hash(brotli) },
                            gzip = new { path = "app.css.gz", length = gzipBytes.Length, sha256 = Hash(gzipBytes) }
                        }
                    } }
                }));
            }
        } finally { Directory.Delete(root, true); }
    }

    private static byte[] Encode(byte[] content, Func<Stream, Stream> compressor) {
        using var output = new MemoryStream();
        using (var stream = compressor(output)) stream.Write(content);
        return output.ToArray();
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
