using System;
using System.IO;
using System.Numerics;

namespace SocketJack.Unity.Physics
{
    /// <summary>Player intent, independent of Unity types. Sequences reject stale unordered delivery.</summary>
    public struct PhysicsInput
    {
        public uint Sequence, Tick;
        public float Horizontal, Vertical;
        public bool Jump;
    }

    /// <summary>Sparse correction state; regular motion is simulated locally from input.</summary>
    public struct PhysicsSnapshot
    {
        public uint Tick, LastInputSequence;
        public double Time;
        public Vector3 Position, Velocity, AngularVelocity;
        public Quaternion Rotation;
    }

    /// <summary>Fixed, versioned binary physics messages. No JSON, CLR type metadata or runtime
    /// code generation. Lengths include this codec's version and message-kind bytes; transport
    /// and IP headers must be added separately when calculating the wire budget.</summary>
    public static class PhysicsBinarySerializer
    {
        public const int InputBytes = 15;
        public const int SnapshotBytes = 70;

        public static byte[] SerializeInput(PhysicsInput input)
        {
            RequireAxis(input.Horizontal); RequireAxis(input.Vertical);
            using (var stream = new MemoryStream(InputBytes))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((byte)1); writer.Write((byte)1);
                writer.Write(input.Sequence); writer.Write(input.Tick);
                writer.Write((short)Math.Round(input.Horizontal * short.MaxValue));
                writer.Write((short)Math.Round(input.Vertical * short.MaxValue));
                writer.Write((byte)(input.Jump ? 1 : 0));
                return stream.ToArray();
            }
        }
        public static PhysicsInput DeserializeInput(byte[] bytes)
        {
            RequirePacket(bytes, InputBytes, 1);
            using (var stream = new MemoryStream(bytes, false))
            using (var reader = new BinaryReader(stream))
            {
                stream.Position = 2;
                var input = new PhysicsInput { Sequence = reader.ReadUInt32(), Tick = reader.ReadUInt32() };
                short horizontal = reader.ReadInt16(), vertical = reader.ReadInt16();
                byte flags = reader.ReadByte();
                if (horizontal == short.MinValue || vertical == short.MinValue || flags > 1) throw new InvalidDataException("Invalid physics input.");
                input.Horizontal = horizontal / (float)short.MaxValue;
                input.Vertical = vertical / (float)short.MaxValue;
                input.Jump = flags == 1;
                return input;
            }
        }
        public static byte[] SerializeSnapshot(PhysicsSnapshot snapshot)
        {
            Validate(snapshot);
            using (var stream = new MemoryStream(SnapshotBytes))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((byte)1); writer.Write((byte)2);
                writer.Write(snapshot.Tick); writer.Write(snapshot.LastInputSequence); writer.Write(snapshot.Time);
                Write(writer, snapshot.Position);
                var rotation = Quaternion.Normalize(snapshot.Rotation);
                writer.Write(rotation.X); writer.Write(rotation.Y); writer.Write(rotation.Z); writer.Write(rotation.W);
                Write(writer, snapshot.Velocity); Write(writer, snapshot.AngularVelocity);
                return stream.ToArray();
            }
        }
        public static PhysicsSnapshot DeserializeSnapshot(byte[] bytes)
        {
            RequirePacket(bytes, SnapshotBytes, 2);
            using (var stream = new MemoryStream(bytes, false))
            using (var reader = new BinaryReader(stream))
            {
                stream.Position = 2;
                var snapshot = new PhysicsSnapshot
                {
                    Tick = reader.ReadUInt32(), LastInputSequence = reader.ReadUInt32(), Time = reader.ReadDouble(),
                    Position = ReadVector(reader),
                    Rotation = new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                    Velocity = ReadVector(reader), AngularVelocity = ReadVector(reader)
                };
                Validate(snapshot);
                snapshot.Rotation = Quaternion.Normalize(snapshot.Rotation);
                return snapshot;
            }
        }
        internal static void Validate(PhysicsSnapshot value)
        {
            float length = value.Rotation.LengthSquared();
            if (!Finite(value.Position) || !Finite(value.Velocity) || !Finite(value.AngularVelocity) ||
                !Finite(value.Rotation.X) || !Finite(value.Rotation.Y) || !Finite(value.Rotation.Z) || !Finite(value.Rotation.W) ||
                !Finite(length) || length < 0.000001f || double.IsNaN(value.Time) || double.IsInfinity(value.Time))
                throw new InvalidDataException("Non-finite or invalid physics snapshot.");
        }
        private static void RequireAxis(float value)
        { if (!Finite(value) || value < -1 || value > 1) throw new ArgumentOutOfRangeException(nameof(value), "Input axes must be finite and within [-1, 1]."); }
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool Finite(Vector3 value) => Finite(value.X) && Finite(value.Y) && Finite(value.Z);
        private static void RequirePacket(byte[] bytes, int length, byte kind)
        { if (bytes == null || bytes.Length != length || bytes[0] != 1 || bytes[1] != kind) throw new InvalidDataException("Invalid physics message version, kind or length."); }
        private static void Write(BinaryWriter writer, Vector3 value) { writer.Write(value.X); writer.Write(value.Y); writer.Write(value.Z); }
        private static Vector3 ReadVector(BinaryReader reader) => new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    }
}
