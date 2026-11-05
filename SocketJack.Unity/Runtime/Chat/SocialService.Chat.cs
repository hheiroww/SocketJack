using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;

namespace SocketJack.Multiplayer.Chat
{
    public sealed partial class SocialService
    {
        private sealed class Rate
        {
            internal double Tokens;
            internal DateTime Updated;
        }
        private readonly Dictionary<string, Rate> rates = new Dictionary<string, Rate>(StringComparer.Ordinal);
        private readonly Dictionary<long, ChatMessage> messages = new Dictionary<long, ChatMessage>();
        private readonly Queue<long> messageOrder = new Queue<long>();
        private readonly Dictionary<string, Queue<long>> inboxes = new Dictionary<string, Queue<long>>(StringComparer.Ordinal);
        private long nextMessageId;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        /// <summary>Resolve channel authorization for an authenticated non-text feature, such as voice.
        /// This does not create a chat message or consume text rate limits. The caller must enforce its own
        /// bandwidth/rate budget and obtain actorId from authenticated server state.</summary>
        public IReadOnlyList<string> ResolveAudience(string actorId, ChatChannel channel, string scopeId = null, string targetId = null)
        {
            if (!Enum.IsDefined(typeof(ChatChannel), channel) || channel == ChatChannel.System) throw Error("channel_denied", "This channel is unavailable.");
            if (string.IsNullOrWhiteSpace(scopeId)) scopeId = null; if (string.IsNullOrWhiteSpace(targetId)) targetId = null;
            var actor = Account(actorId); var source = Presence(actor.AccountId) ?? throw Error("offline", "An online session is required.");
            lock (gate)
            {
                if (state.Mutes.Any(mute => Same(mute.AccountId, actor.AccountId) && mute.UntilUtc > Now())) throw Error("muted", "Communication is temporarily muted for this account.");
                return Route(actor, source, channel, scopeId, targetId, out _).ToArray();
            }
        }

        /// <summary>Route a bounded player message using trusted presence and persistent membership.
        /// The host must establish actorId from authentication; scopeId/targetId do not grant permission.</summary>
        public ChatSendResult Send(string actorId, ChatChannel channel, string text, string scopeId = null, string targetId = null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(scopeId)) scopeId = null;
                if (string.IsNullOrWhiteSpace(targetId)) targetId = null;
                if (!Enum.IsDefined(typeof(ChatChannel), channel) || channel == ChatChannel.System) throw Error("channel_denied", "This channel cannot be sent by a player.");
                var actor = Account(actorId); var source = Presence(actor.AccountId) ?? throw Error("offline", "An online session is required for chat.");
                text = ChatText(text); var links = Links.Parse(text); DateTime now = Now();
                lock (gate)
                {
                    if (state.Mutes.Any(m => Same(m.AccountId, actor.AccountId) && m.UntilUtc > now)) throw Error("muted", "Chat is temporarily muted for this account.");
                    string scope; var recipients = Route(actor, source, channel, scopeId, targetId, out scope);
                    if (recipients.Count == 0) throw Error("recipient_unavailable", "Recipient is unavailable.");
                    CheckInboxCapacity(recipients); ConsumeRate(actor.AccountId, now);
                    var message = new ChatMessage { Id = NextMessageId(now), TimestampUtc = now, SenderId = actor.AccountId, SenderName = actor.DisplayName,
                        Channel = channel, ScopeId = scope, TargetId = channel == ChatChannel.Whisper ? scope : null, Text = text, Links = links };
                    Deliver(message, recipients);
                    return new ChatSendResult { Success = true, Message = SocialCopies.Message(message), RecipientIds = recipients.ToArray() };
                }
            }
            catch (SocialException error) { return new ChatSendResult { Success = false, Error = error.Message }; }
            catch (EncoderFallbackException) { return new ChatSendResult { Success = false, Error = "Text contains invalid Unicode." }; }
        }
        /// <summary>Trusted server operation. Never expose as an unauthenticated player endpoint.</summary>
        public ChatSendResult SendSystem(string text, IEnumerable<string> recipientIds, string scopeId = "")
        {
            text = ChatText(text); if (recipientIds == null) throw new ArgumentNullException(nameof(recipientIds));
            var recipients = recipientIds.Take(options.MaxAudience + 1).Select(id => Account(id).AccountId).Distinct(StringComparer.Ordinal).ToList();
            if (recipients.Count == 0 || recipients.Count > options.MaxAudience) throw Error("audience_capacity", "System audience is empty or too large.");
            lock (gate)
            {
                CheckInboxCapacity(recipients); DateTime now = Now(); var message = new ChatMessage { Id = NextMessageId(now), TimestampUtc = now,
                    SenderId = Guid.Empty.ToString("N"), SenderName = "System", Channel = ChatChannel.System, ScopeId = Plain(scopeId ?? "", 0, 128), Text = text };
                Deliver(message, recipients); return new ChatSendResult { Success = true, Message = SocialCopies.Message(message), RecipientIds = recipients.ToArray() };
            }
        }
        public IReadOnlyList<ChatMessage> ReadMessages(string accountId, long afterId = 0, int max = 100)
        {
            string id = Account(accountId).AccountId; if (afterId < 0 || max < 1 || max > options.MaxMessagesPerInbox) throw Error("invalid_history", "Invalid history cursor or page size.");
            lock (gate)
            {
                if (!inboxes.TryGetValue(id, out var inbox)) return Array.Empty<ChatMessage>();
                return inbox.Where(key => key > afterId && messages.ContainsKey(key)).Take(max).Select(key => SocialCopies.Message(messages[key])).ToArray();
            }
        }
        public void SetChatMuted(string actorId, string targetId, DateTime untilUtc, string reason = "")
        {
            var actor = Account(actorId); var target = Account(targetId); if (!Moderator(actor.AccountId)) throw Error("moderator_required", "Chat moderation permission is required.");
            reason = Plain(reason, 0, 512); DateTime now = Now(); untilUtc = untilUtc.Kind == DateTimeKind.Utc ? untilUtc : untilUtc.ToUniversalTime();
            if (untilUtc > now.AddDays(365)) throw Error("invalid_mute", "Mute duration exceeds the configured one-year safety bound.");
            Mutate(next => { next.Mutes.RemoveAll(m => Same(m.AccountId, target.AccountId)); if (untilUtc > now) { if (next.Mutes.Count >= options.MaxKnownAccounts) throw Error("mute_capacity", "Mute capacity reached."); next.Mutes.Add(new SocialMute { AccountId = target.AccountId, UntilUtc = untilUtc, Reason = reason }); } return true; });
        }
        public ChatReport ReportMessage(string actorId, long messageId, string reason)
        {
            var actor = Account(actorId); reason = Plain(reason, 1, 512);
            lock (gate)
            {
                if (!inboxes.TryGetValue(actor.AccountId, out var inbox) || !inbox.Contains(messageId) || !messages.TryGetValue(messageId, out var message)) throw Error("message_missing", "Only a retained message delivered to this account can be reported.");
                return SocialCopies.Report(Mutate(next =>
                {
                    if (next.Reports.Any(r => r.MessageId == messageId && Same(r.ReporterId, actor.AccountId))) throw Error("already_reported", "This message was already reported.");
                    if (next.Reports.Count >= options.MaxReports) throw Error("report_capacity", "Report capacity reached.");
                    var report = new ChatReport { Id = NewId(), ReporterId = actor.AccountId, MessageId = messageId, CreatedUtc = Now(), Reason = reason, Message = SocialCopies.Message(message) }; next.Reports.Add(report); return report;
                }));
            }
        }
        public IReadOnlyList<ChatReport> GetReports(string actorId, int max = 100)
        {
            var actor = Account(actorId); if (!Moderator(actor.AccountId)) throw Error("moderator_required", "Chat moderation permission is required.");
            if (max < 1 || max > 1000) throw Error("invalid_page", "Invalid report page size."); lock (gate) return state.Reports.Take(max).Select(SocialCopies.Report).ToArray();
        }
        public SocialCommandResult ExecuteSlash(string actorId, string text, string scopeId = null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(text)) throw Error("empty_command", "Enter a chat message or command.");
                text = text.Trim(); if (!text.StartsWith("/", StringComparison.Ordinal)) return CommandChat(Send(actorId, ChatChannel.Proximity, text));
                string command, body; SplitFirst(text, out command, out body); command = command.ToLowerInvariant();
                ChatChannel channel;
                switch (command)
                {
                    case "/g": case "/global": channel = ChatChannel.Global; break;
                    case "/trade": case "/t": channel = ChatChannel.Trade; break;
                    case "/z": case "/zone": channel = ChatChannel.Zone; break;
                    case "/lfg": channel = ChatChannel.LookingForGroup; break;
                    case "/p": case "/party": channel = ChatChannel.Party; break;
                    case "/raid": case "/r": channel = ChatChannel.Raid; break;
                    case "/c": case "/community": case "/guild": channel = ChatChannel.Community; break;
                    case "/co": case "/officers": channel = ChatChannel.CommunityOfficers; break;
                    case "/say": case "/s": channel = ChatChannel.Proximity; break;
                    case "/yell": case "/y": channel = ChatChannel.Yell; break;
                    case "/me": case "/emote": channel = ChatChannel.Emote; break;
                    case "/w": case "/whisper":
                        string target, message; SplitFirst(body, out target, out message); return CommandChat(Send(actorId, ChatChannel.Whisper, message, targetId: LookupTarget(target).AccountId));
                    case "/ignore": case "/unignore":
                        SetIgnored(actorId, LookupTarget(body).AccountId, command == "/ignore"); return new SocialCommandResult { Success = true, Response = command == "/ignore" ? "Account ignored." : "Account no longer ignored." };
                    case "/invite":
                        var account = LookupTarget(body); var group = GetGroups(actorId).FirstOrDefault(g => g.Kind == SocialGroupKind.Party) ?? CreateGroup(actorId, SocialGroupKind.Party);
                        InviteGroup(actorId, group.Id, account.AccountId); return new SocialCommandResult { Success = true, Response = "Party invitation sent." };
                    case "/join":
                        JoinCommunity(actorId, body); return new SocialCommandResult { Success = true, Response = "Joined community." };
                    case "/help":
                        return new SocialCommandResult { Success = true, Response = "/g /trade /z /lfg /p /raid /c /co /say /yell /w account message /me /ignore /unignore /invite /join" };
                    default: throw Error("unknown_command", "Unknown chat command. Use /help.");
                }
                return CommandChat(Send(actorId, channel, body, scopeId));
            }
            catch (SocialException error) { return new SocialCommandResult { Success = false, Error = error.Message }; }
        }
        private List<string> Route(ChatAccount actor, ChatPresence source, ChatChannel channel, string scopeId, string targetId, out string scope)
        {
            IEnumerable<ChatPresence> audience; scope = "";
            if (channel == ChatChannel.Whisper)
            {
                var target = Account(targetId ?? scopeId); var recipient = Presence(target.AccountId);
                if (recipient == null || Ignored(state, target.AccountId, actor.AccountId)) throw Error("recipient_unavailable", "Recipient is unavailable.");
                scope = target.AccountId; audience = new[] { source, recipient };
            }
            else if (channel == ChatChannel.Party || channel == ChatChannel.Raid)
            {
                var kind = channel == ChatChannel.Party ? SocialGroupKind.Party : SocialGroupKind.Raid;
                var memberships = state.Groups.Where(g => g.Kind == kind && g.Members.Any(m => Same(m.AccountId, actor.AccountId))).ToArray();
                var group = scopeId == null ? memberships.SingleOrDefault() : memberships.SingleOrDefault(g => g.Id == Key(scopeId));
                if (group == null) throw Error("not_member", "Account does not belong to the requested group."); scope = group.Id;
                audience = group.Members.Select(m => Presence(m.AccountId)).Where(p => p != null);
            }
            else if (channel == ChatChannel.Community || channel == ChatChannel.CommunityOfficers)
            {
                var memberships = state.Communities.Where(c => c.Members.Any(m => Same(m.AccountId, actor.AccountId))).ToArray();
                if (scopeId == null && memberships.Length != 1) throw Error("scope_required", "Select a community for this channel.");
                var community = scopeId == null ? memberships.SingleOrDefault() : memberships.SingleOrDefault(c => c.Id == Key(scopeId));
                if (community == null) throw Error("not_member", "Account does not belong to the requested community.");
                if (channel == ChatChannel.CommunityOfficers && !community.Members.Any(m => Same(m.AccountId, actor.AccountId) && m.Role != CommunityRole.Member)) throw Error("officer_required", "Officer chat requires an officer role.");
                scope = community.Id; audience = community.Members.Where(m => channel != ChatChannel.CommunityOfficers || m.Role != CommunityRole.Member).Select(m => Presence(m.AccountId)).Where(p => p != null);
            }
            else if (channel == ChatChannel.Zone || channel == ChatChannel.Proximity || channel == ChatChannel.Yell || channel == ChatChannel.Emote)
            {
                if (!ValidGamePresence(source)) throw Error("game_presence_required", "A validated game presence is required for zone/proximity chat.");
                float range = channel == ChatChannel.Yell ? options.YellRange : options.ProximityRange; bool spatial = channel != ChatChannel.Zone;
                audience = spatial && provider is IChatSpatialMembershipProvider indexed ? indexed.GetNearbyPresences(source.WorldId, source.RoomId, source.Position, range) : provider.GetOnlinePresences();
                audience = (audience ?? Enumerable.Empty<ChatPresence>()).Where(p => ValidGamePresence(p) && p.WorldId == source.WorldId && p.RoomId == source.RoomId && (!spatial || Vector3.DistanceSquared(p.Position, source.Position) <= range * range));
                scope = source.WorldId + ":" + source.RoomId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            else audience = (provider.GetOnlinePresences() ?? Enumerable.Empty<ChatPresence>()).Where(p => !options.GlobalChannelsPerWorld || (p != null && p.WorldId == source.WorldId));
            var recipients = new HashSet<string>(StringComparer.Ordinal);
            int scanned = 0;
            foreach (var presence in audience)
            {
                if (++scanned > options.MaxKnownAccounts) throw Error("audience_capacity", "Presence scan capacity reached.");
                if (presence == null || !presence.IsOnline) continue; string id = Key(presence.AccountId);
                if (!Ignored(state, id, actor.AccountId)) recipients.Add(id);
                if (recipients.Count > options.MaxAudience) throw Error("audience_capacity", "Channel audience exceeds its configured capacity.");
            }
            recipients.Add(actor.AccountId); if (recipients.Count > options.MaxAudience) throw Error("audience_capacity", "Channel audience exceeds its configured capacity.");
            return recipients.OrderBy(id => id, StringComparer.Ordinal).ToList();
        }
        private ChatPresence Presence(string accountId)
        {
            if (!provider.TryGetPresence(accountId, out var value) || value == null || !value.IsOnline || !Same(value.AccountId, accountId)) return null;
            return new ChatPresence { AccountId = Key(value.AccountId), DisplayName = value.DisplayName, IsOnline = true, HasGamePresence = value.HasGamePresence,
                WorldId = value.WorldId ?? "", RoomId = value.RoomId, Position = value.Position };
        }
        private static bool ValidGamePresence(ChatPresence value) => value != null && value.IsOnline && value.HasGamePresence && value.WorldId != null && value.WorldId.Length <= 64 &&
            Finite(value.Position.X) && Finite(value.Position.Y) && Finite(value.Position.Z);
        private void CheckInboxCapacity(List<string> recipients)
        { if (recipients.Count(id => !inboxes.ContainsKey(id)) > options.MaxKnownAccounts - inboxes.Count) throw Error("inbox_capacity", "Chat inbox capacity reached."); }
        private void ConsumeRate(string accountId, DateTime now)
        {
            if (!rates.TryGetValue(accountId, out var rate))
            {
                if (rates.Count >= options.MaxKnownAccounts)
                    foreach (string id in rates.Where(r => now - r.Value.Updated > TimeSpan.FromMinutes(10)).Take(256).Select(r => r.Key).ToArray()) rates.Remove(id);
                if (rates.Count >= options.MaxKnownAccounts) throw Error("rate_capacity", "Chat rate-tracker capacity reached.");
                rates.Add(accountId, rate = new Rate { Tokens = options.MessageBurst, Updated = now });
            }
            rate.Tokens = Math.Min(options.MessageBurst, rate.Tokens + Math.Max(0, (now - rate.Updated).TotalSeconds) * options.MessagesPerSecond); rate.Updated = now;
            if (rate.Tokens < 1) throw Error("rate_limited", "Messages are being sent too quickly."); rate.Tokens--;
        }
        private long NextMessageId(DateTime now)
        { long clock = checked(new DateTimeOffset(now).ToUnixTimeMilliseconds() * 1024); nextMessageId = Math.Max(checked(nextMessageId + 1), Math.Max(1, clock)); return nextMessageId; }
        private void Deliver(ChatMessage message, List<string> recipients)
        {
            messages.Add(message.Id, message); messageOrder.Enqueue(message.Id);
            while (messageOrder.Count > options.MaxStoredMessages) messages.Remove(messageOrder.Dequeue());
            foreach (string recipient in recipients)
            {
                if (!inboxes.TryGetValue(recipient, out var inbox)) inboxes.Add(recipient, inbox = new Queue<long>());
                inbox.Enqueue(message.Id); while (inbox.Count > options.MaxMessagesPerInbox) inbox.Dequeue();
            }
        }
        private string ChatText(string text)
        { text = Plain(text, 1, options.MaxTextCharacters); if (Utf8.GetByteCount(text) > options.MaxTextBytes) throw Error("text_too_large", "Chat text exceeds the byte limit."); return text; }
        private ChatAccount LookupTarget(string value)
        {
            if (Guid.TryParse(value, out _)) return Account(value);
            var account = options.LookupAccountByName?.Invoke(Plain(value, 1, 64));
            if (account == null)
            {
                var matches = (provider.GetOnlinePresences() ?? Enumerable.Empty<ChatPresence>()).Take(options.MaxKnownAccounts + 1).Where(p => p != null && string.Equals(p.DisplayName, value, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
                if (matches.Length != 1) throw Error("account_missing", "Account name is unavailable or ambiguous."); return Account(matches[0].AccountId);
            }
            return Account(account.AccountId);
        }
        private static void SplitFirst(string value, out string first, out string remainder)
        { int space = value.IndexOf(' '); if (space < 0) { first = value; remainder = ""; } else { first = value.Substring(0, space); remainder = value.Substring(space + 1).Trim(); } }
        private static SocialCommandResult CommandChat(ChatSendResult result) => new SocialCommandResult { Success = result.Success, Error = result.Error, Chat = result };
    }
}
