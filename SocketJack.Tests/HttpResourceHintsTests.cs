using System.Net;
using System.Net.Sockets;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Net;

namespace SocketJack.Tests;

[TestClass]
public sealed class HttpResourceHintsTests {
    [TestMethod]
    public async Task ResourceHintsReachBrowserInDocumentHeaderAndPreserveExistingLinks() {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        using var server = new HttpServer(new NetworkOptions { Logging = false, UsePeerToPeer = false }, port);
        server.OnHttpRequest += (NetworkConnection _, ref HttpContext context, CancellationToken _) => {
            context.Response.Headers["Link"] = "</guide>; rel=help";
            context.Response.PreloadResource("/assets/main.css?v=1234", HttpPreloadDestination.Style);
            context.Response.PreloadResource("/assets/inter.woff2", HttpPreloadDestination.Font);
            context.Response.PreloadModule("https://cdn.example.test/entry.mjs");
            context.Response.Body = "<p>Content</p>";
        };
        Assert.IsTrue(server.Listen());
        using var client = new System.Net.Http.HttpClient();
        using var result = await client.GetAsync($"http://127.0.0.1:{port}/document");
        Assert.AreEqual(HttpStatusCode.OK, result.StatusCode);
        Assert.AreEqual("</guide>; rel=help, </assets/main.css?v=1234>; rel=preload; as=style, " +
            "</assets/inter.woff2>; rel=preload; as=font; crossorigin=anonymous, " +
            "<https://cdn.example.test/entry.mjs>; rel=modulepreload", result.Headers.GetValues("Link").Single());
    }

    [DataTestMethod]
    [DataRow("//other.example/style.css")]
    [DataRow("https://user@other.example/style.css")]
    [DataRow("/file name.png")]
    [DataRow("/x>\r\nX-Test: injected")]
    [DataRow("/a.png#unused")]
    public void VersionedResponseHintsRejectAmbiguousOrUnsafeUrls(string url) {
        Assert.ThrowsException<ArgumentException>(() => HttpResourceHints.Preload(url, HttpPreloadDestination.Image));
    }
}
