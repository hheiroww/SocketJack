using SocketJack.Net;
using SocketJack.Net.P2P;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SocketJack.Serialization {
    /// <summary>
    /// SocketJack binary format v1. Byte arrays are raw bytes, integers use variable-length encoding,
    /// and decoding creates inert values until Wrapper applies the socket's type whitelist.
    /// Select the same serializer at both endpoints. Usable with TCP and UDP.
    /// </summary>
    public sealed class BinarySerializer : ISerializer {
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
        public BinarySerializer() { fallback.JsonOptions.Converters.Add(new JsonValueConverter()); }

        public byte[] Serialize(object value) {
            Span<byte> scratch = stackalloc byte[512];
            var writer = new Encoder(scratch);
            try {
                writer.Byte((byte)'S'); writer.Byte((byte)'B'); writer.Byte(1);
                Write(ref writer, value, 0);
                return writer.Finish();
            } finally { writer.Dispose(); }
        }
        public Wrapper Deserialize(byte[] data) {
            var value = Decode(data);
            return value as Wrapper ?? throw new InvalidDataException("Expected a SocketJack binary wrapper.");
        }
        object Decode(byte[] data) {
            if (data == null) throw new ArgumentNullException(nameof(data));
            var reader = new Decoder(data);
            if (reader.Byte() != 'S' || reader.Byte() != 'B' || reader.Byte() != 1)
                throw new InvalidDataException("Unknown SocketJack binary format/version.");
            object value = Read(ref reader, 0);
            if (reader.Remaining != 0) throw new InvalidDataException("Trailing binary data.");
            return value;
        }
        public PeerRedirect DeserializeRedirect(ISocket target, byte[] data) {
            object value = Decode(data);
            var wrapper = value as Wrapper ?? new Wrapper { Type = typeof(PeerRedirect).FullName, value = value };
            return UnwrapRedirect(target, wrapper);
        }
        internal PeerRedirect UnwrapRedirect(ISocket target, Wrapper wrapper) {
            if (!(wrapper.value is IDictionary<string, object> fields)) throw new InvalidDataException("Invalid binary redirect.");
            string type = (string)fields[nameof(PeerRedirect.Type)];
            object value = fields[nameof(PeerRedirect.Value)];
            // Unwrap enforces the application type whitelist before constructing the payload.
            var payload = value as Wrapper ?? new Wrapper { Type = type, value = value };
            if (target.Options.Whitelist.Resolve(type) != payload.GetValueType(target))
                throw new TypeNotAllowedException(type);
            InboundMessageSecurity.RequireRedirect(target, type, fields.TryGetValue(nameof(PeerRedirect.Recipient), out var destination) ? destination as string : null);
            object decoded = payload.Unwrap(target);
            return new PeerRedirect { Type = type, Value = decoded,
                Sender = fields.TryGetValue(nameof(PeerRedirect.Sender), out var sender) ? (string)sender : null,
                Recipient = fields.TryGetValue(nameof(PeerRedirect.Recipient), out var recipient) ? (string)recipient : null };
        }
        public object GetPropertyValue(PropertyValueArgs args) => PropertyValue(args.Name, args.Value, args.Reference.Info.PropertyType);
        internal object PropertyValue(string name, object source, Type type) {
            if (source is IDictionary<string, object> properties && properties.TryGetValue(name, out var value))
                return GetValue(value, type, true);
            if (source is JsonValue json && json.Value.TryGetProperty(name, out var field))
                return fallback.GetValue(field, type, true);
            return null;
        }
        public object GetValue(object value, Type type, bool parse) {
            if (value == null || value is Wrapper) return value;
            type = Nullable.GetUnderlyingType(type) ?? type;
            if (value is JsonValue json) return fallback.GetValue(json.Value, type, true);
            if (type.IsInstanceOfType(value)) return value;
            if (type.IsEnum) return Enum.ToObject(type, value);
            if (value is object[] items && type.IsArray) {
                var convert = arrayConverters.GetOrAdd(type.GetElementType(), element => {
                    try { return (ArrayConverter)typeof(BinarySerializer).GetMethod(nameof(ConvertArray), BindingFlags.Static | BindingFlags.NonPublic).MakeGenericMethod(element).CreateDelegate(typeof(ArrayConverter)); }
                    catch (NotSupportedException) { return (codec, values) => {
                        var array = System.Array.CreateInstance(element, values.Length);
                        for (int i = 0; i < values.Length; i++) array.SetValue(codec.GetValue(values[i], element, true), i);
                        return array;
                    }; }
                });
                return convert(this, items);
            }
            if (value is IConvertible && typeof(IConvertible).IsAssignableFrom(type))
                return Convert.ChangeType(value, type, CultureInfo.InvariantCulture);
            // Uncommon collection/value types retain the existing JSON converter semantics.
            // The expected type comes from an already whitelisted property, never from binary type activation.
            using (var document = JsonDocument.Parse(fallback.Serialize(value)))
                return fallback.GetValue(document.RootElement, type, true);
        }
        delegate object ArrayConverter(BinarySerializer codec, object[] values);
        static readonly ConcurrentDictionary<Type, ArrayConverter> arrayConverters = new ConcurrentDictionary<Type, ArrayConverter>();
        static object ConvertArray<T>(BinarySerializer codec, object[] values) {
            var result = new T[values.Length];
            for (int i = 0; i < values.Length; i++) {
                object value = codec.GetValue(values[i], typeof(T), true);
                result[i] = value == null ? default : (T)value;
            }
            return result;
        }
        // Cache only local reflection metadata. Decoding still creates inert dictionaries and Wrapper
        // applies the receiving socket's current whitelist and SafeMode policy before activation.
        sealed class Member {
            internal byte[] Name;
            internal byte[] Utf8Name;
            internal string TextName;
            internal Func<object, object> Get;
            internal PropertyInfo Info;
        }
        delegate void ObjectWriter(BinarySerializer codec, ref Encoder writer, object value, int depth);
        sealed class Members { internal Member[] Properties; internal ObjectWriter Write; }
        // Filled only from local CLR metadata; incoming type strings never populate this cache.
        static readonly ConcurrentDictionary<string, Members> schemas = new ConcurrentDictionary<string, Members>(StringComparer.Ordinal);
        sealed class TypeName { internal string Text; internal byte[] Utf8; }
        TypeName lastType;
        internal void Prepare(Type type) { if (!type.IsValueType && !type.IsArray && type != typeof(string)) GetMembers(type); }
        static readonly ConcurrentDictionary<(Type, BindingFlags), Members> members = new ConcurrentDictionary<(Type, BindingFlags), Members>();
        static Members GetMembers(Type type) => members.GetOrAdd((type, Wrapper.ReflectionFlags), key => {
            var result = new List<Member>();
            Span<byte> prefix = stackalloc byte[5];
            foreach (var property in key.Item1.GetProperties(key.Item2)) {
                if (!property.CanRead || property.GetIndexParameters().Length != 0) continue;
                Func<object, object> get;
                try {
                    var instance = Expression.Parameter(typeof(object));
                    get = Expression.Lambda<Func<object, object>>(Expression.Convert(Expression.Property(Expression.Convert(instance, key.Item1), property), typeof(object)), instance).Compile();
                } catch (Exception ex) when (ex is PlatformNotSupportedException || ex is ArgumentException || ex is MemberAccessException) {
                    get = instance => property.GetValue(instance);
                }
                byte[] text = Encoding.UTF8.GetBytes(property.Name);
                int count = 0; uint length = (uint)text.Length;
                while (length >= 128) { prefix[count++] = (byte)(length | 128); length >>= 7; }
                prefix[count++] = (byte)length;
                var name = new byte[count + text.Length]; prefix.Slice(0, count).CopyTo(name); text.CopyTo(name, count);
                result.Add(new Member { Name = name, Utf8Name = text, TextName = property.Name, Get = get, Info = property });
            }
            var metadata = new Members { Properties = result.ToArray() };
            try {
                var codec = Expression.Parameter(typeof(BinarySerializer));
                var writer = Expression.Parameter(typeof(Encoder).MakeByRefType());
                var instance = Expression.Parameter(typeof(object)); var depth = Expression.Parameter(typeof(int));
                var calls = new List<Expression>();
                foreach (var property in metadata.Properties) {
                    calls.Add(Expression.Call(writer, typeof(Encoder).GetMethod(nameof(Encoder.Raw), BindingFlags.Instance | BindingFlags.NonPublic), Expression.Constant(property.Name)));
                    Expression value = Expression.Property(Expression.Convert(instance, key.Item1), property.Info);
                    Type valueType = property.Info.PropertyType;
                    if (valueType.IsEnum) { valueType = Enum.GetUnderlyingType(valueType); value = Expression.Convert(value, valueType); }
                    string method = null; Type parameter = valueType;
                    if (valueType == typeof(sbyte) || valueType == typeof(short) || valueType == typeof(int) || valueType == typeof(long)) { method = nameof(Signed); parameter = typeof(long); }
                    else if (valueType == typeof(byte) || valueType == typeof(ushort) || valueType == typeof(uint) || valueType == typeof(ulong)) { method = nameof(Unsigned); parameter = typeof(ulong); }
                    else if (valueType == typeof(bool)) method = nameof(Boolean);
                    else if (valueType == typeof(float)) method = nameof(Single);
                    else if (valueType == typeof(double)) method = nameof(Double);
                    else if (valueType == typeof(string)) method = nameof(Text);
                    else if (valueType == typeof(byte[])) method = nameof(Bytes);
                    if (method != null) calls.Add(Expression.Call(typeof(BinarySerializer).GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic), writer, Expression.Convert(value, parameter)));
                    else calls.Add(Expression.Call(codec, typeof(BinarySerializer).GetMethod(nameof(Write), BindingFlags.Instance | BindingFlags.NonPublic), writer, Expression.Convert(value, typeof(object)), Expression.Add(depth, Expression.Constant(1))));
                }
                metadata.Write = Expression.Lambda<ObjectWriter>(calls.Count == 0 ? Expression.Empty() : Expression.Block(calls), codec, writer, instance, depth).Compile();
            } catch (Exception ex) when (ex is PlatformNotSupportedException || ex is ArgumentException || ex is MemberAccessException || ex is NotSupportedException) { }
            if (key.Item1.FullName != null) schemas.TryAdd(key.Item1.FullName, metadata);
            return metadata;
        });
        static void Signed(ref Encoder writer, long value) { writer.Byte((byte)Tag.Signed); writer.Number(unchecked((ulong)((value << 1) ^ (value >> 63)))); }
        static void Unsigned(ref Encoder writer, ulong value) { writer.Byte((byte)Tag.Unsigned); writer.Number(value); }
        static void Boolean(ref Encoder writer, bool value) => writer.Byte((byte)(value ? Tag.True : Tag.False));
        static void Single(ref Encoder writer, float value) { writer.Byte((byte)Tag.Float); BinaryPrimitives.WriteInt32LittleEndian(writer.Space(4), BitConverter.SingleToInt32Bits(value)); }
        static void Double(ref Encoder writer, double value) { writer.Byte((byte)Tag.Double); BinaryPrimitives.WriteInt64LittleEndian(writer.Space(8), BitConverter.DoubleToInt64Bits(value)); }
        static void Text(ref Encoder writer, string value) { if (value == null) { writer.Byte((byte)Tag.Null); return; } writer.Byte((byte)Tag.String); writer.Text(value); }
        static void Bytes(ref Encoder writer, byte[] value) { if (value == null) { writer.Byte((byte)Tag.Null); return; } writer.Byte((byte)Tag.Bytes); writer.Blob(value); }
        void Write(ref Encoder writer, object value, int depth) {
            if (depth > MaxDepth) throw new InvalidDataException("Binary nesting limit exceeded.");
            if (value == null) { writer.Byte((byte)Tag.Null); return; }
            if (value is Wrapper wrapper) {
                writer.Byte((byte)Tag.Wrapper); writer.Text(wrapper.Type ?? ""); Write(ref writer, wrapper.value, depth + 1); return;
            }
            if (value is byte[] bytes) { writer.Byte((byte)Tag.Bytes); writer.Blob(bytes); return; }
            if (value is string text) { writer.Byte((byte)Tag.String); writer.Text(text); return; }
            Type type = value.GetType();
            if (type.IsEnum) { value = Convert.ChangeType(value, Enum.GetUnderlyingType(type), CultureInfo.InvariantCulture); type = value.GetType(); }
            switch (Type.GetTypeCode(type)) {
                case TypeCode.Boolean: writer.Byte((byte)((bool)value ? Tag.True : Tag.False)); return;
                case TypeCode.SByte: case TypeCode.Int16: case TypeCode.Int32: case TypeCode.Int64:
                    writer.Byte((byte)Tag.Signed); long signed = Convert.ToInt64(value); writer.Number(unchecked((ulong)((signed << 1) ^ (signed >> 63)))); return;
                case TypeCode.Byte: case TypeCode.UInt16: case TypeCode.UInt32: case TypeCode.UInt64:
                    writer.Byte((byte)Tag.Unsigned); writer.Number(Convert.ToUInt64(value)); return;
                case TypeCode.Single: writer.Byte((byte)Tag.Float); BinaryPrimitives.WriteInt32LittleEndian(writer.Space(4), BitConverter.SingleToInt32Bits((float)value)); return;
                case TypeCode.Double: writer.Byte((byte)Tag.Double); BinaryPrimitives.WriteInt64LittleEndian(writer.Space(8), BitConverter.DoubleToInt64Bits((double)value)); return;
                case TypeCode.Decimal:
                    writer.Byte((byte)Tag.Decimal);
                    foreach (int part in decimal.GetBits((decimal)value)) BinaryPrimitives.WriteInt32LittleEndian(writer.Space(4), part);
                    return;
            }
            if (value is System.Array array && array.Rank == 1) {
                if (depth == MaxDepth && array.Length != 0) throw new InvalidDataException("Binary nesting limit exceeded.");
                writer.Byte((byte)Tag.Array); writer.Number((ulong)array.Length);
                // Common primitive arrays avoid enumerator allocation and per-element boxing.
                if (type == typeof(int[])) { foreach (int item in (int[])value) Signed(ref writer, item); }
                else if (type == typeof(long[])) { foreach (long item in (long[])value) Signed(ref writer, item); }
                else if (type == typeof(uint[])) { foreach (uint item in (uint[])value) Unsigned(ref writer, item); }
                else if (type == typeof(ulong[])) { foreach (ulong item in (ulong[])value) Unsigned(ref writer, item); }
                else if (type == typeof(float[])) { foreach (float item in (float[])value) Single(ref writer, item); }
                else if (type == typeof(double[])) { foreach (double item in (double[])value) Double(ref writer, item); }
                else if (type == typeof(bool[])) { foreach (bool item in (bool[])value) Boolean(ref writer, item); }
                else if (type == typeof(string[])) { foreach (string item in (string[])value) Text(ref writer, item); }
                else foreach (var item in array) Write(ref writer, item, depth + 1);
                return;
            }
            // Dictionary keys, date/time values and specialized converters stay self-contained.
            if (value is IEnumerable || type.IsValueType || value is Type) {
                writer.Byte((byte)Tag.Json); writer.Blob(fallback.Serialize(value)); return;
            }
            writer.Byte((byte)Tag.Object);
            var metadata = GetMembers(type);
            if (depth == MaxDepth && metadata.Properties.Length != 0) throw new InvalidDataException("Binary nesting limit exceeded.");
            writer.Number((ulong)metadata.Properties.Length);
            if (metadata.Write != null) metadata.Write(this, ref writer, value, depth);
            else foreach (var property in metadata.Properties) { writer.Raw(property.Name); Write(ref writer, property.Get(value), depth + 1); }
        }
        object Read(ref Decoder reader, int depth, Members schema = null) {
            if (depth > MaxDepth) throw new InvalidDataException("Binary nesting limit exceeded.");
            switch ((Tag)reader.Byte()) {
                case Tag.Null: return null;
                case Tag.False: return false;
                case Tag.True: return true;
                case Tag.Signed: ulong n = reader.Number(); return unchecked((long)(n >> 1) ^ -((long)n & 1));
                case Tag.Unsigned: return reader.Number();
                case Tag.Float: return BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(reader.Slice(4)));
                case Tag.Double: return BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(reader.Slice(8)));
                case Tag.Decimal:
                    var parts = new int[4]; for (int i = 0; i < 4; i++) parts[i] = BinaryPrimitives.ReadInt32LittleEndian(reader.Slice(4));
                    try { return new decimal(parts); } catch (ArgumentException ex) { throw new InvalidDataException("Invalid binary decimal.", ex); }
                case Tag.String: return reader.Text();
                case Tag.Bytes: return reader.Slice(reader.Length()).ToArray();
                case Tag.Wrapper:
                    var nameBytes = reader.Slice(reader.Length());
                    var cached = System.Threading.Volatile.Read(ref lastType);
                    string typeName = cached != null && nameBytes.SequenceEqual(cached.Utf8) ? cached.Text : Encoding.UTF8.GetString(nameBytes);
                    schemas.TryGetValue(typeName, out var known);
                    if (known != null && !ReferenceEquals(typeName, cached?.Text))
                        System.Threading.Volatile.Write(ref lastType, new TypeName { Text = typeName, Utf8 = Encoding.UTF8.GetBytes(typeName) });
                    return new Wrapper { Type = typeName, value = Read(ref reader, depth + 1, known) };
                case Tag.Array:
                    int length = reader.Length(); var items = new object[length];
                    for (int i = 0; i < length; i++) items[i] = Read(ref reader, depth + 1);
                    return items;
                case Tag.Object:
                    int count = reader.Length();
                    if (count > reader.Remaining / 2) throw new InvalidDataException("Invalid binary member count.");
                    var properties = new Dictionary<string, object>(count, StringComparer.Ordinal);
                    for (int i = 0; i < count; i++) {
                        var bytes = reader.Slice(reader.Length());
                        var expected = schema != null && i < schema.Properties.Length ? schema.Properties[i] : null;
                        string name = expected != null && bytes.SequenceEqual(expected.Utf8Name) ? expected.TextName : Encoding.UTF8.GetString(bytes);
                        var item = Read(ref reader, depth + 1);
                        if (!properties.TryAdd(name, item)) throw new InvalidDataException("Duplicate binary member.");
                    }
                    return properties;
                case Tag.Json:
                    using (var document = JsonDocument.Parse(reader.BlobMemory())) return new JsonValue { Value = document.RootElement.Clone() };
                default: throw new InvalidDataException("Unknown binary value tag.");
            }
        }
        // Small objects encode on the stack. Large raw arrays are copied exactly once into the
        // returned message; temporary encoding blocks never exceed 32 KiB and are returned on failure.
        ref struct Encoder {
            Span<byte> buffer;
            int position, total;
            byte[] rented;
            List<(byte[] Data, int Count, bool Pooled)> segments;
            internal Encoder(Span<byte> scratch) { buffer = scratch; position = total = 0; rented = null; segments = null; }
            internal Span<byte> Space(int count) {
                if (count > buffer.Length - position) Flush();
                var result = buffer.Slice(position, count); position += count; return result;
            }
            internal void Byte(byte value) { if (position == buffer.Length) Flush(); buffer[position++] = value; }
            internal void Number(ulong value) {
                while (value >= 128) { Byte((byte)(value | 128)); value >>= 7; }
                Byte((byte)value);
            }
            void Add(byte[] data, int count, bool pooled) {
                int next = checked(total + count);
                if (segments == null) segments = new List<(byte[], int, bool)>();
                segments.Add((data, count, pooled)); total = next;
            }
            void Flush() {
                if (position == 0) return;
                byte[] block = ArrayPool<byte>.Shared.Rent(position);
                try { buffer.Slice(0, position).CopyTo(block); Add(block, position, true); }
                catch { ArrayPool<byte>.Shared.Return(block); throw; }
                position = 0;
            }
            internal void Blob(byte[] bytes) {
                Number((ulong)bytes.Length);
                Raw(bytes);
            }
            internal void Raw(byte[] bytes) {
                if (bytes.Length <= buffer.Length - position) { bytes.AsSpan().CopyTo(Space(bytes.Length)); return; }
                Flush(); Add(bytes, bytes.Length, false);
            }
            internal void Text(string text) {
                int count = Encoding.UTF8.GetByteCount(text); Number((ulong)count);
                if (count <= buffer.Length - position) { Encoding.UTF8.GetBytes(text.AsSpan(), Space(count)); return; }
                Flush();
                if (count <= buffer.Length) { Encoding.UTF8.GetBytes(text.AsSpan(), Space(count)); return; }
                if (rented == null) { rented = ArrayPool<byte>.Shared.Rent(32768); buffer = rented.AsSpan(0, 32768); }
                var encoder = Encoding.UTF8.GetEncoder(); var remaining = text.AsSpan();
                bool complete;
                do {
                    encoder.Convert(remaining, buffer.Slice(position), true, out int chars, out int bytes, out complete);
                    position += bytes; remaining = remaining.Slice(chars);
                    if (!complete) Flush();
                } while (!complete);
            }
            internal byte[] Finish() {
                byte[] result = new byte[checked(total + position)]; int offset = 0;
                if (segments != null) foreach (var part in segments) { part.Data.AsSpan(0, part.Count).CopyTo(result.AsSpan(offset)); offset += part.Count; }
                buffer.Slice(0, position).CopyTo(result.AsSpan(offset)); return result;
            }
            internal void Dispose() {
                if (segments != null) foreach (var part in segments) if (part.Pooled) ArrayPool<byte>.Shared.Return(part.Data);
                if (rented != null) ArrayPool<byte>.Shared.Return(rented);
            }
        }
        ref struct Decoder {
            readonly byte[] data;
            int position;
            internal Decoder(byte[] data) { this.data = data; position = 0; }
            internal int Remaining => data.Length - position;
            internal byte Byte() { if (position == data.Length) throw new EndOfStreamException(); return data[position++]; }
            internal ReadOnlySpan<byte> Slice(int length) {
                if ((uint)length > (uint)Remaining) throw new EndOfStreamException();
                var result = data.AsSpan(position, length); position += length; return result;
            }
            internal int Length() {
                ulong length = Number();
                if (length > (ulong)Remaining) throw new InvalidDataException("Invalid binary length.");
                return (int)length;
            }
            internal string Text() => Encoding.UTF8.GetString(Slice(Length()));
            internal ReadOnlyMemory<byte> BlobMemory() { int length = Length(); var result = data.AsMemory(position, length); position += length; return result; }
            internal ulong Number() {
                ulong value = 0;
                for (int shift = 0; shift < 64; shift += 7) {
                    byte part = Byte();
                    if (shift == 63 && part > 1) break;
                    value |= (ulong)(part & 127) << shift;
                    if (part < 128) { if (shift != 0 && part == 0) break; return value; }
                }
                throw new InvalidDataException("Invalid binary integer.");
            }
        }
    }
}
