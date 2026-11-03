using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Numerics;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Multiplayer.Serialization;
using SocketJack.Multiplayer.Transport;

namespace SocketJack.Multiplayer.Tests
{
    [TestClass]
    public class NetworkSerializationTests
    {
        private enum SignedCode : short { Minimum = short.MinValue }
        [Flags] private enum WideFlags : ulong { High = 1UL << 63, Low = 1 }
        private struct Score { public int Hits, Points; }
        private struct CustomNumber { public int Value; }
        private sealed class ReferenceValue { public int Value; }

        [TestMethod]
        public void BinaryFormatterRoundTripsOnlyExplicitTypedValues()
        {
            var formatter = new SocketJackBinaryFormatter();
            var values = new object[] { true, false, byte.MaxValue, sbyte.MinValue, short.MinValue, ushort.MaxValue,
                int.MinValue, uint.MaxValue, long.MinValue, ulong.MaxValue, -123.5f, double.MaxValue, 123456789.12345m,
                '\ud800', "Player \ud83d\ude80 / 日本語", Guid.Parse("12345678-1234-5678-9abc-123456789abc"), new byte[] { 0, 255, 8 },
                new DateTime(638000000000000000, DateTimeKind.Utc), new DateTimeOffset(2026, 11, 3, 12, 30, 0, TimeSpan.FromHours(-5)),
                TimeSpan.FromTicks(long.MinValue), new Vector2(1, -2), new Vector3(3, 4, 5), new Vector4(6, 7, 8, 9),
                new Quaternion(1, 2, 3, 4), SignedCode.Minimum, WideFlags.High | WideFlags.Low };
            foreach (object value in values)
            {
                Type type = value.GetType();
                Assert.IsTrue(formatter.CanSerialize(type), type.Name);
                byte[] bytes = formatter.Serialize(value, type, 1024);
                object copy = formatter.Deserialize(bytes, type, 1024);
                if (value is byte[] expected) CollectionAssert.AreEqual(expected, (byte[])copy);
                else Assert.AreEqual(value, copy, type.Name);
            }
            Assert.AreEqual("sj.binary.v1", formatter.ProtocolId);
            Assert.AreEqual(5, formatter.Serialize(true, typeof(bool)).Length);
            Assert.AreEqual(8, formatter.Serialize(7, typeof(int)).Length);
        }

        [TestMethod]
        public void NullAndNullableHandlingDoesNotPermitArbitraryTypes()
        {
            var formatter = new SocketJackBinaryFormatter();
            foreach (Type type in new[] { typeof(string), typeof(byte[]), typeof(int?), typeof(WideFlags?) })
            {
                Assert.IsTrue(formatter.CanSerialize(type));
                byte[] bytes = formatter.Serialize(null, type, 4);
                Assert.AreEqual(4, bytes.Length);
                Assert.IsNull(formatter.Deserialize(bytes, type, 4));
            }
            Assert.AreEqual(42, formatter.Deserialize(formatter.Serialize(42, typeof(int?)), typeof(int?)));
            Assert.ThrowsException<ArgumentException>(() => formatter.Serialize(null, typeof(int)));
            foreach (Type type in new[] { typeof(object), typeof(Type), typeof(Exception), typeof(List<int>), typeof(int[]), typeof(Uri), typeof(ReferenceValue) })
            {
                Assert.IsFalse(formatter.CanSerialize(type));
                Assert.ThrowsException<NotSupportedException>(() => formatter.Serialize(null, type));
                Assert.ThrowsException<NotSupportedException>(() => formatter.Deserialize(new byte[] { 1, 0, 0, 0 }, type));
            }
            Assert.IsFalse(formatter.CanSerialize(null));
            Assert.ThrowsException<ArgumentException>(() => formatter.Serialize("42", typeof(int)));
        }

        [TestMethod]
        public void ExactSerializedByteBoundsIncludeHeaderAndUtf8Prefix()
        {
            var formatter = new SocketJackBinaryFormatter();
            string text = "\ud83d\ude80ab"; // Six UTF-8 bytes, plus the four-byte header and four-byte length.
            byte[] bytes = formatter.Serialize(text, typeof(string), 14);
            Assert.AreEqual(14, bytes.Length);
            Assert.AreEqual(text, formatter.Deserialize(bytes, typeof(string), 14));
            Assert.ThrowsException<InvalidDataException>(() => formatter.Serialize(text, typeof(string), 13));
            Assert.ThrowsException<InvalidDataException>(() => formatter.Deserialize(bytes, typeof(string), 13));
            byte[] data = { 1, 2, 3 };
            Assert.AreEqual(11, formatter.Serialize(data, typeof(byte[]), 11).Length);
            Assert.ThrowsException<InvalidDataException>(() => formatter.Serialize(data, typeof(byte[]), 10));
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => formatter.Serialize(1, typeof(int), 3));
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => formatter.Serialize(1, typeof(int), SocketJackBinaryFormatter.MaximumValueBytes + 1));
            Assert.ThrowsException<InvalidDataException>(() => formatter.Serialize(new string('x', 100000), typeof(string), 16));
        }

        [TestMethod]
        public void DecoderRejectsMalformedLengthsTagsUtf8NullsAndTrailingBytes()
        {
            var formatter = new SocketJackBinaryFormatter();
            byte[] text = formatter.Serialize("x", typeof(string));
            byte[] huge = (byte[])text.Clone(); BinaryPrimitives.WriteInt32LittleEndian(huge.AsSpan(4), int.MaxValue);
            Assert.ThrowsException<InvalidDataException>(() => formatter.Deserialize(huge, typeof(string)));
            byte[] negative = (byte[])text.Clone(); BinaryPrimitives.WriteInt32LittleEndian(negative.AsSpan(4), -1);
            Assert.ThrowsException<InvalidDataException>(() => formatter.Deserialize(negative, typeof(string)));
            byte[] utf8 = (byte[])text.Clone(); utf8[8] = 255;
            Assert.ThrowsException<InvalidDataException>(() => formatter.Deserialize(utf8, typeof(string)));
            byte[] marker = (byte[])text.Clone(); marker[3] = 2;
            Assert.ThrowsException<InvalidDataException>(() => formatter.Deserialize(marker, typeof(string)));
            byte[] version = (byte[])text.Clone(); version[0] = 99;
            Assert.ThrowsException<InvalidDataException>(() => formatter.Deserialize(version, typeof(string)));
            Assert.ThrowsException<InvalidDataException>(() => formatter.Deserialize(text.Concat(new byte[] { 0 }).ToArray(), typeof(string)));
            Assert.ThrowsException<InvalidDataException>(() => formatter.Deserialize(formatter.Serialize(1f, typeof(float)), typeof(int)));
            byte[] boolean = formatter.Serialize(true, typeof(bool)); boolean[4] = 255;
            Assert.ThrowsException<InvalidDataException>(() => formatter.Deserialize(boolean, typeof(bool)));
            byte[] truncated = formatter.Serialize(Guid.Empty, typeof(Guid)).Take(19).ToArray();
            Assert.ThrowsException<InvalidDataException>(() => formatter.Deserialize(truncated, typeof(Guid)));
            Assert.ThrowsException<System.Text.EncoderFallbackException>(() => formatter.Serialize("\ud800", typeof(string)));
        }

        [TestMethod]
        public void ExplicitCustomCodecsHaveStableOrderedConfigurationAndFreezeBeforeUse()
        {
            var first = new SocketJackBinaryFormatter(); var second = new SocketJackBinaryFormatter();
            RegisterScore(first, "score.i32x2.v1"); RegisterNumber(first);
            RegisterNumber(second); RegisterScore(second, "score.i32x2.v1");
            Assert.AreEqual(first.ProtocolId, second.ProtocolId);
            byte[] bytes = first.Serialize(new Score { Hits = 7, Points = 19 }, typeof(Score), 12);
            var copy = (Score)second.Deserialize(bytes, typeof(Score), 12);
            Assert.AreEqual(7, copy.Hits); Assert.AreEqual(19, copy.Points);
            Assert.ThrowsException<InvalidOperationException>(() => first.Register<ReferenceValue>(1030, "reference.v1", (w, v) => w.Write(v.Value), r => new ReferenceValue { Value = r.ReadInt32() }));
            var changed = new SocketJackBinaryFormatter(); RegisterScore(changed, "score.i32x2.v2"); RegisterNumber(changed);
            Assert.AreNotEqual(first.ProtocolId, changed.ProtocolId);
            var duplicate = new SocketJackBinaryFormatter(); RegisterScore(duplicate, "score.i32x2.v1");
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => duplicate.Register<CustomNumber>(1024, "number.v1", (w, v) => w.Write(v.Value), r => new CustomNumber { Value = r.ReadInt32() }));
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => new SocketJackBinaryFormatter().Register<Score>(1, "score.v1", (w, v) => w.Write(v.Hits), r => new Score()));
        }

        [TestMethod]
        public void CustomCodecCannotOverrunTheValueWriterOrReturnInvalidNull()
        {
            var formatter = new SocketJackBinaryFormatter();
            formatter.Register<ReferenceValue>(1024, "reference.i32.v1", (w, v) => w.Write(new byte[100]), r => null);
            Assert.ThrowsException<InvalidDataException>(() => formatter.Serialize(new ReferenceValue { Value = 1 }, typeof(ReferenceValue), 12));
            byte[] invalidResult = { 1, 0, 4, 1 }; // Explicit registered type1024, non-null marker.
            Assert.ThrowsException<InvalidDataException>(() => formatter.Deserialize(invalidResult, typeof(ReferenceValue), 12));
        }

        [TestMethod]
        public void CodecBytesAndIdentityDoNotDependOnCurrentCulture()
        {
            var original = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
                var first = new SocketJackBinaryFormatter(); RegisterScore(first, "score.i32x2.v1");
                byte[] english = first.Serialize(1234567.8901m, typeof(decimal)); string id = first.ProtocolId;
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
                var second = new SocketJackBinaryFormatter(); RegisterScore(second, "score.i32x2.v1");
                CollectionAssert.AreEqual(english, second.Serialize(1234567.8901m, typeof(decimal)));
                Assert.AreEqual(id, second.ProtocolId);
            }
            finally { CultureInfo.CurrentCulture = original; }
        }

        [TestMethod]
        public void RegisteredGenericTypeIdentityOmitsAssemblyVersions()
        {
            Type First(int version)
            {
                var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("GameSchema") { Version = new Version(version, 0, 0, 0) }, AssemblyBuilderAccess.Run);
                var type = assembly.DefineDynamicModule("GameSchema").DefineType("Game.Counter", TypeAttributes.Public).CreateType();
                return typeof(List<>).MakeGenericType(type);
            }
            var firstType = First(1); var secondType = First(2);
            Assert.AreNotEqual(firstType.FullName, secondType.FullName, "The test must exercise assembly-qualified generic FullName values.");
            var first = new SocketJackBinaryFormatter(); var second = new SocketJackBinaryFormatter();
            RegisterRuntimeType(first, firstType); RegisterRuntimeType(second, secondType);
            Assert.AreEqual(first.ProtocolId, second.ProtocolId, "Wire schema identity must not include local assembly versions.");
        }

        [TestMethod]
        public void CustomSerializerWorksAcrossTheExistingTcpMultiplayerPath()
        {
            INetworkSerializer serverSerializer = new XorIntSerializer();
            INetworkSerializer clientSerializer = new XorIntSerializer();
            using var server = new NetworkServer(GameTransport.Create(TransportType.Tcp));
            using var client = new NetworkClient(GameTransport.Create(TransportType.Tcp));
            Assert.AreEqual(TransportType.Tcp, server.Transport.Type);
            server.Start(new IPEndPoint(IPAddress.Loopback, 0)); client.Connect(server.Transport.LocalEndPoint);
            Pump(() => client.IsConnected, server, client);
            var obj = server.Spawn(1, client.LocalPeerId, state: serverSerializer.Serialize(42, typeof(int), 32));
            Pump(() => client.Objects.ContainsKey(obj.Id), server, client);
            Assert.AreEqual(42, clientSerializer.Deserialize(client.Objects[obj.Id].State, typeof(int), 32));
            server.InputReceived += input => server.SetState(input.ObjectId, serverSerializer.Serialize(serverSerializer.Deserialize(input.Payload, typeof(int), 32), typeof(int), 32));
            Assert.IsTrue(client.SendInput(obj.Id, clientSerializer.Serialize(91, typeof(int), 32), DeliveryMode.ReliableUnordered));
            Pump(() => (int)clientSerializer.Deserialize(client.Objects[obj.Id].State, typeof(int), 32) == 91, server, client);
            Assert.ThrowsException<InvalidDataException>(() => new SocketJackBinaryFormatter().Deserialize(client.Objects[obj.Id].State, typeof(int), 32));
        }

        private static void RegisterScore(SocketJackBinaryFormatter formatter, string schema) => formatter.Register<Score>(1024, schema,
            (w, v) => { w.Write(v.Hits); w.Write(v.Points); }, r => new Score { Hits = r.ReadInt32(), Points = r.ReadInt32() });
        private static void RegisterNumber(SocketJackBinaryFormatter formatter) => formatter.Register<CustomNumber>(1025, "number.i32.v1", (w, v) => w.Write(v.Value), r => new CustomNumber { Value = r.ReadInt32() });
        private static void WriteZero<T>(BinaryWriter writer, T value) => writer.Write(0);
        private static T ReadZero<T>(BinaryReader reader) { reader.ReadInt32(); return default; }
        private static void RegisterRuntimeType(SocketJackBinaryFormatter formatter, Type type)
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
            var write = typeof(NetworkSerializationTests).GetMethod(nameof(WriteZero), flags).MakeGenericMethod(type)
                .CreateDelegate(typeof(Action<,>).MakeGenericType(typeof(BinaryWriter), type));
            var read = typeof(NetworkSerializationTests).GetMethod(nameof(ReadZero), flags).MakeGenericMethod(type)
                .CreateDelegate(typeof(Func<,>).MakeGenericType(typeof(BinaryReader), type));
            typeof(SocketJackBinaryFormatter).GetMethod(nameof(SocketJackBinaryFormatter.Register)).MakeGenericMethod(type)
                .Invoke(formatter, new object[] { (ushort)1024, "game.counter.i32.v1", write, read });
        }
        private static void Pump(Func<bool> done, NetworkServer server, NetworkClient client)
        {
            var watch = Stopwatch.StartNew();
            while (!done() && watch.Elapsed < TimeSpan.FromSeconds(8)) { server.Update(.02); client.Update(.02); Thread.Sleep(1); }
            Assert.IsTrue(done(), "TCP multiplayer formatter condition timed out.");
        }
        private sealed class XorIntSerializer : INetworkSerializer
        {
            public string ProtocolId => "test.xor-int32.v1";
            public bool CanSerialize(Type declaredType) => declaredType == typeof(int);
            public byte[] Serialize(object value, Type declaredType, int maxBytes = 8192)
            {
                if (!CanSerialize(declaredType)) throw new NotSupportedException();
                if (maxBytes < 5) throw new InvalidDataException();
                byte[] bytes = new byte[5]; bytes[0] = 85;
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(1), (int)value ^ 0x12345678); return bytes;
            }
            public object Deserialize(byte[] bytes, Type declaredType, int maxBytes = 8192)
            {
                if (!CanSerialize(declaredType)) throw new NotSupportedException();
                if (bytes == null || bytes.Length != 5 || bytes.Length > maxBytes || bytes[0] != 85) throw new InvalidDataException();
                return BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(1)) ^ 0x12345678;
            }
        }

    }
}
