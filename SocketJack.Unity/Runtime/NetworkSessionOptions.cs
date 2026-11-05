using System;
using SocketJack.Multiplayer.Transport;

namespace SocketJack.Multiplayer
{
    /// <summary>Limits for a manually pumped authoritative session. A session requires four transport channels.</summary>
    public sealed class NetworkSessionOptions
    {
        /// <summary>Default canonical state authority for owned objects. Dedicated servers should keep this enabled.</summary>
        public bool ServerAuthority { get; set; } = true;
        public int TickRate { get; set; } = 30;
        public int SnapshotRate { get; set; } = 10;
        public int MaxCatchUpTicks { get; set; } = 4;
        public int MaxPeers { get; set; } = 4096;
        public int MaxObjects { get; set; } = 100000;
        public int MaxObjectsPerRoom { get; set; } = 4096;
        public int MaxStateBytes { get; set; } = 1024;
        public int MaxMessageBytes { get; set; } = 1024;
        public int MaxQueuedMessagesPerPeer { get; set; } = 1024;
        public int MaxQueuedBytesPerPeer { get; set; } = 1024 * 1024;
        public int MaxReliableSendsPerUpdate { get; set; } = 64;
        public int MaxStateUpdatesPerTick { get; set; } = 64;
        public int MaxInboundMessagesPerPeerPerSecond { get; set; } = 240;
        public int MaxVisibleObjectsPerPeer { get; set; } = 4096;
        public int MaxInterestPeersPerUpdate { get; set; } = 64;
        public int MaxInterestChecksPerUpdate { get; set; } = 16384;
        /// <summary>Optional application/prefab/codec manifest. Peers must supply the same value.</summary>
        public string CompatibilityId { get; set; } = "";

        internal NetworkSessionOptions Snapshot(IGameTransport transport)
        {
            if (CompatibilityId == null || System.Text.Encoding.UTF8.GetByteCount(CompatibilityId) > 512 ||
                TickRate < 1 || TickRate > 240 || SnapshotRate < 1 || SnapshotRate > TickRate || MaxCatchUpTicks < 1 || MaxCatchUpTicks > 32 ||
                MaxPeers < 1 || MaxPeers > 65535 || MaxObjects < 1 || MaxObjectsPerRoom < 1 || MaxObjectsPerRoom > MaxObjects ||
                MaxStateBytes < 0 || MaxStateBytes > ushort.MaxValue || MaxStateBytes > transport.MaxPayloadSize - SessionWire.SpawnHeaderSize ||
                MaxMessageBytes < 0 || MaxMessageBytes > ushort.MaxValue || MaxMessageBytes > transport.MaxPayloadSize - 17 ||
                MaxQueuedMessagesPerPeer < 1 || MaxQueuedBytesPerPeer < Math.Max(MaxStateBytes + SessionWire.SpawnHeaderSize, MaxMessageBytes + 17) ||
                MaxReliableSendsPerUpdate < 1 || MaxStateUpdatesPerTick < 1 || MaxInboundMessagesPerPeerPerSecond < 1 ||
                MaxVisibleObjectsPerPeer < 1 || MaxVisibleObjectsPerPeer > 100000 || MaxInterestPeersPerUpdate < 1 || MaxInterestChecksPerUpdate < 1)
                throw new ArgumentOutOfRangeException(nameof(NetworkSessionOptions), "Session limits must fit transport packet size and have positive budgets.");
            return (NetworkSessionOptions)MemberwiseClone();
        }
    }

    /// <summary>Authoritative object state. Call NetworkServer.SetState to change replicated data.</summary>
    public sealed class NetworkObject
    {
        public ulong Id { get; internal set; }
        public uint PrefabId { get; internal set; }
        public ulong OwnerId { get; internal set; }
        public int RoomId { get; internal set; }
        public uint Revision { get; internal set; }
        public uint Tick { get; internal set; }
        public bool ServerAuthority { get; internal set; } = true;
        /// <summary>Incremented by the server whenever authority changes. Revokes in-flight owner traffic.</summary>
        public uint AuthorityEpoch { get; internal set; } = 1;
        /// <summary>Client view token. Changes whenever this peer re-enters visibility for the object.</summary>
        public ulong ViewId { get; internal set; }
        internal uint LastOwnerStateSequence;
        internal bool HasOwnerStateSequence;
        internal byte[] StateData = Array.Empty<byte>();
        public byte[] State => (byte[])StateData.Clone();
    }

    public sealed class NetworkInput
    {
        public ulong PeerId { get; internal set; }
        public ulong ObjectId { get; internal set; }
        public byte[] Payload { get; internal set; }
        public DeliveryMode Delivery { get; internal set; }
    }

    public sealed class NetworkMessage
    {
        public ulong PeerId { get; internal set; }
        public ushort MessageId { get; internal set; }
        public byte[] Payload { get; internal set; }
        public DeliveryMode Delivery { get; internal set; }
        public byte Channel { get; internal set; }
    }
}
