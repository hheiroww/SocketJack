using System;
using System.IO;
using System.Numerics;

namespace SocketJack.Multiplayer.Voice
{
    internal static class VoiceWire
    {
        internal const ushort InputMessage = 60010, FrameMessage = 60011;
        internal static byte[] Encode(VoiceFrame frame, bool fromServer, ulong? recipientView = null)
        {
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream))
            {
                writer.Write((ushort)0x564a); writer.Write((byte)1); writer.Write(fromServer); writer.Write((byte)frame.Channel); writer.Write((ushort)frame.SampleRate);
                writer.Write(frame.ObjectId); writer.Write(frame.AuthorityEpoch); writer.Write(recipientView ?? frame.ViewId); writer.Write(frame.StreamId); writer.Write(frame.Sequence);
                writer.Write(GuidBytes(frame.ScopeId)); writer.Write(GuidBytes(frame.TargetId));
                if (fromServer) { writer.Write(frame.PeerId); writer.Write(GuidBytes(frame.AccountId)); writer.Write(frame.Position.X); writer.Write(frame.Position.Y); writer.Write(frame.Position.Z); }
                writer.Write((ushort)frame.EncodedAudio.Length); writer.Write(frame.EncodedAudio); return stream.ToArray();
            }
        }
        internal static VoiceFrame Decode(byte[] bytes, bool fromServer, VoiceOptions options)
        {
            if (bytes == null || bytes.Length > options.MaxEncodedBytes + 105) throw new InvalidDataException("Voice packet bound exceeded.");
            using (var stream = new MemoryStream(bytes, false)) using (var reader = new BinaryReader(stream))
            {
                if (reader.ReadUInt16() != 0x564a || reader.ReadByte() != 1 || reader.ReadBoolean() != fromServer) throw new InvalidDataException("Invalid voice protocol/direction.");
                var frame = new VoiceFrame { Channel = (VoiceChannel)reader.ReadByte(), SampleRate = reader.ReadUInt16(), ObjectId = reader.ReadUInt64(), AuthorityEpoch = reader.ReadUInt32(),
                    ViewId = reader.ReadUInt64(), StreamId = reader.ReadUInt32(), Sequence = reader.ReadUInt32(), ScopeId = ReadGuid(reader), TargetId = ReadGuid(reader) };
                if (!Enum.IsDefined(typeof(VoiceChannel), frame.Channel) || frame.ObjectId == 0 || frame.AuthorityEpoch == 0 || frame.StreamId == 0 || frame.SampleRate != options.SampleRate) throw new InvalidDataException("Invalid voice metadata/configuration.");
                if (fromServer)
                {
                    frame.PeerId = reader.ReadUInt64(); frame.AccountId = ReadGuid(reader); frame.Position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                    if (frame.PeerId == 0 || frame.AccountId.Length == 0 || !Finite(frame.Position)) throw new InvalidDataException("Invalid canonical speaker metadata.");
                }
                int count = reader.ReadUInt16(); if (count < 1 || count > options.MaxEncodedBytes || stream.Length - stream.Position != count) throw new InvalidDataException("Invalid voice audio length.");
                frame.EncodedAudio = reader.ReadBytes(count);
                if (!OpusVoiceCodec.IsValidPacket(frame.EncodedAudio, frame.SampleRate, options.MaxEncodedBytes)) throw new InvalidDataException("Only bounded 20 ms mono Opus packets are accepted.");
                return frame;
            }
        }
        private static byte[] GuidBytes(string value) => string.IsNullOrEmpty(value) ? Guid.Empty.ToByteArray() : Guid.Parse(value).ToByteArray();
        private static string ReadGuid(BinaryReader reader)
        { var bytes = reader.ReadBytes(16); if (bytes.Length != 16) throw new EndOfStreamException(); var id = new Guid(bytes); return id == Guid.Empty ? "" : id.ToString("N"); }
        internal static bool Finite(Vector3 value) => !float.IsNaN(value.X) && !float.IsInfinity(value.X) && !float.IsNaN(value.Y) && !float.IsInfinity(value.Y) && !float.IsNaN(value.Z) && !float.IsInfinity(value.Z);
        internal static bool Newer(uint value, uint previous) => unchecked((int)(value - previous)) > 0;
    }
}
