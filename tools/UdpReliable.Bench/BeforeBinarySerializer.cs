using SocketJack.Net;
using SocketJack.Net.P2P;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SocketJack.Serialization {
    /// <summary>
    /// SocketJack binary format v1. Byte arrays are raw bytes, integers use variable-length encoding,
    /// and decoding creates inert values until Wrapper applies the socket's type whitelist.
    /// Select the same serializer at both endpoints. Usable with TCP and UDP.
    /// </summary>
    public sealed class BeforeBinarySerializer : ISerializer {
        readonly Json.JsonSerializer fallback = new Json.JsonSerializer();
        const int MaxDepth = 64;
        enum Tag : byte { Null, False, True, Signed, Unsigned, Float, Double, Decimal, String, Bytes, Array, Object, Wrapper, Json }
        sealed class JsonValue { internal JsonElement Value; }
        sealed class JsonValueConverter : JsonConverter<JsonValue> {
            public override JsonValue Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) {
                using (var document = JsonDocument.ParseValue(ref reader)) return new JsonValue { Value = document.RootElement.Clone() };
            }
            public override void Write(Utf8JsonWriter writer, JsonValue value, JsonSerializerOptions options) => value.Value.WriteTo(writer);
        }
        public BeforeBinarySerializer() { fallback.JsonOptions.Converters.Add(new JsonValueConverter()); }

        public byte[] Serialize(object value) {
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream, Encoding.UTF8, true)) {
                writer.Write((byte)'S'); writer.Write((byte)'B'); writer.Write((byte)1);
                Write(writer, value, 0);
                return stream.ToArray();
            }
        }
        public Wrapper Deserialize(byte[] data) {
            var value = Decode(data);
            return value as Wrapper ?? throw new InvalidDataException("Expected a SocketJack binary wrapper.");
        }
        object Decode(byte[] data) {
            if (data == null) throw new ArgumentNullException(nameof(data));
            using (var stream = new MemoryStream(data, false)) using (var reader = new BinaryReader(stream, Encoding.UTF8, true)) {
                if (reader.ReadByte() != 'S' || reader.ReadByte() != 'B' || reader.ReadByte() != 1)
                    throw new InvalidDataException("Unknown SocketJack binary format/version.");
                object value = Read(reader, 0);
                if (stream.Position != stream.Length) throw new InvalidDataException("Trailing binary data.");
                return value;
            }
        }
        public PeerRedirect DeserializeRedirect(ISocket target, byte[] data) => throw new NotSupportedException("Benchmark reference codec only.");
        public object GetPropertyValue(PropertyValueArgs args) {
            if (args.Value is IDictionary<string, object> properties && properties.TryGetValue(args.Name, out var value))
                return GetValue(value, args.Reference.Info.PropertyType, true);
            return null;
        }
        public object GetValue(object value, Type type, bool parse) {
            if (value == null || value is Wrapper) return value;
            type = Nullable.GetUnderlyingType(type) ?? type;
            if (value is JsonValue json) return fallback.GetValue(json.Value, type, true);
            if (type.IsInstanceOfType(value)) return value;
            if (type.IsEnum) return Enum.ToObject(type, value);
            if (value is object[] items && type.IsArray) {
                Type element = type.GetElementType(); var result = System.Array.CreateInstance(element, items.Length);
                for (int i = 0; i < items.Length; i++) result.SetValue(GetValue(items[i], element, true), i);
                return result;
            }
            if (value is IConvertible && typeof(IConvertible).IsAssignableFrom(type))
                return Convert.ChangeType(value, type, CultureInfo.InvariantCulture);
            // Uncommon collection/value types retain the existing JSON converter semantics.
            // The expected type comes from an already whitelisted property, never from binary type activation.
            using (var document = JsonDocument.Parse(fallback.Serialize(value)))
                return fallback.GetValue(document.RootElement, type, true);
        }
        void Write(BinaryWriter writer, object value, int depth) {
            if (depth > MaxDepth) throw new InvalidDataException("Binary nesting limit exceeded.");
            if (value == null) { writer.Write((byte)Tag.Null); return; }
            if (value is Wrapper wrapper) {
                writer.Write((byte)Tag.Wrapper); Text(writer, wrapper.Type ?? ""); Write(writer, wrapper.value, depth + 1); return;
            }
            if (value is byte[] bytes) { writer.Write((byte)Tag.Bytes); Blob(writer, bytes); return; }
            if (value is string text) { writer.Write((byte)Tag.String); Text(writer, text); return; }
            Type type = value.GetType();
            if (type.IsEnum) { value = Convert.ChangeType(value, Enum.GetUnderlyingType(type), CultureInfo.InvariantCulture); type = value.GetType(); }
            switch (Type.GetTypeCode(type)) {
                case TypeCode.Boolean: writer.Write((byte)((bool)value ? Tag.True : Tag.False)); return;
                case TypeCode.SByte: case TypeCode.Int16: case TypeCode.Int32: case TypeCode.Int64:
                    writer.Write((byte)Tag.Signed); long signed = Convert.ToInt64(value); Number(writer, unchecked((ulong)((signed << 1) ^ (signed >> 63)))); return;
                case TypeCode.Byte: case TypeCode.UInt16: case TypeCode.UInt32: case TypeCode.UInt64:
                    writer.Write((byte)Tag.Unsigned); Number(writer, Convert.ToUInt64(value)); return;
                case TypeCode.Single: writer.Write((byte)Tag.Float); writer.Write((float)value); return;
                case TypeCode.Double: writer.Write((byte)Tag.Double); writer.Write((double)value); return;
                case TypeCode.Decimal: writer.Write((byte)Tag.Decimal); writer.Write((decimal)value); return;
            }
            if (value is System.Array array && array.Rank == 1) {
                writer.Write((byte)Tag.Array); Number(writer, (ulong)array.Length);
                foreach (var item in array) Write(writer, item, depth + 1);
                return;
            }
            // Dictionary keys, date/time values and specialized converters stay self-contained.
            if (value is IEnumerable || type.IsValueType || value is Type) {
                writer.Write((byte)Tag.Json); Blob(writer, fallback.Serialize(value)); return;
            }
            writer.Write((byte)Tag.Object);
            var properties = Wrapper.GetPropertyReferences(type);
            int count = 0;
            foreach (var property in properties) if (property.Info.CanRead && property.Info.GetIndexParameters().Length == 0) count++;
            Number(writer, (ulong)count);
            foreach (var property in properties) {
                if (!property.Info.CanRead || property.Info.GetIndexParameters().Length != 0) continue;
                Text(writer, property.Info.Name); Write(writer, property.GetValue(value), depth + 1);
            }
        }
        object Read(BinaryReader reader, int depth) {
            if (depth > MaxDepth) throw new InvalidDataException("Binary nesting limit exceeded.");
            switch ((Tag)reader.ReadByte()) {
                case Tag.Null: return null;
                case Tag.False: return false;
                case Tag.True: return true;
                case Tag.Signed: ulong n = Number(reader); return unchecked((long)(n >> 1) ^ -((long)n & 1));
                case Tag.Unsigned: return Number(reader);
                case Tag.Float: return reader.ReadSingle();
                case Tag.Double: return reader.ReadDouble();
                case Tag.Decimal: return reader.ReadDecimal();
                case Tag.String: return Encoding.UTF8.GetString(Blob(reader));
                case Tag.Bytes: return Blob(reader);
                case Tag.Wrapper: return new Wrapper { Type = Encoding.UTF8.GetString(Blob(reader)), value = Read(reader, depth + 1) };
                case Tag.Array:
                    int length = Length(reader); var items = new object[length];
                    for (int i = 0; i < length; i++) items[i] = Read(reader, depth + 1);
                    return items;
                case Tag.Object:
                    int count = Length(reader); var properties = new Dictionary<string, object>(StringComparer.Ordinal);
                    for (int i = 0; i < count; i++) properties.Add(Encoding.UTF8.GetString(Blob(reader)), Read(reader, depth + 1));
                    return properties;
                case Tag.Json:
                    using (var document = JsonDocument.Parse(Blob(reader))) return new JsonValue { Value = document.RootElement.Clone() };
                default: throw new InvalidDataException("Unknown binary value tag.");
            }
        }
        static void Text(BinaryWriter writer, string value) => Blob(writer, Encoding.UTF8.GetBytes(value));
        static void Blob(BinaryWriter writer, byte[] bytes) { Number(writer, (ulong)bytes.Length); writer.Write(bytes); }
        static byte[] Blob(BinaryReader reader) => reader.ReadBytes(Length(reader));
        static int Length(BinaryReader reader) {
            ulong value = Number(reader);
            if (value > int.MaxValue || value > (ulong)(reader.BaseStream.Length - reader.BaseStream.Position))
                throw new InvalidDataException("Invalid binary length.");
            return (int)value;
        }
        static void Number(BinaryWriter writer, ulong value) {
            while (value >= 128) { writer.Write((byte)(value | 128)); value >>= 7; }
            writer.Write((byte)value);
        }
        static ulong Number(BinaryReader reader) {
            ulong value = 0;
            for (int shift = 0; shift < 64; shift += 7) {
                byte part = reader.ReadByte();
                if (shift == 63 && part > 1) break;
                value |= (ulong)(part & 127) << shift;
                if (part < 128) {
                    if (shift != 0 && part == 0) break;
                    return value;
                }
            }
            throw new InvalidDataException("Invalid binary integer.");
        }
    }
}
