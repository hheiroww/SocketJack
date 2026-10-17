using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Net;
using SocketJack.Serialization;
using System.Security;

namespace SocketJack.Tests;

[TestClass]
public sealed class BinarySegmentedDecodingTests {
    public sealed class Payload {
        public int Number { get; set; }
        public byte[] Data { get; set; }
        public string Text { get; set; }
        public decimal Amount { get; set; }
        public DateTime Stamp { get; set; }
    }
    static NetworkOptions Options(bool allowed = true) {
        var options = new NetworkOptions { Serializer = new BinarySerializer { ShareCompressionDictionary = false }, Logging = false, AutoReconnect = false, UsePeerToPeer = false };
        options.Whitelist.Add(typeof(Payload));
        if (allowed) options.Authorization.AnonymousMessageTypes.Add(typeof(Payload));
        return options;
    }
    static Payload Value() {
        var data = new byte[8192]; new Random(22101).NextBytes(data);
        return new Payload { Number = -147, Data = data, Text = "Split UTF-8: \u00e9 \u65e5 \ud83c\udf0d", Amount = 147.125m, Stamp = new DateTime(2026, 10, 15, 12, 30, 0, DateTimeKind.Utc) };
    }
    static void Equal(Payload expected, Payload actual) {
        Assert.AreEqual(expected.Number, actual.Number); Assert.AreEqual(expected.Text, actual.Text);
        Assert.AreEqual(expected.Amount, actual.Amount); Assert.AreEqual(expected.Stamp, actual.Stamp);
        CollectionAssert.AreEqual(expected.Data, actual.Data);
    }
    [DataTestMethod]
    [DataRow(1)][DataRow(3)][DataRow(31)][DataRow(1024)]
    public void SegmentedValuesCrossBoundariesAndOwnTheirByteFields(int size) {
        using var socket = new SocketJack.Net.TcpClient(Options());
        var binary = (BinarySerializer)socket.Options.Serializer;
        var value = Value(); byte[] raw = binary.Serialize(new Wrapper(value, socket));
        var segments = Enumerable.Range(0, (raw.Length + size - 1) / size).Select(i => (ReadOnlyMemory<byte>)raw.AsMemory(i * size, Math.Min(size, raw.Length - i * size))).ToArray();
        var actual = (Payload)binary.DeserializeSegments(segments, segments.Length, raw.Length).Unwrap(socket);
        Equal(value, actual);
        Array.Clear(raw);
        Equal(value, actual);
    }
    static (NetworkConnection connection, byte[] frame) Frame(NetworkBase socket) {
        var connection = new NetworkConnection(socket, null); socket.Connection = connection;
        SafeModeHandshake.Validate(SafeModeHandshake.Create(socket.Options), socket.Options, connection);
        connection.BinaryCompressionReady = true;
        var binary = (BinarySerializer)socket.Options.Serializer;
        byte[] raw = binary.Serialize(new Wrapper(Value(), socket));
        var cache = binary.GetCompressionCache(socket, connection);
        cache.Observe(raw); var definitions = cache.Observe(raw);
        byte[] frame = BinaryCompressionCodec.Encode(raw, definitions, new HashSet<ulong>(definitions.Select(d => d.Id)));
        Assert.IsTrue(BinaryCompressionCodec.HasMagic(frame));
        Assert.IsTrue(frame.Length < raw.Length / 4);
        return (connection, frame);
    }
    [TestMethod]
    public void ReferencedNetworkFrameUsesSegmentedReadAndPreservesAuthorization() {
        using var socket = new SocketJack.Net.TcpClient(Options());
        var packet = Frame(socket);
        Assert.IsTrue(BinaryNetworkCompression.TryDeserialize(socket, packet.connection, packet.frame, out _), "The fixture must exercise the segmented fast path.");
        var actual = (Payload)InboundMessageDecoder.Read(socket, packet.connection, packet.frame);
        Equal(Value(), actual);
        Array.Clear(packet.frame); Equal(Value(), actual);
        using var denied = new SocketJack.Net.TcpServer(Options(false), 0);
        var forbidden = Frame(denied);
        Assert.ThrowsException<SecurityException>(() => InboundMessageDecoder.Read(denied, forbidden.connection, forbidden.frame));
    }
    [TestMethod]
    public void SegmentParserFallsBackForOverlappingCopiesAndBoundsItsMetadata() {
        byte[] raw = Enumerable.Repeat((byte)'A', 32768).ToArray();
        byte[] frame = BinaryCompressionCodec.Encode(raw, [], new HashSet<ulong>());
        var segments = new ReadOnlyMemory<byte>[256];
        Assert.IsFalse(BinaryCompressionCodec.TryReadSegments(frame, raw.Length, _ => null, segments, out _, out _));
        CollectionAssert.AreEqual(raw, BinaryCompressionCodec.Decode(frame, raw.Length, _ => null, out _));
        using var bytes = new MemoryStream(); using var writer = new BinaryWriter(bytes);
        writer.Write(new byte[] { (byte)'S', (byte)'B', (byte)'C', 1, 1, 0xac, 2, 0 }); // 300 output bytes, no definitions.
        for (int i = 0; i < 300; i++) writer.Write(new byte[] { 0, 1, 42 });
        Assert.IsFalse(BinaryCompressionCodec.TryReadSegments(bytes.ToArray(), 300, _ => null, segments, out _, out _));
        Assert.ThrowsException<InvalidDataException>(() => BinaryCompressionCodec.TryReadSegments(frame, 100, _ => null, segments, out _, out _));
    }
}
