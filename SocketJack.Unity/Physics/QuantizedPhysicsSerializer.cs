using System;
using System.IO;
using System.Numerics;
using SocketJack.Multiplayer.Serialization;

namespace SocketJack.Multiplayer.Physics
{
    /// <summary>Stateless optional physics encoding. FullPrecision preserves the legacy 70-byte
    /// snapshot. Version 2 packets include the exact precision profile identity and independently
    /// tag position, rotation, velocity and angular-velocity groups for quantization/raw escape.
    /// 2D retains XY plus visual Z depth, Quaternion Z/W, XY velocity and Z angular velocity.
    /// All peers must select matching profiles and the same 2D/3D codec before spawning.</summary>
    public static class QuantizedPhysicsSerializer
    {
        public const int MaximumSnapshotBytes = 96;
        public static byte[] SerializeSnapshot(PhysicsSnapshot value, NetworkPrecisionProfile profile) => Write(value, profile, false);
        public static PhysicsSnapshot DeserializeSnapshot(byte[] bytes, NetworkPrecisionProfile profile) => Read(bytes, profile, false);
        public static byte[] SerializeSnapshot2D(PhysicsSnapshot value, NetworkPrecisionProfile profile) => Write(value, profile, true);
        public static PhysicsSnapshot DeserializeSnapshot2D(byte[] bytes, NetworkPrecisionProfile profile) => Read(bytes, profile, true);
        private static byte[] Write(PhysicsSnapshot value, NetworkPrecisionProfile profile, bool twoD)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile)); PhysicsBinarySerializer.Validate(value);
            if (twoD) RequirePlanar(value);
            if (profile.IsFullPrecision) return PhysicsBinarySerializer.SerializeSnapshot(value);
            value.Rotation = Quaternion.Normalize(value.Rotation);
            using (var writer = new QuantizationWriter(MaximumSnapshotBytes))
            {
                writer.Bits(2, 8); writer.Bits(twoD ? 3UL : 2UL, 8); writer.Bits(profile.Fingerprint, 64);
                writer.Bits(value.Tick, 32); writer.Bits(value.LastInputSequence, 32); writer.Bits(unchecked((ulong)BitConverter.DoubleToInt64Bits(value.Time)), 64);
                Span<float> group = stackalloc float[4];
                group[0] = value.Position.X; group[1] = value.Position.Y; group[2] = value.Position.Z;
                NetworkQuantization.WriteSingles(writer, group.Slice(0, 3), profile.Position, false);
                group[0] = value.Rotation.X; group[1] = value.Rotation.Y; group[2] = value.Rotation.Z; group[3] = value.Rotation.W;
                NetworkQuantization.WriteSingles(writer, twoD ? group.Slice(2, 2) : group, profile.Rotation, false, true);
                group[0] = value.Velocity.X; group[1] = value.Velocity.Y; group[2] = value.Velocity.Z;
                NetworkQuantization.WriteSingles(writer, group.Slice(0, twoD ? 2 : 3), profile.Velocity, false);
                group[0] = value.AngularVelocity.X; group[1] = value.AngularVelocity.Y; group[2] = value.AngularVelocity.Z;
                NetworkQuantization.WriteSingles(writer, twoD ? group.Slice(2, 1) : group.Slice(0, 3), profile.AngularVelocity, false);
                return writer.Finish();
            }
        }
        private static PhysicsSnapshot Read(byte[] bytes, NetworkPrecisionProfile profile, bool twoD)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (profile.IsFullPrecision) { var legacy = PhysicsBinarySerializer.DeserializeSnapshot(bytes); if (twoD) RequirePlanar(legacy); return legacy; }
            var reader = new QuantizationReader(bytes, MaximumSnapshotBytes);
            if (reader.Bits(8) != 2 || reader.Bits(8) != (twoD ? 3UL : 2UL) || reader.Bits(64) != profile.Fingerprint)
                throw new InvalidDataException("Physics version, dimensionality or precision profile differs.");
            var value = new PhysicsSnapshot { Tick = (uint)reader.Bits(32), LastInputSequence = (uint)reader.Bits(32), Time = BitConverter.Int64BitsToDouble(unchecked((long)reader.Bits(64))) };
            Span<float> group = stackalloc float[4];
            NetworkQuantization.ReadSingles(reader, group.Slice(0, 3), profile.Position, false); value.Position = new Vector3(group[0], group[1], group[2]);
            NetworkQuantization.ReadSingles(reader, group.Slice(0, twoD ? 2 : 4), profile.Rotation, false);
            value.Rotation = twoD ? new Quaternion(0, 0, group[0], group[1]) : new Quaternion(group[0], group[1], group[2], group[3]);
            NetworkQuantization.ReadSingles(reader, group.Slice(0, twoD ? 2 : 3), profile.Velocity, false); value.Velocity = new Vector3(group[0], group[1], twoD ? 0 : group[2]);
            NetworkQuantization.ReadSingles(reader, group.Slice(0, twoD ? 1 : 3), profile.AngularVelocity, false);
            value.AngularVelocity = twoD ? new Vector3(0, 0, group[0]) : new Vector3(group[0], group[1], group[2]);
            reader.Finish(); PhysicsBinarySerializer.Validate(value); value.Rotation = Quaternion.Normalize(value.Rotation); return value;
        }
        private static void RequirePlanar(PhysicsSnapshot value)
        {
            if (value.Rotation.X != 0 || value.Rotation.Y != 0 || value.Velocity.Z != 0 || value.AngularVelocity.X != 0 || value.AngularVelocity.Y != 0)
                throw new InvalidDataException("2D snapshots require planar rotation, XY velocity and Z angular velocity.");
        }
    }
}
