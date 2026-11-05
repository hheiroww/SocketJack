using System;
using System.IO;
using System.Numerics;
using SocketJack.Multiplayer.Serialization;

namespace SocketJack.Multiplayer.Physics
{
    public struct TransformSnapshot
    {
        public Vector3 Position, Scale;
        public Quaternion Rotation;
    }
    /// <summary>Absolute, stateless transform values. Full precision preserves the original 40-byte codec.</summary>
    public static class QuantizedTransformSerializer
    {
        public const int MaximumBytes = 64;
        public static byte[] Serialize(TransformSnapshot value, NetworkPrecisionProfile profile)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile)); Validate(value);
            using (var writer = new QuantizationWriter(MaximumBytes))
            {
                if (!profile.IsFullPrecision) { writer.Bits(2, 8); writer.Bits(4, 8); writer.Bits(profile.Fingerprint, 64); }
                Span<float> v = stackalloc float[4];
                v[0] = value.Position.X; v[1] = value.Position.Y; v[2] = value.Position.Z;
                Write(writer, v.Slice(0, 3), profile.Position, profile.IsFullPrecision);
                v[0] = value.Rotation.X; v[1] = value.Rotation.Y; v[2] = value.Rotation.Z; v[3] = value.Rotation.W;
                Write(writer, v, profile.Rotation, profile.IsFullPrecision);
                v[0] = value.Scale.X; v[1] = value.Scale.Y; v[2] = value.Scale.Z;
                Write(writer, v.Slice(0, 3), profile.Scale, profile.IsFullPrecision);
                return writer.Finish();
            }
        }
        public static TransformSnapshot Deserialize(byte[] bytes, NetworkPrecisionProfile profile)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            var reader = new QuantizationReader(bytes, MaximumBytes);
            if (!profile.IsFullPrecision && (reader.Bits(8) != 2 || reader.Bits(8) != 4 || reader.Bits(64) != profile.Fingerprint))
                throw new InvalidDataException("Transform precision profile differs.");
            Span<float> v = stackalloc float[4];
            Read(reader, v.Slice(0, 3), profile.Position, profile.IsFullPrecision);
            var value = new TransformSnapshot { Position = new Vector3(v[0], v[1], v[2]) };
            Read(reader, v, profile.Rotation, profile.IsFullPrecision); value.Rotation = new Quaternion(v[0], v[1], v[2], v[3]);
            Read(reader, v.Slice(0, 3), profile.Scale, profile.IsFullPrecision); value.Scale = new Vector3(v[0], v[1], v[2]);
            reader.Finish(); Validate(value); value.Rotation = Quaternion.Normalize(value.Rotation); return value;
        }
        private static void Write(QuantizationWriter writer, ReadOnlySpan<float> values, NetworkQuantizationRange range, bool full)
        {
            if (!full) { NetworkQuantization.WriteSingles(writer, values, range, false); return; }
            foreach (float value in values) writer.Bits(unchecked((uint)BitConverter.SingleToInt32Bits(value)), 32);
        }
        private static void Read(QuantizationReader reader, Span<float> values, NetworkQuantizationRange range, bool full)
        {
            if (!full) { NetworkQuantization.ReadSingles(reader, values, range, false); return; }
            for (int i = 0; i < values.Length; i++) values[i] = BitConverter.Int32BitsToSingle(unchecked((int)reader.Bits(32)));
        }
        private static void Validate(TransformSnapshot value)
        {
            float length = value.Rotation.LengthSquared();
            foreach (float part in new[] { value.Position.X, value.Position.Y, value.Position.Z, value.Scale.X, value.Scale.Y, value.Scale.Z,
                value.Rotation.X, value.Rotation.Y, value.Rotation.Z, value.Rotation.W, length })
                if (float.IsNaN(part) || float.IsInfinity(part)) throw new InvalidDataException("Non-finite transform.");
            if (length < .0001f) throw new InvalidDataException("Invalid transform rotation.");
        }
    }
}
