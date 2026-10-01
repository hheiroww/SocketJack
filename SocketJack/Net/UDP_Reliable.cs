using System;
using System.Threading;

namespace SocketJack.Net {
    public enum UdpMode { UDP, UDP_Reliable }
    public enum UdpReliableProfile { Adaptive, FastLan }
    public enum UdpDeliveryMode { Ordered, Independent }

    /// <summary>Configure before connecting/listening. Limits are local resource budgets, not bandwidth caps.</summary>
    public sealed class UdpReliableOptions {
        public UdpReliableProfile Profile { get; set; } = UdpReliableProfile.Adaptive;
        /// <summary>Wait for earlier objects and run callbacks sequentially. Default: false.</summary>
        public bool Ordering { get; set; }
        public UdpDeliveryMode Delivery {
            get => Ordering ? UdpDeliveryMode.Ordered : UdpDeliveryMode.Independent;
            set => Ordering = value == UdpDeliveryMode.Ordered;
        }
        /// <summary>Maximum concurrent independent object decoders/callbacks per peer.</summary>
        public int ReceiveWorkers { get; set; } = 4;
        /// <summary>Individual reusable transport I/O buffers, at most 32 KiB. Aggregate queues are separate.</summary>
        public int BufferSize { get; set; } = 32 * 1024;
        /// <summary>Aggregate operating-system socket queue capacity, shared by all peers. This is not a datagram or individual I/O buffer size.</summary>
        public int SocketQueueBytes { get; set; } = 1024 * 1024;
        public int DatagramSize { get; set; } = 1200;
        public long SendQueueBytes { get; set; } = 128L * 1024 * 1024;
        public long ReceiveQueueBytes { get; set; } = 128L * 1024 * 1024;
        public long GlobalReceiveQueueBytes { get; set; } = 512L * 1024 * 1024;
        public int MaximumPeers { get; set; } = 1024;
        public int MaximumConcurrentMessages { get; set; } = 8;
        public int MaximumQueuedMessages { get; set; } = 1024;
        public int MaximumFlightPackets { get; set; } = 512;
        public TimeSpan DeliveryTimeout { get; set; } = TimeSpan.FromSeconds(30);
        internal UdpReliableOptions Snapshot() {
            if (BufferSize < 256 || BufferSize > 32768 || SocketQueueBytes < BufferSize || SocketQueueBytes > 64 * 1024 * 1024 || DatagramSize < 256 || DatagramSize > BufferSize || ReceiveWorkers < 1 || ReceiveWorkers > 64 || SendQueueBytes < 1 || ReceiveQueueBytes < 1 ||
                GlobalReceiveQueueBytes < ReceiveQueueBytes || MaximumPeers < 1 || MaximumConcurrentMessages < 1 ||
                MaximumConcurrentMessages > 64 || MaximumQueuedMessages < MaximumConcurrentMessages || MaximumQueuedMessages > 65536 || MaximumFlightPackets < 4 || MaximumFlightPackets > 65536 ||
                DeliveryTimeout <= TimeSpan.Zero || DeliveryTimeout > TimeSpan.FromDays(1))
                throw new ArgumentOutOfRangeException(nameof(UdpReliableOptions));
            return (UdpReliableOptions)MemberwiseClone();
        }
    }

    /// <summary>Transport counters include retries and headers; payload counters exclude them.</summary>
    public sealed class UdpReliableStatistics {
        internal long sent, received, payloadSent, payloadReceived, retries, queued;
        internal double rtt;
        public long WireBytesSent => Interlocked.Read(ref sent);
        public long WireBytesReceived => Interlocked.Read(ref received);
        public long PayloadBytesSent => Interlocked.Read(ref payloadSent);
        public long PayloadBytesReceived => Interlocked.Read(ref payloadReceived);
        public long Retransmissions => Interlocked.Read(ref retries);
        public long QueuedBytes => Interlocked.Read(ref queued);
        public double RoundTripMilliseconds => Volatile.Read(ref rtt);
    }

    /// <summary>Reliable, indexed UDP. Both endpoints must select this mode. It does not provide encryption.</summary>
    public static class UDP_Reliable {
        public static UdpClient CreateClient(NetworkOptions options = null, string name = "UDP_Reliable") {
            options = options ?? NetworkOptions.NewDefault();
            options.UdpMode = UdpMode.UDP_Reliable;
            return new UdpClient(options, name);
        }
        public static UdpServer CreateServer(int port, NetworkOptions options = null, string name = "UDP_Reliable") {
            options = options ?? NetworkOptions.NewDefault();
            options.UdpMode = UdpMode.UDP_Reliable;
            return new UdpServer(options, port, name);
        }
    }
}
