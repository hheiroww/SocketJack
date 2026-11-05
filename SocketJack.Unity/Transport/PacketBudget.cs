using System;
using System.Net.Sockets;

namespace SocketJack.Multiplayer.Transport
{
    /// <summary>Optional complete packet budget for transport decorators such as authenticated relays.</summary>
    public interface IPacketBudgetSource { PacketBudget Budget { get; } }
    /// <summary>Budget for one unfragmented IP/UDP game packet. MaxDatagramBytes includes
    /// SocketJack's reliability header; MaxPayloadBytes excludes it. Frame payloads keep the
    /// entire IP packet strictly below 999 bytes. Larger logical objects can span multiple frames.</summary>
    public sealed class PacketBudget
    {
        public const int DefaultPathMtu = 1280;
        public const int UdpHeaderSize = 8;
        public const int ReliableHeaderSize = 40;
        public const int MaximumSessionHeaderSize = 49;
        public const int ObjectFrameHeaderSize = 28;
        public const int ObjectPacketLimit = 998;
        public static PacketBudget Default { get; } = new PacketBudget();

        public int PathMtu { get; }
        public int IpHeaderBytes { get; }
        public int UdpHeaderBytes => UdpHeaderSize;
        public int TransportHeaderBytes => ReliableHeaderSize;
        public int SessionHeaderBytes => MaximumSessionHeaderSize;
        public int FrameHeaderBytes => ObjectFrameHeaderSize;
        public int ExtraOverheadBytes { get; }
        public int MaxDatagramBytes { get; }
        public int MaxPayloadBytes => MaxDatagramBytes - ReliableHeaderSize;
        public int NetworkHeaderBytes => IpHeaderBytes + UdpHeaderSize + ExtraOverheadBytes;
        public int MaxObjectPacketBytes => Math.Min(ObjectPacketLimit, NetworkHeaderBytes + MaxDatagramBytes);
        public int MaxFramePayloadBytes => Math.Max(0, MaxObjectPacketBytes - NetworkHeaderBytes - ReliableHeaderSize);
        /// <summary>Single-frame state estimate after reserving the largest session header.
        /// This is not a maximum logical object size; a fragmenting transport may send larger objects.</summary>
        public int MaxObjectStateBytes => Math.Max(0, MaxFramePayloadBytes - ObjectFrameHeaderSize - MaximumSessionHeaderSize);

        public PacketBudget(int pathMtu = DefaultPathMtu, AddressFamily addressFamily = AddressFamily.InterNetworkV6,
            int extraOverheadBytes = 0, int maxDatagramBytes = 1200)
        {
            if (addressFamily != AddressFamily.InterNetwork && addressFamily != AddressFamily.InterNetworkV6)
                throw new ArgumentOutOfRangeException(nameof(addressFamily));
            if (pathMtu < 256 || pathMtu > 65535 || extraOverheadBytes < 0 || extraOverheadBytes > pathMtu ||
                maxDatagramBytes < ReliableHeaderSize || maxDatagramBytes > 65507)
                throw new ArgumentOutOfRangeException(nameof(pathMtu), "Invalid packet budget.");
            PathMtu = pathMtu;
            IpHeaderBytes = addressFamily == AddressFamily.InterNetwork ? 20 : 40;
            ExtraOverheadBytes = extraOverheadBytes;
            MaxDatagramBytes = Math.Min(maxDatagramBytes, pathMtu - NetworkHeaderBytes);
            if (MaxDatagramBytes < ReliableHeaderSize) throw new ArgumentOutOfRangeException(nameof(extraOverheadBytes), "Headers leave no transport payload budget.");
        }

        public static PacketBudget FromOptions(TransportOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            options.Validate(); options.ValidateUdp();
            return new PacketBudget(options.PathMtu, options.IpHeaderBytes == 20 ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6,
                options.ExtraPacketOverheadBytes, options.MaxPacketSize);
        }

        /// <summary>Complete IP packet bytes for transport payload bytes, including every reserved network header.</summary>
        public int GetPacketBytes(int payloadBytes)
        {
            if (payloadBytes < 0 || payloadBytes > MaxPayloadBytes) throw new ArgumentOutOfRangeException(nameof(payloadBytes));
            return NetworkHeaderBytes + ReliableHeaderSize + payloadBytes;
        }

        /// <summary>Complete object IP packet bytes, reserving the largest supported session header.</summary>
        public int GetObjectPacketBytes(int stateBytes)
        {
            if (stateBytes < 0 || stateBytes > MaxObjectStateBytes) throw new ArgumentOutOfRangeException(nameof(stateBytes));
            return NetworkHeaderBytes + ReliableHeaderSize + ObjectFrameHeaderSize + MaximumSessionHeaderSize + stateBytes;
        }
    }
}
