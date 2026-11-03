using System;
using System.IO;
using System.Text;

namespace SocketJack.Unity
{
    // A deliberately closed binary protocol: no CLR type names, reflection, or object activation.
    internal static class SessionWire
    {
        internal const byte Version = 1;
        internal const int SpawnHeaderSize = 36;
        internal enum Kind : byte { Welcome = 1, Spawn = 2, Despawn = 3, State = 4, Input = 5, Message = 6, ClearView = 7 }

        internal static byte[] Write(Kind kind, Action<BinaryWriter> fields)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(Version); writer.Write((byte)kind); fields(writer);
                return stream.ToArray();
            }
        }

        internal static BinaryReader Read(byte[] bytes, int limit, out Kind kind)
        {
            if (bytes == null || bytes.Length < 2 || bytes.Length > limit || bytes[0] != Version)
                throw new InvalidDataException("Invalid session packet.");
            kind = (Kind)bytes[1];
            var stream = new MemoryStream(bytes, false); stream.Position = 2;
            return new BinaryReader(stream, Encoding.UTF8, false);
        }

        internal static void Bytes(BinaryWriter writer, byte[] payload)
        { writer.Write((ushort)payload.Length); writer.Write(payload); }
        internal static byte[] Bytes(BinaryReader reader, int maximum)
        {
            int length = reader.ReadUInt16();
            if (length > maximum || length > reader.BaseStream.Length - reader.BaseStream.Position)
                throw new InvalidDataException("Invalid payload length.");
            return reader.ReadBytes(length);
        }
        internal static void End(BinaryReader reader)
        { if (reader.BaseStream.Position != reader.BaseStream.Length) throw new InvalidDataException("Trailing packet data."); }
        internal static bool Newer(uint value, uint previous) => unchecked((int)(value - previous)) > 0;

        internal static byte[] Spawn(NetworkObject obj) => Write(Kind.Spawn, w =>
        { w.Write(obj.Id); w.Write(obj.PrefabId); w.Write(obj.OwnerId); w.Write(obj.RoomId); w.Write(obj.Revision); w.Write(obj.Tick); Bytes(w, obj.StateData); });
        internal static byte[] State(NetworkObject obj) => Write(Kind.State, w =>
        { w.Write(obj.Id); w.Write(obj.Revision); w.Write(obj.Tick); Bytes(w, obj.StateData); });
        internal static byte[] Despawn(ulong id) => Write(Kind.Despawn, w => w.Write(id));
        internal static void ValidateDelta(double delta)
        { if (double.IsNaN(delta) || double.IsInfinity(delta) || delta < 0) throw new ArgumentOutOfRangeException(nameof(delta)); }
        internal static byte[] Copy(byte[] bytes, int maximum, string parameter)
        {
            bytes = bytes ?? Array.Empty<byte>();
            if (bytes.Length > maximum) throw new ArgumentOutOfRangeException(parameter, "Payload exceeds the configured message limit. Split larger application data into bounded messages.");
            return (byte[])bytes.Clone();
        }
    }
}
