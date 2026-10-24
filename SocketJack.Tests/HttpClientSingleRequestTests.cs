using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace SocketJack.Tests;

[TestClass]
public class HttpClientSingleRequestTests {
    [TestMethod]
    public async Task GetOnceReturnsRedirectWithoutFollowingLocation() {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Reply(listener, "HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:1/must-not-be-requested\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", Array.Empty<byte>());
        using var client = new SocketJack.Net.HttpClient();
        var response = await client.GetOnceAsync($"http://127.0.0.1:{port}/redirect");
        await server;
        Assert.AreEqual("302 Found", response.Context.StatusCode);
        Assert.AreEqual("http://127.0.0.1:1/must-not-be-requested", response.Headers["Location"]);
    }

    [TestMethod]
    public async Task SendOnceStreamsExactBytesAndReportsProgress() {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var payload = new byte[131091]; new Random(41).NextBytes(payload);
        var server = Reply(listener, $"HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n", payload);
        using var client = new SocketJack.Net.HttpClient();
        using var output = new MemoryStream(); long received = 0;
        var response = await client.SendOnceAsync("GET", $"http://127.0.0.1:{port}/payload", responseStream: output, onChunk: (_, count) => received += count);
        await server;
        Assert.AreEqual("200 OK", response.Context.StatusCode);
        CollectionAssert.AreEqual(payload, output.ToArray());
        Assert.AreEqual((long)payload.Length, received);
    }

    [TestMethod]
    public async Task SingleRequestRejectsUnsafeUrlsAndCancelledRequests() {
        using var client = new SocketJack.Net.HttpClient();
        await Assert.ThrowsExceptionAsync<ArgumentException>(() => client.GetOnceAsync("file:///payload"));
        await Assert.ThrowsExceptionAsync<ArgumentException>(() => client.GetOnceAsync("https://user:password@example.invalid"));
        await Assert.ThrowsExceptionAsync<ArgumentException>(() => client.SendOnceAsync("GET\r\nInjected", "https://example.invalid"));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => client.GetOnceAsync("https://example.invalid", cancellationToken: cancelled.Token));
    }

    static async Task Reply(TcpListener listener, string headers, byte[] payload) {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var connection = await listener.AcceptTcpClientAsync(timeout.Token);
        using var stream = connection.GetStream();
        var buffer = new byte[1]; using var request = new MemoryStream();
        while (request.Length < 32768) {
            if (await stream.ReadAsync(buffer, timeout.Token) == 0) throw new EndOfStreamException();
            request.WriteByte(buffer[0]);
            if (request.Length >= 4 && Encoding.ASCII.GetString(request.GetBuffer(), (int)request.Length - 4, 4) == "\r\n\r\n") break;
        }
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), timeout.Token);
        await stream.WriteAsync(payload, timeout.Token);
    }
}
