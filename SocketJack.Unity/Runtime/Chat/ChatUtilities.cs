using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;

namespace SocketJack.Multiplayer.Chat
{
    public sealed class ChatChannelStyle
    {
        public string Label { get; }
        public string ColorHex { get; }
        public string Command { get; }
        internal ChatChannelStyle(string label, string color, string command) { Label = label; ColorHex = color; Command = command; }
    }
    public static class MmoChatPresets
    {
        public static IReadOnlyDictionary<ChatChannel, ChatChannelStyle> Channels { get; } = new ReadOnlyDictionary<ChatChannel, ChatChannelStyle>(new Dictionary<ChatChannel, ChatChannelStyle>
        {
            [ChatChannel.Global] = new ChatChannelStyle("Global", "#E6C07B", "/g"),
            [ChatChannel.Trade] = new ChatChannelStyle("Trade", "#FFC0C0", "/trade"),
            [ChatChannel.Zone] = new ChatChannelStyle("Zone", "#F2D18E", "/z"),
            [ChatChannel.LookingForGroup] = new ChatChannelStyle("Looking for group", "#D6C7A1", "/lfg"),
            [ChatChannel.Party] = new ChatChannelStyle("Party", "#AAAAFF", "/p"),
            [ChatChannel.Raid] = new ChatChannelStyle("Raid", "#FF7F00", "/raid"),
            [ChatChannel.Community] = new ChatChannelStyle("Community", "#40FF40", "/c"),
            [ChatChannel.CommunityOfficers] = new ChatChannelStyle("Community officers", "#40C080", "/co"),
            [ChatChannel.Proximity] = new ChatChannelStyle("Say", "#FFFFFF", "/say"),
            [ChatChannel.Yell] = new ChatChannelStyle("Yell", "#FF4040", "/yell"),
            [ChatChannel.Whisper] = new ChatChannelStyle("Whisper", "#FF80FF", "/w"),
            [ChatChannel.Emote] = new ChatChannelStyle("Emote", "#FF8040", "/me"),
            [ChatChannel.System] = new ChatChannelStyle("System", "#FFFF00", ""),
            [ChatChannel.Combat] = new ChatChannelStyle("Combat", "#FF8040", "")
        });
    }
    /// <summary>Only server-registered link kinds and valid IDs become clickable metadata.
    /// Input uses [kind:id] tokens, never arbitrary rich-text/HTML. Render Text as plain escaped text.</summary>
    public sealed class ChatLinkRegistry
    {
        private readonly object gate = new object();
        private readonly Dictionary<string, Func<string, string>> resolvers = new Dictionary<string, Func<string, string>>(StringComparer.Ordinal);
        private static readonly Regex Tokens = new Regex(@"\[(?<kind>[a-z][a-z0-9_-]{0,15}):(?<id>[A-Za-z0-9_.-]{1,64})\]", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
        public void Register(string kind, Func<string, bool> validateId, Func<string, string> label)
        {
            if (kind == null || !Regex.IsMatch(kind, "^[a-z][a-z0-9_-]{0,15}$") || validateId == null || label == null) throw new ArgumentException("Invalid chat link registration.");
            lock (gate)
            {
                if (resolvers.Count >= 64 || resolvers.ContainsKey(kind)) throw new InvalidOperationException("Duplicate link kind or registry capacity reached.");
                resolvers.Add(kind, id => validateId(id) ? label(id) : null);
            }
        }
        internal List<ChatLink> Parse(string text)
        {
            var result = new List<ChatLink>();
            foreach (Match match in Tokens.Matches(text))
            {
                if (result.Count >= 16) throw new SocialException("too_many_links", "A message may contain at most 16 registered links.");
                Func<string, string> resolver; string kind = match.Groups["kind"].Value, id = match.Groups["id"].Value;
                lock (gate) resolvers.TryGetValue(kind, out resolver);
                string label;
                try { label = resolver == null ? null : resolver(id); } catch (Exception) { label = null; }
                if (string.IsNullOrWhiteSpace(label) || label.Length > 80 || label.Any(c => char.IsControl(c) || c == '<' || c == '>'))
                    throw new SocialException("invalid_link", "A chat link kind or ID is not registered/valid.");
                result.Add(new ChatLink { Kind = kind, Id = id, Label = label, Start = match.Index, Length = match.Length });
            }
            return result;
        }
    }
    public sealed class MemorySocialStore : ISocialStore
    {
        private readonly object gate = new object(); private SocialSnapshot snapshot = new SocialSnapshot();
        public SocialSnapshot Load() { lock (gate) return SocialCopies.Snapshot(snapshot); }
        public void Save(SocialSnapshot value) { if (value == null) throw new ArgumentNullException(nameof(value)); lock (gate) snapshot = SocialCopies.Snapshot(value); }
    }
    internal static class SocialCopies
    {
        internal static ChatMessage Message(ChatMessage x) => new ChatMessage { Id = x.Id, TimestampUtc = x.TimestampUtc, SenderId = x.SenderId, SenderName = x.SenderName,
            Channel = x.Channel, ScopeId = x.ScopeId, TargetId = x.TargetId, Text = x.Text, Links = x.Links.Select(l => new ChatLink { Kind = l.Kind, Id = l.Id, Label = l.Label, Start = l.Start, Length = l.Length }).ToList() };
        internal static Community Community(Community x) => new Community { Id = x.Id, Name = x.Name, Description = x.Description, OwnerId = x.OwnerId, IsOpen = x.IsOpen,
            Members = x.Members.Select(m => new CommunityMember { AccountId = m.AccountId, DisplayName = m.DisplayName, Role = m.Role }).ToList(), BannedAccountIds = new List<string>(x.BannedAccountIds) };
        internal static SocialGroup Group(SocialGroup x) => new SocialGroup { Id = x.Id, Name = x.Name, Description = x.Description, Kind = x.Kind, LeaderId = x.LeaderId,
            Members = x.Members.Select(m => new SocialGroupMember { AccountId = m.AccountId, DisplayName = m.DisplayName, Role = m.Role, Subgroup = m.Subgroup }).ToList() };
        internal static SocialInvite Invite(SocialInvite x) => new SocialInvite { Id = x.Id, Kind = x.Kind, ScopeId = x.ScopeId, Name = x.Name, InviterId = x.InviterId,
            InviterName = x.InviterName, TargetId = x.TargetId, TargetName = x.TargetName, ExpiresUtc = x.ExpiresUtc };
        internal static SocialSnapshot Snapshot(SocialSnapshot x) => new SocialSnapshot { Version = x.Version,
            Communities = (x.Communities ?? new List<Community>()).Select(Community).ToList(), Groups = (x.Groups ?? new List<SocialGroup>()).Select(Group).ToList(),
            Invites = (x.Invites ?? new List<SocialInvite>()).Select(Invite).ToList(),
            Ignores = (x.Ignores ?? new List<SocialIgnore>()).Select(i => new SocialIgnore { AccountId = i.AccountId, IgnoredAccountIds = new List<string>(i.IgnoredAccountIds) }).ToList(),
            Mutes = (x.Mutes ?? new List<SocialMute>()).Select(m => new SocialMute { AccountId = m.AccountId, UntilUtc = m.UntilUtc, Reason = m.Reason }).ToList(),
            Reports = (x.Reports ?? new List<ChatReport>()).Select(Report).ToList() };
        internal static ChatReport Report(ChatReport x) => new ChatReport { Id = x.Id, ReporterId = x.ReporterId, MessageId = x.MessageId,
            CreatedUtc = x.CreatedUtc, Reason = x.Reason, Message = Message(x.Message) };
    }
}
