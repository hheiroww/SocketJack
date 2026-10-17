using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Net;
using SocketJack.Serialization;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace SocketJack.Tests;

[TestClass, DoNotParallelize]
public sealed class BinaryCompressionUdpImpairmentTests {
    public sealed class Payload {
        public int Index { get; set; }
        public byte[] Bytes { get; set; }
    }

    static NetworkOptions Options(bool compression) {
        var options = new NetworkOptions {
            UsePeerToPeer = false, Logging = false, AutoReconnect = false,
            Serializer = new BinarySerializer { EnableCompression = compression }, EnablePatternCache = false,
            UdpReliable = new UdpReliableOptions { Ordering = false, Profile = UdpReliableProfile.FastLan, DatagramSize = 32768 }
        };
        options.Whitelist.Add(typeof(Payload));
        options.Authorization.AnonymousMessageTypes.Add(typeof(Payload));
        return options;
    }

    [DataTestMethod]
    [DataRow(false, .01)][DataRow(true, .01)]
    [DataRow(false, .05)][DataRow(true, .05)]
    public async Task LargeDatagramsWithPacketLossRoundTripWithCompressionEnabledOrDisabled(bool compression, double loss) {
        int port;
        using (var available = new System.Net.Sockets.UdpClient(0)) port = ((IPEndPoint)available.Client.LocalEndPoint).Port;
        using var server = UDP_Reliable.CreateServer(port, Options(compression));
        using var proxy = new LossProxy(port, loss);
        using var client = UDP_Reliable.CreateClient(Options(compression));
        var received = new ConcurrentDictionary<int, byte[]>(); int duplicates = 0;
        var errors = new ConcurrentQueue<string>();
        server.OnError += e => errors.Enqueue(e.Exception.Message);
        client.OnError += e => errors.Enqueue(e.Exception.Message);
        server.RegisterCallback<Payload>(e => { if (!received.TryAdd(e.Object.Index, e.Object.Bytes)) Interlocked.Increment(ref duplicates); });
        client.RegisterCallback<Payload>(_ => { });
        Assert.IsTrue(server.Listen());
        Assert.IsTrue(await client.Connect("127.0.0.1", proxy.Port), string.Join("; ", errors));
        Assert.AreEqual(compression, client.Connection.Compatibility.BinaryCompression);
        byte[] bytes = new byte[256 * 1024]; new Random(812).NextBytes(bytes);
        for (int index = 0; index < 8; index++) {
            var timer = Stopwatch.StartNew();
            try { await client.SendAsync(new Payload { Index = index, Bytes = bytes }).WaitAsync(TimeSpan.FromSeconds(20)); }
            catch (Exception error) {
                Assert.Fail($"Compression={compression}, loss={loss}, failed message={index}, received={received.Count}, " +
                    $"dictionary={((BinarySerializer)client.Options.Serializer).CompressionDictionaryCount}, dropped={proxy.Dropped}, " +
                    $"retries={client.ReliableStatistics?.Retransmissions}, queued={client.ReliableStatistics?.QueuedBytes}, " +
                    $"wire={client.ReliableStatistics?.WireBytesSent}; {error}; " + string.Join("; ", errors));
            }
            Console.WriteLine($"Compression={compression}, loss={loss}, message={index}, elapsed={timer.ElapsedMilliseconds}ms, " +
                $"received={received.Count}, dictionary={((BinarySerializer)client.Options.Serializer).CompressionDictionaryCount}, " +
                $"dropped={proxy.Dropped}, retries={client.ReliableStatistics?.Retransmissions}.");
        }
        var deadline = Stopwatch.StartNew();
        while (received.Count != 8 && deadline.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(10);
        Assert.AreEqual(8, received.Count); Assert.AreEqual(0, duplicates);
        foreach (byte[] data in received.Values) CollectionAssert.AreEqual(bytes, data);
        Assert.AreEqual(compression, ((BinarySerializer)client.Options.Serializer).CompressionDictionaryCount > 0);
        Assert.IsTrue(proxy.Dropped > 0); Assert.IsTrue(proxy.Duplicated > 0);
    }

    // The same deterministic impairment pattern as the reliable UDP regression fixture.
    // Its sockets deliberately keep the OS default queue sizes to reproduce that path.
    sealed class LossProxy : IDisposable {
        readonly System.Net.Sockets.UdpClient downstream = new(new IPEndPoint(IPAddress.Loopback, 0));
        readonly System.Net.Sockets.UdpClient upstream = new(new IPEndPoint(IPAddress.Loopback, 0));
        readonly CancellationTokenSource stop = new();
        readonly Random random = new(1701);
        readonly object gate = new();
        readonly IPEndPoint server;
        readonly double loss;
        IPEndPoint client;
        int packets;
        public int Dropped { get; private set; }
        public int Duplicated { get; private set; }
        public int Port => ((IPEndPoint)downstream.Client.LocalEndPoint).Port;
        public LossProxy(int port, double loss) {
            server = new IPEndPoint(IPAddress.Loopback, port); this.loss = loss;
            _ = Loop(downstream, upstream, true); _ = Loop(upstream, downstream, false);
        }
        async Task Loop(System.Net.Sockets.UdpClient input, System.Net.Sockets.UdpClient output, bool toServer) {
            try {
                while (!stop.IsCancellationRequested) {
                    var packet = await input.ReceiveAsync(stop.Token);
                    if (toServer) client = packet.RemoteEndPoint;
                    var target = toServer ? server : client;
                    if (target == null) continue;
                    bool drop, duplicate; int delay;
                    lock (gate) {
                        packets++;
                        drop = random.NextDouble() < loss || packets % 211 < 4;
                        duplicate = packets % 37 == 0;
                        delay = packets % 11 == 0 ? 12 : 0;
                        if (drop) Dropped++;
                        if (duplicate) Duplicated++;
                    }
                    if (!drop) _ = Forward(output, target, packet.Buffer, delay, duplicate);
                }
            } catch (OperationCanceledException) { } catch (ObjectDisposedException) { }
            catch (SocketException) when (stop.IsCancellationRequested) { }
        }
        async Task Forward(System.Net.Sockets.UdpClient socket, IPEndPoint target, byte[] data, int delay, bool duplicate) {
            try {
                if (delay != 0) await Task.Delay(delay, stop.Token);
                await socket.SendAsync(data, target, stop.Token);
                if (duplicate) await socket.SendAsync(data, target, stop.Token);
            } catch (OperationCanceledException) { } catch (ObjectDisposedException) { }
            catch (SocketException) when (stop.IsCancellationRequested) { }
        }
        public void Dispose() { stop.Cancel(); downstream.Dispose(); upstream.Dispose(); }
    }
}
