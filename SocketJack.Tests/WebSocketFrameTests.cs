using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Net;
using SocketJack.Net.WebSockets;
using SocketJack.Serialization;
using System.Reflection;
using System.Text;

namespace SocketJack.Tests;

[TestClass]
public class WebSocketFrameTests {
    private sealed class PartialStream : Stream {
        private readonly MemoryStream input;
        public readonly MemoryStream Written = new();
        public PartialStream(byte[] bytes) => input = new(bytes);
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => input.Length;
        public override long Position { get => input.Position; set => throw new NotSupportedException(); }
        public override int Read(byte[] b, int o, int c) => input.Read(b, o, Math.Min(c, 1));
        public override Task<int> ReadAsync(byte[] b, int o, int c, CancellationToken t) { t.ThrowIfCancellationRequested(); return Task.FromResult(Read(b, o, c)); }
        public override void Write(byte[] b, int o, int c) => Written.Write(b, o, c);
        public override Task WriteAsync(byte[] b, int o, int c, CancellationToken t) { Write(b, o, c); return Task.CompletedTask; }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
    }
    private static byte[] Frame(byte[] data, int opcode = 1, bool fin = true) {
        using var output = new MemoryStream();
        output.WriteByte((byte)((fin ? 128 : 0) | opcode));
        if (data.Length < 126) output.WriteByte((byte)data.Length);
        else { output.WriteByte(126); output.WriteByte((byte)(data.Length >> 8)); output.WriteByte((byte)data.Length); }
        output.Write(data); return output.ToArray();
    }
    private static WebSocketClient Client(PartialStream stream) {
        var options = new NetworkOptions { SafeMode = false, UseCompression = false, UsePeerToPeer = false };
        options.Whitelist.Add(typeof(string)); options.Authorization.AnonymousMessageTypes.Add(typeof(string));
        var client = new WebSocketClient(options);
        client.Connection = new NetworkConnection(client, null);
        typeof(WebSocketClient).GetField("_stream", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(client, stream);
        return client;
    }
    [TestMethod]
    public async Task PartialHeadersAndKeepaliveFramesDoNotCorruptTypedMessages() {
        using var encoder = new WebSocketClient(new NetworkOptions { SafeMode = false });
        var text = new string('x', 300);
        var message = encoder.Options.Serializer.Serialize(new Wrapper(text, encoder));
        var ping = Encoding.UTF8.GetBytes("KeepAlive");
        var stream = new PartialStream(Frame(ping, 9).Concat(Frame(ping, 10)).Concat(Frame(message)).ToArray());
        using var client = Client(stream);
        var errors = new List<Exception>(); client.OnError += e => errors.Add(e.Exception);
        Assert.IsNull(await client.ReceiveAsync());
        Assert.IsNull(await client.ReceiveAsync());
        Assert.AreEqual(text, (await client.ReceiveAsync()).Object);
        Assert.AreEqual(0, errors.Count);
        var pong = stream.Written.ToArray(); Assert.AreEqual(0x8A, pong[0]); Assert.IsTrue((pong[1] & 128) != 0);
        var unmasked = pong.Skip(6).Select((b, i) => (byte)(b ^ pong[2 + i % 4])).ToArray();
        CollectionAssert.AreEqual(ping, unmasked);
    }
    [TestMethod]
    public async Task FragmentedMessageSurvivesInterleavedPing() {
        using var encoder = new WebSocketClient(new NetworkOptions { SafeMode = false });
        var message = encoder.Options.Serializer.Serialize(new Wrapper("connected", encoder));
        int split = message.Length / 2;
        var stream = new PartialStream(Frame(message.Take(split).ToArray(), 1, false)
            .Concat(Frame(Encoding.UTF8.GetBytes("KeepAlive"), 9)).Concat(Frame(message.Skip(split).ToArray(), 0)).ToArray());
        using var client = Client(stream);
        Assert.IsNull(await client.ReceiveAsync()); Assert.IsNull(await client.ReceiveAsync());
        Assert.AreEqual("connected", (await client.ReceiveAsync()).Object);
    }
    [DataTestMethod][DataRow(false)][DataRow(true)]
    public async Task EndOfStreamTerminatesReceiveInsteadOfSpinning(bool truncated) {
        using var client = Client(new PartialStream(truncated ? new byte[] { 0x81, 5, 1 } : Array.Empty<byte>()));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        Assert.AreEqual(-1, (await client.ReceiveAsync(timeout)).Length);
    }
}
