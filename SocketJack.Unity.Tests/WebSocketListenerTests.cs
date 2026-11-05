using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Multiplayer.Transport;

namespace SocketJack.Multiplayer.Tests
{
    [TestClass]
    public sealed class WebSocketListenerTests
    {
        [TestMethod]
        public async Task HttpAndWebSocketRoutesEnforceOriginAndRestartCleanly()
        {
            await using var listener = new WebSocketListener();
            listener.HttpHandler = (request, token) => Task.FromResult(WebSocketHttpResponse.Text(request.Method + " " + request.Path + request.QueryString + " " + Encoding.UTF8.GetString(request.Body), "text/plain"));
            listener.UpgradeAuthorizer = (request, token) => Task.FromResult(request.Path == "/echo");
            listener.WebSocketAccepted = Echo;
            listener.Start(new IPEndPoint(IPAddress.Loopback, 0));
            using var http = new HttpClient();
            string root = "http://" + listener.LocalEndPoint;
            var response = await http.PostAsync(root + "/api/test?q=ok", new StringContent("hello"));
            Assert.AreEqual("POST /api/test?q=ok hello", await response.Content.ReadAsStringAsync());
            using (var socket = new ClientWebSocket())
            {
                socket.Options.SetRequestHeader("Origin", root);
                await socket.ConnectAsync(new Uri("ws://" + listener.LocalEndPoint + "/echo"), CancellationToken.None);
                await socket.SendAsync(new ArraySegment<byte>(new byte[] { 1, 2, 3 }), WebSocketMessageType.Binary, true, CancellationToken.None);
                byte[] bytes = new byte[8]; var received = await socket.ReceiveAsync(new ArraySegment<byte>(bytes), CancellationToken.None);
                CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, bytes.Take(received.Count).ToArray());
            }
            using (var denied = new ClientWebSocket())
            {
                denied.Options.SetRequestHeader("Origin", "https://unrelated.invalid");
                await Assert.ThrowsExceptionAsync<WebSocketException>(() => denied.ConnectAsync(new Uri("ws://" + listener.LocalEndPoint + "/echo"), CancellationToken.None));
            }
            using (var denied = new ClientWebSocket())
                await Assert.ThrowsExceptionAsync<WebSocketException>(() => denied.ConnectAsync(new Uri("ws://" + listener.LocalEndPoint + "/not-echo"), CancellationToken.None));
            await listener.StopAsync().WaitAsync(TimeSpan.FromSeconds(3)); Assert.AreEqual(0, listener.ConnectionCount);
            listener.Start(new IPEndPoint(IPAddress.Loopback, 0));
            Assert.AreEqual("GET /again ", await http.GetStringAsync("http://" + listener.LocalEndPoint + "/again"));
            await listener.StopAsync();
        }

        [TestMethod]
        public async Task StrictHttpParserRejectsAmbiguityAndBoundsWithoutInvokingRoutes()
        {
            await using var listener = new WebSocketListener(new WebSocketListenerOptions { MaxHeaderBytes = 512, MaxBodyBytes = 8 });
            int invoked = 0;
            listener.HttpHandler = (request, token) => { Interlocked.Increment(ref invoked); return Task.FromResult(WebSocketHttpResponse.Text("unexpected")); };
            listener.Start(new IPEndPoint(IPAddress.Loopback, 0));
            foreach (string request in new[] {
                "GET / HTTP/1.1\r\nHost: a\r\nHost: b\r\n\r\n",
                "POST / HTTP/1.1\r\nHost: a\r\nContent-Length: 0\r\nContent-Length: 0\r\n\r\n",
                "POST / HTTP/1.1\r\nHost: a\r\nTransfer-Encoding: chunked\r\n\r\n",
                "POST / HTTP/1.1\r\nHost: a\r\nContent-Length: 99999\r\n\r\n",
                "POST / HTTP/1.1\r\nHost: a\r\nContent-Length: +1\r\n\r\n",
                "GET http://elsewhere.invalid/ HTTP/1.1\r\nHost: a\r\n\r\n",
                "GET /%0d%0aevil HTTP/1.1\r\nHost: a\r\n\r\n",
                "GET / HTTP/1.1\r\nHost: a\r\nFolded: x\nInjected: y\r\n\r\n",
                "GET / HTTP/1.1\r\nHost: a\r\n Folded: x\r\n\r\n",
                "GET / HTTP/1.1\r\nHost: a\r\nX-Large: " + new string('x', 600) + "\r\n\r\n",
                "GET / HTTP/1.1\r\nHost: a\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: YWJj\r\nSec-WebSocket-Version: 13\r\n\r\n",
                "GET / HTTP/1.1\r\nHost: a\r\n\r\nGET /second HTTP/1.1\r\nHost: a\r\n\r\n" })
            {
                string reply = await Raw(listener.LocalEndPoint, request);
                Assert.IsFalse(reply.Contains("200 OK"));
            }
            Assert.AreEqual(0, invoked);
            await listener.StopAsync();
            Assert.ThrowsException<ArgumentException>(() => new WebSocketListener(new WebSocketListenerOptions { AllowedOrigins = new[] { "*" } }));
        }

        [TestMethod]
        public async Task StopCancelsPartialHeadersAndOpenWebSocketsAndConnectionCapIsBounded()
        {
            await using var listener = new WebSocketListener(new WebSocketListenerOptions { MaxConnections = 1, HandshakeTimeout = TimeSpan.FromMilliseconds(150) });
            listener.WebSocketAccepted = Echo;
            listener.Start(new IPEndPoint(IPAddress.Loopback, 0));
            using (var slow = new TcpClient())
            {
                await slow.ConnectAsync(listener.LocalEndPoint.Address, listener.LocalEndPoint.Port);
                await slow.GetStream().WriteAsync(Encoding.ASCII.GetBytes("GET /"));
                await Until(() => listener.ConnectionCount == 1);
                using var second = new ClientWebSocket();
                await Assert.ThrowsExceptionAsync<WebSocketException>(() => second.ConnectAsync(new Uri("ws://" + listener.LocalEndPoint + "/"), CancellationToken.None));
                await Until(() => listener.ConnectionCount == 0);
            }
            using var opened = new ClientWebSocket();
            await opened.ConnectAsync(new Uri("ws://" + listener.LocalEndPoint + "/"), CancellationToken.None);
            await listener.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Assert.AreEqual(0, listener.ConnectionCount); Assert.IsFalse(listener.IsRunning);
        }
        private static async Task Echo(WebSocketHttpRequest request, WebSocket socket, CancellationToken token)
        {
            byte[] bytes = new byte[1024];
            var received = await socket.ReceiveAsync(new ArraySegment<byte>(bytes), token);
            if (received.MessageType != WebSocketMessageType.Close)
                await socket.SendAsync(new ArraySegment<byte>(bytes, 0, received.Count), received.MessageType, true, token);
        }
        private static async Task<string> Raw(IPEndPoint endpoint, string request)
        {
            using var client = new TcpClient(); await client.ConnectAsync(endpoint.Address, endpoint.Port);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var stream = client.GetStream(); byte[] input = Encoding.ASCII.GetBytes(request); await stream.WriteAsync(input, 0, input.Length, timeout.Token);
            using var result = new MemoryStream(); byte[] buffer = new byte[1024];
            try { int count; while ((count = await stream.ReadAsync(buffer, 0, buffer.Length, timeout.Token)) != 0) result.Write(buffer, 0, count); }
            catch (IOException) { }
            return Encoding.ASCII.GetString(result.ToArray());
        }
        private static async Task Until(Func<bool> ready)
        { var watch = Stopwatch.StartNew(); while (!ready() && watch.Elapsed < TimeSpan.FromSeconds(4)) await Task.Delay(1); Assert.IsTrue(ready()); }
    }
}
