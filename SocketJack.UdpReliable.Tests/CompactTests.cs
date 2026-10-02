using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Net;
using SocketJack.Serialization;
using System.Net;
using System.Collections.Concurrent;

namespace SocketJack.UdpReliable.Tests;

[TestClass]
public sealed class CompactTests {
    static int Port() { using var socket = new System.Net.Sockets.UdpClient(0); return ((IPEndPoint)socket.Client.LocalEndPoint).Port; }
    static NetworkOptions Options(bool ordering = false) => Approve(new NetworkOptions { UsePeerToPeer = false,
        Serializer = new BinarySerializer(), EnablePatternCache = false,
        UdpReliable = new() { Ordering = ordering, Profile = UdpReliableProfile.FastLan } });

    static NetworkOptions Approve(NetworkOptions options) {
        var assembly = typeof(Payload).Assembly;
        options.VerifiedAssemblies.Add(assembly);
        options.Authorization.RequireAuthentication = false; // This fixture exercises anonymous data transport, not login.
        return options;
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OrderingControlsWhetherSlowCallbackBlocksNextObject(bool ordering) {
        Assert.IsFalse(new UdpReliableOptions().Ordering);
        Assert.AreEqual(UdpDeliveryMode.Independent, new UdpReliableOptions().Delivery);
        int port = Port(); using var server = UDP_Reliable.CreateServer(port, Options(ordering));
        using var client = UDP_Reliable.CreateClient(Options(ordering));
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var next = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.RegisterCallback<int>(e => { if (e.Object == 1) { entered.Set(); release.Wait(TimeSpan.FromSeconds(10)); } else next.TrySetResult(); });
        Assert.IsTrue(server.Listen()); Assert.IsTrue(await client.Connect("127.0.0.1", port));
        try {
            await client.SendAsync(1); Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(5)));
            await client.SendAsync(2);
            if (ordering) { await Task.Delay(150); Assert.IsFalse(next.Task.IsCompleted); }
            else await next.Task.WaitAsync(TimeSpan.FromSeconds(5));
        } finally { release.Set(); }
        await next.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [DataTestMethod]
    [DataRow(.01, 1200)]
    [DataRow(.05, 1200)]
    [DataRow(.01, 32768)]
    [DataRow(.05, 32768)]
    public async Task BinaryIndependentObjectsReassembleAcrossPacketIdReuse(double loss, int datagramSize) {
        int port = Port(); var so = Options(); var co = Options(); so.UdpReliable.DatagramSize = co.UdpReliable.DatagramSize = datagramSize;
        using var server = UDP_Reliable.CreateServer(port, so);
        using var proxy = new ImpairmentProxy(port, loss); using var client = UDP_Reliable.CreateClient(co);
        var received = new ConcurrentDictionary<int, byte[]>(); int duplicates = 0;
        server.RegisterCallback<Payload>(e => { if (!received.TryAdd(e.Object.Index, e.Object.Bytes)) Interlocked.Increment(ref duplicates); });
        client.RegisterCallback<Payload>(_ => { });
        Assert.IsTrue(server.Listen()); Assert.IsTrue(await client.Connect("127.0.0.1", proxy.Port));
        byte[] bytes = new byte[256 * 1024]; new Random(812).NextBytes(bytes);
        for (int i = 0; i < 8; i++) await client.SendAsync(new Payload { Index = i, Bytes = bytes }).WaitAsync(TimeSpan.FromSeconds(20));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (received.Count != 8 && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.AreEqual(8, received.Count); Assert.AreEqual(0, duplicates);
        foreach (byte[] data in received.Values) CollectionAssert.AreEqual(bytes, data);
        Assert.IsTrue(proxy.Dropped > 0); Assert.IsTrue(proxy.Duplicated > 0);
    }

    [TestMethod]
    public void CompactHeadersUseSmallRecycledPacketIndexesAndRejectMalformedNumbers() {
        byte[] bytes = new byte[32];
        foreach (ulong id in new[] { 1UL, 127UL, 128UL, (1UL << 63) | 1, ulong.MaxValue - 1 }) {
            foreach (int packet in new[] { 0, 127, 128, 65535, int.MaxValue }) {
                var frame = new UdpReliableTransport.Frame { Type = UdpReliableTransport.Op.Data, Session = 123, Id = id, A = packet };
                int length = UdpReliableWire.Write(bytes, frame);
                Assert.IsTrue(UdpReliableWire.Read(bytes, length, out var op, out var session, out var actual, out var index, out _, out var header));
                Assert.AreEqual(id, actual); Assert.AreEqual(packet, index); Assert.AreEqual(123UL, session); Assert.AreEqual(length, header);
                if (id == 1 && packet == 0) Assert.AreEqual(14, length); // formerly 40 bytes
                Assert.IsFalse(UdpReliableWire.Read(bytes, length - 1, out _, out _, out _, out _, out _, out _));
            }
        }
        var first = new UdpReliableTransport.Frame { Type = UdpReliableTransport.Op.Data, Session = 123, Id = 1, A = 0 };
        var second = new UdpReliableTransport.Frame { Type = UdpReliableTransport.Op.Data, Session = 123, Id = 2, A = 0 };
        int size = UdpReliableWire.Write(bytes, first); byte[] old = bytes[..size];
        UdpReliableWire.Write(bytes, second); Assert.AreEqual(old[^1], bytes[size - 1]); Assert.AreNotEqual(old[12], bytes[12]);
        Array.Fill(bytes, (byte)255, 12, 20);
        Assert.IsFalse(UdpReliableWire.Read(bytes, bytes.Length, out _, out _, out _, out _, out _, out _));
    }

    [TestMethod]
    public void BinaryEncodingPreservesPayloadAndWhitelistAndRejectsTruncation() {
        var serializer = new BinarySerializer(); using var socket = new SocketJack.Net.UdpClient(Options());
        socket.RegisterCallback<Payload>(_ => { });
        byte[] data = new byte[32768]; new Random(90).NextBytes(data);
        var source = new Payload { Index = -123, Bytes = data }; var wrapper = new Wrapper(source, socket);
        byte[] encoded = serializer.Serialize(wrapper);
        Assert.IsTrue(encoded.Length < data.Length + 200);
        var restored = (Payload)serializer.Deserialize(encoded).Unwrap(socket);
        Assert.AreEqual(source.Index, restored.Index); CollectionAssert.AreEqual(data, restored.Bytes);
        foreach (int cut in new[] { 0, 1, 2, 3, encoded.Length / 2, encoded.Length - 1 }) {
            bool rejected = false; try { serializer.Deserialize(encoded[..cut]); } catch (Exception ex) when (ex is IOException || ex is InvalidDataException) { rejected = true; }
            Assert.IsTrue(rejected, $"Truncation at {cut} was accepted");
        }
        socket.Options.Whitelist.Remove(typeof(Payload));
        Assert.ThrowsException<TypeNotAllowedException>(() => serializer.Deserialize(encoded).Unwrap(socket));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new UdpReliableOptions { BufferSize = 32769 }.Snapshot());
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new UdpReliableOptions { DatagramSize = 32769 }.Snapshot());
    }
}
