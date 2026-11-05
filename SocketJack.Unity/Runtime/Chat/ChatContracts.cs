using System;
using System.Collections.Generic;
using System.Numerics;

namespace SocketJack.Multiplayer.Chat
{
    public enum ChatChannel { Global, Trade, Zone, LookingForGroup, Party, Raid, Community, CommunityOfficers, Proximity, Yell, Whisper, Emote, System }
    public enum SocialGroupKind { Party, Raid }
    public enum CommunityRole { Owner, Officer, Member }
    public enum SocialInviteKind { Group, Community }

    /// <summary>Trusted account directory result. Never populate from an unauthenticated request body.</summary>
    public sealed class ChatAccount
    {
        public string AccountId { get; set; }
        public string DisplayName { get; set; }
    }
    /// <summary>Trusted server presence. HasGamePresence must be true for position/zone routing;
    /// ordinary web users can participate in global, trade, group and community channels.</summary>
    public sealed class ChatPresence
    {
        public string AccountId { get; set; }
        public string DisplayName { get; set; }
        public bool IsOnline { get; set; }
        public bool HasGamePresence { get; set; }
        public string WorldId { get; set; } = "";
        public int RoomId { get; set; }
        public Vector3 Position { get; set; }
    }
    public interface IChatMembershipProvider
    {
        bool TryGetPresence(string accountId, out ChatPresence presence);
        IEnumerable<ChatPresence> GetOnlinePresences();
    }
    /// <summary>Optional spatial index for nearby chat in large worlds. The service still validates every result.</summary>
    public interface IChatSpatialMembershipProvider : IChatMembershipProvider
    {
        IEnumerable<ChatPresence> GetNearbyPresences(string worldId, int roomId, Vector3 position, float radius);
    }
    public sealed class ChatLink
    {
        public string Kind { get; set; }
        public string Id { get; set; }
        public string Label { get; set; }
        public int Start { get; set; }
        public int Length { get; set; }
    }
    public sealed class ChatMessage
    {
        public long Id { get; set; }
        public DateTime TimestampUtc { get; set; }
        public string SenderId { get; set; }
        public string SenderName { get; set; }
        public ChatChannel Channel { get; set; }
        public string ScopeId { get; set; }
        public string TargetId { get; set; }
        public string Text { get; set; }
        public List<ChatLink> Links { get; set; } = new List<ChatLink>();
    }
    public sealed class ChatSendResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        public ChatMessage Message { get; set; }
        public string[] RecipientIds { get; set; } = Array.Empty<string>();
    }
    public sealed class SocialCommandResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        public string Response { get; set; }
        public ChatSendResult Chat { get; set; }
    }
    public sealed class CommunityMember
    {
        public string AccountId { get; set; }
        public string DisplayName { get; set; }
        public CommunityRole Role { get; set; }
    }
    public sealed class Community
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; } = "";
        public string OwnerId { get; set; }
        public bool IsOpen { get; set; }
        public List<CommunityMember> Members { get; set; } = new List<CommunityMember>();
        public List<string> BannedAccountIds { get; set; } = new List<string>();
    }
    public sealed class SocialGroupMember
    {
        public string AccountId { get; set; }
        public string DisplayName { get; set; }
        public string Role { get; set; } = "Member";
        public int Subgroup { get; set; }
    }
    public sealed class SocialGroup
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; } = "";
        public SocialGroupKind Kind { get; set; }
        public string LeaderId { get; set; }
        public List<SocialGroupMember> Members { get; set; } = new List<SocialGroupMember>();
    }
    public sealed class SocialInvite
    {
        public string Id { get; set; }
        public SocialInviteKind Kind { get; set; }
        public string ScopeId { get; set; }
        public string Name { get; set; }
        public string InviterId { get; set; }
        public string InviterName { get; set; }
        public string TargetId { get; set; }
        public string TargetName { get; set; }
        public DateTime ExpiresUtc { get; set; }
    }
    public sealed class SocialIgnore
    {
        public string AccountId { get; set; }
        public List<string> IgnoredAccountIds { get; set; } = new List<string>();
    }
    public sealed class SocialMute
    {
        public string AccountId { get; set; }
        public DateTime UntilUtc { get; set; }
        public string Reason { get; set; }
    }
    public sealed class ChatReport
    {
        public string Id { get; set; }
        public string ReporterId { get; set; }
        public long MessageId { get; set; }
        public DateTime CreatedUtc { get; set; }
        public string Reason { get; set; }
        public ChatMessage Message { get; set; }
    }
    /// <summary>Persistence data only; no CLR type activation, account credentials or engine references.
    /// Transient chat history is intentionally separate from persistent social membership.</summary>
    public sealed class SocialSnapshot
    {
        public int Version { get; set; } = 1;
        public List<Community> Communities { get; set; } = new List<Community>();
        public List<SocialGroup> Groups { get; set; } = new List<SocialGroup>();
        public List<SocialInvite> Invites { get; set; } = new List<SocialInvite>();
        public List<SocialIgnore> Ignores { get; set; } = new List<SocialIgnore>();
        public List<SocialMute> Mutes { get; set; } = new List<SocialMute>();
        public List<ChatReport> Reports { get; set; } = new List<ChatReport>();
    }
    public interface ISocialStore
    {
        SocialSnapshot Load();
        /// <summary>Persist atomically or throw. A failed save does not commit the service's proposed mutation.</summary>
        void Save(SocialSnapshot snapshot);
    }
    public sealed class SocialOptions
    {
        public Func<string, ChatAccount> LookupAccount { get; set; }
        public Func<string, ChatAccount> LookupAccountByName { get; set; }
        public Func<string, bool> IsModerator { get; set; } = _ => false;
        public Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;
        public int MaxTextCharacters { get; set; } = 512;
        public int MaxTextBytes { get; set; } = 2048;
        public int MaxAudience { get; set; } = 8192;
        public int MaxStoredMessages { get; set; } = 10000;
        public int MaxMessagesPerInbox { get; set; } = 200;
        public int MaxKnownAccounts { get; set; } = 100000;
        public int MessagesPerSecond { get; set; } = 2;
        public int MessageBurst { get; set; } = 5;
        public int MaxCommunities { get; set; } = 10000;
        public int MaxCommunitiesPerAccount { get; set; } = 10;
        public int MaxCommunityMembers { get; set; } = 1000;
        public int MaxGroups { get; set; } = 10000;
        public int PartySize { get; set; } = 5;
        public int RaidSize { get; set; } = 40;
        public int MaxInvites { get; set; } = 50000;
        public int MaxInvitesPerAccount { get; set; } = 100;
        public int MaxIgnoresPerAccount { get; set; } = 200;
        public TimeSpan InviteLifetime { get; set; } = TimeSpan.FromDays(7);
        public float ProximityRange { get; set; } = 30;
        public float YellRange { get; set; } = 100;
        public bool GlobalChannelsPerWorld { get; set; }
        public int MaxReports { get; set; } = 5000;
        internal SocialOptions Snapshot() => (SocialOptions)MemberwiseClone();
    }
    public sealed class SocialException : InvalidOperationException
    {
        public string Code { get; }
        public SocialException(string code, string message) : base(message) { Code = code; }
    }
}
