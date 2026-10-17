using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Net;
using SocketJack.Serialization;
using System.Collections.Concurrent;
using System.Net;
using System.Reflection;

namespace SocketJack.Tests;

[TestClass]
public sealed class BinaryCompressionDeliveryTests {
    public sealed class DeliveryMessage {
        public int Number { get; set; }
        public byte[] Data { get; set; }
    }

    sealed class Pair : IDisposable {
        internal readonly SocketJack.Net.TcpClient Sender = new(Options());
        internal readonly SocketJack.Net.TcpClient Receiver = new(Options());
        internal readonly NetworkConnection Sending;
        internal readonly NetworkConnection Receiving;
        internal BinaryCompressionCache SenderCache => ((BinarySerializer)Sender.Options.Serializer).GetCompressionCache(Sender);
        internal BinaryCompressionCache ReceiverCache => ((BinarySerializer)Receiver.Options.Serializer).GetCompressionCache(Receiver);

        internal Pair() {
            Sending = new NetworkConnection(Sender, null);
            Receiving = new NetworkConnection(Receiver, null);
            Sender.Connection = Sending;
            Receiver.Connection = Receiving;
            SafeModeHandshake.Validate(SafeModeHandshake.Create(Receiver.Options), Sender.Options, Sending);
            SafeModeHandshake.Validate(SafeModeHandshake.Create(Sender.Options), Receiver.Options, Receiving);
            BinaryNetworkCompression.Synchronize(Sender, Sending);
            BinaryNetworkCompression.Synchronize(Receiver, Receiving);
            Assert.IsTrue(BinaryNetworkCompression.Enabled(Sender, Sending));
            Assert.IsTrue(BinaryNetworkCompression.Enabled(Receiver, Receiving));
        }

        public void Dispose() {
            SenderCache.Dispose();
            ReceiverCache.Dispose();
            Sender.Dispose();
            Receiver.Dispose();
        }
    }

    static NetworkOptions Options() => new() {
        Serializer = new BinarySerializer(), UsePeerToPeer = false,
        EnablePatternCache = false, AutoReconnect = false, Logging = false
    };

    static byte[] RandomBytes(int length, int seed) {
        var bytes = new byte[length]; new Random(seed).NextBytes(bytes); return bytes;
    }

    static BinaryCompressionDefinition Definition(int seed, int length = 1024) {
        byte[] bytes = RandomBytes(length, seed);
        return new BinaryCompressionDefinition(BinaryCompressionCodec.Hash(bytes), bytes);
    }

    static byte[] Data(BinaryCompressionDefinition definition, int seed) =>
        RandomBytes(23, seed).Concat(definition.Data).Concat(RandomBytes(25, seed + 1)).ToArray();

    static byte[] Encode(Pair pair, byte[] raw) => BinaryNetworkCompression.Encode(pair.Sender, pair.Sending, raw);
    static byte[] Decode(Pair pair, byte[] encoded) => BinaryNetworkCompression.Decode(pair.Receiver, pair.Receiving, encoded);

    [TestMethod]
    public void PeerSupplyingDefinitionCanReceiveAnImmediateReferencedReply() {
        using var pair = new Pair();
        var definition = Definition(1501);
        Assert.IsTrue(pair.SenderCache.Import(definition));
        Assert.IsNull(Decode(pair, BinaryCompressionCodec.Update(new[] { definition })));
        byte[] raw = Data(definition, 1502);
        byte[] reply = BinaryNetworkCompression.Encode(pair.Receiver, pair.Receiving, raw);
        Assert.IsTrue(reply.Length < raw.Length / 2, "The uploading peer already owns its immutable definition; the reply need not send it back inline.");
        CollectionAssert.AreEqual(raw, BinaryNetworkCompression.Decode(pair.Sender, pair.Sending, reply));
    }

    [TestMethod]
    public void DataSurvivesReorderingWhenDefinitionUpdateIsLost() {
        using var pair = new Pair();
        var definition = Definition(501);
        Assert.IsTrue(pair.SenderCache.Import(definition));
        byte[] first = Data(definition, 502), second = Data(definition, 504);
        byte[] firstFrame = Encode(pair, first), secondFrame = Encode(pair, second);

        // No update or acknowledgment is delivered. Either frame must stand alone.
        CollectionAssert.AreEqual(second, Decode(pair, secondFrame));
        CollectionAssert.AreEqual(first, Decode(pair, firstFrame));
        CollectionAssert.AreEqual(second, Decode(pair, secondFrame));
    }

    [TestMethod]
    public void LostAcknowledgmentDoesNotAllowPrematureDictionaryReferences() {
        using var pair = new Pair();
        var definition = Definition(511);
        Assert.IsTrue(pair.SenderCache.Import(definition));
        Assert.IsNull(Decode(pair, BinaryCompressionCodec.Update(new[] { definition })));
        Assert.IsTrue(pair.ReceiverCache.TryGet(definition.Id, out _));

        // The receiver's acknowledgment is deliberately lost. Decode without any repository
        // to prove that the sender still includes everything needed by this data frame.
        byte[] raw = Data(definition, 512);
        byte[] encoded = Encode(pair, raw);
        CollectionAssert.AreEqual(raw,
            BinaryCompressionCodec.Decode(encoded, raw.Length, _ => null, out _));
    }

    [TestMethod]
    public void AcknowledgedDictionaryReferencesSurviveReorderingAndDuplicateAcknowledgments() {
        using var pair = new Pair();
        var definition = Definition(521);
        Assert.IsTrue(pair.SenderCache.Import(definition));
        Assert.IsNull(Decode(pair, BinaryCompressionCodec.Update(new[] { definition })));
        byte[] acknowledgment = BinaryCompressionCodec.Acknowledge(new[] { definition.Id });
        Assert.IsNull(BinaryNetworkCompression.Decode(pair.Sender, pair.Sending, acknowledgment));
        Assert.IsNull(BinaryNetworkCompression.Decode(pair.Sender, pair.Sending, acknowledgment));

        byte[] first = Data(definition, 522), second = Data(definition, 524);
        byte[] firstFrame = Encode(pair, first), secondFrame = Encode(pair, second);
        Assert.IsTrue(BinaryCompressionCodec.HasMagic(firstFrame));
        Assert.IsTrue(BinaryCompressionCodec.HasMagic(secondFrame));
        Assert.IsTrue(firstFrame.Length < first.Length / 4);
        Assert.ThrowsException<InvalidDataException>(() =>
            BinaryCompressionCodec.Decode(firstFrame, first.Length, _ => null, out _));

        CollectionAssert.AreEqual(second, Decode(pair, secondFrame));
        CollectionAssert.AreEqual(first, Decode(pair, firstFrame));
    }

    [TestMethod]
    public void FullReceiverDictionaryDoesNotMakeUnacknowledgedDataUndecodable() {
        using var pair = new Pair();
        for (int i = 0; i < BinaryCompressionCodec.MaximumDefinitions; i++)
            Assert.IsTrue(pair.ReceiverCache.Import(Definition(600 + i, 64)));
        var definition = Definition(5000);
        Assert.IsTrue(pair.SenderCache.Import(definition));
        Assert.IsNull(Decode(pair, BinaryCompressionCodec.Update(new[] { definition })));
        Assert.IsFalse(pair.ReceiverCache.TryGet(definition.Id, out _));

        byte[] first = Data(definition, 5001), second = Data(definition, 5003);
        byte[] firstFrame = Encode(pair, first), secondFrame = Encode(pair, second);
        CollectionAssert.AreEqual(second, Decode(pair, secondFrame));
        CollectionAssert.AreEqual(first, Decode(pair, firstFrame));
    }

    [TestMethod]
    public void ReorderedUdpDictionaryFrameIsDroppedUntilHandshakeCompletes() {
        using var client = new SocketJack.Net.UdpClient(Options());
        client.Connection = new NetworkConnection(client, null);
        var receive = typeof(SocketJack.Net.UdpClient).GetMethod("ReceiveSafeModeReply", BindingFlags.Instance | BindingFlags.NonPublic);
        byte[] update = BinaryCompressionCodec.Update(new[] { Definition(551) });

        Assert.AreEqual(true, receive.Invoke(client, new object[] { update }));
        Assert.IsFalse(client.Connection.SafeModeVerified);
        Assert.IsFalse(client.Connection.BinaryCompressionReady);
        Assert.AreEqual(0, ((BinarySerializer)client.Options.Serializer).CompressionDictionaryCount);

        Assert.AreEqual(true, receive.Invoke(client, new object[] { SafeModeHandshake.Create(Options()) }));
        Assert.IsTrue(client.Connection.SafeModeVerified);
        Assert.IsTrue(client.Connection.BinaryCompressionReady);
    }

    [TestMethod]
    public async Task LateWebSocketClientReceivesDictionaryPagesAfterUpgradeBeforeTypedTraffic() {
        static NetworkOptions TransportOptions() {
            var options = Options();
            options.Whitelist.Add(typeof(DeliveryMessage));
            options.Authorization.AnonymousMessageTypes.Add(typeof(DeliveryMessage));
            return options;
        }
        static async Task Until(Func<bool> condition, Func<string> message) {
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(20);
            Assert.IsTrue(condition(), message());
        }

        int port;
        using (var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0)) {
            listener.Start(); port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        using var server = new SocketJack.Net.WebSockets.WebSocketServer(port) { Options = TransportOptions() };
        using var first = new SocketJack.Net.WebSockets.WebSocketClient(TransportOptions());
        using var late = new SocketJack.Net.WebSockets.WebSocketClient(TransportOptions());
        var errors = new ConcurrentQueue<string>();
        server.OnError += e => errors.Enqueue(e.Exception.ToString());
        first.OnError += e => errors.Enqueue(e.Exception.ToString());
        late.OnError += e => errors.Enqueue(e.Exception.ToString());
        var received = new ConcurrentDictionary<int, byte[]>();
        first.RegisterCallback<DeliveryMessage>(e => received[e.Object.Number] = e.Object.Data);
        var lateReceived = new TaskCompletionSource<DeliveryMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        late.RegisterCallback<DeliveryMessage>(e => lateReceived.TrySetResult(e.Object));

        Assert.IsTrue(server.Listen());
        Assert.IsTrue(await first.Connect("127.0.0.1", port), string.Join("; ", errors));
        await Until(() => server.Clients.Count == 1, () => "The first WebSocket client was not registered.");
        var firstConnection = server.Clients.Values.Single();
        byte[] data = RandomBytes(16384, 561);
        for (int i = 0; i < 3; i++)
            await server.SendAsync(firstConnection, new DeliveryMessage { Number = i, Data = data });
        await Until(() => received.Count == 3, () => "Training messages did not arrive: " + string.Join("; ", errors));
        foreach (var actual in received.Values) CollectionAssert.AreEqual(data, actual);
        var definitions = ((BinarySerializer)server.Options.Serializer).GetCompressionCache(server).Definitions;
        Assert.IsTrue(definitions.Sum(d => d.Data.Length) > 4096, "The fixture must require post-handshake dictionary pages.");

        Assert.IsTrue(await late.Connect("127.0.0.1", port), string.Join("; ", errors));
        var lateCache = ((BinarySerializer)late.Options.Serializer).GetCompressionCache(late);
        await Until(() => definitions.All(d => lateCache.TryGet(d.Id, out _)),
            () => "The late WebSocket client did not receive all dictionary pages: " + string.Join("; ", errors));
        server.SendBroadcast(new DeliveryMessage { Number = 99, Data = data });
        var result = await lateReceived.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.AreEqual(99, result.Number);
        CollectionAssert.AreEqual(data, result.Data);
        Assert.IsTrue(late.Connection.BinaryCompressionReady);
        Assert.IsTrue(late.Connected, string.Join("; ", errors));
    }
}
