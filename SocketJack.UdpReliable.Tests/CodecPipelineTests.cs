using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Extensions;
using SocketJack.Net;
using SocketJack.Serialization;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Security;
using System.Security.Cryptography;
using System.Text;

namespace SocketJack.UdpReliable.Tests;

[TestClass]
public sealed class CodecPipelineTests {
    public enum LargeEnum : ulong { Maximum = ulong.MaxValue }
    public sealed class Primitives {
        public long Signed { get; set; }
        public ulong Unsigned { get; set; }
        public short Short { get; set; }
        public ushort UShort { get; set; }
        public sbyte Small { get; set; }
        public byte Byte { get; set; }
        public uint UInt { get; set; }
        public bool Boolean { get; set; }
        public float Float { get; set; }
        public double Double { get; set; }
        public LargeEnum Enum { get; set; }
        public int? Nullable { get; set; }
        public string Text { get; set; }
        public byte[] Bytes { get; set; }
        public int[] Numbers { get; set; }
    }
    public sealed class Blob { public int Number { get; set; } public byte[] Data { get; set; } }
    public sealed class Reordered { public byte[] Data { get; set; } public string Extra { get; set; } public int Number { get; set; } }
    public sealed class Nested { public object Value { get; set; } }
    public sealed class Node { public Node Next { get; set; } public int Number { get; set; } }
    static NetworkOptions Options(bool binary = true) {
        var options = new NetworkOptions { UsePeerToPeer = false, UseCompression = false, EnablePatternCache = false, Chunking = false, Fps = 0, AutoReconnect = false };
        if (binary) options.Serializer = new BinarySerializer();
        options.VerifiedAssemblies.Add(typeof(Blob).Assembly, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(Blob).Assembly.Location))));
        foreach (var type in new[] { typeof(Primitives), typeof(LargeEnum), typeof(int?), typeof(Blob), typeof(Nested), typeof(Node) }) options.Whitelist.Add(type);
        options.Authorization.AnonymousMessageTypes.Add(typeof(Blob));
        return options;
    }
    [TestMethod]
    public void CompiledWritersKeepV1BytesForPrimitiveBoundariesAndArrays() {
        var codec = new BinarySerializer(); var original = new BeforeBinarySerializer();
        object[] values = {
            new Primitives { Signed = long.MinValue, Unsigned = ulong.MaxValue, Short = short.MinValue, UShort = ushort.MaxValue,
                Small = sbyte.MinValue, Byte = byte.MaxValue, UInt = uint.MaxValue, Boolean = true, Float = float.NaN, Double = -0.0,
                Enum = LargeEnum.Maximum, Nullable = int.MinValue, Text = "😀é\0", Bytes = new byte[32768], Numbers = new[] { int.MinValue, 127, 128, int.MaxValue } },
            new Primitives(), new[] { true, false }, new[] { long.MinValue, long.MaxValue }, new[] { uint.MinValue, uint.MaxValue },
            new[] { ulong.MinValue, ulong.MaxValue }, new[] { float.NaN, float.MinValue }, new[] { double.NaN, double.MaxValue }, new string[] { null, "hello", "é" }
        };
        foreach (var value in values) {
            var wrapper = new Wrapper { Type = value.GetType().FullName, value = value };
            byte[] expected = original.Serialize(wrapper), actual = codec.Serialize(wrapper);
            int mismatch = Enumerable.Range(0, Math.Min(expected.Length, actual.Length)).FirstOrDefault(i => expected[i] != actual[i]);
            CollectionAssert.AreEqual(expected, actual, value.GetType().Name + ": lengths " + expected.Length + "/" + actual.Length + ", mismatch " + mismatch + ", prefix " + Convert.ToHexString(expected.AsSpan(Math.Max(0,mismatch-3), Math.Min(24, expected.Length-Math.Max(0,mismatch-3)))) + "/" + Convert.ToHexString(actual.AsSpan(Math.Max(0,mismatch-3), Math.Min(24, actual.Length-Math.Max(0,mismatch-3)))));
        }
        using var socket = new SocketJack.Net.UdpClient(Options());
        var result = (Primitives)codec.Deserialize(codec.Serialize(new Wrapper(values[0], socket))).Unwrap(socket);
        Assert.AreEqual(long.MinValue, result.Signed); Assert.AreEqual(ulong.MaxValue, result.Unsigned);
        Assert.AreEqual(LargeEnum.Maximum, result.Enum); Assert.AreEqual(int.MinValue, result.Nullable);
        Assert.IsTrue(float.IsNaN(result.Float)); CollectionAssert.AreEqual(((Primitives)values[0]).Numbers, result.Numbers);
        foreach (object array in values.Skip(2)) {
            var restored = codec.Deserialize(codec.Serialize(new Wrapper(array, socket))).Unwrap(socket);
            CollectionAssert.AreEqual((System.Collections.ICollection)array, (System.Collections.ICollection)restored);
        }
        var nullNumbers = new Wrapper { Type = typeof(int[]).FullName, value = new object[] { null, 7L } };
        CollectionAssert.AreEqual(new[] { 0, 7 }, (int[])codec.Deserialize(codec.Serialize(nullNumbers)).Unwrap(socket));
        var root = new Node(); var current = root; for (int i = 0; i < 70; i++) current = current.Next = new Node();
        Assert.ThrowsException<InvalidDataException>(() => codec.Serialize(root));
    }
    [TestMethod]
    public void CachedMetadataDoesNotCacheAllowlistBlacklistOrAssemblyApproval() {
        var options = Options(); using var socket = new SocketJack.Net.UdpClient(options);
        var codec = options.Serializer; var source = new Blob { Number = 17, Data = new byte[] { 1, 2, 3 } };
        byte[] bytes = codec.Serialize(new Wrapper(source, socket));
        Assert.AreEqual(17, ((Blob)codec.Deserialize(bytes).Unwrap(socket)).Number);
        ((List<string>)options.Whitelist).Remove(typeof(Blob).FullName);
        Assert.ThrowsException<TypeNotAllowedException>(() => codec.Deserialize(bytes).Unwrap(socket));
        options.Whitelist.Add(typeof(Blob)); options.Blacklist.Add(typeof(Blob));
        Assert.ThrowsException<TypeNotAllowedException>(() => codec.Deserialize(bytes).Unwrap(socket));
        options.Blacklist.Clear(); Assert.IsInstanceOfType<Blob>(codec.Deserialize(bytes).Unwrap(socket));
        var pins = (ConcurrentDictionary<Assembly, string>)typeof(VerifiedAssemblyRegistry).GetField("pins", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(options.VerifiedAssemblies);
        pins[typeof(Blob).Assembly] = new string('0', 64);
        Assert.ThrowsException<SecurityException>(() => codec.Deserialize(bytes).Unwrap(socket));
        var list = new TypeList(new[] { typeof(Blob), typeof(string), typeof(Span<int>) });
        Assert.AreEqual(typeof(Blob), list.Resolve(typeof(Blob).FullName));
        ((List<string>)list)[0] = typeof(int).AssemblyQualifiedName;
        Assert.IsNull(list.Resolve(typeof(Blob).FullName)); Assert.AreEqual(typeof(int), list.Resolve(typeof(int).FullName));
        list.Clear(); Assert.IsNull(list.Resolve(typeof(int).FullName));
    }
    [TestMethod]
    public void DllApprovalStillPrecedesAuthorizationCallbacksAndSetters() {
        var options = Options(); using var server = new SocketJack.Net.TcpServer(options, 0);
        using var connection = new NetworkConnection(server, null);
        byte[] bytes = options.Serializer.Serialize(new Wrapper(new Blob { Number = 4 }, server));
        int calls = 0; options.Authorization.AuthorizeMessage = _ => { calls++; return true; };
        using (InboundMessageSecurity.Enter(server, connection)) options.Serializer.Deserialize(bytes).Unwrap(server);
        Assert.IsTrue(calls > 0); calls = 0;
        var pins = (ConcurrentDictionary<Assembly, string>)typeof(VerifiedAssemblyRegistry).GetField("pins", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(options.VerifiedAssemblies);
        pins[typeof(Blob).Assembly] = new string('0', 64);
        using (InboundMessageSecurity.Enter(server, connection))
            Assert.ThrowsException<SecurityException>(() => options.Serializer.Deserialize(bytes).Unwrap(server));
        Assert.AreEqual(0, calls);
    }
    [TestMethod]
    public void NestedWrappersAndSharedCodecStayIndependent() {
        var shared = new BinarySerializer();
        Parallel.For(0, 100, i => {
            var options = Options(); using var socket = new SocketJack.Net.UdpClient(options);
            options.Blacklist.Remove(typeof(object).FullName); options.Whitelist.Add(typeof(object));
            options.Serializer = shared;
            var codec = options.Serializer;
            var value = new Nested { Value = new Blob { Number = i, Data = new byte[] { (byte)i } } };
            var restored = (Nested)codec.Deserialize(codec.Serialize(new Wrapper(value, socket))).Unwrap(socket);
            Assert.AreEqual(i, ((Blob)restored.Value).Number);
        });
    }
    [TestMethod]
    public void CachedNamesDoNotAssumeFieldOrderOrBorrowInputMemory() {
        using var socket = new SocketJack.Net.UdpClient(Options()); var codec = socket.Options.Serializer;
        codec.Serialize(new Wrapper(new Blob { Number = 1, Data = new byte[1] }, socket));
        byte[] encoded = codec.Serialize(new Wrapper { Type = typeof(Blob).FullName,
            value = new Reordered { Data = new byte[] { 3, 2, 1 }, Extra = "ignored", Number = 7 } });
        var inert = codec.Deserialize(encoded); Array.Fill(encoded, (byte)0);
        var restored = (Blob)inert.Unwrap(socket);
        Assert.AreEqual(7, restored.Number); CollectionAssert.AreEqual(new byte[] { 3, 2, 1 }, restored.Data);
    }
    [TestMethod]
    public void SegmentBuilderUsesLocalCountersAndLinearRebuild() {
        byte[] source = new byte[4 * 1024 * 1024]; new Random(312).NextBytes(source);
        var ids = new HashSet<string>();
        for (int iteration = 0; iteration < 50; iteration++) {
            var small = new byte[8001].GetSegments();
            Assert.AreEqual(1L, small[0].Index); Assert.AreEqual(3L, small[^1].Index);
            Assert.IsTrue(ids.Add(small[0].SID)); Assert.IsTrue(ulong.TryParse(small[0].SID, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _));
        }
        var chunks = source.GetSegments(); var first = chunks[0];
        Segment.Cache[first.SID] = chunks.Reverse().ToList();
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        byte[] rebuilt = Segment.Rebuild(first);
        Assert.IsTrue(GC.GetAllocatedBytesForCurrentThread() - allocated < source.Length * 2L);
        CollectionAssert.AreEqual(source, rebuilt); Assert.IsFalse(Segment.Cache.ContainsKey(first.SID));
        var bounded = source.GetSegments(32768); Assert.IsTrue(bounded.All(s => Convert.FromBase64String(s.Data).Length <= 32768));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => source.GetSegments(32769));
        var invalid = new[] { chunks[0], chunks[0] }; Segment.Cache[first.SID] = invalid.ToList();
        Assert.ThrowsException<InvalidDataException>(() => Segment.Rebuild(first)); Segment.Cache.TryRemove(first.SID, out _);
        var duplicate = new Segment("duplicate", new byte[] { 1 }, 1, 2);
        Segment.Cache[duplicate.SID] = new() { duplicate, duplicate };
        Assert.ThrowsException<InvalidDataException>(() => Segment.Rebuild(duplicate)); Segment.Cache.TryRemove(duplicate.SID, out _);
        var malformed = new Segment("malformed", new byte[] { 1 }, 1, 1) { Data = "!!!!" };
        Segment.Cache[malformed.SID] = new() { malformed };
        Assert.ThrowsException<FormatException>(() => Segment.Rebuild(malformed)); Segment.Cache.TryRemove(malformed.SID, out _);
        var whitespace = new Segment("whitespace", new byte[] { 1, 2 }, 1, 1); whitespace.Data = " \r\n" + whitespace.Data + "\t";
        Segment.Cache[whitespace.SID] = new() { whitespace }; CollectionAssert.AreEqual(new byte[] { 1, 2 }, Segment.Rebuild(whitespace));
    }
    [TestMethod]
    public void TerminatedSegmentsPreserveHeaderAndActualPayload() {
        byte[] source = new byte[8001]; new Random(78).NextBytes(source);
        byte[] framed = source.Terminate();
        Assert.AreEqual(source.Length, int.Parse(Encoding.ASCII.GetString(framed, 0, 15).TrimStart('\0')));
        CollectionAssert.AreEqual(source, framed[15..]);
        int offset = 0;
        foreach (var segment in source.GetTerminatedSegments()) {
            byte[] data = Convert.FromBase64String(segment.Data);
            int length = int.Parse(Encoding.ASCII.GetString(data, 0, 15).TrimStart('\0'));
            Assert.AreEqual(length + 15, data.Length); CollectionAssert.AreEqual(source[offset..(offset + length)], data[15..]); offset += length;
        }
        Assert.AreEqual(source.Length, offset);
    }
    [DataTestMethod][DataRow(false)][DataRow(true)]
    public async Task ActualTcpSegmentedSendUsesNormalWrapperAndReassembles(bool binary) {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        using var server = new SocketJack.Net.TcpServer(Options(binary), port); using var client = new SocketJack.Net.TcpClient(Options(binary));
        var got = new TaskCompletionSource<Blob>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.RegisterCallback<Blob>(e => got.TrySetResult(e.Object)); client.RegisterCallback<Blob>(_ => { });
        var errors = new ConcurrentQueue<string>(); server.OnError += e => errors.Enqueue(e.Exception.ToString()); client.OnError += e => errors.Enqueue(e.Exception.ToString());
        Assert.IsTrue(server.Listen()); Assert.IsTrue(await client.Connect("127.0.0.1", port));
        byte[] payload = new byte[100000]; new Random(731).NextBytes(payload);
        ((ISocket)client).SendSegmented(client.Connection, new Blob { Number = 91, Data = payload });
        Blob result;
        try { result = await got.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch { Assert.Fail(string.Join("\n", errors)); return; }
        Assert.AreEqual(91, result.Number); CollectionAssert.AreEqual(payload, result.Data);
    }
}
