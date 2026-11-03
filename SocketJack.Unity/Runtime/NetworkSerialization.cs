using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace SocketJack.Multiplayer.Serialization
{
    public enum NetworkSerializerKind { BinaryFormatterSocketJack = 0, CustomProvider = 1 }

    /// <summary>Serializes one locally declared, explicitly supported game-state value. ProtocolId
    /// must identify the format version and every setting that changes its wire representation.
    /// Peers must validate that identity, schema, authority and byte limits before deserializing.
    /// This contract does not replace the fixed session, transport or physics protocols.</summary>
    public interface INetworkSerializer
    {
        string ProtocolId { get; }
        bool CanSerialize(Type declaredType);
        byte[] Serialize(object value, Type declaredType, int maxBytes = 8192);
        object Deserialize(byte[] bytes, Type declaredType, int maxBytes = 8192);
    }

    /// <summary>Implement on a Unity MonoBehaviour to supply a custom Inspector-selected formatter,
    /// or supply INetworkSerializer directly through the stopped manager's runtime configuration.</summary>
    public interface INetworkSerializerProvider
    {
        INetworkSerializer CreateSerializer();
    }

    /// <summary>The "Binary formatter (SocketJack)" option: a bounded, versioned value codec with
    /// an explicit type whitelist. Unrelated to System.Runtime.Serialization.Formatters.Binary.BinaryFormatter.
    /// It never transmits CLR type names, traverses arbitrary objects or activates types from peer data.
    /// Register fixed custom codecs before ProtocolId is read; the first read freezes configuration.
    /// Custom codec delegates must validate lengths before allocating. Use an instance on one game thread.</summary>
    public sealed class SocketJackBinaryFormatter : INetworkSerializer
    {
        public const int MaximumValueBytes = 1024 * 1024;
        public const int HeaderBytes = 4;
        private const byte Version = 1;
        private const ushort EnumTypeId = 31;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private sealed class Codec
        {
            internal ushort Id;
            internal Type Type;
            internal string Schema;
            internal Action<BinaryWriter, object> Write;
            internal Func<BinaryReader, object> Read;
        }
        private readonly Dictionary<Type, Codec> _codecs = new Dictionary<Type, Codec>();
        private readonly Dictionary<ushort, Codec> _custom = new Dictionary<ushort, Codec>();
        private readonly Dictionary<Type, Codec> _enums = new Dictionary<Type, Codec>();
        private string _protocolId;

        public SocketJackBinaryFormatter()
        {
            Builtin<bool>(1, (w, v) => w.Write((byte)(v ? 1 : 0)), r => { byte value = r.ReadByte(); if (value > 1) throw new InvalidDataException("Invalid boolean."); return value == 1; });
            Builtin<byte>(2, (w, v) => w.Write(v), r => r.ReadByte());
            Builtin<sbyte>(3, (w, v) => w.Write(v), r => r.ReadSByte());
            Builtin<short>(4, (w, v) => w.Write(v), r => r.ReadInt16());
            Builtin<ushort>(5, (w, v) => w.Write(v), r => r.ReadUInt16());
            Builtin<int>(6, (w, v) => w.Write(v), r => r.ReadInt32());
            Builtin<uint>(7, (w, v) => w.Write(v), r => r.ReadUInt32());
            Builtin<long>(8, (w, v) => w.Write(v), r => r.ReadInt64());
            Builtin<ulong>(9, (w, v) => w.Write(v), r => r.ReadUInt64());
            Builtin<float>(10, (w, v) => w.Write(v), r => r.ReadSingle());
            Builtin<double>(11, (w, v) => w.Write(v), r => r.ReadDouble());
            Builtin<decimal>(12, (w, v) => w.Write(v), r => r.ReadDecimal());
            Builtin<char>(13, (w, v) => w.Write((ushort)v), r => (char)r.ReadUInt16());
            Builtin<string>(14, WriteString, r => Utf8.GetString(ReadBytes(r)));
            Builtin<Guid>(15, (w, v) => w.Write(v.ToByteArray()), r => new Guid(ExactBytes(r, 16)));
            Builtin<byte[]>(16, (w, v) => { w.Write(v.Length); w.Write(v); }, ReadBytes);
            Builtin<DateTime>(17, (w, v) => { w.Write(v.Ticks); w.Write((byte)v.Kind); }, r =>
            {
                long ticks = r.ReadInt64(); byte kind = r.ReadByte();
                if (kind > (byte)DateTimeKind.Local) throw new InvalidDataException("Invalid DateTime kind.");
                return new DateTime(ticks, (DateTimeKind)kind);
            });
            Builtin<DateTimeOffset>(18, (w, v) => { w.Write(v.Ticks); w.Write((short)v.Offset.TotalMinutes); }, r => new DateTimeOffset(r.ReadInt64(), TimeSpan.FromMinutes(r.ReadInt16())));
            Builtin<TimeSpan>(19, (w, v) => w.Write(v.Ticks), r => TimeSpan.FromTicks(r.ReadInt64()));
            Builtin<Vector2>(20, (w, v) => { w.Write(v.X); w.Write(v.Y); }, r => new Vector2(r.ReadSingle(), r.ReadSingle()));
            Builtin<Vector3>(21, (w, v) => { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }, r => new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()));
            Builtin<Vector4>(22, (w, v) => { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); w.Write(v.W); }, r => new Vector4(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()));
            Builtin<Quaternion>(23, (w, v) => { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); w.Write(v.W); }, r => new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()));
        }

        public string ProtocolId
        {
            get
            {
                if (_protocolId != null) return _protocolId;
                if (_custom.Count == 0) return _protocolId = "sj.binary.v1";
                using (var stream = new MemoryStream())
                using (var writer = new BinaryWriter(stream, Utf8, true))
                {
                    foreach (var codec in _custom.Values.OrderBy(c => c.Id))
                    { writer.Write(codec.Id); writer.Write(codec.Schema); writer.Write(StableTypeName(codec.Type)); }
                    writer.Flush();
                    using (var sha = SHA256.Create())
                    {
                        byte[] hash = sha.ComputeHash(stream.ToArray());
                        return _protocolId = "sj.binary.v1+" + BitConverter.ToString(hash, 0, 16).Replace("-", "").ToLowerInvariant();
                    }
                }
            }
        }

        /// <summary>Adds a locally selected type codec. Stable IDs, schema IDs and declared types
        /// are included in ProtocolId; schemaId must change when the custom representation changes.
        /// Delegates must use bounded fixed fields or validate lengths against remaining reader bytes.</summary>
        public void Register<T>(ushort typeId, string schemaId, Action<BinaryWriter, T> write, Func<BinaryReader, T> read)
        {
            if (_protocolId != null) throw new InvalidOperationException("Formatter configuration is frozen after ProtocolId or serialization is used.");
            if (typeId < 256 || _custom.ContainsKey(typeId)) throw new ArgumentOutOfRangeException(nameof(typeId), "Custom IDs must be unique and at least 256.");
            if (string.IsNullOrWhiteSpace(schemaId) || schemaId.Length > 128 || schemaId.Any(c => c < 33 || c > 126))
                throw new ArgumentException("A stable printable ASCII schema ID of at most 128 characters is required.", nameof(schemaId));
            if (write == null) throw new ArgumentNullException(nameof(write));
            if (read == null) throw new ArgumentNullException(nameof(read));
            Type type = typeof(T);
            if (type.IsEnum || type.ContainsGenericParameters || Nullable.GetUnderlyingType(type) != null || _codecs.ContainsKey(type) ||
                type.FullName == null || type.FullName.Length > 1024 || _custom.Count >= 128)
                throw new ArgumentException("This type cannot be registered or the custom codec limit was reached.", nameof(T));
            var codec = Make(typeId, schemaId, write, read);
            _codecs.Add(type, codec); _custom.Add(typeId, codec);
        }

        public bool CanSerialize(Type declaredType) => declaredType != null && FindCodec(declaredType) != null;

        public byte[] Serialize(object value, Type declaredType, int maxBytes = 8192)
        {
            ValidateLimit(maxBytes);
            var codec = RequireCodec(declaredType);
            if (value == null && declaredType.IsValueType && Nullable.GetUnderlyingType(declaredType) == null)
                throw new ArgumentException("A non-nullable value type cannot contain null.", nameof(value));
            if (value != null && !codec.Type.IsInstanceOfType(value)) throw new ArgumentException("Value does not match its declared type.", nameof(value));
            _ = ProtocolId;
            using (var stream = new BoundedStream(maxBytes))
            using (var writer = new BinaryWriter(stream, Utf8, true))
            {
                writer.Write(Version); writer.Write(codec.Id); writer.Write((byte)(value == null ? 0 : 1));
                if (value != null) codec.Write(writer, value);
                writer.Flush();
                return stream.ToArray();
            }
        }

        public object Deserialize(byte[] bytes, Type declaredType, int maxBytes = 8192)
        {
            ValidateLimit(maxBytes);
            if (bytes == null || bytes.Length < HeaderBytes || bytes.Length > maxBytes) throw new InvalidDataException("Network value length exceeds its configured bound.");
            var codec = RequireCodec(declaredType);
            _ = ProtocolId;
            try
            {
                using (var stream = new MemoryStream(bytes, false))
                using (var reader = new BinaryReader(stream, Utf8, false))
                {
                    if (reader.ReadByte() != Version || reader.ReadUInt16() != codec.Id) throw new InvalidDataException("Network value version or type does not match its declared schema.");
                    byte present = reader.ReadByte();
                    if (present > 1 || (present == 0 && declaredType.IsValueType && Nullable.GetUnderlyingType(declaredType) == null))
                        throw new InvalidDataException("Invalid network value null marker.");
                    object value = present == 0 ? null : codec.Read(reader);
                    if ((value == null && present != 0) || (value != null && !codec.Type.IsInstanceOfType(value)) || stream.Position != stream.Length)
                        throw new InvalidDataException("Network value result or trailing data does not match its schema.");
                    return value;
                }
            }
            catch (EndOfStreamException error) { throw new InvalidDataException("Truncated network value.", error); }
            catch (ArgumentException error) { throw new InvalidDataException("Invalid network value encoding.", error); }
            catch (OverflowException error) { throw new InvalidDataException("Invalid network value range.", error); }
        }

        private Codec FindCodec(Type type)
        {
            type = Nullable.GetUnderlyingType(type) ?? type;
            if (_codecs.TryGetValue(type, out var codec)) return codec;
            if (!type.IsEnum) return null;
            if (_enums.TryGetValue(type, out codec)) return codec;
            Type underlying = Enum.GetUnderlyingType(type);
            Codec number = _codecs[underlying];
            codec = new Codec { Id = EnumTypeId, Type = type, Write = (w, v) => number.Write(w, Convert.ChangeType(v, underlying, CultureInfo.InvariantCulture)),
                Read = r => Enum.ToObject(type, number.Read(r)) };
            _enums.Add(type, codec);
            return codec;
        }
        private Codec RequireCodec(Type type)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            return FindCodec(type) ?? throw new NotSupportedException("The declared member type has no explicitly configured network codec.");
        }
        private void Builtin<T>(ushort id, Action<BinaryWriter, T> write, Func<BinaryReader, T> read) => _codecs.Add(typeof(T), Make(id, "builtin", write, read));
        private static Codec Make<T>(ushort id, string schema, Action<BinaryWriter, T> write, Func<BinaryReader, T> read) => new Codec {
            Id = id, Type = typeof(T), Schema = schema, Write = (w, value) => write(w, (T)value), Read = r => read(r) };
        private static void ValidateLimit(int maxBytes)
        { if (maxBytes < HeaderBytes || maxBytes > MaximumValueBytes) throw new ArgumentOutOfRangeException(nameof(maxBytes)); }
        private static void WriteString(BinaryWriter writer, string value)
        {
            int count = Utf8.GetByteCount(value);
            if (count > ((BoundedStream)writer.BaseStream).Remaining - sizeof(int)) throw new InvalidDataException("String exceeds the configured value bound.");
            writer.Write(count); writer.Write(Utf8.GetBytes(value));
        }
        internal static string StableTypeName(Type type)
        {
            if (type.IsArray) return StableTypeName(type.GetElementType()) + "[" + new string(',', type.GetArrayRank() - 1) + "]";
            if (!type.IsGenericType) return type.FullName;
            return type.GetGenericTypeDefinition().FullName + "<" + string.Join(",", type.GetGenericArguments().Select(StableTypeName)) + ">";
        }
        private static byte[] ReadBytes(BinaryReader reader)
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > reader.BaseStream.Length - reader.BaseStream.Position) throw new InvalidDataException("Invalid network byte/string length.");
            return ExactBytes(reader, count);
        }
        private static byte[] ExactBytes(BinaryReader reader, int count)
        { var bytes = reader.ReadBytes(count); if (bytes.Length != count) throw new EndOfStreamException(); return bytes; }

        private sealed class BoundedStream : MemoryStream
        {
            private readonly int _limit;
            internal BoundedStream(int limit) : base(Math.Min(limit, 256)) { _limit = limit; }
            internal long Remaining => _limit - Position;
            private void Require(long count) { if (count < 0 || Position > _limit - count) throw new InvalidDataException("Serialized network value exceeds its configured bound."); }
            public override void Write(byte[] buffer, int offset, int count) { Require(count); base.Write(buffer, offset, count); }
            public override void Write(ReadOnlySpan<byte> buffer) { Require(buffer.Length); base.Write(buffer); }
            public override void WriteByte(byte value) { Require(1); base.WriteByte(value); }
            public override void SetLength(long value)
            { if (value < 0 || value > _limit) throw new InvalidDataException("Serialized value exceeds its configured bound."); base.SetLength(value); }
        }
    }
}
