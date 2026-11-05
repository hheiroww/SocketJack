using System;
using System.IO;
using System.Numerics;
using System.Security.Cryptography;

namespace SocketJack.Multiplayer.Serialization
{
    public enum NetworkPrecisionPreset { FullPrecision = 0, Balanced = 1, Compact = 2, Custom = 3 }
    public enum NetworkQuantizationSemantic { Scalar = 0, Position = 1, Rotation = 2, Scale = 3, Velocity = 4, AngularVelocity = 5 }

    /// <summary>Immutable scalar grid. Values outside the inclusive range escape to full precision.
    /// A grid supports at most 52 bits; IEEE rounding that cannot meet Step/2 also escapes.</summary>
    public sealed class NetworkQuantizationRange
    {
        public double Minimum { get; }
        public double Maximum { get; }
        public double Step { get; }
        public int Bits { get; }
        internal ulong MaximumCode { get; }
        public NetworkQuantizationRange(double minimum, double maximum, double step)
        {
            if (!NetworkQuantization.Finite(minimum) || !NetworkQuantization.Finite(maximum) || !NetworkQuantization.Finite(step) ||
                maximum <= minimum || step <= 0 || !NetworkQuantization.Finite(maximum - minimum))
                throw new ArgumentOutOfRangeException(nameof(step), "Supply finite ordered bounds and a positive step.");
            double count = Math.Ceiling((maximum - minimum) / step);
            if (!NetworkQuantization.Finite(count) || count < 1 || count > 4503599627370495d)
                throw new ArgumentOutOfRangeException(nameof(step), "The grid must fit 52 bits.");
            Minimum = minimum == 0 ? 0 : minimum; Maximum = maximum == 0 ? 0 : maximum; Step = step;
            MaximumCode = (ulong)count;
            int bits = 0; for (ulong remaining = MaximumCode; remaining != 0; remaining >>= 1) bits++;
            Bits = bits;
        }
        internal bool TryEncode(double value, out ulong code)
        {
            code = 0;
            if (!NetworkQuantization.Finite(value) || value < Minimum || value > Maximum) return false;
            double rounded = Math.Floor((value - Minimum) / Step + .5);
            if (!NetworkQuantization.Finite(rounded) || rounded < 0) return false;
            code = rounded > MaximumCode ? MaximumCode : (ulong)rounded;
            double decoded = Decode(code);
            return Math.Abs(decoded - value) <= Step / 2;
        }
        internal double Decode(ulong code)
        {
            if (code > MaximumCode) throw new InvalidDataException("Quantized value is outside its grid.");
            double value = code == MaximumCode ? Maximum : Minimum + code * Step;
            if (!NetworkQuantization.Finite(value) || value < Minimum || value > Maximum) throw new InvalidDataException("Invalid quantization grid value.");
            return value;
        }
    }

    /// <summary>Immutable wire precision settings. Position/scale/velocity use game units per component;
    /// Rotation uses quaternion-component steps in [-1,1], not Euler degrees. Physics angular velocity
    /// uses radians/second. Custom profiles with the same settings have the same signature as a preset.
    /// Choose world ranges deliberately; out-of-range groups retain exact full precision.</summary>
    public sealed class NetworkPrecisionProfile
    {
        public NetworkPrecisionPreset Preset { get; }
        public bool IsFullPrecision => Preset == NetworkPrecisionPreset.FullPrecision;
        public NetworkQuantizationRange Position { get; }
        public NetworkQuantizationRange Rotation { get; }
        public NetworkQuantizationRange Scale { get; }
        public NetworkQuantizationRange Velocity { get; }
        public NetworkQuantizationRange AngularVelocity { get; }
        public NetworkQuantizationRange Scalar { get; }
        public ulong Fingerprint { get; }
        public string ProtocolId { get; }
        public static NetworkPrecisionProfile FullPrecision { get; } = Create(NetworkPrecisionPreset.FullPrecision, false);
        public static NetworkPrecisionProfile Balanced { get; } = Create(NetworkPrecisionPreset.Balanced, false);
        public static NetworkPrecisionProfile Compact { get; } = Create(NetworkPrecisionPreset.Compact, true);

        public NetworkPrecisionProfile(NetworkQuantizationRange position, NetworkQuantizationRange rotation,
            NetworkQuantizationRange scale, NetworkQuantizationRange velocity, NetworkQuantizationRange angularVelocity = null,
            NetworkQuantizationRange scalar = null)
            : this(NetworkPrecisionPreset.Custom, position, rotation, scale, velocity, angularVelocity ?? velocity, scalar ?? position) { }
        private NetworkPrecisionProfile(NetworkPrecisionPreset preset, NetworkQuantizationRange position, NetworkQuantizationRange rotation,
            NetworkQuantizationRange scale, NetworkQuantizationRange velocity, NetworkQuantizationRange angularVelocity, NetworkQuantizationRange scalar)
        {
            Preset = preset; Position = position ?? throw new ArgumentNullException(nameof(position));
            Rotation = rotation ?? throw new ArgumentNullException(nameof(rotation)); Scale = scale ?? throw new ArgumentNullException(nameof(scale));
            Velocity = velocity ?? throw new ArgumentNullException(nameof(velocity)); AngularVelocity = angularVelocity ?? throw new ArgumentNullException(nameof(angularVelocity));
            Scalar = scalar ?? throw new ArgumentNullException(nameof(scalar));
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((byte)1); writer.Write(IsFullPrecision);
                foreach (var range in new[] { Position, Rotation, Scale, Velocity, AngularVelocity, Scalar })
                { writer.Write(range.Minimum); writer.Write(range.Maximum); writer.Write(range.Step); }
                writer.Flush();
                using (var sha = SHA256.Create())
                {
                    byte[] digest = sha.ComputeHash(stream.ToArray()); ulong fingerprint = 0;
                    for (int i = 0; i < 8; i++) fingerprint |= (ulong)digest[i] << (i * 8);
                    Fingerprint = fingerprint;
                    ProtocolId = "sj.precision.v1." + fingerprint.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
                }
            }
        }
        private static NetworkPrecisionProfile Create(NetworkPrecisionPreset preset, bool compact) => new NetworkPrecisionProfile(preset,
            new NetworkQuantizationRange(compact ? -512 : -1024, compact ? 512 : 1024, compact ? 1d / 16 : 1d / 128),
            new NetworkQuantizationRange(-1, 1, compact ? 1d / 256 : 1d / 32768),
            new NetworkQuantizationRange(0, compact ? 8 : 16, compact ? 1d / 128 : 1d / 1024),
            new NetworkQuantizationRange(compact ? -64 : -128, compact ? 64 : 128, compact ? 1d / 16 : 1d / 128),
            new NetworkQuantizationRange(-64, 64, compact ? 1d / 32 : 1d / 256),
            new NetworkQuantizationRange(-1024, 1024, compact ? 1d / 16 : 1d / 128));
        public static NetworkPrecisionProfile ForPreset(NetworkPrecisionPreset preset)
        {
            switch (preset)
            {
                case NetworkPrecisionPreset.FullPrecision: return FullPrecision;
                case NetworkPrecisionPreset.Balanced: return Balanced;
                case NetworkPrecisionPreset.Compact: return Compact;
                default: throw new ArgumentOutOfRangeException(nameof(preset), "Construct a custom profile explicitly.");
            }
        }
        public NetworkPrecisionProfile With(NetworkQuantizationRange position = null, NetworkQuantizationRange rotation = null,
            NetworkQuantizationRange scale = null, NetworkQuantizationRange velocity = null, NetworkQuantizationRange angularVelocity = null,
            NetworkQuantizationRange scalar = null) => new NetworkPrecisionProfile(position ?? Position, rotation ?? Rotation, scale ?? Scale,
                velocity ?? Velocity, angularVelocity ?? AngularVelocity, scalar ?? Scalar);
        public NetworkQuantizationRange GetRange(NetworkQuantizationSemantic semantic)
        {
            switch (semantic)
            {
                case NetworkQuantizationSemantic.Scalar: return Scalar;
                case NetworkQuantizationSemantic.Position: return Position;
                case NetworkQuantizationSemantic.Rotation: return Rotation;
                case NetworkQuantizationSemantic.Scale: return Scale;
                case NetworkQuantizationSemantic.Velocity: return Velocity;
                case NetworkQuantizationSemantic.AngularVelocity: return AngularVelocity;
                default: throw new ArgumentOutOfRangeException(nameof(semantic));
            }
        }
    }

    /// <summary>Explicit per-member numeric codec. Select the semantic/profile locally and include
    /// ProtocolId in the binding schema. Supports float/double, Numerics vectors/quaternions and nullable
    /// forms. Quaternion components are preserved numerically; physics codecs additionally normalize them.
    /// It never infers a type or precision profile from peer input.</summary>
    public sealed class QuantizedNetworkSerializer : INetworkSerializer
    {
        public NetworkPrecisionProfile Profile { get; }
        public NetworkQuantizationSemantic Semantic { get; }
        public string ProtocolId { get; }
        public QuantizedNetworkSerializer(NetworkPrecisionProfile profile, NetworkQuantizationSemantic semantic = NetworkQuantizationSemantic.Scalar)
        {
            Profile = profile ?? throw new ArgumentNullException(nameof(profile)); Profile.GetRange(semantic); Semantic = semantic;
            ProtocolId = "sj.numeric.v1/" + profile.ProtocolId + "/" + ((int)semantic).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        public bool CanSerialize(Type type) => type != null && TypeCode(Nullable.GetUnderlyingType(type) ?? type) != 0;
        public byte[] Serialize(object value, Type declaredType, int maxBytes = 8192)
        {
            byte type = RequireType(declaredType); bool nullable = Nullable.GetUnderlyingType(declaredType) != null;
            if (value == null && !nullable) throw new ArgumentException("Numeric value cannot be null.", nameof(value));
            if (value != null && !(Nullable.GetUnderlyingType(declaredType) ?? declaredType).IsInstanceOfType(value)) throw new ArgumentException("Numeric type differs.");
            using (var writer = new QuantizationWriter(maxBytes))
            {
                writer.Bits(1, 8); writer.Bits((ulong)(type | (nullable ? 128 : 0)), 8); writer.Bits((byte)Semantic, 8); writer.Bits(Profile.Fingerprint, 64);
                if (nullable) writer.Bits(value == null ? 0UL : 1UL, 1);
                if (value != null)
                {
                    if (type == 2) NetworkQuantization.WriteDouble(writer, (double)value, Profile.GetRange(Semantic), Profile.IsFullPrecision);
                    else
                    {
                        Span<float> components = stackalloc float[4]; int count = Components(value, type, components);
                        NetworkQuantization.WriteSingles(writer, components.Slice(0, count), Profile.GetRange(Semantic), Profile.IsFullPrecision);
                    }
                }
                return writer.Finish();
            }
        }
        public object Deserialize(byte[] bytes, Type declaredType, int maxBytes = 8192)
        {
            byte type = RequireType(declaredType); bool nullable = Nullable.GetUnderlyingType(declaredType) != null;
            var reader = new QuantizationReader(bytes, maxBytes);
            if (reader.Bits(8) != 1 || reader.Bits(8) != (ulong)(type | (nullable ? 128 : 0)) || reader.Bits(8) != (byte)Semantic || reader.Bits(64) != Profile.Fingerprint)
                throw new InvalidDataException("Numeric codec type, semantic or precision profile differs.");
            object value = null;
            if (!nullable || reader.Bits(1) != 0)
            {
                if (type == 2) value = NetworkQuantization.ReadDouble(reader, Profile.GetRange(Semantic), Profile.IsFullPrecision);
                else
                {
                    Span<float> v = stackalloc float[4]; int count = type <= 2 ? 1 : type == 3 ? 2 : type == 4 ? 3 : 4;
                    NetworkQuantization.ReadSingles(reader, v.Slice(0, count), Profile.GetRange(Semantic), Profile.IsFullPrecision);
                    switch (type)
                    {
                        case 1: value = v[0]; break;
                        case 3: value = new Vector2(v[0], v[1]); break;
                        case 4: value = new Vector3(v[0], v[1], v[2]); break;
                        case 5: value = new Vector4(v[0], v[1], v[2], v[3]); break;
                        case 6: value = new Quaternion(v[0], v[1], v[2], v[3]); break;
                    }
                }
            }
            reader.Finish(); return value;
        }
        private static byte RequireType(Type type) { if (type == null) throw new ArgumentNullException(nameof(type)); byte id = TypeCode(Nullable.GetUnderlyingType(type) ?? type); if (id == 0) throw new NotSupportedException("Numeric codec does not support this local type."); return id; }
        private static byte TypeCode(Type type) => type == typeof(float) ? (byte)1 : type == typeof(double) ? (byte)2 : type == typeof(Vector2) ? (byte)3 : type == typeof(Vector3) ? (byte)4 : type == typeof(Vector4) ? (byte)5 : type == typeof(Quaternion) ? (byte)6 : (byte)0;
        private static int Components(object value, byte type, Span<float> output)
        {
            switch (type)
            {
                case 1: output[0] = (float)value; return 1;
                case 3: var v2 = (Vector2)value; output[0] = v2.X; output[1] = v2.Y; return 2;
                case 4: var v3 = (Vector3)value; output[0] = v3.X; output[1] = v3.Y; output[2] = v3.Z; return 3;
                case 5: var v4 = (Vector4)value; output[0] = v4.X; output[1] = v4.Y; output[2] = v4.Z; output[3] = v4.W; return 4;
                case 6: var q = (Quaternion)value; output[0] = q.X; output[1] = q.Y; output[2] = q.Z; output[3] = q.W; return 4;
                default: throw new NotSupportedException();
            }
        }
    }

    internal static class NetworkQuantization
    {
        internal static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        internal static void WriteSingles(QuantizationWriter writer, ReadOnlySpan<float> values, NetworkQuantizationRange range, bool full, bool rotation = false)
        {
            Span<ulong> codes = stackalloc ulong[4]; bool raw = full || range.Bits >= 32; float length = 0;
            for (int i = 0; i < values.Length; i++)
            {
                if (!Finite(values[i])) throw new InvalidDataException("Numeric values must be finite.");
                if (!range.TryEncode(values[i], out codes[i])) raw = true;
                else { float decoded = (float)range.Decode(codes[i]); if (!Finite(decoded) || Math.Abs((double)decoded - values[i]) > range.Step / 2) raw = true; length += decoded * decoded; }
            }
            if (rotation && (!Finite(length) || length < .000001f)) raw = true;
            writer.Bits(raw ? 1UL : 0UL, 1);
            for (int i = 0; i < values.Length; i++) writer.Bits(raw ? unchecked((uint)BitConverter.SingleToInt32Bits(values[i])) : codes[i], raw ? 32 : range.Bits);
        }
        internal static void ReadSingles(QuantizationReader reader, Span<float> values, NetworkQuantizationRange range, bool full)
        {
            bool raw = reader.Bits(1) != 0;
            if (!raw && (full || range.Bits >= 32)) throw new InvalidDataException("Invalid quantization tag for this profile.");
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = raw ? BitConverter.Int32BitsToSingle(unchecked((int)reader.Bits(32))) : (float)range.Decode(reader.Bits(range.Bits));
                if (!Finite(values[i])) throw new InvalidDataException("Numeric values must be finite.");
            }
        }
        internal static void WriteDouble(QuantizationWriter writer, double value, NetworkQuantizationRange range, bool full)
        {
            if (!Finite(value)) throw new InvalidDataException("Numeric values must be finite.");
            bool raw = full || !range.TryEncode(value, out _); writer.Bits(raw ? 1UL : 0UL, 1);
            if (raw) writer.Bits(unchecked((ulong)BitConverter.DoubleToInt64Bits(value)), 64);
            else { range.TryEncode(value, out ulong code); writer.Bits(code, range.Bits); }
        }
        internal static double ReadDouble(QuantizationReader reader, NetworkQuantizationRange range, bool full)
        {
            bool raw = reader.Bits(1) != 0; if (!raw && full) throw new InvalidDataException("Invalid quantization tag.");
            double value = raw ? BitConverter.Int64BitsToDouble(unchecked((long)reader.Bits(64))) : range.Decode(reader.Bits(range.Bits));
            if (!Finite(value)) throw new InvalidDataException("Numeric values must be finite."); return value;
        }
    }
    internal sealed class QuantizationWriter : IDisposable
    {
        private readonly MemoryStream stream = new MemoryStream(128);
        private readonly int limit;
        private int used; private byte pending;
        internal QuantizationWriter(int limit) { if (limit < 1 || limit > SocketJackBinaryFormatter.MaximumValueBytes) throw new ArgumentOutOfRangeException(nameof(limit)); this.limit = limit; }
        internal void Bits(ulong value, int count)
        {
            if (count < 0 || count > 64) throw new ArgumentOutOfRangeException(nameof(count));
            if ((stream.Length * 8 + used + count + 7) / 8 > limit) throw new InvalidDataException("Quantized value exceeds its configured byte limit.");
            for (int i = 0; i < count; i++)
            {
                pending |= (byte)(((value >> i) & 1) << used);
                if (++used == 8) { stream.WriteByte(pending); used = 0; pending = 0; }
            }
        }
        internal byte[] Finish() { if (used != 0) { stream.WriteByte(pending); used = 0; pending = 0; } return stream.ToArray(); }
        public void Dispose() => stream.Dispose();
    }
    internal sealed class QuantizationReader
    {
        private readonly byte[] bytes;
        private int position;
        internal QuantizationReader(byte[] bytes, int limit)
        {
            if (limit < 1 || limit > SocketJackBinaryFormatter.MaximumValueBytes) throw new ArgumentOutOfRangeException(nameof(limit));
            if (bytes == null || bytes.Length == 0 || bytes.Length > limit) throw new InvalidDataException("Invalid quantized value length."); this.bytes = bytes;
        }
        internal ulong Bits(int count)
        {
            if (count < 0 || count > 64 || count > bytes.Length * 8 - position) throw new InvalidDataException("Truncated quantized value.");
            ulong value = 0;
            for (int i = 0; i < count; i++, position++) value |= (ulong)((bytes[position >> 3] >> (position & 7)) & 1) << i;
            return value;
        }
        internal void Finish() { int remaining = bytes.Length * 8 - position; if (remaining >= 8 || (remaining > 0 && Bits(remaining) != 0)) throw new InvalidDataException("Trailing data or nonzero padding in quantized value."); }
    }
}
