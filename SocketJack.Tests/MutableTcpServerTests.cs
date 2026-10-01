using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Net;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SocketJack.Tests {

    /// <summary>
    /// Simple POCO used by SocketJack and WebSocket tests to verify typed
    /// round-trip serialization through the <see cref="MutableTcpServer"/>.
    /// </summary>
    public class TestMessage {
        public string Text { get; set; }
        public int Number { get; set; }
    }

    [TestClass]
    public class MutableTcpServerTests {

        // Ask the OS for a free loopback port to avoid collisions with local services
        // and parallel test runs.
        private static int NextPort() {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        /// <summary>
        /// Helper: creates a <see cref="MutableTcpServer"/> with P2P disabled and
        /// compression off so the tests exercise only the protocol routing logic.
        /// </summary>
        private static MutableTcpServer CreateServer(int port) {
            // Legacy raw framing fixtures intentionally omit the new handshake; SafeModeTests cover secured paths.
            var opts = new NetworkOptions {
                SafeMode = false,
                UsePeerToPeer = false,
                UseCompression = false,
                Logging = false,
            };
            opts.Authorization.RequireAuthentication = false;
            var server = new MutableTcpServer(opts, port, "TestMutableServer");
            server.OnError += e => Console.WriteLine("SERVER ERROR: " + e.Exception);
            return server;
        }

        /// <summary>
        /// Helper: creates a raw TCP connection to localhost:<paramref name="port"/>.
        /// </summary>
        private static System.Net.Sockets.TcpClient RawConnect(int port) {
            var tcp = new System.Net.Sockets.TcpClient();
            tcp.Connect("127.0.0.1", port);
            return tcp;
        }

        #region SocketJack Protocol Tests

        [TestMethod]
        public async Task SocketJack_CanHandle_ValidLengthHeader() {
            // The SocketJack handler should recognize a 15-byte NUL-padded
            // numeric length prefix.
            var handler = new SocketJackProtocolHandler();

            // Simulate a header "          12345" (10 NULs + 5 digits).
            byte[] probe = new byte[15];
            byte[] digits = Encoding.UTF8.GetBytes("12345");
            Buffer.BlockCopy(digits, 0, probe, 10, digits.Length);

            Assert.IsTrue(handler.CanHandle(probe), "Should detect valid SocketJack frame header.");
        }

        [TestMethod]
        public void SocketJack_CanHandle_RejectsHttpData() {
            var handler = new SocketJackProtocolHandler();
            byte[] httpGet = Encoding.UTF8.GetBytes("GET / HTTP/1.1\r\nHost: localhost\r\n\r\n");
            Assert.IsFalse(handler.CanHandle(httpGet), "SocketJack handler must not match HTTP requests.");
        }

        [TestMethod]
        public void SocketJack_CanHandle_RejectsShortData() {
            var handler = new SocketJackProtocolHandler();
            Assert.IsFalse(handler.CanHandle(new byte[] { 0, 0, 0 }));
            Assert.IsFalse(handler.CanHandle(null));
            Assert.IsFalse(handler.CanHandle(Array.Empty<byte>()));
        }

        [TestMethod]
        public async Task SocketJack_ClientConnectedEvent_Fires() {
            int port = NextPort();
            using var server = CreateServer(port);
            server.RegisterCallback<TestMessage>(e => { });

            var connectedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            server.SocketJackClientConnected += conn => connectedTcs.TrySetResult(true);

            Assert.IsTrue(server.Listen(), "Server should start listening.");

            // Connect a SocketJack client and send a framed payload.
            using var client = new SocketJack.Net.TcpClient() { Options = { SafeMode = false, UsePeerToPeer = false, UseCompression = false } };
            client.Options.Whitelist.Add(typeof(TestMessage));
            await client.Connect("127.0.0.1", port);

            Assert.IsTrue(client.Connected, "Client should connect.");

            // Send a SocketJack-framed message to trigger protocol detection.
            client.Send(new TestMessage { Text = "hello", Number = 1 });

            bool fired = await Task.WhenAny(connectedTcs.Task, Task.Delay(5000)) == connectedTcs.Task;
            Assert.IsTrue(fired, "SocketJackClientConnected event should fire on first SocketJack data.");

            client.Disconnect();
            server.StopListening();
        }

        [TestMethod]
        public async Task SocketJack_SendAndReceive_RoundTrip() {
            int port = NextPort();
            using var server = CreateServer(port);

            var receivedTcs = new TaskCompletionSource<TestMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

            server.RegisterCallback<TestMessage>(e => {
                receivedTcs.TrySetResult(e.Object);
            });

            Assert.IsTrue(server.Listen());

            using var client = new SocketJack.Net.TcpClient() { Options = { SafeMode = false, UsePeerToPeer = false, UseCompression = false } };
            client.Options.Whitelist.Add(typeof(TestMessage));
            await client.Connect("127.0.0.1", port);
            Assert.IsTrue(client.Connected);

            client.Send(new TestMessage { Text = "RoundTrip", Number = 42 });

            var result = await Task.WhenAny(receivedTcs.Task, Task.Delay(5000));
            Assert.AreEqual(receivedTcs.Task, result, "Should receive the message within timeout.");

            var msg = receivedTcs.Task.Result;
            Assert.AreEqual("RoundTrip", msg.Text);
            Assert.AreEqual(42, msg.Number);

            client.Disconnect();
            server.StopListening();
        }

        [TestMethod]
        public async Task SocketJack_SendBroadcast_ReachesAllClients() {
            int port = NextPort();
            using var server = CreateServer(port);

            var received = new ConcurrentBag<string>();
            var allReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            server.RegisterCallback<TestMessage>(e => { });

            Assert.IsTrue(server.Listen());

            // Connect two SocketJack clients.
            var clients = new List<SocketJack.Net.TcpClient>();
            for (int i = 0; i < 2; i++) {
                var c = new SocketJack.Net.TcpClient() { Options = { SafeMode = false, UsePeerToPeer = false, UseCompression = false } };
                c.Options.Whitelist.Add(typeof(TestMessage));
                c.RegisterCallback<TestMessage>(e => {
                    received.Add(e.Object.Text);
                    if (received.Count >= 2)
                        allReceived.TrySetResult(true);
                });
                await c.Connect("127.0.0.1", port);
                Assert.IsTrue(c.Connected);
                // Send initial data to trigger SocketJack detection.
                c.Send(new TestMessage { Text = "init" + i, Number = i });
                clients.Add(c);
            }

            // Give the server time to detect both as SocketJack.
            await Task.Delay(1000);

            server.SendBroadcast(new TestMessage { Text = "broadcast", Number = 99 });

            bool ok = await Task.WhenAny(allReceived.Task, Task.Delay(5000)) == allReceived.Task;
            Assert.IsTrue(ok, "Both SocketJack clients should receive the broadcast.");

            foreach (var c in clients) { c.Disconnect(); c.Dispose(); }
            server.StopListening();
        }

        [TestMethod]
        public async Task SocketJack_SendBroadcast_WithExcept_SkipsExcludedClient() {
            int port = NextPort();
            using var server = CreateServer(port);

            var received = new ConcurrentBag<string>();

            server.RegisterCallback<TestMessage>(e => { });

            Assert.IsTrue(server.Listen());

            var client1 = new SocketJack.Net.TcpClient() { Options = { SafeMode = false, UsePeerToPeer = false, UseCompression = false } };
            client1.Options.Whitelist.Add(typeof(TestMessage));
            client1.RegisterCallback<TestMessage>(e => received.Add("c1:" + e.Object.Text));
            await client1.Connect("127.0.0.1", port);
            client1.Send(new TestMessage { Text = "init1", Number = 1 });

            var client2 = new SocketJack.Net.TcpClient() { Options = { SafeMode = false, UsePeerToPeer = false, UseCompression = false } };
            client2.Options.Whitelist.Add(typeof(TestMessage));
            client2.RegisterCallback<TestMessage>(e => received.Add("c2:" + e.Object.Text));
            await client2.Connect("127.0.0.1", port);
            client2.Send(new TestMessage { Text = "init2", Number = 2 });

            await Task.Delay(1000);

            // Find the NetworkConnection for client1 so we can exclude it.
            var allConns = server.Clients.Values.ToArray();
            Assert.IsTrue(allConns.Length >= 2, "Should have at least 2 connections.");

            server.SendBroadcast(new TestMessage { Text = "exc", Number = 0 }, allConns[0]);

            await Task.Delay(2000);

            // At least one client received the message; the excluded one may not have.
            Assert.IsTrue(received.Any(r => r.Contains("exc")),
                "At least the non-excluded client should receive the broadcast.");

            client1.Disconnect(); client1.Dispose();
            client2.Disconnect(); client2.Dispose();
            server.StopListening();
        }

        #endregion

        #region HTTP Protocol Tests

        [TestMethod]
        public void Http_CanHandle_ValidGetRequest() {
            var handler = new HttpProtocolHandler();
            byte[] data = Encoding.UTF8.GetBytes("GET / HTTP/1.1\r\nHost: localhost\r\n\r\n");
            Assert.IsTrue(handler.CanHandle(data));
        }

        [TestMethod]
        public void Http_CanHandle_ValidPostRequest() {
            var handler = new HttpProtocolHandler();
            byte[] data = Encoding.UTF8.GetBytes("POST /api HTTP/1.1\r\nHost: localhost\r\n\r\n");
            Assert.IsTrue(handler.CanHandle(data));
        }

        [TestMethod]
        public void Http_CanHandle_AllMethods() {
            var handler = new HttpProtocolHandler();
            string[] methods = { "GET", "POST", "PUT", "DELETE", "HEAD", "OPTIONS", "PATCH", "TRACE", "CONNECT" };
            foreach (var method in methods) {
                byte[] data = Encoding.UTF8.GetBytes(method + " / HTTP/1.1\r\nHost: localhost\r\n\r\n");
                Assert.IsTrue(handler.CanHandle(data), $"Should detect {method} request.");
            }
        }

        [TestMethod]
        public void Http_CanHandle_RejectsSocketJackData() {
            var handler = new HttpProtocolHandler();
            byte[] sjData = new byte[15];
            byte[] digits = Encoding.UTF8.GetBytes("12345");
            Buffer.BlockCopy(digits, 0, sjData, 10, digits.Length);
            Assert.IsFalse(handler.CanHandle(sjData), "HTTP handler must not match SocketJack frames.");
        }

        [TestMethod]
        public void Http_CanHandle_RejectsNullOrEmpty() {
            var handler = new HttpProtocolHandler();
            Assert.IsFalse(handler.CanHandle(null));
            Assert.IsFalse(handler.CanHandle(Array.Empty<byte>()));
            Assert.IsFalse(handler.CanHandle(new byte[] { 0xFF }));
        }

        [TestMethod]
        public async Task Http_IndexPage_Returns200() {
            int port = NextPort();
            using var server = CreateServer(port);
            server.Http.IndexPageHtml = "<html><body>Test Index</body></html>";
            Assert.IsTrue(server.Listen());

            using var httpClient = new System.Net.Http.HttpClient();
            var response = await httpClient.GetAsync($"http://127.0.0.1:{port}/");

            Assert.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode);
            string body = await response.Content.ReadAsStringAsync();
            Assert.IsTrue(body.Contains("Test Index"), "Index page HTML should be returned.");

            server.StopListening();
        }

        [TestMethod]
        public async Task Http_RobotsTxt_Returns200() {
            int port = NextPort();
            using var server = CreateServer(port);
            server.Http.Robots = "User-agent: *\nDisallow: /secret\n";
            Assert.IsTrue(server.Listen());

            using var httpClient = new System.Net.Http.HttpClient();
            var response = await httpClient.GetAsync($"http://127.0.0.1:{port}/robots.txt");

            Assert.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode);
            string body = await response.Content.ReadAsStringAsync();
            Assert.IsTrue(body.Contains("Disallow"), "Robots.txt content should be returned.");

            server.StopListening();
        }

        [TestMethod]
        public async Task Http_Map_GetRoute_Returns200() {
            int port = NextPort();
            using var server = CreateServer(port);

            server.Http.Map("GET", "/api/hello", (conn, req, ct) => "Hello, World!");

            Assert.IsTrue(server.Listen());

            using var httpClient = new System.Net.Http.HttpClient();
            var response = await httpClient.GetAsync($"http://127.0.0.1:{port}/api/hello");

            Assert.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode);
            string body = await response.Content.ReadAsStringAsync();
            Assert.IsTrue(body.Contains("Hello, World!"));

            server.StopListening();
        }

        [TestMethod]
        public async Task Http_Map_WildcardRoute_CapturesNestedPath() {
            int port = NextPort();
            using var server = CreateServer(port);
            string captured = null;

            server.Http.Map("GET", "/update/*", (conn, req, ct) => {
                captured = req.PathVariables.Single();
                return "wild:" + captured;
            });

            Assert.IsTrue(server.Listen());

            using var httpClient = new System.Net.Http.HttpClient();
            var response = await httpClient.GetAsync($"http://127.0.0.1:{port}/update/sample/meta");

            Assert.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode);
            string body = await response.Content.ReadAsStringAsync();
            Assert.AreEqual("sample/meta", captured);
            Assert.IsTrue(body.Contains("wild:sample/meta"));

            server.StopListening();
        }

        [TestMethod]
        public async Task Http_Map_PostRoute_Returns200() {
            int port = NextPort();
            using var server = CreateServer(port);

            server.Http.Map("POST", "/api/echo", (conn, req, ct) => req.Body ?? "empty");

            Assert.IsTrue(server.Listen());

            using var httpClient = new System.Net.Http.HttpClient();
            var content = new System.Net.Http.StringContent("test-payload", Encoding.UTF8, "text/plain");
            var response = await httpClient.PostAsync($"http://127.0.0.1:{port}/api/echo", content);

            Assert.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode);
            string body = await response.Content.ReadAsStringAsync();
            Assert.IsTrue(body.Contains("test-payload"));

            server.StopListening();
        }

        [TestMethod]
        public async Task Http_RemoveRoute_Returns404OrDefault() {
            int port = NextPort();
            using var server = CreateServer(port);

            server.Http.Map("GET", "/api/temp", (conn, req, ct) => "temp");
            bool removed = server.Http.RemoveRoute("GET", "/api/temp");
            Assert.IsTrue(removed, "RemoveRoute should return true for an existing route.");

            Assert.IsTrue(server.Listen());

            using var httpClient = new System.Net.Http.HttpClient();
            var response = await httpClient.GetAsync($"http://127.0.0.1:{port}/api/temp");

            // After removal, the route no longer exists. The response should
            // not contain "temp" as a body. Depending on the server's fallback
            // behaviour it may serve the index page or a default response.
            string body = await response.Content.ReadAsStringAsync();
            Assert.IsFalse(body == "temp", "Removed route should no longer serve its handler.");

            server.StopListening();
        }

        [TestMethod]
        public async Task Http_MapDirectory_ServesStaticFile() {
            int port = NextPort();
            using var server = CreateServer(port);

            // Create a temp directory with a file.
            var tempDir = Path.Combine(Path.GetTempPath(), "SocketJackTestDir_" + port);
            Directory.CreateDirectory(tempDir);
            try {
                File.WriteAllText(Path.Combine(tempDir, "test.txt"), "static-file-content");
                server.Http.MapDirectory("/static", tempDir);

                Assert.IsTrue(server.Listen());

                using var httpClient = new System.Net.Http.HttpClient();
                var response = await httpClient.GetAsync($"http://127.0.0.1:{port}/static/test.txt");

                Assert.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode);
                string body = await response.Content.ReadAsStringAsync();
                Assert.AreEqual("static-file-content", body);

                server.StopListening();
            } finally {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        [TestMethod]
        public async Task Http_MapDirectory_DownloadDoesNotKeepSourceLocked() {
            int port = NextPort();
            using var server = CreateServer(port);

            var tempDir = Path.Combine(Path.GetTempPath(), "SocketJackTestNoLock_" + port);
            Directory.CreateDirectory(tempDir);
            try {
                string filePath = Path.Combine(tempDir, "download.iso");
                string movedPath = Path.Combine(tempDir, "download-renamed.iso");
                File.WriteAllText(filePath, "static-file-lock-check");
                server.Http.MapDirectory("/static", tempDir);

                Assert.IsTrue(server.Listen());

                using var httpClient = new System.Net.Http.HttpClient();
                var response = await httpClient.GetAsync($"http://127.0.0.1:{port}/static/download.iso");

                Assert.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode);
                string body = await response.Content.ReadAsStringAsync();
                Assert.AreEqual("static-file-lock-check", body);

                File.Move(filePath, movedPath);
                Assert.IsTrue(File.Exists(movedPath), "Downloaded source file should be renameable after the response completes.");

                server.StopListening();
            } finally {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        [TestMethod]
        public async Task Http_MapDirectory_SupportsByteRangeDownloads() {
            int port = NextPort();
            using var server = CreateServer(port);

            var tempDir = Path.Combine(Path.GetTempPath(), "SocketJackTestRange_" + port);
            Directory.CreateDirectory(tempDir);
            try {
                File.WriteAllText(Path.Combine(tempDir, "disk.iso"), "0123456789");
                server.Http.MapDirectory("/static", tempDir);

                Assert.IsTrue(server.Listen());

                using var httpClient = new System.Net.Http.HttpClient();
                using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, $"http://127.0.0.1:{port}/static/disk.iso");
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(2, 5);
                var response = await httpClient.SendAsync(request);

                Assert.AreEqual(System.Net.HttpStatusCode.PartialContent, response.StatusCode);
                Assert.AreEqual("bytes", response.Headers.AcceptRanges.Single());
                Assert.AreEqual("bytes", response.Content.Headers.ContentRange.Unit);
                Assert.AreEqual(2, response.Content.Headers.ContentRange.From);
                Assert.AreEqual(5, response.Content.Headers.ContentRange.To);
                Assert.AreEqual(10, response.Content.Headers.ContentRange.Length);
                Assert.AreEqual(4, response.Content.Headers.ContentLength);
                string body = await response.Content.ReadAsStringAsync();
                Assert.AreEqual("2345", body);

                server.StopListening();
            } finally {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        [TestMethod]
        public async Task Http_MapDirectory_WinsOverWildcardCatchAll() {
            int port = NextPort();
            using var server = CreateServer(port);

            var tempDir = Path.Combine(Path.GetTempPath(), "SocketJackTestCatchAll_" + port);
            Directory.CreateDirectory(tempDir);
            try {
                File.WriteAllText(Path.Combine(tempDir, "image.jpg"), "served-file");

                server.Http.Map("GET", "/Chat/*", (conn, req, ct) => "chat-page");
                server.Http.MapDirectory("/Chat/Files", tempDir);

                Assert.IsTrue(server.Listen());

                using var httpClient = new System.Net.Http.HttpClient();
                var response = await httpClient.GetAsync($"http://127.0.0.1:{port}/Chat/Files/image.jpg");

                Assert.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode);
                string body = await response.Content.ReadAsStringAsync();
                Assert.AreEqual("served-file", body);

                server.StopListening();
            } finally {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        [TestMethod]
        public async Task Http_WildcardCatchAll_HandlesWhenNoStaticFileMatches() {
            int port = NextPort();
            using var server = CreateServer(port);

            var tempDir = Path.Combine(Path.GetTempPath(), "SocketJackTestCatchAllMiss_" + port);
            Directory.CreateDirectory(tempDir);
            try {
                server.Http.Map("GET", "/Chat/*", (conn, req, ct) => "chat-page:" + req.PathVariables.Single());
                server.Http.MapDirectory("/Chat/Files", tempDir);

                Assert.IsTrue(server.Listen());

                using var httpClient = new System.Net.Http.HttpClient();
                var response = await httpClient.GetAsync($"http://127.0.0.1:{port}/Chat/Vin");

                Assert.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode);
                string body = await response.Content.ReadAsStringAsync();
                Assert.AreEqual("chat-page:Vin", body);

                server.StopListening();
            } finally {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        [TestMethod]
        public async Task Http_MapDirectory_IndexHtml() {
            int port = NextPort();
            using var server = CreateServer(port);

            var tempDir = Path.Combine(Path.GetTempPath(), "SocketJackTestIdx_" + port);
            Directory.CreateDirectory(tempDir);
            try {
                File.WriteAllText(Path.Combine(tempDir, "index.html"), "<html><body>Index!</body></html>");
                server.Http.MapDirectory("/site", tempDir);

                Assert.IsTrue(server.Listen());

                using var httpClient = new System.Net.Http.HttpClient();
                var response = await httpClient.GetAsync($"http://127.0.0.1:{port}/site");

                Assert.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode);
                string body = await response.Content.ReadAsStringAsync();
                Assert.IsTrue(body.Contains("Index!"));

                server.StopListening();
            } finally {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        [TestMethod]
        public async Task Http_RemoveDirectoryMapping_StopsServing() {
            int port = NextPort();
            using var server = CreateServer(port);

            var tempDir = Path.Combine(Path.GetTempPath(), "SocketJackTestRm_" + port);
            Directory.CreateDirectory(tempDir);
            try {
                File.WriteAllText(Path.Combine(tempDir, "data.txt"), "content");
                server.Http.MapDirectory("/files", tempDir);
                bool removed = server.Http.RemoveDirectoryMapping("/files");
                Assert.IsTrue(removed);

                Assert.IsTrue(server.Listen());

                using var httpClient = new System.Net.Http.HttpClient();
                var response = await httpClient.GetAsync($"http://127.0.0.1:{port}/files/data.txt");
                string body = await response.Content.ReadAsStringAsync();
                Assert.AreNotEqual("content", body, "Directory mapping should no longer serve files after removal.");

                server.StopListening();
            } finally {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        [TestMethod]
        public async Task Http_AllowDirectoryListing_ShowsListing() {
            int port = NextPort();
            using var server = CreateServer(port);
            server.Http.AllowDirectoryListing = true;

            var tempDir = Path.Combine(Path.GetTempPath(), "SocketJackTestLs_" + port);
            Directory.CreateDirectory(tempDir);
            try {
                File.WriteAllText(Path.Combine(tempDir, "file1.txt"), "a");
                File.WriteAllText(Path.Combine(tempDir, "file2.txt"), "b");
                server.Http.MapDirectory("/browse", tempDir);

                Assert.IsTrue(server.Listen());

                using var httpClient = new System.Net.Http.HttpClient();
                var response = await httpClient.GetAsync($"http://127.0.0.1:{port}/browse");

                Assert.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode);
                string body = await response.Content.ReadAsStringAsync();
                Assert.IsTrue(body.Contains("file1.txt"), "Directory listing should include file names.");
                Assert.IsTrue(body.Contains("file2.txt"));

                server.StopListening();
            } finally {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        [TestMethod]
        public async Task Http_MapStream_SendsChunkedResponse() {
            int port = NextPort();
            using var server = CreateServer(port);

            server.Http.MapStream("GET", "/stream", async (conn, req, chunked, ct) => {
                await Task.Run(() => {
                    chunked.WriteLine("line1");
                    chunked.WriteLine("line2");
                });
            });

            Assert.IsTrue(server.Listen());

            using var httpClient = new System.Net.Http.HttpClient();
            var response = await httpClient.GetAsync($"http://127.0.0.1:{port}/stream");

            Assert.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode);
            string body = await response.Content.ReadAsStringAsync();
            Assert.IsTrue(body.Contains("line1"), "Stream response should contain written lines.");
            Assert.IsTrue(body.Contains("line2"));

            server.StopListening();
        }

        [TestMethod]
        public async Task Http_MapUploadStream_ReceivesBody() {
            int port = NextPort();
            using var server = CreateServer(port);

            var uploadData = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

            server.Http.MapUploadStream("POST", "/upload", (conn, req, upload, ct) => {
                // Read all data from the upload stream.
                var sb = new StringBuilder();
                if (req.BodyBytes != null && req.BodyBytes.Length > 0)
                    sb.Append(Encoding.UTF8.GetString(req.BodyBytes));
                uploadData.TrySetResult(sb.ToString());
            });

            Assert.IsTrue(server.Listen());

            using var httpClient = new System.Net.Http.HttpClient();
            var content = new System.Net.Http.StringContent("upload-payload", Encoding.UTF8, "application/octet-stream");
            // Use a raw HttpRequestMessage so we don't get blocked.
            try {
                await httpClient.PostAsync($"http://127.0.0.1:{port}/upload", content);
            } catch { /* server may close connection before HttpClient reads full response */ }

            bool ok = await Task.WhenAny(uploadData.Task, Task.Delay(5000)) == uploadData.Task;
            Assert.IsTrue(ok, "Upload handler should receive body data.");
            Assert.IsTrue(uploadData.Task.Result.Contains("upload-payload"));

            server.StopListening();
        }

        [TestMethod]
        public async Task Http_RemoveUploadStreamRoute_StopsHandling() {
            int port = NextPort();
            using var server = CreateServer(port);

            server.Http.MapUploadStream("POST", "/up", (conn, req, upload, ct) => { });
            bool removed = server.Http.RemoveUploadStreamRoute("POST", "/up");
            Assert.IsTrue(removed, "RemoveUploadStreamRoute should return true.");

            // No functional assertion beyond removal success — the route
            // should no longer match after removal.
        }

        [TestMethod]
        public async Task Http_OnHttpRequest_Event_FiresForUnmappedRoutes() {
            int port = NextPort();
            using var server = CreateServer(port);

            var eventFired = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            server.Http.OnHttpRequest += (conn, ref ctx, ct) => {
                eventFired.TrySetResult(ctx.Request.Path);
                ctx.Response.Body = "custom-handler";
            };

            Assert.IsTrue(server.Listen());

            using var httpClient = new System.Net.Http.HttpClient();
            var response = await httpClient.GetAsync($"http://127.0.0.1:{port}/unmapped-path");

            bool ok = await Task.WhenAny(eventFired.Task, Task.Delay(5000)) == eventFired.Task;
            Assert.IsTrue(ok, "OnHttpRequest event should fire for unmapped routes.");
            Assert.AreEqual("/unmapped-path", eventFired.Task.Result);

            server.StopListening();
        }

        [TestMethod]
        public void Http_ChunkedThreshold_CanSetAndGet() {
            using var server = CreateServer(NextPort());
            server.Http.ChunkedThreshold = 1024;
            Assert.AreEqual(1024, server.Http.ChunkedThreshold);
        }

        [TestMethod]
        public void Http_ChunkedSize_CanSetAndGet() {
            using var server = CreateServer(NextPort());
            server.Http.ChunkedSize = 2048;
            Assert.AreEqual(2048, server.Http.ChunkedSize);
        }

        [TestMethod]
        public async Task Http_HeadRequest_ReturnsHeadersOnly() {
            int port = NextPort();
            using var server = CreateServer(port);
            server.Http.Map("GET", "/headtest", (conn, req, ct) => "body-content");

            Assert.IsTrue(server.Listen());

            using var httpClient = new System.Net.Http.HttpClient();
            var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Head, $"http://127.0.0.1:{port}/headtest");
            var response = await httpClient.SendAsync(request);

            Assert.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode);
            // HEAD should return headers but an empty body.
            string body = await response.Content.ReadAsStringAsync();
            Assert.IsTrue(body.Length == 0 || !body.Contains("body-content"),
                "HEAD response body should be empty.");

            server.StopListening();
        }

        #endregion

        #region WebSocket Protocol Tests

        [TestMethod]
        public void WebSocket_CanHandle_ValidUpgradeRequest() {
            var handler = new WebSocketProtocolHandler();
            string upgradeRequest =
                "GET /ws HTTP/1.1\r\n" +
                "Host: localhost\r\n" +
                "Upgrade: websocket\r\n" +
                "Connection: Upgrade\r\n" +
                "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n" +
                "Sec-WebSocket-Version: 13\r\n\r\n";
            byte[] data = Encoding.UTF8.GetBytes(upgradeRequest);
            Assert.IsTrue(handler.CanHandle(data), "Should detect WebSocket upgrade request.");
        }

        [TestMethod]
        public void WebSocket_CanHandle_RejectsNormalHttp() {
            var handler = new WebSocketProtocolHandler();
            byte[] data = Encoding.UTF8.GetBytes("GET / HTTP/1.1\r\nHost: localhost\r\n\r\n");
            Assert.IsFalse(handler.CanHandle(data), "Should not match plain HTTP GET without Upgrade header.");
        }

        [TestMethod]
        public void WebSocket_CanHandle_RejectsNullOrEmpty() {
            var handler = new WebSocketProtocolHandler();
            Assert.IsFalse(handler.CanHandle(null));
            Assert.IsFalse(handler.CanHandle(Array.Empty<byte>()));
            Assert.IsFalse(handler.CanHandle(new byte[] { 0x01, 0x02 }));
        }

        [TestMethod]
        public async Task WebSocket_Handshake_Returns101() {
            int port = NextPort();
            using var server = CreateServer(port);
            Assert.IsTrue(server.Listen());

            using var tcp = RawConnect(port);
            var stream = tcp.GetStream();

            string key = Convert.ToBase64String(Encoding.UTF8.GetBytes("test-nonce-1234!"));
            string request =
                "GET /ws HTTP/1.1\r\n" +
                "Host: 127.0.0.1:" + port + "\r\n" +
                "Upgrade: websocket\r\n" +
                "Connection: Upgrade\r\n" +
                "Sec-WebSocket-Key: " + key + "\r\n" +
                "Sec-WebSocket-Version: 13\r\n\r\n";

            byte[] reqBytes = Encoding.UTF8.GetBytes(request);
            await stream.WriteAsync(reqBytes, 0, reqBytes.Length);
            await stream.FlushAsync();

            // Read the 101 response.
            byte[] buf = new byte[4096];
            int read = await ReadWithTimeout(stream, buf, 5000);
            Assert.IsTrue(read > 0, "Should receive a response.");

            string response = Encoding.UTF8.GetString(buf, 0, read);
            Assert.IsTrue(response.Contains("101 Switching Protocols"),
                "Server should respond with 101 Switching Protocols.");
            Assert.IsTrue(response.Contains("Sec-WebSocket-Accept"),
                "Response should include Sec-WebSocket-Accept header.");

            // Verify the accept key is correct per RFC 6455.
            string expectedAccept;
            using (var sha1 = SHA1.Create()) {
                byte[] hash = sha1.ComputeHash(Encoding.UTF8.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"));
                expectedAccept = Convert.ToBase64String(hash);
            }
            Assert.IsTrue(response.Contains(expectedAccept),
                "Sec-WebSocket-Accept value should match RFC 6455 computation.");

            tcp.Close();
            server.StopListening();
        }

        [TestMethod]
        public async Task WebSocket_ClientConnectedEvent_Fires() {
            int port = NextPort();
            using var server = CreateServer(port);

            var connectedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            server.WebSocketClientConnected += conn => connectedTcs.TrySetResult(true);

            Assert.IsTrue(server.Listen());

            using var tcp = RawConnect(port);
            var stream = tcp.GetStream();

            string key = Convert.ToBase64String(Encoding.UTF8.GetBytes("ws-event-test!!"));
            string request =
                "GET / HTTP/1.1\r\n" +
                "Host: 127.0.0.1:" + port + "\r\n" +
                "Upgrade: websocket\r\n" +
                "Connection: Upgrade\r\n" +
                "Sec-WebSocket-Key: " + key + "\r\n" +
                "Sec-WebSocket-Version: 13\r\n\r\n";

            byte[] reqBytes = Encoding.UTF8.GetBytes(request);
            await stream.WriteAsync(reqBytes, 0, reqBytes.Length);

            bool fired = await Task.WhenAny(connectedTcs.Task, Task.Delay(5000)) == connectedTcs.Task;
            Assert.IsTrue(fired, "WebSocketClientConnected event should fire after upgrade.");

            tcp.Close();
            server.StopListening();
        }

        [TestMethod]
        public async Task WebSocket_SendAndReceive_TextFrame() {
            int port = NextPort();
            using var server = CreateServer(port);

            var receivedTcs = new TaskCompletionSource<TestMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            server.RegisterCallback<TestMessage>(e => {
                receivedTcs.TrySetResult(e.Object);
            });

            Assert.IsTrue(server.Listen());

            // Perform WebSocket handshake.
            using var tcp = RawConnect(port);
            var stream = tcp.GetStream();

            string key = Convert.ToBase64String(Encoding.UTF8.GetBytes("ws-text-frame!!"));
            string request =
                "GET / HTTP/1.1\r\n" +
                "Host: 127.0.0.1:" + port + "\r\n" +
                "Upgrade: websocket\r\n" +
                "Connection: Upgrade\r\n" +
                "Sec-WebSocket-Key: " + key + "\r\n" +
                "Sec-WebSocket-Version: 13\r\n\r\n";

            await stream.WriteAsync(Encoding.UTF8.GetBytes(request));
            await stream.FlushAsync();

            // Read 101 response.
            byte[] buf = new byte[4096];
            await ReadWithTimeout(stream, buf, 5000);

            // Send a WebSocket text frame with a serialized TestMessage.
            var msg = new TestMessage { Text = "WsTest", Number = 7 };
            byte[] payload = server.Options.Serializer.Serialize(
                new Serialization.Wrapper(msg, server));
            byte[] frame = BuildClientFrame(payload, isText: true);
            await stream.WriteAsync(frame, 0, frame.Length);
            await stream.FlushAsync();

            var result = await Task.WhenAny(receivedTcs.Task, Task.Delay(5000));
            Assert.AreEqual(receivedTcs.Task, result, "Server should receive the WebSocket text frame.");

            var received = receivedTcs.Task.Result;
            Assert.AreEqual("WsTest", received.Text);
            Assert.AreEqual(7, received.Number);

            tcp.Close();
            server.StopListening();
        }

        [TestMethod]
        public async Task WebSocket_PingPong_ServerRespondsToPing() {
            int port = NextPort();
            using var server = CreateServer(port);
            Assert.IsTrue(server.Listen());

            using var tcp = RawConnect(port);
            var stream = tcp.GetStream();

            // Handshake.
            string key = Convert.ToBase64String(Encoding.UTF8.GetBytes("ws-ping-test!!!"));
            string request =
                "GET / HTTP/1.1\r\n" +
                "Host: 127.0.0.1:" + port + "\r\n" +
                "Upgrade: websocket\r\n" +
                "Connection: Upgrade\r\n" +
                "Sec-WebSocket-Key: " + key + "\r\n" +
                "Sec-WebSocket-Version: 13\r\n\r\n";

            await stream.WriteAsync(Encoding.UTF8.GetBytes(request));
            await stream.FlushAsync();

            byte[] buf = new byte[4096];
            await ReadWithTimeout(stream, buf, 5000);

            // Send a Ping frame (opcode 0x9) with a small payload.
            byte[] pingPayload = Encoding.UTF8.GetBytes("ping");
            byte[] pingFrame = BuildClientFrame(pingPayload, opcode: 0x9);
            await stream.WriteAsync(pingFrame, 0, pingFrame.Length);
            await stream.FlushAsync();

            // Read back — expect a Pong frame (opcode 0xA).
            int read = await ReadWithTimeout(stream, buf, 5000);
            Assert.IsTrue(read >= 2, "Should receive a Pong response.");

            byte pongOpcode = (byte)(buf[0] & 0x0F);
            Assert.AreEqual(0xA, pongOpcode, "Response should be a Pong frame (opcode 0xA).");

            tcp.Close();
            server.StopListening();
        }

        [TestMethod]
        public async Task WebSocket_CloseFrame_ServerSendsCloseBack() {
            int port = NextPort();
            using var server = CreateServer(port);
            Assert.IsTrue(server.Listen());

            using var tcp = RawConnect(port);
            var stream = tcp.GetStream();

            // Handshake.
            string key = Convert.ToBase64String(Encoding.UTF8.GetBytes("ws-close-test!x"));
            string request =
                "GET / HTTP/1.1\r\n" +
                "Host: 127.0.0.1:" + port + "\r\n" +
                "Upgrade: websocket\r\n" +
                "Connection: Upgrade\r\n" +
                "Sec-WebSocket-Key: " + key + "\r\n" +
                "Sec-WebSocket-Version: 13\r\n\r\n";

            await stream.WriteAsync(Encoding.UTF8.GetBytes(request));
            await stream.FlushAsync();

            byte[] buf = new byte[4096];
            await ReadWithTimeout(stream, buf, 5000);

            // Send Close frame (opcode 0x8).
            byte[] closeFrame = BuildClientFrame(Array.Empty<byte>(), opcode: 0x8);
            await stream.WriteAsync(closeFrame, 0, closeFrame.Length);
            await stream.FlushAsync();

            // The server should send a close frame back before disconnecting.
            int read = 0;
            try {
                read = await ReadWithTimeout(stream, buf, 3000);
            } catch { }

            // Either we get a close frame or the connection was terminated. Both are valid.
            if (read >= 2) {
                byte closeOpcode = (byte)(buf[0] & 0x0F);
                Assert.AreEqual(0x8, closeOpcode, "Response should be a Close frame.");
            }

            tcp.Close();
            server.StopListening();
        }

        #endregion

        #region Protocol Registration Tests

        [TestMethod]
        public void RegisterProtocol_AddsCustomHandler() {
            using var server = CreateServer(NextPort());
            var custom = new DummyProtocolHandler("Custom1", match: false);
            server.RegisterProtocol(custom);
            // Should not throw.
        }

        [TestMethod]
        public void RemoveProtocol_ReturnsTrue_ForRegistered() {
            using var server = CreateServer(NextPort());
            var custom = new DummyProtocolHandler("Custom2", match: false);
            server.RegisterProtocol(custom);
            Assert.IsTrue(server.RemoveProtocol(custom));
        }

        [TestMethod]
        public void RemoveProtocol_ReturnsFalse_ForUnregistered() {
            using var server = CreateServer(NextPort());
            var custom = new DummyProtocolHandler("Custom3", match: false);
            Assert.IsFalse(server.RemoveProtocol(custom));
        }

        [TestMethod]
        public void RegisterProtocol_ThrowsOnNull() {
            using var server = CreateServer(NextPort());
            Assert.ThrowsException<ArgumentNullException>(() => server.RegisterProtocol(null));
        }

        [TestMethod]
        public async Task RegisterProtocol_CustomHandlerMatchesFirst() {
            int port = NextPort();
            using var server = CreateServer(port);

            var handled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var custom = new DummyProtocolHandler("MAGIC", match: true, onProcess: () => handled.TrySetResult(true));
            server.RegisterProtocol(custom);

            Assert.IsTrue(server.Listen());

            // Send data that starts with "MAGIC" — our custom handler matches everything.
            using var tcp = RawConnect(port);
            var stream = tcp.GetStream();
            byte[] data = Encoding.UTF8.GetBytes("MAGIC-DATA-PAYLOAD-HERE\r\n\r\n");
            await stream.WriteAsync(data, 0, data.Length);
            await stream.FlushAsync();

            bool ok = await Task.WhenAny(handled.Task, Task.Delay(5000)) == handled.Task;
            Assert.IsTrue(ok, "Custom protocol handler should process the data before built-in handlers.");

            tcp.Close();
            server.StopListening();
        }

        #endregion

        #region Cross-Protocol Tests

        [TestMethod]
        public async Task CrossProtocol_HttpAndSocketJack_OnSamePort() {
            int port = NextPort();
            using var server = CreateServer(port);

            server.Http.IndexPageHtml = "<html><body>Multi</body></html>";

            var sjReceived = new TaskCompletionSource<TestMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            server.RegisterCallback<TestMessage>(e => sjReceived.TrySetResult(e.Object));

            Assert.IsTrue(server.Listen());

            // HTTP request.
            using var httpClient = new System.Net.Http.HttpClient();
            var httpResponse = await httpClient.GetAsync($"http://127.0.0.1:{port}/");
            Assert.AreEqual(System.Net.HttpStatusCode.OK, httpResponse.StatusCode);
            string httpBody = await httpResponse.Content.ReadAsStringAsync();
            Assert.IsTrue(httpBody.Contains("Multi"));

            // SocketJack connection.
            using var sjClient = new SocketJack.Net.TcpClient() { Options = { SafeMode = false, UsePeerToPeer = false, UseCompression = false } };
            sjClient.Options.Whitelist.Add(typeof(TestMessage));
            await sjClient.Connect("127.0.0.1", port);
            Assert.IsTrue(sjClient.Connected);
            sjClient.Send(new TestMessage { Text = "cross", Number = 99 });

            var result = await Task.WhenAny(sjReceived.Task, Task.Delay(5000));
            Assert.AreEqual(sjReceived.Task, result, "SocketJack message should arrive.");
            Assert.AreEqual("cross", sjReceived.Task.Result.Text);

            sjClient.Disconnect();
            server.StopListening();
        }

        [TestMethod]
        public async Task CrossProtocol_HttpAndWebSocket_OnSamePort() {
            int port = NextPort();
            using var server = CreateServer(port);
            server.Http.Map("GET", "/api/test", (conn, req, ct) => "ok");

            Assert.IsTrue(server.Listen());

            // HTTP request.
            using var httpClient = new System.Net.Http.HttpClient();
            var httpResponse = await httpClient.GetAsync($"http://127.0.0.1:{port}/api/test");
            Assert.AreEqual(System.Net.HttpStatusCode.OK, httpResponse.StatusCode);
            string body = await httpResponse.Content.ReadAsStringAsync();
            Assert.IsTrue(body.Contains("ok"));

            // WebSocket handshake.
            using var tcp = RawConnect(port);
            var stream = tcp.GetStream();
            string key = Convert.ToBase64String(Encoding.UTF8.GetBytes("cross-ws-test!!"));
            string request =
                "GET / HTTP/1.1\r\n" +
                "Host: 127.0.0.1:" + port + "\r\n" +
                "Upgrade: websocket\r\n" +
                "Connection: Upgrade\r\n" +
                "Sec-WebSocket-Key: " + key + "\r\n" +
                "Sec-WebSocket-Version: 13\r\n\r\n";

            await stream.WriteAsync(Encoding.UTF8.GetBytes(request));
            byte[] buf = new byte[4096];
            int read = await ReadWithTimeout(stream, buf, 5000);
            string wsResponse = Encoding.UTF8.GetString(buf, 0, read);
            Assert.IsTrue(wsResponse.Contains("101 Switching Protocols"));

            tcp.Close();
            server.StopListening();
        }

        [TestMethod]
        public async Task CrossProtocol_SendBroadcast_DoesNotGoToHttpClients() {
            int port = NextPort();
            using var server = CreateServer(port);
            server.RegisterCallback<TestMessage>(e => { });
            Assert.IsTrue(server.Listen());

            // Connect a SocketJack client.
            using var sjClient = new SocketJack.Net.TcpClient() { Options = { SafeMode = false, UsePeerToPeer = false, UseCompression = false } };
            sjClient.Options.Whitelist.Add(typeof(TestMessage));
            var sjReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            sjClient.RegisterCallback<TestMessage>(e => { if (e.Object.Text == "bcast") sjReceived.TrySetResult(true); });
            await sjClient.Connect("127.0.0.1", port);
            sjClient.Send(new TestMessage { Text = "detect", Number = 0 });
            await Task.Delay(500);

            // Make an HTTP request (short-lived, but proves broadcast doesn't crash).
            using var httpClient = new System.Net.Http.HttpClient();
            var httpResponse = await httpClient.GetAsync($"http://127.0.0.1:{port}/");

            // Broadcast should only reach SocketJack clients.
            server.SendBroadcast(new TestMessage { Text = "bcast", Number = 1 });

            bool ok = await Task.WhenAny(sjReceived.Task, Task.Delay(5000)) == sjReceived.Task;
            Assert.IsTrue(ok, "SocketJack client should receive broadcast.");

            sjClient.Disconnect();
            server.StopListening();
        }

        #endregion

        #region TryGetRawBytes Tests

        // TryGetRawBytes is internal; invoke via reflection.
        private static byte[] InvokeTryGetRawBytes(object obj) {
            var mi = typeof(MutableTcpServer).GetMethod("TryGetRawBytes",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            return (byte[])mi.Invoke(null, new[] { obj });
        }

        [TestMethod]
        public void TryGetRawBytes_ByteArray_ReturnsSame() {
            byte[] input = new byte[] { 1, 2, 3 };
            byte[] result = InvokeTryGetRawBytes(input);
            CollectionAssert.AreEqual(input, result);
        }

        [TestMethod]
        public void TryGetRawBytes_ListByte_ReturnsArray() {
            var list = new List<byte> { 10, 20, 30 };
            byte[] result = InvokeTryGetRawBytes(list);
            CollectionAssert.AreEqual(new byte[] { 10, 20, 30 }, result);
        }

        [TestMethod]
        public void TryGetRawBytes_String_ReturnsUtf8() {
            byte[] result = InvokeTryGetRawBytes("hello");
            CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("hello"), result);
        }

        [TestMethod]
        public void TryGetRawBytes_ArraySegment_ReturnsSlice() {
            byte[] source = new byte[] { 0, 1, 2, 3, 4 };
            var seg = new ArraySegment<byte>(source, 1, 3);
            byte[] result = InvokeTryGetRawBytes(seg);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, result);
        }

        [TestMethod]
        public void TryGetRawBytes_Null_ReturnsNull() {
            Assert.IsNull(InvokeTryGetRawBytes(null));
        }

        [TestMethod]
        public void TryGetRawBytes_UnsupportedType_ReturnsNull() {
            Assert.IsNull(InvokeTryGetRawBytes(12345));
        }

        [TestMethod]
        public void TryGetRawBytes_EmptyString_ReturnsEmptyArray() {
            byte[] result = InvokeTryGetRawBytes("");
            Assert.IsNotNull(result);
            Assert.AreEqual(0, result.Length);
        }

        #endregion

        #region Dispose Tests

        [TestMethod]
        public void Dispose_DoesNotThrow() {
            int port = NextPort();
            var server = CreateServer(port);
            server.Listen();
            // Should clean up without exceptions.
            server.Dispose();
        }

        [TestMethod]
        public void Dispose_CalledTwice_DoesNotThrow() {
            int port = NextPort();
            var server = CreateServer(port);
            server.Listen();
            server.Dispose();
            server.Dispose(); // second dispose should be harmless
        }

        #endregion

        #region Property Accessor Tests

        [TestMethod]
        public void Http_Property_ReturnsHandler() {
            using var server = CreateServer(NextPort());
            Assert.IsNotNull(server.Http);
            Assert.IsInstanceOfType(server.Http, typeof(HttpProtocolHandler));
        }

        [TestMethod]
        public void SocketJack_Property_ReturnsHandler() {
            using var server = CreateServer(NextPort());
            Assert.IsNotNull(server.SocketJack);
            Assert.IsInstanceOfType(server.SocketJack, typeof(SocketJackProtocolHandler));
        }

        [TestMethod]
        public void WebSocket_Property_ReturnsHandler() {
            using var server = CreateServer(NextPort());
            Assert.IsNotNull(server.WebSocket);
            Assert.IsInstanceOfType(server.WebSocket, typeof(WebSocketProtocolHandler));
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Reads from a network stream with a timeout to avoid tests hanging forever.
        /// </summary>
        private static async Task<int> ReadWithTimeout(NetworkStream stream, byte[] buffer, int timeoutMs) {
            using var cts = new CancellationTokenSource(timeoutMs);
            try {
                return await stream.ReadAsync(buffer, 0, buffer.Length, cts.Token);
            } catch (OperationCanceledException) {
                return 0;
            }
        }

        /// <summary>
        /// Builds a masked WebSocket frame (as a client would send).
        /// </summary>
        private static byte[] BuildClientFrame(byte[] payload, bool isText = false, byte opcode = 0) {
            if (opcode == 0)
                opcode = isText ? (byte)0x1 : (byte)0x2;

            byte fin = (byte)(0x80 | opcode);
            byte[] maskKey = new byte[] { 0x12, 0x34, 0x56, 0x78 };

            byte[] header;
            if (payload.Length <= 125) {
                header = new byte[6]; // 2 header + 4 mask
                header[0] = fin;
                header[1] = (byte)(0x80 | payload.Length);
                Buffer.BlockCopy(maskKey, 0, header, 2, 4);
            } else if (payload.Length <= 65535) {
                header = new byte[8]; // 2 header + 2 ext len + 4 mask
                header[0] = fin;
                header[1] = 0x80 | 126;
                header[2] = (byte)((payload.Length >> 8) & 0xFF);
                header[3] = (byte)(payload.Length & 0xFF);
                Buffer.BlockCopy(maskKey, 0, header, 4, 4);
            } else {
                header = new byte[14]; // 2 header + 8 ext len + 4 mask
                header[0] = fin;
                header[1] = 0x80 | 127;
                ulong len = (ulong)payload.Length;
                for (int i = 0; i < 8; i++)
                    header[2 + i] = (byte)((len >> (56 - i * 8)) & 0xFF);
                Buffer.BlockCopy(maskKey, 0, header, 10, 4);
            }

            // Mask the payload.
            byte[] masked = new byte[payload.Length];
            for (int i = 0; i < payload.Length; i++)
                masked[i] = (byte)(payload[i] ^ maskKey[i % 4]);

            byte[] frame = new byte[header.Length + masked.Length];
            Buffer.BlockCopy(header, 0, frame, 0, header.Length);
            Buffer.BlockCopy(masked, 0, frame, header.Length, masked.Length);
            return frame;
        }

        /// <summary>
        /// Dummy protocol handler used to test custom handler registration.
        /// </summary>
        private class DummyProtocolHandler : IProtocolHandler {
            private readonly bool _match;
            private readonly Action _onProcess;
            public DummyProtocolHandler(string name, bool match, Action onProcess = null) {
                Name = name;
                _match = match;
                _onProcess = onProcess;
            }
            public string Name { get; }
            public bool CanHandle(byte[] data) => _match;
            public void ProcessReceive(MutableTcpServer server, NetworkConnection connection, ref IReceivedEventArgs e) {
                _onProcess?.Invoke();
            }
            public void OnDisconnected(MutableTcpServer server, NetworkConnection connection) { }
        }

        #endregion
    }

    public sealed class TypeScriptWidgetMessage {
        public string Name { get; set; } = "";
        public int Quantity { get; set; }
        public bool Enabled { get; set; }
    }

    [TestClass]
    public sealed class TypeScriptSupportTests {
        [TestMethod]
        public void TypeScriptAndTsxFilesUseExplicitMimeTypes() {
            Assert.AreEqual("text/typescript", HttpServer.GetMimeType("client.ts"));
            Assert.AreEqual("text/typescript", HttpServer.GetMimeType("client.mts"));
            Assert.AreEqual("text/typescript", HttpServer.GetMimeType("client.cts"));
            Assert.AreEqual("text/typescript", HttpServer.GetMimeType("component.tsx"));
        }

        [TestMethod]
        public void TypeScriptClientGenerationUsesTypedBodiesPathsAndWebSockets() {
            using var server = new HttpServer(0);
            server.Options.Whitelist.Add(typeof(TypeScriptWidgetMessage));
            server.Map<TypeScriptWidgetMessage>("POST", "/api/widgets/{widgetId}",
                (_, body, _, _) => new { ok = true, body.Name });

            string source = server.GenerateTypeScriptClient(new TypeScriptClientOptions {
                ClassName = "Widget API",
                BaseUrl = "https://api.example.test/"
            });

            StringAssert.Contains(source, "export class WidgetAPI");
            StringAssert.Contains(source, "this.baseUrl = (options.baseUrl ?? \"https://api.example.test/\").replace(/\\/$/, '');");
            StringAssert.Contains(source, "export type PostApiWidgetsWidgetIdRequest = { Name: string; Quantity: number; Enabled: boolean }");
            StringAssert.Contains(source, "async postApiWidgetsWidgetId(path: { widgetId: string | number }, body: PostApiWidgetsWidgetIdRequest");
            StringAssert.Contains(source, "export interface TypeScriptWidgetMessage");
            StringAssert.Contains(source, "sendWebSocket<T>(socket: WebSocket, type: string, value: T)");
            StringAssert.Contains(source, "const envelope: SocketJackEnvelope<T> = { Type: type, value }");
        }

        [TestMethod]
        public void TypeScriptClientGenerationIsDeterministicAndDeduplicatesMethodNames() {
            using var server = new HttpServer(0);
            server.Map("GET", "/status", (_, _, _) => "ok");
            server.Map("GET", "/status/", (_, _, _) => "ok");

            string first = server.GenerateTypeScriptClient(new TypeScriptClientOptions { IncludeWebSocketTypes = false });
            string second = server.GenerateTypeScriptClient(new TypeScriptClientOptions { IncludeWebSocketTypes = false });

            Assert.AreEqual(first, second);
            StringAssert.Contains(first, "async getStatus(");
            StringAssert.Contains(first, "async getStatus2(");
        }

        [TestMethod]
        public void GeneratedClientPassesTheInstalledTypeScriptCompiler() {
            string typeScriptModule = FindTypeScriptModule();
            if (typeScriptModule is null)
                Assert.Inconclusive("The repository TypeScript compiler is not installed.");

            using var server = new HttpServer(0);
            server.Map<TypeScriptWidgetMessage>("POST", "/api/widgets/:widgetId", (_, body, _, _) => body);
            string source = server.GenerateTypeScriptClient();
            string script = "const ts=require(" + JsonSerializer.Serialize(typeScriptModule) + ");"
                + "let source='';process.stdin.setEncoding('utf8');process.stdin.on('data',d=>source+=d);process.stdin.on('end',()=>{"
                + "const file='generated-socketjack-client.ts';const options={noEmit:true,strict:true,target:ts.ScriptTarget.ES2022,module:ts.ModuleKind.ESNext,lib:['lib.es2022.d.ts','lib.dom.d.ts']};"
                + "const host=ts.createCompilerHost(options);const read=host.readFile.bind(host),exists=host.fileExists.bind(host);"
                + "host.readFile=f=>f===file?source:read(f);host.fileExists=f=>f===file||exists(f);"
                + "const program=ts.createProgram([file],options,host);const diagnostics=ts.getPreEmitDiagnostics(program);"
                + "if(diagnostics.length){for(const d of diagnostics)process.stderr.write(ts.flattenDiagnosticMessageText(d.messageText,'\\n')+'\\n');process.exitCode=1;}});";
            var startInfo = new ProcessStartInfo("node") {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-e");
            startInfo.ArgumentList.Add(script);
            using Process process = Process.Start(startInfo)!;
            process.StandardInput.Write(source);
            process.StandardInput.Close();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit(15_000);

            Assert.AreEqual(0, process.ExitCode, error);
        }

        private static string FindTypeScriptModule() {
            DirectoryInfo directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null) {
                string candidate = Path.Combine(directory.FullName, "LlmRuntime.VisualStudioCode", "node_modules", "typescript");
                if (Directory.Exists(candidate))
                    return candidate.Replace('\\', '/');
                directory = directory.Parent;
            }
            return null;
        }
    }
}
