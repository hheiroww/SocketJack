using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Net;
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;

namespace SocketJack.UdpReliable.Tests;

public sealed class Payload { public int Index { get; set; } public byte[] Bytes { get; set; } }

[TestClass]
public sealed class ReliableTests {
    static int Port() { using var s = new System.Net.Sockets.UdpClient(0); return ((IPEndPoint)s.Client.LocalEndPoint).Port; }
    static NetworkOptions Options(UdpDeliveryMode delivery = UdpDeliveryMode.Ordered) => Approve(new NetworkOptions {
        UsePeerToPeer = false, ConnectionTimeout = TimeSpan.FromSeconds(8),
        UdpReliable = new() { Profile = UdpReliableProfile.FastLan, Delivery = delivery, DeliveryTimeout = TimeSpan.FromSeconds(12) }
    });
    static NetworkOptions Approve(NetworkOptions options) {
        var assembly = typeof(Payload).Assembly;
        options.VerifiedAssemblies.Add(assembly, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(assembly.Location))));
        options.Authorization.RequireAuthentication = false; // This fixture exercises anonymous data transport, not login.
        return options;
    }

    static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    static async Task Until(Func<bool> condition, int seconds = 15) {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    [DataTestMethod]
    [DataRow(0.0)] [DataRow(0.01)] [DataRow(0.05)]
    public async Task OrderedLargeObjectsSurviveLossDuplicationReordering(double loss) {
        int port = Port(); using var server = UDP_Reliable.CreateServer(port, Options());
        using var proxy = new ImpairmentProxy(port, loss);
        using var client = UDP_Reliable.CreateClient(Options());
        var received = new ConcurrentQueue<int>(); var replies = new ConcurrentQueue<int>();
        var source = new byte[192 * 1024]; new Random(42).NextBytes(source);
        var errors = new ConcurrentQueue<Exception>();
        server.OnError += e => errors.Enqueue(e.Exception); client.OnError += e => errors.Enqueue(e.Exception);
        server.RegisterCallback<Payload>(e => { CollectionAssert.AreEqual(source, e.Object.Bytes); received.Enqueue(e.Object.Index); e.Connection.Send(e.Object); });
        client.RegisterCallback<Payload>(e => { CollectionAssert.AreEqual(source, e.Object.Bytes); replies.Enqueue(e.Object.Index); });
        Assert.IsTrue(server.Listen()); Assert.IsTrue(await client.Connect("127.0.0.1", proxy.Port), string.Join("\n", errors));
        var sends = Enumerable.Range(0, 12).Select(i => client.SendAsync(new Payload { Index = i, Bytes = source })).ToArray();
        await Task.WhenAll(sends).WaitAsync(TimeSpan.FromSeconds(50));
        await Until(() => replies.Count == 12, 50);
        CollectionAssert.AreEqual(Enumerable.Range(0, 12).ToArray(), received.ToArray());
        CollectionAssert.AreEqual(Enumerable.Range(0, 12).ToArray(), replies.ToArray());
        Assert.AreEqual(0, errors.Count, string.Join("\n", errors));
        Assert.IsTrue(proxy.Dropped > 0); Assert.IsTrue(proxy.Duplicated > 0);
        Assert.IsTrue(client.ReliableStatistics.Retransmissions > 0);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CompressionCachingAndBroadcastUseReliablePath(bool binary) {
        int port = Port(); var so = Options(); so.UseCompression = true; so.EnablePatternCache = true;
        if (binary) so.Serializer = new SocketJack.Serialization.BinarySerializer();
        using var server = UDP_Reliable.CreateServer(port, so);
        var co = Options(); co.UseCompression = true; co.EnablePatternCache = true;
        if (binary) co.Serializer = new SocketJack.Serialization.BinarySerializer();
        using var first = UDP_Reliable.CreateClient(co); using var second = UDP_Reliable.CreateClient(co);
        var errors = new ConcurrentQueue<Exception>();
        server.OnError += e => errors.Enqueue(e.Exception); first.OnError += e => errors.Enqueue(e.Exception); second.OnError += e => errors.Enqueue(e.Exception);
        int replies = 0; server.RegisterCallback<string>(e => server.SendBroadcast(e.Object));
        first.RegisterCallback<string>(e => Interlocked.Increment(ref replies)); second.RegisterCallback<string>(e => Interlocked.Increment(ref replies));
        Assert.IsTrue(server.Listen()); Assert.IsTrue(await first.Connect("127.0.0.1", port)); Assert.IsTrue(await second.Connect("127.0.0.1", port));
        for (int i = 0; i < 8; i++) await first.SendAsync(new string('x', 10000));
        try { await Until(() => replies == 16); } catch { Assert.Fail($"Replies={replies}; errors={string.Join("\n", errors)}"); }
        Assert.AreEqual(0, errors.Count, string.Join("\n", errors));
    }

    [TestMethod]
    public async Task StreamIsExplicitlyAcceptedAndHashVerified() {
        int port = Port(); using var server = UDP_Reliable.CreateServer(port, Options());
        using var proxy = new ImpairmentProxy(port, .05);
        using var client = UDP_Reliable.CreateClient(Options());
        using var target = new MemoryStream(); UdpTransferRequest accepted = null;
        server.TransferRequested += request => { accepted = request; request.Accept(target); };
        Assert.IsTrue(server.Listen()); Assert.IsTrue(await client.Connect("127.0.0.1", proxy.Port));
        byte[] bytes = new byte[2 * 1024 * 1024]; new Random(72).NextBytes(bytes);
        using var source = new MemoryStream(bytes);
        var result = await client.SendStreamAsync(source).WaitAsync(TimeSpan.FromSeconds(60));
        var remote = await accepted.Completion;
        Assert.AreEqual(bytes.LongLength, result.BytesTransferred);
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), result.Sha256);
        Assert.AreEqual(remote.Sha256, result.Sha256); CollectionAssert.AreEqual(bytes, target.ToArray());
    }

    [TestMethod]
    public async Task RefusedStreamAndCanceledAdmissionDoNotBreakSession() {
        int port = Port(); using var server = UDP_Reliable.CreateServer(port, Options()); using var client = UDP_Reliable.CreateClient(Options());
        var received = Signal<string>(); server.RegisterCallback<string>(e => received.TrySetResult(e.Object));
        Assert.IsTrue(server.Listen()); Assert.IsTrue(await client.Connect("127.0.0.1", port));
        await Assert.ThrowsExceptionAsync<IOException>(() => client.SendStreamAsync(new MemoryStream(new byte[5])));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => client.SendAsync("canceled", cancel.Token));
        await client.SendAsync("still connected"); Assert.AreEqual("still connected", await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    public async Task UnknownVersionTruncationAndStaleSessionAreIgnored() {
        int port = Port(); using var server = UDP_Reliable.CreateServer(port, Options()); using var proxy = new ImpairmentProxy(port, 0, false); using var client = UDP_Reliable.CreateClient(Options());
        int count = 0; server.RegisterCallback<string>(e => Interlocked.Increment(ref count));
        Assert.IsTrue(server.Listen()); Assert.IsTrue(await client.Connect("127.0.0.1", proxy.Port));
        await client.SendAsync("once"); await Until(() => count == 1);
        byte[] frame = (byte[])proxy.LastData.Clone();
        await proxy.InjectServer(frame); // duplicate valid packet
        frame[8] ^= 127; await proxy.InjectServer(frame); // foreign session
        frame[2] = 99; await proxy.InjectServer(frame);
        await proxy.InjectServer(new byte[] { 83, 74, 85, 82, 1 });
        await client.SendAsync("twice"); await Until(() => count == 2); await Task.Delay(100);
        Assert.AreEqual(2, count);
    }

    [TestMethod]
    public async Task BlackholeTimesOutAndReconnectStartsFreshSession() {
        int port = Port(); var opts = Options(); opts.UdpReliable.DeliveryTimeout = TimeSpan.FromMilliseconds(800);
        using var server = UDP_Reliable.CreateServer(port, opts); using var proxy = new ImpairmentProxy(port, 0, false); using var client = UDP_Reliable.CreateClient(opts);
        var delivered = new ConcurrentQueue<string>(); server.RegisterCallback<string>(e => delivered.Enqueue(e.Object));
        Assert.IsTrue(server.Listen()); Assert.IsTrue(await client.Connect("127.0.0.1", proxy.Port));
        await client.SendAsync("before"); await Until(() => delivered.Count == 1);
        byte[] stale = (byte[])proxy.LastData.Clone();
        proxy.Blackhole = true;
        await Assert.ThrowsExceptionAsync<TimeoutException>(() => client.SendAsync("lost"));
        await Until(() => !client.Connected); await Until(() => server.Clients.Count == 0);
        proxy.Blackhole = false;
        Assert.IsTrue(await client.Connect("127.0.0.1", proxy.Port)); await client.SendAsync("fresh");
        await proxy.InjectServer(stale); await Until(() => delivered.Count == 2); await Task.Delay(100);
        CollectionAssert.AreEqual(new[] { "before", "fresh" }, delivered.ToArray());
    }

    [TestMethod]
    public async Task IndependentMessageCanPassMissingLargeMessage() {
        int port = Port(); using var server = UDP_Reliable.CreateServer(port, Options(UdpDeliveryMode.Independent));
        using var proxy = new ImpairmentProxy(port, 0, false);
        using var client = UDP_Reliable.CreateClient(Options(UdpDeliveryMode.Independent));
        var received = new ConcurrentQueue<int>(); server.RegisterCallback<Payload>(e => received.Enqueue(e.Object.Index));
        client.RegisterCallback<Payload>(_ => { });
        Assert.IsTrue(server.Listen()); Assert.IsTrue(await client.Connect("127.0.0.1", proxy.Port));
        proxy.HoldFragmented = true;
        var first = client.SendAsync(new Payload { Index = 1, Bytes = new byte[128 * 1024] });
        await Until(() => proxy.Held > 0);
        await client.SendAsync(new Payload { Index = 2, Bytes = new byte[10] }); await Until(() => received.Count == 1);
        Assert.AreEqual(2, received.Single()); Assert.IsFalse(first.IsCompleted);
        proxy.HoldFragmented = false; await first; await Until(() => received.Count == 2);
        CollectionAssert.AreEqual(new[] { 2, 1 }, received.ToArray());
    }

    [TestMethod]
    public async Task AdmissionBackpressureCancellationAndSynchronousExhaustionAreExplicit() {
        int port = Port(); var options = Options(); options.UdpReliable.SendQueueBytes = 256 * 1024;
        using var server = UDP_Reliable.CreateServer(port, Options()); using var proxy = new ImpairmentProxy(port, 0, false);
        using var client = UDP_Reliable.CreateClient(options);
        int count = 0; var errors = new ConcurrentQueue<Exception>();
        server.RegisterCallback<Payload>(e => Interlocked.Increment(ref count)); client.RegisterCallback<Payload>(_ => { }); client.OnError += e => errors.Enqueue(e.Exception);
        Assert.IsTrue(server.Listen()); Assert.IsTrue(await client.Connect("127.0.0.1", proxy.Port));
        proxy.HoldFragmented = true;
        var payload = new Payload { Bytes = new byte[128 * 1024] };
        var first = client.SendAsync(payload); await Until(() => proxy.Held > 0);
        using var cancellation = new CancellationTokenSource();
        var second = client.SendAsync(payload, cancellation.Token); await Task.Delay(50); Assert.IsFalse(second.IsCompleted);
        client.Send(payload); await Until(() => !errors.IsEmpty); Assert.IsInstanceOfType(errors.First(), typeof(IOException));
        cancellation.Cancel(); await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => second);
        Assert.IsTrue(client.Connected); proxy.HoldFragmented = false; await first; await client.SendAsync(payload); await Until(() => count == 2);
    }

    [TestMethod]
    public async Task FileTransferDoesNotOverwriteAndCancellationRemovesPartialFile() {
        string directory = Path.Combine(Path.GetTempPath(), "socketjack-udp-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        string destination = Path.Combine(directory, "target.bin");
        try {
            int port = Port(); using var server = UDP_Reliable.CreateServer(port, Options()); using var client = UDP_Reliable.CreateClient(Options());
            UdpTransferRequest request = null; server.TransferRequested += r => { request = r; r.AcceptFile(destination); };
            Assert.IsTrue(server.Listen()); Assert.IsTrue(await client.Connect("127.0.0.1", port));
            await File.WriteAllTextAsync(destination, "existing");
            await Assert.ThrowsExceptionAsync<IOException>(() => client.SendStreamAsync(new MemoryStream(new byte[5])));
            Assert.AreEqual("existing", await File.ReadAllTextAsync(destination)); File.Delete(destination);
            using var cancel = new CancellationTokenSource();
            try { await client.SendStreamAsync(new MemoryStream(new byte[4 * 1024 * 1024]), progress: new InlineProgress(p => { if (p.BytesTransferred >= 256 * 1024) cancel.Cancel(); }), cancellationToken: cancel.Token); Assert.Fail("Expected cancellation."); }
            catch (OperationCanceledException) { }
            await Until(() => request.Completion.IsCompleted);
            Assert.IsFalse(File.Exists(destination)); Assert.AreEqual(0, Directory.GetFiles(directory).Length);
        } finally { foreach (string file in Directory.GetFiles(directory)) File.Delete(file); Directory.Delete(directory); }
    }

    [TestMethod]
    [TestCategory("LargeFile")]
    [Timeout(900000)]
    public async Task FileBeyondFourGiBUsesBoundedMemoryAndMatchesDiskHash() {
        string directory = Path.Combine(Path.GetTempPath(), "socketjack-udp-large-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        string sourcePath = Path.Combine(directory, "source.bin"), destination = Path.Combine(directory, "received.bin");
        long length = 4L * 1024 * 1024 * 1024 + 65537;
        try {
            byte[] block = new byte[1024 * 1024]; new Random(991).NextBytes(block);
            using (var file = new FileStream(sourcePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, block.Length, true))
                for (long offset = 0; offset < length; offset += block.Length) await file.WriteAsync(block.AsMemory(0, (int)Math.Min(block.Length, length - offset)));
            int port = Port(); using var server = UDP_Reliable.CreateServer(port, Options()); using var client = UDP_Reliable.CreateClient(Options());
            server.TransferRequested += r => r.AcceptFile(destination);
            Assert.IsTrue(server.Listen()); Assert.IsTrue(await client.Connect("127.0.0.1", port));
            long maxManaged = 0; var watch = System.Diagnostics.Stopwatch.StartNew();
            var transfer = client.SendFileAsync(sourcePath);
            while (!transfer.IsCompleted) { maxManaged = Math.Max(maxManaged, GC.GetTotalMemory(false)); await Task.WhenAny(transfer, Task.Delay(100)); }
            var result = await transfer; Assert.AreEqual(length, result.BytesTransferred); Assert.AreEqual(length, new FileInfo(destination).Length);
            using var source = File.OpenRead(sourcePath); using var received = File.OpenRead(destination);
            string expected = Convert.ToHexString(await SHA256.HashDataAsync(source)).ToLowerInvariant();
            Assert.AreEqual(expected, result.Sha256); Assert.AreEqual(expected, Convert.ToHexString(await SHA256.HashDataAsync(received)).ToLowerInvariant());
            Assert.IsTrue(maxManaged < 512L * 1024 * 1024, $"Managed heap grew to {maxManaged}.");
            Console.WriteLine($"Large file: {length} bytes, {watch.Elapsed.TotalSeconds:F2}s, peak sampled managed bytes={maxManaged}, SHA256={expected}");
        } finally { foreach (string file in Directory.GetFiles(directory)) File.Delete(file); Directory.Delete(directory); }
    }
    sealed class InlineProgress(Action<UdpTransferProgress> action) : IProgress<UdpTransferProgress> { public void Report(UdpTransferProgress value) => action(value); }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UnauthenticatedPeerRedirectIsRejected(bool binary) {
        int port = Port(); var so = Options(); so.UsePeerToPeer = true;
        var firstOptions = Options(); firstOptions.UsePeerToPeer = true;
        var secondOptions = Options(); secondOptions.UsePeerToPeer = true;
        if (binary) { so.Serializer = new SocketJack.Serialization.BinarySerializer(); firstOptions.Serializer = new SocketJack.Serialization.BinarySerializer(); secondOptions.Serializer = new SocketJack.Serialization.BinarySerializer(); }
        using var server = UDP_Reliable.CreateServer(port, so); using var first = UDP_Reliable.CreateClient(firstOptions); using var second = UDP_Reliable.CreateClient(secondOptions);
        var received = Signal<string>(); var reply = Signal<string>();
        server.RegisterCallback<string>(e => { if (e.Object == "interface") ((ISocket)server).Send(e.Connection, "reply"); });
        first.RegisterCallback<string>(e => reply.TrySetResult(e.Object)); second.RegisterCallback<string>(e => received.TrySetResult(e.Object));
        Assert.IsTrue(server.Listen()); Assert.IsTrue(await first.Connect("127.0.0.1", port)); Assert.IsTrue(await second.Connect("127.0.0.1", port));
        await Until(() => first.RemoteIdentity != null && second.RemoteIdentity != null);
        var errors = new ConcurrentQueue<Exception>(); server.OnError += e => errors.Enqueue(e.Exception); first.OnError += e => errors.Enqueue(e.Exception); second.OnError += e => errors.Enqueue(e.Exception);
        first.Send(second.RemoteIdentity, "redirect");
        await Until(() => errors.Any(e => e is System.Security.SecurityException));
        await Until(() => !first.Connected);
        Assert.IsFalse(received.Task.IsCompleted, "An unauthenticated client must not forward data to another peer.");
    }

    [TestMethod]
    public async Task ReliableModeDoesNotDowngradeToPlainUdp() {
        int port = Port(); using var server = new SocketJack.Net.UdpServer(Options(), port);
        var options = Options(); options.ConnectionTimeout = TimeSpan.FromMilliseconds(500);
        using var client = UDP_Reliable.CreateClient(options);
        Assert.IsTrue(server.Listen()); Assert.IsFalse(await client.Connect("127.0.0.1", port)); Assert.IsFalse(client.Connected);
    }

    [TestMethod]
    public async Task LostCompletionAcknowledgmentRetriesWithoutDuplicateDispatch() {
        int port = Port(); using var server = UDP_Reliable.CreateServer(port, Options()); using var proxy = new ImpairmentProxy(port, 0, false);
        using var client = UDP_Reliable.CreateClient(Options());
        int callbacks = 0; server.RegisterCallback<Payload>(e => Interlocked.Increment(ref callbacks)); client.RegisterCallback<Payload>(_ => { });
        Assert.IsTrue(server.Listen()); Assert.IsTrue(await client.Connect("127.0.0.1", proxy.Port));
        proxy.DropCompletions = true;
        var send = client.SendAsync(new Payload { Bytes = new byte[65536] }); await Until(() => callbacks == 1);
        await Task.Delay(200); Assert.IsFalse(send.IsCompleted); Assert.AreEqual(1, callbacks);
        proxy.DropCompletions = false; await send.WaitAsync(TimeSpan.FromSeconds(3)); Assert.AreEqual(1, callbacks);
    }

    [TestMethod]
    public async Task SlowCallbackRetainsReceiveBudgetWithoutStallingProtocol() {
        int port = Port(); var options = Options(); options.MaximumBufferSize = 240 * 1024;
        options.UdpReliable.ReceiveQueueBytes = 256 * 1024;
        using var server = UDP_Reliable.CreateServer(port, options); using var client = UDP_Reliable.CreateClient(Options());
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        int callbacks = 0;
        server.RegisterCallback<Payload>(e => { if (Interlocked.Increment(ref callbacks) == 1) { entered.Set(); release.Wait(TimeSpan.FromSeconds(5)); } });
        client.RegisterCallback<Payload>(_ => { });
        Assert.IsTrue(server.Listen()); Assert.IsTrue(await client.Connect("127.0.0.1", port));
        try {
            var message = new Payload { Bytes = new byte[128 * 1024] };
            await client.SendAsync(message); Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(2)));
            var next = client.SendAsync(message); await Task.Delay(150); Assert.IsFalse(next.IsCompleted);
            Assert.IsTrue(client.Connected); release.Set(); await next; await Until(() => callbacks == 2);
        } finally { release.Set(); }
    }

    [TestMethod]
    public void InvalidConfigurationDoesNotLeaveListenerBound() {
        int port = Port(); var options = Options(); options.UdpReliable.DatagramSize = 1;
        using var server = UDP_Reliable.CreateServer(port, options);
        Assert.IsFalse(server.Listen()); Assert.IsFalse(server.IsListening);
        options.UdpReliable.DatagramSize = 1200; Assert.IsTrue(server.Listen());
    }

    [TestMethod]
    public async Task CachePromotionCannotAdvancePastAnUnsendableStoreFrame() {
        int port = Port(); var options = Options(); options.PatternCacheMinimumBytes = 1; options.PatternCachePromotionThreshold = 1;
        using var client = UDP_Reliable.CreateClient(options); using var server = UDP_Reliable.CreateServer(port, Options());
        string value = new string('z', 10000);
        options.UdpReliable.SendQueueBytes = options.Serializer.Serialize(new SocketJack.Serialization.Wrapper(value, client)).Length;
        int count = 0; var errors = new ConcurrentQueue<Exception>(); client.OnError += e => errors.Enqueue(e.Exception); server.OnError += e => errors.Enqueue(e.Exception);
        server.RegisterCallback<string>(e => { Assert.AreEqual(value, e.Object); Interlocked.Increment(ref count); });
        Assert.IsTrue(server.Listen()); Assert.IsTrue(await client.Connect("127.0.0.1", port));
        for (int i = 0; i < 3; i++) await client.SendAsync(value);
        await Until(() => count == 3); Assert.AreEqual(0, errors.Count, string.Join("\n", errors));
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PlainUdpStillExchangesTypedMessages(bool binary) {
        int port = Port(); var so = Options(); var co = Options();
        if (binary) { so.Serializer = new SocketJack.Serialization.BinarySerializer(); co.Serializer = new SocketJack.Serialization.BinarySerializer(); }
        using var server = new SocketJack.Net.UdpServer(so, port); using var client = new SocketJack.Net.UdpClient(co);
        var received = Signal<string>(); server.RegisterCallback<string>(e => received.TrySetResult(e.Object));
        Assert.IsTrue(server.Listen()); Assert.IsTrue(await client.Connect("127.0.0.1", port)); client.Send("plain");
        Assert.AreEqual("plain", await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
