using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Net;
using SocketJack.Serialization;
using System.Net;

namespace SocketJack.UdpReliable.Tests;

[TestClass]
public sealed class OptimizationTests {
    public sealed class Scalars {
        public int Signed { get; set; }
        public ulong Unsigned { get; set; }
        public decimal Decimal { get; set; }
        public double Double { get; set; }
        public float Float { get; set; }
        public string Text { get; set; }
        public byte[] Bytes { get; set; }
    }
    public sealed class GetterOnce {
        public int Calls;
        public int Value => ++Calls;
    }
    static NetworkOptions Options() {
        var options = new NetworkOptions { Serializer = new BinarySerializer(), UsePeerToPeer = false, EnablePatternCache = false };
        var assembly = typeof(Scalars).Assembly;
        options.VerifiedAssemblies.Add(assembly);
        options.Authorization.AnonymousMessageTypes.Add(typeof(Payload));
        return options;
    }
    [TestMethod]
    public void SpanCodecPreservesV1BytesAcrossBlockBoundariesAndReadsOldMessages() {
        var before = new BeforeBinarySerializer(); var after = new BinarySerializer();
        object[] values = { null, false, true, long.MinValue, long.MaxValue, ulong.MaxValue,
            decimal.MinValue, -0.0, double.NaN, float.PositiveInfinity, "héllo 👋", new object[] { "nested", new int[] { -1, 0, 127, 128 } },
            new Dictionary<string, int> { ["one"] = 1 }, DateTime.UnixEpoch,
            new Scalars { Signed = int.MinValue, Unsigned = ulong.MaxValue, Decimal = 79228.162514m, Double = double.Epsilon, Float = float.MinValue,
                Text = string.Concat(Enumerable.Repeat("é😀", 20000)), Bytes = new byte[262144] } };
        foreach (var value in values) {
            var input = new Wrapper { Type = value?.GetType().FullName ?? "System.String", value = value };
            byte[] old = before.Serialize(input), current = after.Serialize(input);
            CollectionAssert.AreEqual(old, current, value?.GetType().FullName);
            Assert.AreEqual(input.Type, after.Deserialize(old).Type);
        }
        using var socket = new SocketJack.Net.UdpClient(Options()); socket.RegisterCallback<Scalars>(_ => { });
        var original = (Scalars)values[^1]; new Random(731).NextBytes(original.Bytes);
        var restored = (Scalars)after.Deserialize(before.Serialize(new Wrapper(original, socket))).Unwrap(socket);
        Assert.AreEqual(original.Text, restored.Text); Assert.AreEqual(original.Decimal, restored.Decimal);
        Assert.AreEqual(original.Double, restored.Double); Assert.AreEqual(original.Float, restored.Float);
        CollectionAssert.AreEqual(original.Bytes, restored.Bytes);
        var getter = new GetterOnce(); after.Serialize(getter); Assert.AreEqual(1, getter.Calls);
        var fields = new Wrapper { Type = typeof(Scalars).FullName, value = new Dictionary<string, object> { [nameof(Scalars.Signed)] = -77, [nameof(Scalars.Text)] = "dictionary DTO" } };
        var fromFields = (Scalars)after.Deserialize(after.Serialize(fields)).Unwrap(socket);
        Assert.AreEqual(-77, fromFields.Signed); Assert.AreEqual("dictionary DTO", fromFields.Text);
    }
    [TestMethod]
    public void SharedSerializerIsConcurrentAndRejectsMalformedLengthsAndNumbers() {
        var serializer = new BinarySerializer();
        Parallel.For(0, 1000, i => {
            string value = i + new string('x', i % 600);
            var result = serializer.Deserialize(serializer.Serialize(new Wrapper { Type = "System.String", value = value }));
            Assert.AreEqual(value, result.value);
        });
        foreach (byte[] invalid in new[] {
            new byte[] { 83, 66, 1, 12, 0, 4, 128, 0 }, // noncanonical integer
            new byte[] { 83, 66, 1, 12, 0, 9, 255, 255, 255, 255, 15 }, // oversized blob
            new byte[] { 83, 66, 1, 12, 0, 11, 2, 1, 97, 0, 1, 97, 0 }, // duplicate keys
            new byte[] { 83, 66, 1, 12, 0, 0, 0 }, // trailing byte
            new byte[] { 83, 66, 1, 12, 0, 255 } }) {
            Assert.ThrowsException<InvalidDataException>(() => serializer.Deserialize(invalid));
        }
    }
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task InterfaceReplyWorksWithoutPeerRouting(bool binary) {
        using var allocation = new System.Net.Sockets.UdpClient(0);
        int port = ((IPEndPoint)allocation.Client.LocalEndPoint).Port; allocation.Close();
        var so = Options(); var co = Options();
        if (!binary) so.Serializer = co.Serializer = new SocketJack.Serialization.Json.JsonSerializer();
        so.Authorization.AnonymousMessageTypes.Add(typeof(string));
        using var server = UDP_Reliable.CreateServer(port, so); using var client = UDP_Reliable.CreateClient(co);
        var errors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        server.OnError += e => errors.Enqueue(e.Exception); client.OnError += e => errors.Enqueue(e.Exception);
        var replied = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.RegisterCallback<string>(e => ((ISocket)server).Send(e.Connection, "reply:" + e.Object));
        client.RegisterCallback<string>(e => replied.TrySetResult(e.Object));
        Assert.IsTrue(server.Listen()); Assert.IsTrue(await client.Connect("127.0.0.1", port));
        await client.SendAsync("request");
        try { Assert.AreEqual("reply:request", await replied.Task.WaitAsync(TimeSpan.FromSeconds(5))); }
        catch { Assert.Fail(string.Join("\n", errors)); }
    }
    [DataTestMethod]
    [DataRow(32768, 1200, 4, 64)]
    [DataRow(1200, 32768, 64, 4)]
    [DataRow(32768, 32768, 64, 64)]
    public async Task NegotiatedWindowRespectsBothEndpoints(int clientMtu, int serverMtu, int clientPackets, int serverPackets) {
        using var allocation = new System.Net.Sockets.UdpClient(0);
        int port = ((IPEndPoint)allocation.Client.LocalEndPoint).Port; allocation.Close();
        var co = Options(); var so = Options();
        co.UdpReliable.DatagramSize = clientMtu; so.UdpReliable.DatagramSize = serverMtu;
        co.UdpReliable.MaximumFlightPackets = clientPackets; so.UdpReliable.MaximumFlightPackets = serverPackets;
        co.UdpReliable.Profile = so.UdpReliable.Profile = UdpReliableProfile.FastLan;
        using var server = UDP_Reliable.CreateServer(port, so); using var client = UDP_Reliable.CreateClient(co);
        var result = new TaskCompletionSource<Payload>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.RegisterCallback<Payload>(e => result.TrySetResult(e.Object)); client.RegisterCallback<Payload>(_ => { });
        Assert.IsTrue(server.Listen()); Assert.IsTrue(await client.Connect("127.0.0.1", port));
        Assert.AreEqual(Math.Min(clientMtu, serverMtu), client.ReliablePeer.Mtu);
        Assert.IsTrue(client.ReliablePeer.FlightLimit <= Math.Min(clientPackets, serverPackets));
        byte[] payload = new byte[262144]; new Random(17).NextBytes(payload);
        await client.SendAsync(new Payload { Bytes = payload }).WaitAsync(TimeSpan.FromSeconds(10));
        CollectionAssert.AreEqual(payload, (await result.Task.WaitAsync(TimeSpan.FromSeconds(10))).Bytes);
    }
}
