using System;
using System.Buffers.Binary;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("SocketJack.UdpReliable.Tests")]

namespace SocketJack.Net {
    // v3 retains v2 data framing and negotiates receive-flight capacity in the endpoint-validated handshake.
    // DATA omits the last field: its object's accepted reservation supplies the length.
    // Packet indexes restart at zero for EVERY object. The object/session tuple prevents stale reuse.
    internal static class UdpReliableWire {
        internal const int MaximumHeader = 32;
        internal static int Write(byte[] bytes, UdpReliableTransport.Frame frame) {
            bytes[0] = (byte)'S'; bytes[1] = (byte)'J'; bytes[2] = 3;
            bytes[3] = (byte)((byte)frame.Type | ((frame.Id >> 63) != 0 ? 128 : 0));
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(4, 8), frame.Session);
            int position = 12;
            WriteNumber(bytes, ref position, frame.Id & long.MaxValue);
            WriteNumber(bytes, ref position, (uint)frame.A);
            if (frame.Type != UdpReliableTransport.Op.Data) WriteNumber(bytes, ref position, (uint)frame.B);
            return position;
        }
        internal static bool Read(byte[] bytes, int length, out UdpReliableTransport.Op op, out ulong session, out ulong id, out int a, out int b, out int position) {
            op = 0; session = id = 0; a = b = 0; position = 12;
            if (length < 14 || bytes[0] != 'S' || bytes[1] != 'J' || bytes[2] != 3) return false;
            op = (UdpReliableTransport.Op)(bytes[3] & 127);
            if (op < UdpReliableTransport.Op.Hello || op > UdpReliableTransport.Op.Inline) return false;
            session = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(4, 8));
            if (session == 0 || !ReadNumber(bytes, length, ref position, out id) || id > long.MaxValue) return false;
            if ((bytes[3] & 128) != 0) id |= 1UL << 63;
            if (!ReadNumber(bytes, length, ref position, out ulong first) || first > int.MaxValue) return false;
            a = (int)first;
            if (op == UdpReliableTransport.Op.Data) return true;
            if (!ReadNumber(bytes, length, ref position, out ulong second) || second > int.MaxValue) return false;
            b = (int)second; return true;
        }
        static void WriteNumber(byte[] bytes, ref int position, ulong value) {
            while (value >= 128) { bytes[position++] = (byte)(value | 128); value >>= 7; }
            bytes[position++] = (byte)value;
        }
        static bool ReadNumber(byte[] bytes, int length, ref int position, out ulong value) {
            value = 0;
            for (int shift = 0; shift < 64 && position < length; shift += 7) {
                byte part = bytes[position++];
                if (shift == 63 && part > 1) return false;
                value |= (ulong)(part & 127) << shift;
                if (part < 128) return shift == 0 || part != 0; // Reject overlong representations.
            }
            return false;
        }
    }
}
