using System;
using System.IO;
using System.Numerics;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using SocketJack.Multiplayer.Voice;

namespace SocketJack.Multiplayer.Voice.Web
{
    public sealed class AccountVoiceFrame
    {
        public string AccountId { get; internal set; }
        public ulong SessionId { get; internal set; }
        public VoiceChannel Channel { get; internal set; }
        public string ScopeId { get; internal set; }
        public string TargetId { get; internal set; }
        public uint StreamId { get; internal set; }
        public uint Sequence { get; internal set; }
        public int SampleRate { get; internal set; }
        public long TimestampMilliseconds { get; internal set; }
        public Vector3 Position { get; internal set; }
        public byte[] EncodedAudio { get; internal set; }
    }
    internal static class AccountVoiceWire
    {
        internal const int MaximumPacket = 512;
        internal static byte[] Encode(AccountVoiceFrame frame, bool server)
        {
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream))
            {
                writer.Write((ushort)0x4156); writer.Write((byte)1); writer.Write(server); writer.Write((byte)frame.Channel); writer.Write((ushort)frame.SampleRate);
                writer.Write(frame.StreamId); writer.Write(frame.Sequence); writer.Write(frame.TimestampMilliseconds); writer.Write(Id(frame.ScopeId)); writer.Write(Id(frame.TargetId));
                if (server) { writer.Write(frame.SessionId); writer.Write(Id(frame.AccountId)); writer.Write(frame.Position.X); writer.Write(frame.Position.Y); writer.Write(frame.Position.Z); }
                writer.Write((ushort)frame.EncodedAudio.Length); writer.Write(frame.EncodedAudio); return stream.ToArray();
            }
        }
        internal static AccountVoiceFrame Decode(byte[] bytes, bool server, VoiceOptions options)
        {
            if (bytes == null || bytes.Length > MaximumPacket) throw new InvalidDataException("Voice packet exceeds its fixed bound.");
            using (var stream = new MemoryStream(bytes, false)) using (var reader = new BinaryReader(stream))
            {
                if (reader.ReadUInt16() != 0x4156 || reader.ReadByte() != 1 || reader.ReadBoolean() != server) throw new InvalidDataException("Invalid account voice protocol/direction.");
                var frame = new AccountVoiceFrame { Channel = (VoiceChannel)reader.ReadByte(), SampleRate = reader.ReadUInt16(), StreamId = reader.ReadUInt32(), Sequence = reader.ReadUInt32(), TimestampMilliseconds = reader.ReadInt64(), ScopeId = ReadId(reader), TargetId = ReadId(reader) };
                if (frame.SampleRate != options.SampleRate || frame.StreamId == 0 || frame.TimestampMilliseconds <= 0 || !Enum.IsDefined(typeof(VoiceChannel), frame.Channel)) throw new InvalidDataException("Voice profile/channel differs from this endpoint.");
                if (server)
                {
                    frame.SessionId = reader.ReadUInt64(); frame.AccountId = ReadId(reader); frame.Position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                    if (frame.SessionId == 0 || frame.AccountId.Length == 0 || !Finite(frame.Position.X) || !Finite(frame.Position.Y) || !Finite(frame.Position.Z)) throw new InvalidDataException("Invalid canonical account metadata.");
                }
                int count = reader.ReadUInt16(); if (count < 1 || count > options.MaxEncodedBytes || count != stream.Length - stream.Position) throw new InvalidDataException("Invalid voice audio length.");
                frame.EncodedAudio = reader.ReadBytes(count); if (!OpusVoiceCodec.IsValidPacket(frame.EncodedAudio, frame.SampleRate, options.MaxEncodedBytes)) throw new InvalidDataException("Invalid 20 ms mono Opus packet."); return frame;
            }
        }
        internal static async Task<byte[]> Receive(WebSocket socket, WebSocketMessageType type, int maximum, CancellationToken token)
        {
            byte[] buffer = new byte[maximum]; int count = 0;
            do
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, count, maximum - count), token).ConfigureAwait(true);
                if (result.MessageType == WebSocketMessageType.Close) return null;
                if (result.MessageType != type || (result.Count == 0 && !result.EndOfMessage) || result.Count > maximum - count) throw new InvalidDataException("Invalid bounded voice WebSocket frame.");
                count += result.Count; if (result.EndOfMessage) { var bytes = new byte[count]; Buffer.BlockCopy(buffer, 0, bytes, 0, count); return bytes; }
                if (count == maximum) throw new InvalidDataException("Voice message exceeds its bound.");
            } while (true);
        }
        private static byte[] Id(string value) => string.IsNullOrEmpty(value) ? Guid.Empty.ToByteArray() : Guid.Parse(value).ToByteArray();
        private static string ReadId(BinaryReader reader) { byte[] bytes = reader.ReadBytes(16); if (bytes.Length != 16) throw new EndOfStreamException(); var id = new Guid(bytes); return id == Guid.Empty ? "" : id.ToString("N"); }
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        internal static bool Newer(uint value, uint previous) => unchecked((int)(value - previous)) > 0;
    }
}
