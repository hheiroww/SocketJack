using System;
using System.Collections.Generic;
using System.Linq;

namespace SocketJack.Multiplayer.Chat
{
    /// <summary>Thread-safe game social domain. Call with an actor ID established by the host's authenticated
    /// session, not an ID trusted from a request body. Account authentication/storage belongs to the host.
    /// Persistent mutations save a proposed isolated snapshot before committing it in memory.</summary>
    public sealed partial class SocialService
    {
        private readonly object gate = new object();
        private readonly IChatMembershipProvider provider;
        private readonly ISocialStore store;
        private readonly SocialOptions options;
        private SocialSnapshot state;
        public ChatLinkRegistry Links { get; } = new ChatLinkRegistry();

        public SocialService(IChatMembershipProvider provider, ISocialStore store = null, SocialOptions options = null)
        {
            this.provider = provider ?? throw new ArgumentNullException(nameof(provider)); this.store = store ?? new MemorySocialStore();
            this.options = (options ?? new SocialOptions()).Snapshot(); ValidateOptions();
            var loaded = this.store.Load() ?? new SocialSnapshot(); ValidateSnapshot(loaded); state = SocialCopies.Snapshot(loaded);
        }
        public SocialSnapshot GetSnapshot() { lock (gate) return SocialCopies.Snapshot(state); }
        public IReadOnlyList<Community> GetCommunities(string actorId)
        { string id = Account(actorId).AccountId; lock (gate) return state.Communities.Where(c => c.Members.Any(m => Same(m.AccountId, id))).Select(SocialCopies.Community).ToArray(); }
        public IReadOnlyList<SocialGroup> GetGroups(string actorId)
        { string id = Account(actorId).AccountId; lock (gate) return state.Groups.Where(g => g.Members.Any(m => Same(m.AccountId, id))).Select(SocialCopies.Group).ToArray(); }
        public IReadOnlyList<SocialInvite> GetInvites(string actorId)
        { string id = Account(actorId).AccountId; DateTime now = Now(); lock (gate) return state.Invites.Where(i => Same(i.TargetId, id) && i.ExpiresUtc > now).Select(SocialCopies.Invite).ToArray(); }
        public IReadOnlyList<Community> FindCommunities(string actorId, string search = "", int max = 50)
        {
            Account(actorId); if (max < 1 || max > 100 || search == null || search.Length > 64) throw Error("invalid_search", "Invalid community search.");
            lock (gate) return state.Communities.Where(c => c.IsOpen && c.Name.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0).Take(max).Select(SocialCopies.Community).ToArray();
        }
        public Community CreateCommunity(string actorId, string name, string description = "", bool isOpen = false)
        {
            var actor = Account(actorId); name = Plain(name, 3, 64); description = Plain(description, 0, 1000);
            var result = Mutate(next =>
            {
                if (next.Communities.Count >= options.MaxCommunities || next.Communities.Count(c => c.Members.Any(m => Same(m.AccountId, actor.AccountId))) >= options.MaxCommunitiesPerAccount)
                    throw Error("community_capacity", "Community capacity reached.");
                if (next.Communities.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))) throw Error("name_taken", "Community name is already in use.");
                var community = new Community { Id = NewId(), Name = name, Description = description, OwnerId = actor.AccountId, IsOpen = isOpen };
                community.Members.Add(new CommunityMember { AccountId = actor.AccountId, DisplayName = actor.DisplayName, Role = CommunityRole.Owner }); next.Communities.Add(community); return community;
            }); return SocialCopies.Community(result);
        }
        public Community JoinCommunity(string actorId, string communityId)
        {
            var actor = Account(actorId);
            return SocialCopies.Community(Mutate(next => { var c = CommunityAt(next, communityId); if (!c.IsOpen) throw Error("invite_required", "This community requires an invitation."); AddCommunityMember(next, c, actor); return c; }));
        }
        public SocialInvite InviteCommunity(string actorId, string communityId, string targetId)
        {
            var actor = Account(actorId); var target = Account(targetId);
            return SocialCopies.Invite(Mutate(next =>
            {
                var c = CommunityAt(next, communityId); RequireOfficer(c, actor.AccountId);
                if (c.Members.Any(m => Same(m.AccountId, target.AccountId)) || c.BannedAccountIds.Any(id => Same(id, target.AccountId))) throw Error("invite_denied", "This account cannot be invited.");
                return AddInvite(next, SocialInviteKind.Community, c.Id, c.Name, actor, target);
            }));
        }
        public SocialInvite InviteToCommunity(string actorId, string communityId, string targetId) => InviteCommunity(actorId, communityId, targetId);
        public SocialInvite RespondInvitation(string actorId, string inviteId, bool accept)
        {
            var actor = Account(actorId);
            return SocialCopies.Invite(Mutate(next =>
            {
                var invite = next.Invites.SingleOrDefault(i => i.Id == Key(inviteId) && Same(i.TargetId, actor.AccountId));
                if (invite == null || invite.ExpiresUtc <= Now()) throw Error("invite_missing", "Invitation is unavailable or expired.");
                if (accept)
                {
                    if (invite.Kind == SocialInviteKind.Community) AddCommunityMember(next, CommunityAt(next, invite.ScopeId), actor);
                    else AddGroupMember(next, GroupAt(next, invite.ScopeId), actor);
                }
                next.Invites.Remove(invite); return invite;
            }));
        }
        public Community AcceptCommunityInvite(string actorId, string inviteId)
        { var invite = RequireInviteKind(actorId, inviteId, SocialInviteKind.Community); RespondInvitation(actorId, inviteId, true); return GetCommunities(actorId).Single(c => c.Id == invite.ScopeId); }
        public SocialGroup AcceptGroupInvite(string actorId, string inviteId)
        { var invite = RequireInviteKind(actorId, inviteId, SocialInviteKind.Group); RespondInvitation(actorId, inviteId, true); return GetGroups(actorId).Single(g => g.Id == invite.ScopeId); }
        public void DeclineInvite(string actorId, string inviteId) => RespondInvitation(actorId, inviteId, false);
        public Community ChangeCommunityRole(string actorId, string communityId, string targetId, CommunityRole role)
        {
            var actor = Account(actorId); var target = Account(targetId);
            if (!Enum.IsDefined(typeof(CommunityRole), role)) throw Error("invalid_role", "Unknown community role.");
            return SocialCopies.Community(Mutate(next =>
            {
                var c = CommunityAt(next, communityId); RequireOwner(c, actor.AccountId);
                var member = c.Members.SingleOrDefault(m => Same(m.AccountId, target.AccountId)) ?? throw Error("member_missing", "Account is not a community member.");
                if (role == CommunityRole.Owner)
                {
                    foreach (var previous in c.Members.Where(m => m.Role == CommunityRole.Owner)) previous.Role = CommunityRole.Officer;
                    c.OwnerId = target.AccountId; member.Role = role;
                }
                else { if (member.Role == CommunityRole.Owner) throw Error("owner_transfer_required", "Transfer community ownership before demoting its owner."); member.Role = role; }
                return c;
            }));
        }
        public Community SetCommunityRole(string actorId, string communityId, string targetId, CommunityRole role) => ChangeCommunityRole(actorId, communityId, targetId, role);
        public void RemoveCommunityMember(string actorId, string communityId, string targetId) => ModerateCommunity(actorId, communityId, targetId, false);
        public void KickCommunityMember(string actorId, string communityId, string targetId) => RemoveCommunityMember(actorId, communityId, targetId);
        public void BanCommunityMember(string actorId, string communityId, string targetId) => ModerateCommunity(actorId, communityId, targetId, true);
        public void UnbanCommunityMember(string actorId, string communityId, string targetId)
        { var actor = Account(actorId); string target = Key(targetId); Mutate(next => { var c = CommunityAt(next, communityId); RequireOfficer(c, actor.AccountId); c.BannedAccountIds.RemoveAll(id => Same(id, target)); return true; }); }
        public void LeaveCommunity(string actorId, string communityId)
        {
            var actor = Account(actorId);
            Mutate(next =>
            {
                var c = CommunityAt(next, communityId); var member = c.Members.SingleOrDefault(m => Same(m.AccountId, actor.AccountId)) ?? throw Error("not_member", "Account is not a community member.");
                if (member.Role == CommunityRole.Owner && c.Members.Count > 1) throw Error("owner_transfer_required", "Transfer ownership before leaving the community.");
                c.Members.Remove(member); if (c.Members.Count == 0) { next.Communities.Remove(c); next.Invites.RemoveAll(i => i.Kind == SocialInviteKind.Community && i.ScopeId == c.Id); } return true;
            });
        }
        public SocialGroup CreateGroup(string actorId, SocialGroupKind kind, string name = "")
        {
            var actor = Account(actorId); if (!Enum.IsDefined(typeof(SocialGroupKind), kind)) throw Error("invalid_group", "Unknown group kind.");
            name = Plain(string.IsNullOrWhiteSpace(name) ? actor.DisplayName + "'s " + kind : name, 1, 64);
            return SocialCopies.Group(Mutate(next =>
            {
                if (next.Groups.Count >= options.MaxGroups || next.Groups.Any(g => g.Kind == kind && g.Members.Any(m => Same(m.AccountId, actor.AccountId)))) throw Error("group_capacity", "Account already belongs to this group kind, or group capacity was reached.");
                var group = new SocialGroup { Id = NewId(), Name = name, Kind = kind, LeaderId = actor.AccountId };
                group.Members.Add(new SocialGroupMember { AccountId = actor.AccountId, DisplayName = actor.DisplayName, Role = "Leader", Subgroup = kind == SocialGroupKind.Raid ? 1 : 0 }); next.Groups.Add(group); return group;
            }));
        }
        public SocialInvite InviteGroup(string actorId, string groupId, string targetId)
        {
            var actor = Account(actorId); var target = Account(targetId);
            return SocialCopies.Invite(Mutate(next => { var g = GroupAt(next, groupId); RequireGroupManager(g, actor.AccountId); if (g.Members.Any(m => Same(m.AccountId, target.AccountId))) throw Error("already_member", "Account already belongs to this group."); return AddInvite(next, SocialInviteKind.Group, g.Id, g.Name, actor, target); }));
        }
        public SocialInvite InviteToGroup(string actorId, string groupId, string targetId) => InviteGroup(actorId, groupId, targetId);
        public SocialGroup TransferGroupLeadership(string actorId, string groupId, string targetId)
        {
            var actor = Account(actorId); string target = Key(targetId);
            return SocialCopies.Group(Mutate(next => { var g = GroupAt(next, groupId); RequireLeader(g, actor.AccountId);
                var member = g.Members.SingleOrDefault(m => Same(m.AccountId, target)) ?? throw Error("member_missing", "Account is not a group member.");
                foreach (var previous in g.Members.Where(m => m.Role == "Leader")) previous.Role = "Member"; g.LeaderId = member.AccountId; member.Role = "Leader"; return g; }));
        }
        public SocialGroup ChangeGroupRole(string actorId, string groupId, string targetId, string role)
        {
            if (role == "Leader") return TransferGroupLeadership(actorId, groupId, targetId);
            if (role != "Member" && role != "Assistant") throw Error("invalid_role", "Group role must be Leader, Assistant or Member.");
            var actor = Account(actorId); string target = Key(targetId);
            return SocialCopies.Group(Mutate(next => { var g = GroupAt(next, groupId); RequireLeader(g, actor.AccountId);
                if (role == "Assistant" && g.Kind != SocialGroupKind.Raid) throw Error("invalid_role", "Assistants are available for raids only.");
                var member = g.Members.SingleOrDefault(m => Same(m.AccountId, target)) ?? throw Error("member_missing", "Account is not a group member.");
                if (member.Role == "Leader") throw Error("leader_transfer_required", "Transfer group leadership before demoting its leader."); member.Role = role; return g; }));
        }
        public SocialGroup SetRaidSubgroup(string actorId, string groupId, string targetId, int subgroup)
        {
            var actor = Account(actorId); string target = Key(targetId); int groups = (options.RaidSize + options.PartySize - 1) / options.PartySize;
            return SocialCopies.Group(Mutate(next => { var g = GroupAt(next, groupId); RequireGroupManager(g, actor.AccountId);
                if (g.Kind != SocialGroupKind.Raid || subgroup < 1 || subgroup > groups) throw Error("invalid_subgroup", "Raid subgroup is outside the configured range.");
                var member = g.Members.SingleOrDefault(m => Same(m.AccountId, target)) ?? throw Error("member_missing", "Account is not a raid member.");
                if (g.Members.Count(m => m.Subgroup == subgroup && !Same(m.AccountId, target)) >= options.PartySize) throw Error("subgroup_full", "Raid subgroup is full."); member.Subgroup = subgroup; return g; }));
        }
        public void RemoveGroupMember(string actorId, string groupId, string targetId)
        {
            var actor = Account(actorId); string target = Key(targetId);
            Mutate(next => { var g = GroupAt(next, groupId); RequireLeader(g, actor.AccountId); if (Same(target, g.LeaderId)) throw Error("leader_leave", "Use LeaveGroup for the group leader."); g.Members.RemoveAll(m => Same(m.AccountId, target)); return true; });
        }
        public void LeaveGroup(string actorId, string groupId)
        {
            var actor = Account(actorId);
            Mutate(next => { var g = GroupAt(next, groupId); if (g.Members.RemoveAll(m => Same(m.AccountId, actor.AccountId)) == 0) throw Error("not_member", "Account is not a group member.");
                if (g.Members.Count == 0) { next.Groups.Remove(g); next.Invites.RemoveAll(i => i.Kind == SocialInviteKind.Group && i.ScopeId == g.Id); }
                else if (Same(g.LeaderId, actor.AccountId)) { g.LeaderId = g.Members[0].AccountId; g.Members[0].Role = "Leader"; } return true; });
        }
        public void SetIgnored(string actorId, string targetId, bool ignored)
        {
            var actor = Account(actorId); var target = Account(targetId); if (Same(actor.AccountId, target.AccountId)) throw Error("invalid_ignore", "Cannot ignore yourself.");
            Mutate(next => { var list = next.Ignores.SingleOrDefault(i => Same(i.AccountId, actor.AccountId));
                if (list == null) { if (!ignored) return true; if (next.Ignores.Count >= options.MaxKnownAccounts) throw Error("ignore_capacity", "Ignore-list capacity reached."); next.Ignores.Add(list = new SocialIgnore { AccountId = actor.AccountId }); }
                list.IgnoredAccountIds.RemoveAll(id => Same(id, target.AccountId)); if (ignored) { if (list.IgnoredAccountIds.Count >= options.MaxIgnoresPerAccount) throw Error("ignore_capacity", "Ignore-list capacity reached."); list.IgnoredAccountIds.Add(target.AccountId); }
                if (list.IgnoredAccountIds.Count == 0) next.Ignores.Remove(list); return true; });
        }
        public IReadOnlyList<string> GetIgnored(string actorId)
        { string id = Account(actorId).AccountId; lock (gate) return state.Ignores.SingleOrDefault(i => Same(i.AccountId, id))?.IgnoredAccountIds.ToArray() ?? Array.Empty<string>(); }
        private T Mutate<T>(Func<SocialSnapshot, T> change)
        {
            lock (gate)
            {
                var next = SocialCopies.Snapshot(state); DateTime now = Now(); next.Invites.RemoveAll(i => i.ExpiresUtc <= now); next.Mutes.RemoveAll(m => m.UntilUtc <= now);
                T result = change(next); ValidateSnapshot(next); store.Save(SocialCopies.Snapshot(next)); state = next; return result;
            }
        }
        private SocialInvite AddInvite(SocialSnapshot next, SocialInviteKind kind, string scope, string name, ChatAccount actor, ChatAccount target)
        {
            if (Ignored(next, target.AccountId, actor.AccountId)) throw Error("invite_denied", "Recipient is unavailable.");
            if (next.Invites.Any(i => i.Kind == kind && i.ScopeId == scope && Same(i.TargetId, target.AccountId))) throw Error("invite_exists", "An invitation is already pending.");
            if (next.Invites.Count >= options.MaxInvites || next.Invites.Count(i => Same(i.TargetId, target.AccountId)) >= options.MaxInvitesPerAccount) throw Error("invite_capacity", "Invitation capacity reached.");
            var invite = new SocialInvite { Id = NewId(), Kind = kind, ScopeId = scope, Name = name, InviterId = actor.AccountId, InviterName = actor.DisplayName,
                TargetId = target.AccountId, TargetName = target.DisplayName, ExpiresUtc = Now() + options.InviteLifetime }; next.Invites.Add(invite); return invite;
        }
        private SocialInvite RequireInviteKind(string actor, string id, SocialInviteKind kind)
        { var invite = GetInvites(actor).SingleOrDefault(i => i.Id == Key(id) && i.Kind == kind); return invite ?? throw Error("invite_missing", "Invitation is unavailable."); }
        private void AddCommunityMember(SocialSnapshot next, Community c, ChatAccount actor)
        {
            if (c.BannedAccountIds.Any(id => Same(id, actor.AccountId))) throw Error("community_banned", "Account is banned from this community.");
            if (c.Members.Any(m => Same(m.AccountId, actor.AccountId))) throw Error("already_member", "Account already belongs to this community.");
            if (c.Members.Count >= options.MaxCommunityMembers || next.Communities.Count(x => x.Members.Any(m => Same(m.AccountId, actor.AccountId))) >= options.MaxCommunitiesPerAccount) throw Error("community_capacity", "Community membership capacity reached.");
            c.Members.Add(new CommunityMember { AccountId = actor.AccountId, DisplayName = actor.DisplayName, Role = CommunityRole.Member });
        }
        private void AddGroupMember(SocialSnapshot next, SocialGroup g, ChatAccount actor)
        {
            if (g.Members.Count >= (g.Kind == SocialGroupKind.Party ? options.PartySize : options.RaidSize) || next.Groups.Any(x => x.Kind == g.Kind && x.Members.Any(m => Same(m.AccountId, actor.AccountId)))) throw Error("group_capacity", "Group is full or account already belongs to this group kind.");
            int subgroup = g.Kind == SocialGroupKind.Raid ? Enumerable.Range(1, (options.RaidSize + options.PartySize - 1) / options.PartySize).First(index => g.Members.Count(m => m.Subgroup == index) < options.PartySize) : 0;
            g.Members.Add(new SocialGroupMember { AccountId = actor.AccountId, DisplayName = actor.DisplayName, Role = "Member", Subgroup = subgroup });
        }
        private void ModerateCommunity(string actorId, string cid, string targetId, bool ban)
        {
            var actor = Account(actorId); var target = Account(targetId);
            Mutate(next => { var c = CommunityAt(next, cid); RequireOfficer(c, actor.AccountId); var member = c.Members.SingleOrDefault(m => Same(m.AccountId, target.AccountId));
                if (member != null && (member.Role == CommunityRole.Owner || (member.Role == CommunityRole.Officer && !Same(actor.AccountId, c.OwnerId) && !Moderator(actor.AccountId)))) throw Error("role_denied", "Cannot moderate this role.");
                c.Members.RemoveAll(m => Same(m.AccountId, target.AccountId)); next.Invites.RemoveAll(i => i.Kind == SocialInviteKind.Community && i.ScopeId == c.Id && Same(i.TargetId, target.AccountId));
                if (ban && !c.BannedAccountIds.Any(id => Same(id, target.AccountId))) { if (c.BannedAccountIds.Count >= options.MaxCommunityMembers) throw Error("ban_capacity", "Community ban capacity reached."); c.BannedAccountIds.Add(target.AccountId); } return true; });
        }
        private void RequireOwner(Community c, string actor) { if (!Same(c.OwnerId, actor) && !Moderator(actor)) throw Error("owner_required", "Community ownership is required."); }
        private void RequireOfficer(Community c, string actor)
        { if (!Moderator(actor) && !c.Members.Any(m => Same(m.AccountId, actor) && m.Role != CommunityRole.Member)) throw Error("officer_required", "Community officer permission is required."); }
        private void RequireLeader(SocialGroup g, string actor) { if (!Same(g.LeaderId, actor) && !Moderator(actor)) throw Error("leader_required", "Group leadership is required."); }
        private void RequireGroupManager(SocialGroup g, string actor)
        { if (!Same(g.LeaderId, actor) && !Moderator(actor) && !(g.Kind == SocialGroupKind.Raid && g.Members.Any(m => Same(m.AccountId, actor) && m.Role == "Assistant"))) throw Error("leader_required", "Group leadership or raid assistant permission is required."); }
        private bool Moderator(string actor) => options.IsModerator != null && options.IsModerator(actor);
        private static bool Ignored(SocialSnapshot next, string receiver, string sender) => next.Ignores.Any(i => Same(i.AccountId, receiver) && i.IgnoredAccountIds.Any(id => Same(id, sender)));
        private static Community CommunityAt(SocialSnapshot next, string id) => next.Communities.SingleOrDefault(c => c.Id == Key(id)) ?? throw Error("community_missing", "Community does not exist.");
        private static SocialGroup GroupAt(SocialSnapshot next, string id) => next.Groups.SingleOrDefault(g => g.Id == Key(id)) ?? throw Error("group_missing", "Group does not exist.");
        private ChatAccount Account(string id)
        {
            string canonical = Key(id); ChatAccount account = options.LookupAccount == null ? null : options.LookupAccount(canonical);
            if (account == null && provider.TryGetPresence(canonical, out var presence) && presence != null) account = new ChatAccount { AccountId = presence.AccountId, DisplayName = presence.DisplayName };
            if (account == null || !Same(account.AccountId, canonical)) throw Error("account_missing", "Account is unavailable.");
            return new ChatAccount { AccountId = canonical, DisplayName = Plain(account.DisplayName, 1, 64) };
        }
        private DateTime Now() { DateTime value = options.UtcNow(); return value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime(); }
        private static string Key(string value) { if (!Guid.TryParse(value, out var id) || id == Guid.Empty) throw Error("invalid_id", "A valid account/social ID is required."); return id.ToString("N"); }
        private static bool Same(string a, string b) => Guid.TryParse(a, out var x) && Guid.TryParse(b, out var y) && x == y;
        private static string NewId() => Guid.NewGuid().ToString("N");
        private static SocialException Error(string code, string message) => new SocialException(code, message);
        private static string Plain(string value, int min, int max)
        { value = value?.Trim(); if (value == null || value.Length < min || value.Length > max || value.Any(c => char.IsControl(c) || c == '<' || c == '>')) throw Error("invalid_text", "Text is missing, too long or contains unsupported control/markup characters."); return value; }
        private void ValidateOptions()
        {
            if (options.UtcNow == null || options.MaxTextCharacters < 1 || options.MaxTextCharacters > 4096 || options.MaxTextBytes < options.MaxTextCharacters || options.MaxTextBytes > 16384 ||
                options.MaxAudience < 1 || options.MaxAudience > 65535 || options.MaxStoredMessages < 1 || options.MaxMessagesPerInbox < 1 || options.MaxKnownAccounts < 1 ||
                options.MessagesPerSecond < 1 || options.MessageBurst < 1 || options.MaxCommunities < 1 || options.MaxCommunitiesPerAccount < 1 || options.MaxCommunityMembers < 1 ||
                options.MaxGroups < 1 || options.PartySize < 1 || options.RaidSize < options.PartySize || options.MaxInvites < 1 || options.MaxInvitesPerAccount < 1 || options.MaxIgnoresPerAccount < 1 ||
                options.InviteLifetime <= TimeSpan.Zero || options.InviteLifetime > TimeSpan.FromDays(30) || !Finite(options.ProximityRange) || !Finite(options.YellRange) || options.ProximityRange <= 0 || options.YellRange < options.ProximityRange || options.MaxReports < 1)
                throw new ArgumentOutOfRangeException(nameof(SocialOptions));
        }
        private void ValidateSnapshot(SocialSnapshot value)
        {
            if (value.Version != 1 || value.Communities == null || value.Groups == null || value.Invites == null || value.Ignores == null || value.Mutes == null || value.Reports == null ||
                value.Communities.Count > options.MaxCommunities || value.Groups.Count > options.MaxGroups || value.Invites.Count > options.MaxInvites || value.Ignores.Count > options.MaxKnownAccounts || value.Mutes.Count > options.MaxKnownAccounts || value.Reports.Count > options.MaxReports)
                throw Error("invalid_store", "Social snapshot version or bounds are invalid.");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in value.Communities)
            {
                if (c == null || !ids.Add(Key(c.Id)) || c.Members == null || c.BannedAccountIds == null || c.Members.Count < 1 || c.Members.Count > options.MaxCommunityMembers || c.BannedAccountIds.Count > options.MaxCommunityMembers) throw Error("invalid_store", "Community snapshot bounds are invalid.");
                Plain(c.Name, 3, 64); Plain(c.Description, 0, 1000); Key(c.OwnerId);
                var members = new HashSet<string>(); foreach (var m in c.Members) { if (m == null || !members.Add(Key(m.AccountId)) || !Enum.IsDefined(typeof(CommunityRole), m.Role)) throw Error("invalid_store", "Invalid community member."); Plain(m.DisplayName, 1, 64); }
                if (c.Members.Count(m => m.Role == CommunityRole.Owner && Same(m.AccountId, c.OwnerId)) != 1 || c.Members.Count(m => m.Role == CommunityRole.Owner) != 1) throw Error("invalid_store", "Community ownership is inconsistent.");
                foreach (var id in c.BannedAccountIds) Key(id);
            }
            foreach (var g in value.Groups)
            {
                if (g == null || !ids.Add(Key(g.Id)) || !Enum.IsDefined(typeof(SocialGroupKind), g.Kind) || g.Members == null || g.Members.Count < 1 || g.Members.Count > (g.Kind == SocialGroupKind.Party ? options.PartySize : options.RaidSize)) throw Error("invalid_store", "Invalid group snapshot.");
                Plain(g.Name, 1, 64); Plain(g.Description, 0, 1000); Key(g.LeaderId); var members = new HashSet<string>();
                foreach (var m in g.Members) { if (m == null || !members.Add(Key(m.AccountId)) || (m.Role != "Leader" && m.Role != "Assistant" && m.Role != "Member") ||
                    (g.Kind == SocialGroupKind.Party && (m.Role == "Assistant" || m.Subgroup != 0)) ||
                    (g.Kind == SocialGroupKind.Raid && (m.Subgroup < 1 || m.Subgroup > (options.RaidSize + options.PartySize - 1) / options.PartySize))) throw Error("invalid_store", "Invalid group member."); Plain(m.DisplayName, 1, 64); }
                if (g.Members.Count(m => Same(m.AccountId, g.LeaderId) && m.Role == "Leader") != 1 || g.Members.Count(m => m.Role == "Leader") != 1) throw Error("invalid_store", "Group leadership is inconsistent.");
                if (g.Kind == SocialGroupKind.Raid && g.Members.GroupBy(m => m.Subgroup).Any(group => group.Count() > options.PartySize)) throw Error("invalid_store", "Raid subgroup capacity exceeded.");
            }
            foreach (var invite in value.Invites) { if (invite == null || !ids.Add(Key(invite.Id)) || !Enum.IsDefined(typeof(SocialInviteKind), invite.Kind)) throw Error("invalid_store", "Invalid invitation."); Key(invite.ScopeId); Key(invite.InviterId); Key(invite.TargetId); Plain(invite.Name, 1, 64); Plain(invite.InviterName, 1, 64); Plain(invite.TargetName, 1, 64); }
            foreach (var ignore in value.Ignores) { Key(ignore.AccountId); if (ignore.IgnoredAccountIds == null || ignore.IgnoredAccountIds.Count > options.MaxIgnoresPerAccount) throw Error("invalid_store", "Invalid ignore list."); foreach (var id in ignore.IgnoredAccountIds) Key(id); }
            foreach (var mute in value.Mutes) { Key(mute.AccountId); Plain(mute.Reason ?? "", 0, 512); }
            foreach (var report in value.Reports) { Key(report.Id); Key(report.ReporterId); Plain(report.Reason, 1, 512); if (report.Message == null) throw Error("invalid_store", "Invalid chat report."); }
        }
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
