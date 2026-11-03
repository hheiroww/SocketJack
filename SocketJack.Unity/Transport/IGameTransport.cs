using System;
using System.Net;

namespace SocketJack.Unity.Transport
{
    /// <summary>Wire protocol selected explicitly; transports never silently downgrade.</summary>
    public enum TransportType { Udp = 0, Tcp = 1, Custom = 255 }

    /// <summary>Delivery contract. Channels have independent ordering histories.</summary>
    public enum DeliveryMode : byte { Unreliable = 0, UnreliableSequenced = 1, ReliableOrdered = 2 }

    public enum TransportEventType { Connected, Data, Disconnected }

    /// <summary>Owned payload bytes remain valid after Poll returns. Server peer ID is zero.</summary>
    public sealed class TransportEvent
    {
        public TransportEventType Type { get; }
        public ulong PeerId { get; }
        public byte[] Payload { get; }
        public DeliveryMode Delivery { get; }
        public byte Channel { get; }
        public string Reason { get; }
        public TransportEvent(TransportEventType type, ulong peerId, byte[] payload = null,
            DeliveryMode delivery = DeliveryMode.ReliableOrdered, byte channel = 0, string reason = "")
        { Type = type; PeerId = peerId; Payload = payload ?? Array.Empty<byte>(); Delivery = delivery; Channel = channel; Reason = reason ?? ""; }
    }

    /// <summary>Single-owner, manually pumped transport. Call all methods from the same thread.
    /// Poll performs bounded work and delivers callbacks on its calling thread. Send returns false
    /// for backpressure or disconnected peers; callers must retry reliable application messages.</summary>
    public interface IGameTransport : IDisposable
    {
        TransportType Type { get; }
        bool IsServer { get; }
        bool IsRunning { get; }
        ulong LocalPeerId { get; }
        IPEndPoint LocalEndPoint { get; }
        int MaxPayloadSize { get; }
        event Action<TransportEvent> EventReceived;
        void StartServer(IPEndPoint endpoint);
        void StartClient(IPEndPoint endpoint, byte[] connectionData = null);
        void Poll(int maxEvents = 256);
        bool Send(ulong peerId, ArraySegment<byte> payload, DeliveryMode delivery = DeliveryMode.ReliableOrdered, byte channel = 0);
        void Disconnect(ulong peerId, string reason = "Disconnected");
        void Stop();
    }

    /// <summary>Resource limits are per transport instance. No thread is allocated per peer.</summary>
    public sealed class TransportOptions
    {
        public int MaxPeers { get; set; } = 4096;
        public int MaxPacketSize { get; set; } = 1200;
        public int Channels { get; set; } = 4;
        public int MaxPendingReliablePerPeer { get; set; } = 256;
        public int MaxQueuedBytesPerPeer { get; set; } = 256 * 1024;
        public int MaxConnectionDataSize { get; set; } = 256;
        public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);
        public TimeSpan PeerTimeout { get; set; } = TimeSpan.FromSeconds(15);
        public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(1);
        public TimeSpan RetransmitInterval { get; set; } = TimeSpan.FromMilliseconds(150);
        public int MaxRetransmits { get; set; } = 30;
        /// <summary>Server admission hook. Validate a short-lived game ticket here. Do not send reusable passwords over plaintext transports.</summary>
        public Func<IPEndPoint, byte[], bool> ConnectionValidator { get; set; }

        internal TransportOptions Snapshot()
        {
            Validate();
            return (TransportOptions)MemberwiseClone();
        }
        internal void Validate()
        {
            if (MaxPeers < 1 || MaxPeers > 65535 || MaxPacketSize < 256 || MaxPacketSize > 1400 ||
                Channels < 1 || Channels > 32 || MaxPendingReliablePerPeer < 1 || MaxPendingReliablePerPeer > 65535 ||
                MaxQueuedBytesPerPeer < MaxPacketSize || MaxConnectionDataSize < 0 || MaxConnectionDataSize > 512 ||
                MaxConnectionDataSize > MaxPacketSize - 128 || ConnectTimeout <= TimeSpan.Zero || PeerTimeout <= TimeSpan.Zero ||
                HeartbeatInterval <= TimeSpan.Zero || HeartbeatInterval >= PeerTimeout || RetransmitInterval <= TimeSpan.Zero || MaxRetransmits < 1)
                throw new ArgumentOutOfRangeException(nameof(TransportOptions), "Invalid transport limits or timing options.");
        }
    }

    public static class GameTransport
    {
        public static IGameTransport Create(TransportType type, TransportOptions options = null)
        {
            switch (type)
            {
                case TransportType.Udp: return new UdpGameTransport(options);
                case TransportType.Tcp: return new TcpGameTransport(options);
                default: throw new NotSupportedException("Supply an IGameTransport implementation for a custom transport.");
            }
        }
    }
}
