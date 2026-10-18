using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Net;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SocketJack.Tests;
[TestClass]
public class HttpTransferTests {
    static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    static HttpTransferCreateRequest Request(byte[] data, string target = "test.bin") => new() { Target = target, Sha256 = Hash(data), Length = data.Length };
    static string Root() { var path = Path.Combine(Path.GetTempPath(), "SocketJack-transfer-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    [TestMethod]
    public void DurableResumeDuplicateAndChangedChunks() {
        var root = Root(); var data = Encoding.UTF8.GetBytes("first-second"); var first = data[..5]; string id;
        try {
            using (var store = new HttpTransferStore(new() { Directory = root })) {
                var s = store.Create("alice", Request(data)); id = s.Id;
                Assert.AreEqual(5, store.Append("alice", id, 0, first, Hash(first)).Offset);
                Assert.AreEqual(5, store.Append("alice", id, 0, first, Hash(first)).Offset);
                Assert.ThrowsException<InvalidOperationException>(() => store.Append("alice", id, 0, new byte[5], Hash(new byte[5])));
                Assert.ThrowsException<FileNotFoundException>(() => store.Get("bob", id));
            }
            using (var store = new HttpTransferStore(new() { Directory = root })) {
                Assert.AreEqual(id, store.Create("alice", Request(data)).Id);
                Assert.AreEqual(5, store.Get("alice", id).Offset);
                var remaining = data[5..]; store.Append("alice", id, 5, remaining, Hash(remaining));
                Assert.AreEqual(HttpTransferState.Completed, store.Complete("alice", id).State);
                using var output = new MemoryStream(); store.CopyToAsync("alice", id, output).GetAwaiter().GetResult(); CollectionAssert.AreEqual(data, output.ToArray());
            }
        } finally { Directory.Delete(root, true); }
    }
    [TestMethod]
    public async Task GrowingDownloadDoesNotReleaseLastByteBeforeCommit() {
        var root = Root(); var data = Encoding.UTF8.GetBytes("available-later");
        try {
            using var store = new HttpTransferStore(new() { Directory = root }); var s = store.Create("alice", Request(data));
            using var output = new MemoryStream(); using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var copying = store.CopyToAsync("alice", s.Id, output, cancellationToken: stop.Token);
            store.Append("alice", s.Id, 0, data[..4], Hash(data[..4]));
            await Task.Delay(200); Assert.AreEqual(4, output.Length); Assert.IsFalse(copying.IsCompleted);
            store.Append("alice", s.Id, 4, data[4..], Hash(data[4..]));
            await Task.Delay(200); Assert.AreEqual(data.Length - 1, output.Length); Assert.IsFalse(copying.IsCompleted);
            store.Complete("alice", s.Id); await copying; CollectionAssert.AreEqual(data, output.ToArray());
        } finally { Directory.Delete(root, true); }
    }
    [TestMethod]
    public void OverwriteConfirmationGenerationAndRetention() {
        var root = Root(); var now = DateTimeOffset.UtcNow; long reserved = 0;
        var options = new HttpTransferOptions { Directory = root, UtcNow = () => now, Reserve = (_, n) => reserved += n, Release = (_, n) => reserved -= n };
        try {
            using var store = new HttpTransferStore(options); var s = store.Create("alice", Request(new byte[10]));
            var replacement = Request(new byte[11]);
            Assert.ThrowsException<HttpTransferConflictException>(() => store.Create("alice", replacement));
            replacement.ConfirmGeneration = "stale"; Assert.ThrowsException<HttpTransferConflictException>(() => store.Create("alice", replacement));
            replacement.ConfirmGeneration = s.Generation; var next = store.Create("alice", replacement);
            Assert.AreEqual(next.Generation, store.Find("alice", "test.bin").Generation);
            replacement = Request(new byte[12]); replacement.Overwrite = HttpOverwritePolicy.Never;
            Assert.ThrowsException<IOException>(() => store.Create("alice", replacement));
            now += TimeSpan.FromSeconds(31); Assert.AreEqual(HttpTransferState.Stalled, store.Get("alice", s.Id).State);
            now += TimeSpan.FromHours(1); store.Sweep(); Assert.AreEqual(0, reserved); Assert.AreEqual(HttpTransferState.Expired, store.Get("alice", s.Id).State);
            Assert.AreNotEqual(s.Id, store.Create("alice", Request(new byte[10])).Id);
        } finally { Directory.Delete(root, true); }
    }
    [TestMethod]
    public void InvalidHashAndPathsNeverCommit() {
        var root = Root(); bool committed = false;
        try {
            using var store = new HttpTransferStore(new() { Directory = root, Commit = (_, _) => committed = true });
            Assert.ThrowsException<ArgumentException>(() => store.Create("alice", Request(new byte[1], "../escape")));
            var s = store.Create("alice", Request(new byte[1])); var wrong = new byte[] { 1 };
            store.Append("alice", s.Id, 0, wrong, Hash(wrong)); Assert.ThrowsException<InvalidDataException>(() => store.Complete("alice", s.Id)); Assert.IsFalse(committed);
        } finally { Directory.Delete(root, true); }
    }
    [TestMethod]
    public async Task HttpRoutesAuthorizeAndResumeRanges() {
        var root = Root(); using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        try {
            using var server = new HttpServer(new NetworkOptions { Logging = false, UsePeerToPeer = false }, port);
            using var store = server.MapTransfers("/transfers", new() { Directory = root, Authorize = (_, r) => r.Headers.TryGetValue("X-Owner", out var owner) ? owner : null });
            Assert.IsTrue(server.Listen()); using var http = new System.Net.Http.HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") }; http.DefaultRequestHeaders.Add("X-Owner", "alice");
            var data = Encoding.UTF8.GetBytes("download me");
            using var created = await http.PostAsync("/transfers/start", new StringContent(JsonSerializer.Serialize(Request(data)))); created.EnsureSuccessStatusCode(); var s = JsonSerializer.Deserialize<HttpTransferSession>(await created.Content.ReadAsStringAsync());
            using var appended = await http.PostAsync($"/transfers/append?id={s.Id}&offset=0&sha256={Hash(data)}", new ByteArrayContent(data)); appended.EnsureSuccessStatusCode();
            using var completed = await http.PostAsync($"/transfers/complete?id={s.Id}", null); completed.EnsureSuccessStatusCode();
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/transfers/download?id={s.Id}"); request.Headers.Range = new(3, null); request.Headers.TryAddWithoutValidation("If-Range", s.ETag);
            using var response = await http.SendAsync(request); Assert.AreEqual(HttpStatusCode.PartialContent, response.StatusCode); CollectionAssert.AreEqual(data[3..], await response.Content.ReadAsByteArrayAsync());
        } finally { Directory.Delete(root, true); }
    }
    [TestMethod]
    public async Task HttpGrowingDownloadSendsHeadersAndRejectsOversizedBodies() {
        var root = Root(); using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        try {
            using var server = new HttpServer(new NetworkOptions { Logging = false, UsePeerToPeer = false, BindAddress = IPAddress.Loopback }, port);
            using var store = server.MapTransfers("/transfers", new() { Directory = root, Authorize = (_, r) => r.Headers.TryGetValue("X-Owner", out var owner) ? owner : null });
            Assert.IsTrue(server.Listen()); using var http = new System.Net.Http.HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(10) }; http.DefaultRequestHeaders.Add("X-Owner", "alice");
            byte[] data = Encoding.UTF8.GetBytes("first and last"); var session = store.Create("alice", Request(data));
            using var response = await http.GetAsync($"/transfers/download?id={session.Id}", HttpCompletionOption.ResponseHeadersRead);
            Assert.AreEqual(data.Length, response.Content.Headers.ContentLength); await using var stream = await response.Content.ReadAsStreamAsync();
            store.Append("alice", session.Id, 0, data[..5], Hash(data[..5])); byte[] actual = new byte[data.Length]; await stream.ReadExactlyAsync(actual.AsMemory(0, 5));
            CollectionAssert.AreEqual(data[..5], actual[..5]);
            store.Append("alice", session.Id, 5, data[5..], Hash(data[5..])); store.Complete("alice", session.Id); await stream.ReadExactlyAsync(actual.AsMemory(5)); CollectionAssert.AreEqual(data, actual);
            using var tooLarge = await http.PostAsync("/transfers/start", new StringContent(new string('x', 65537))); Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);
            http.DefaultRequestHeaders.Remove("X-Owner"); using var anonymous = await http.GetAsync($"/transfers/download?id={session.Id}"); Assert.AreEqual(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        } finally { Directory.Delete(root, true); }
    }
}
