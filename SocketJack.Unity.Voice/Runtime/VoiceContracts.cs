using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using SocketJack.Multiplayer.Chat;

namespace SocketJack.Multiplayer.Voice
{
    public enum VoiceChannel : byte { Proximity, Party, Raid, Community, Whisper, Global }
    public sealed class VoiceParticipant
    {
        public ulong PeerId { get; set; }
        public string AccountId { get; set; }
        public string DisplayName { get; set; }
        public Vector3 Position { get; set; }
    }
    /// <summary>Trusted server adapter. Network payloads never supply their sender identity or audience.</summary>
    public interface IVoiceMembershipProvider
    {
        bool TryGetParticipant(ulong peerId, out VoiceParticipant participant);
        IEnumerable<ulong> ResolveAudience(VoiceParticipant speaker, VoiceChannel channel, string scopeId, string targetId);
    }
    /// <summary>Connects authenticated game peer/account mappings to authoritative social channel permissions.</summary>
    public sealed class SocialVoiceMembershipProvider : IVoiceMembershipProvider
    {
        private readonly SocialService social;
        private readonly IChatMembershipProvider presence;
        private readonly Func<ulong, string> accountForPeer;
        private readonly Func<IEnumerable<ulong>> peers;
        public SocialVoiceMembershipProvider(SocialService social, IChatMembershipProvider presence,
            Func<ulong, string> authenticatedAccountForPeer, Func<IEnumerable<ulong>> connectedPeers)
        { this.social = social ?? throw new ArgumentNullException(nameof(social)); this.presence = presence ?? throw new ArgumentNullException(nameof(presence)); accountForPeer = authenticatedAccountForPeer ?? throw new ArgumentNullException(nameof(authenticatedAccountForPeer)); peers = connectedPeers ?? throw new ArgumentNullException(nameof(connectedPeers)); }
        public bool TryGetParticipant(ulong peerId, out VoiceParticipant participant)
        {
            participant = null; string account = accountForPeer(peerId);
            if (!Guid.TryParse(account, out var id) || id == Guid.Empty || !presence.TryGetPresence(id.ToString("N"), out var value) || value == null || !value.IsOnline) return false;
            participant = new VoiceParticipant { PeerId = peerId, AccountId = id.ToString("N"), DisplayName = value.DisplayName, Position = value.Position }; return true;
        }
        public IEnumerable<ulong> ResolveAudience(VoiceParticipant speaker, VoiceChannel channel, string scopeId, string targetId)
        {
            ChatChannel chat = channel == VoiceChannel.Proximity ? ChatChannel.Proximity : channel == VoiceChannel.Party ? ChatChannel.Party : channel == VoiceChannel.Raid ? ChatChannel.Raid : channel == VoiceChannel.Community ? ChatChannel.Community : channel == VoiceChannel.Whisper ? ChatChannel.Whisper : ChatChannel.Global;
            var authorized = new HashSet<string>(social.ResolveAudience(speaker.AccountId, chat, scopeId, targetId), StringComparer.Ordinal);
            return (peers() ?? Enumerable.Empty<ulong>()).Where(peer => authorized.Contains(accountForPeer(peer) ?? "")).ToArray();
        }
    }
    /// <summary>Explicit opt-in local/demo mode. Never use as an authenticated account provider.</summary>
    public sealed class TrustedGuestVoiceMembershipProvider : IVoiceMembershipProvider
    {
        private readonly Func<IEnumerable<ulong>> peers;
        private readonly Func<ulong, Vector3> position;
        public TrustedGuestVoiceMembershipProvider(Func<IEnumerable<ulong>> connectedPeers, Func<ulong, Vector3> trustedPosition)
        { peers = connectedPeers ?? throw new ArgumentNullException(nameof(connectedPeers)); position = trustedPosition ?? throw new ArgumentNullException(nameof(trustedPosition)); }
        public bool TryGetParticipant(ulong peerId, out VoiceParticipant participant)
        {
            participant = null; if (!(peers() ?? Enumerable.Empty<ulong>()).Contains(peerId)) return false;
            var bytes = new byte[16]; Buffer.BlockCopy(BitConverter.GetBytes(peerId), 0, bytes, 0, 8); bytes[15] = 127;
            participant = new VoiceParticipant { PeerId = peerId, AccountId = new Guid(bytes).ToString("N"), DisplayName = "Guest " + peerId, Position = position(peerId) }; return true;
        }
        public IEnumerable<ulong> ResolveAudience(VoiceParticipant speaker, VoiceChannel channel, string scopeId, string targetId)
            => channel == VoiceChannel.Proximity || channel == VoiceChannel.Global ? peers() ?? Enumerable.Empty<ulong>() : Array.Empty<ulong>();
    }
    public sealed class VoiceOptions
    {
        public int SampleRate { get; set; } = 16000;
        public int Bitrate { get; set; } = 24000;
        public int MaxEncodedBytes { get; set; } = 256;
        public int MaxSpeakers { get; set; } = 32;
        public int MaxRecipientsPerFrame { get; set; } = 64;
        public int MaxForwardsPerUpdate { get; set; } = 512;
        public int PacketsPerSecond { get; set; } = 55;
        public int PacketBurst { get; set; } = 10;
        public int JitterFrames { get; set; } = 10;
        public int InitialDelayFrames { get; set; } = 3;
        public float ProximityRange { get; set; } = 30;
        public double StreamTimeoutSeconds { get; set; } = 2;
        public int SamplesPerFrame => SampleRate / 50;
        /// <summary>Validate bounds and return a detached configuration for a running service.</summary>
        public VoiceOptions Snapshot()
        {
            if ((SampleRate != 8000 && SampleRate != 12000 && SampleRate != 16000 && SampleRate != 24000 && SampleRate != 48000) || Bitrate < 6000 || Bitrate > 64000 ||
                MaxEncodedBytes < 32 || MaxEncodedBytes > 400 || MaxSpeakers < 1 || MaxSpeakers > 256 || MaxRecipientsPerFrame < 1 || MaxRecipientsPerFrame > 4096 || MaxForwardsPerUpdate < 1 || MaxForwardsPerUpdate > 65535 ||
                PacketsPerSecond < 1 || PacketsPerSecond > 100 || PacketBurst < 1 || PacketBurst > 100 || JitterFrames < 2 || JitterFrames > 50 || InitialDelayFrames < 1 || InitialDelayFrames >= JitterFrames ||
                float.IsNaN(ProximityRange) || float.IsInfinity(ProximityRange) || ProximityRange <= 0 || double.IsNaN(StreamTimeoutSeconds) || double.IsInfinity(StreamTimeoutSeconds) || StreamTimeoutSeconds < .1 || StreamTimeoutSeconds > 60)
                throw new ArgumentOutOfRangeException(nameof(VoiceOptions));
            return (VoiceOptions)MemberwiseClone();
        }
    }
    public sealed class VoiceFrame
    {
        public ulong PeerId { get; internal set; }
        public ulong ObjectId { get; internal set; }
        public uint AuthorityEpoch { get; internal set; }
        public ulong ViewId { get; internal set; }
        public uint StreamId { get; internal set; }
        public uint Sequence { get; internal set; }
        public VoiceChannel Channel { get; internal set; }
        public string ScopeId { get; internal set; }
        public string TargetId { get; internal set; }
        public string AccountId { get; internal set; }
        public Vector3 Position { get; internal set; }
        public int SampleRate { get; internal set; }
        public byte[] EncodedAudio { get; internal set; }
    }
}
