using System;
using System.Collections.Generic;

namespace SocketJack.Multiplayer.Transport
{
    /// <summary>Optional diagnostics. Capture on the transport's owner/Poll thread and publish the
    /// immutable snapshot to other threads. Counters are monotonic for the transport instance.
    /// Bytes count native game-protocol payload/stream bytes, including retries and protocol headers,
    /// but exclude IP/TCP/UDP/TLS and RFC6455 framing. A missing RTT is unknown, not zero latency.</summary>
    public interface ITransportMetricsSource
    {
        TransportMetricsSnapshot GetTransportMetrics(int maxPeerDetails = 64);
    }
    public sealed class PeerTransportMetrics
    {
        public ulong PeerId { get; }
        public double? RoundTripMilliseconds { get; }
        public int PendingReliablePackets { get; }
        public long QueuedBytes { get; }
        public PeerTransportMetrics(ulong peerId, double? roundTripMilliseconds, int pendingReliablePackets, long queuedBytes)
        { PeerId = peerId; RoundTripMilliseconds = roundTripMilliseconds; PendingReliablePackets = pendingReliablePackets; QueuedBytes = queuedBytes; }
    }
    public sealed class TransportMetricsSnapshot
    {
        public TransportType Transport { get; }
        public long BytesSent { get; }
        public long BytesReceived { get; }
        public long PacketsSent { get; }
        public long PacketsReceived { get; }
        public long ReliableRetransmissions { get; }
        public long InvalidPackets { get; }
        public long SendsRejected { get; }
        public int ConnectedPeers { get; }
        public long QueuedBytes { get; }
        public IReadOnlyList<PeerTransportMetrics> Peers { get; }
        public TransportMetricsSnapshot(TransportType transport, long bytesSent, long bytesReceived, long packetsSent, long packetsReceived,
            long retransmissions, long invalidPackets, long rejected, int connectedPeers, long queuedBytes, IList<PeerTransportMetrics> peers)
        {
            Transport = transport; BytesSent = bytesSent; BytesReceived = bytesReceived; PacketsSent = packetsSent; PacketsReceived = packetsReceived;
            ReliableRetransmissions = retransmissions; InvalidPackets = invalidPackets; SendsRejected = rejected; ConnectedPeers = connectedPeers; QueuedBytes = queuedBytes;
            Peers = new List<PeerTransportMetrics>(peers ?? throw new ArgumentNullException(nameof(peers))).AsReadOnly();
        }
    }
    public static class TransportMetrics
    {
        /// <summary>Unwraps logical object framing without exposing mutable transport internals.</summary>
        public static ITransportMetricsSource Source(IGameTransport transport)
        { while (transport is ObjectFrameTransport framed) transport = framed.InnerTransport; return transport as ITransportMetricsSource; }
        internal static void ValidateDetailLimit(int limit)
        { if (limit < 0 || limit > 4096) throw new ArgumentOutOfRangeException(nameof(limit)); }
    }
}
