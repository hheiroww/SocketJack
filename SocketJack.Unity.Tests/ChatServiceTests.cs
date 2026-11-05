using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Multiplayer.Chat;

namespace SocketJack.Multiplayer.Tests
{
    [TestClass]
    public class ChatServiceTests
    {
        private sealed class Directory : IChatMembershipProvider
        {
            internal readonly Dictionary<string, ChatPresence> Accounts = new Dictionary<string, ChatPresence>();
            internal string Add(string name, int room = 0, Vector3? position = null, bool game = true)
            { string id = Guid.NewGuid().ToString("N"); Accounts[id] = new ChatPresence { AccountId = id, DisplayName = name, IsOnline = true, HasGamePresence = game, WorldId = game ? "world" : "", RoomId = room, Position = position ?? Vector3.Zero }; return id; }
            public bool TryGetPresence(string accountId, out ChatPresence presence) => Accounts.TryGetValue(accountId, out presence);
            public IEnumerable<ChatPresence> GetOnlinePresences() => Accounts.Values.Where(p => p.IsOnline).ToArray();
            internal ChatAccount Lookup(string id) => Accounts.TryGetValue(id, out var p) ? new ChatAccount { AccountId = p.AccountId, DisplayName = p.DisplayName } : null;
        }
        private sealed class Fixture
        {
            internal readonly Directory Directory = new Directory();
            internal readonly MemorySocialStore Store = new MemorySocialStore();
            internal readonly string Alice, Bob, Cara, Far, Web;
            internal DateTime Now = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
            internal SocialOptions Options;
            internal SocialService Service;
            internal Fixture()
            {
                Alice = Directory.Add("Alice"); Bob = Directory.Add("Bob", position: new Vector3(5, 0, 0)); Cara = Directory.Add("Cara", 1);
                Far = Directory.Add("Far", position: new Vector3(80, 0, 0)); Web = Directory.Add("Web", game: false);
                Options = new SocialOptions { LookupAccount = Directory.Lookup, LookupAccountByName = name => Directory.Accounts.Values.Where(p => p.DisplayName == name).Select(p => Directory.Lookup(p.AccountId)).SingleOrDefault(),
                    IsModerator = id => id == Alice, UtcNow = () => Now, MessagesPerSecond = 100, MessageBurst = 100 };
                Service = new SocialService(Directory, Store, Options);
            }
        }
        [TestMethod]
        public void ChannelsUseTrustedWorldRoomDistanceAndWebCannotForgeGamePresence()
        {
            var f = new Fixture(); var global = f.Service.Send(f.Web, ChatChannel.Global, "hello"); Assert.IsTrue(global.Success); Assert.AreEqual(5, global.RecipientIds.Length);
            var zone = f.Service.Send(f.Alice, ChatChannel.Zone, "zone", scopeId: "room1"); CollectionAssert.AreEquivalent(new[] { f.Alice, f.Bob, f.Far }, zone.RecipientIds);
            var say = f.Service.Send(f.Alice, ChatChannel.Proximity, "near"); CollectionAssert.AreEquivalent(new[] { f.Alice, f.Bob }, say.RecipientIds);
            var yell = f.Service.Send(f.Alice, ChatChannel.Yell, "far"); CollectionAssert.AreEquivalent(new[] { f.Alice, f.Bob, f.Far }, yell.RecipientIds);
            Assert.IsFalse(f.Service.Send(f.Web, ChatChannel.Proximity, "spoof", scopeId: "world:0").Success);
            Assert.IsFalse(f.Service.Send(f.Bob, ChatChannel.System, "forged admin").Success);
            Assert.IsFalse(f.Service.ReadMessages(f.Cara).Any(m => m.Channel == ChatChannel.Zone));
        }
        [TestMethod]
        public void CombatAndSystemAreOnlySentByTrustedServerOperations()
        {
            var f = new Fixture(); Assert.IsFalse(f.Service.Send(f.Alice, ChatChannel.Combat, "Forged damage").Success);
            Assert.ThrowsException<SocialException>(() => f.Service.ResolveAudience(f.Alice, ChatChannel.Combat));
            var combat = f.Service.SendCombat("Alice hit target for 5.", new[] { f.Alice, f.Bob }, "encounter-1");
            Assert.AreEqual(ChatChannel.Combat, combat.Message.Channel); Assert.AreEqual("Combat", combat.Message.SenderName);
            CollectionAssert.AreEquivalent(new[] { f.Alice, f.Bob }, combat.RecipientIds);
            Assert.AreEqual(1, f.Service.ReadMessages(f.Bob).Count); Assert.AreEqual(0, f.Service.ReadMessages(f.Cara).Count);
        }
        [TestMethod]
        public void WhisperAndInvitationsRespectIgnoreAndUnknownAccounts()
        {
            var f = new Fixture(); f.Service.SetIgnored(f.Bob, f.Alice, true);
            Assert.IsFalse(f.Service.Send(f.Alice, ChatChannel.Whisper, "private", targetId: f.Bob).Success);
            var c = f.Service.CreateCommunity(f.Alice, "Guild"); Assert.ThrowsException<SocialException>(() => f.Service.InviteCommunity(f.Alice, c.Id, f.Bob));
            f.Service.SetIgnored(f.Bob, f.Alice, false); var whisper = f.Service.ExecuteSlash(f.Alice, "/w Bob hello"); Assert.IsTrue(whisper.Success);
            CollectionAssert.AreEquivalent(new[] { f.Alice, f.Bob }, whisper.Chat.RecipientIds); Assert.AreEqual(f.Bob, whisper.Chat.Message.TargetId);
            Assert.IsFalse(f.Service.Send(f.Alice, ChatChannel.Whisper, "unknown", targetId: Guid.NewGuid().ToString("N")).Success);
            Assert.IsFalse(f.Service.ReadMessages(f.Cara).Any(m => m.Channel == ChatChannel.Whisper));
        }
        [TestMethod]
        public void CommunityRolesInvitesBanAndPersistenceAreAuthoritative()
        {
            var f = new Fixture(); var c = f.Service.CreateCommunity(f.Alice, "Guild", "A guild");
            var invite = f.Service.InviteCommunity(f.Alice, c.Id, f.Bob); Assert.ThrowsException<SocialException>(() => f.Service.RespondInvitation(f.Cara, invite.Id, true));
            f.Service.RespondInvitation(f.Bob, invite.Id, true); Assert.IsFalse(f.Service.Send(f.Bob, ChatChannel.CommunityOfficers, "not an officer", c.Id).Success);
            f.Service.ChangeCommunityRole(f.Alice, c.Id, f.Bob, CommunityRole.Officer);
            var officer = f.Service.Send(f.Bob, ChatChannel.CommunityOfficers, "officers", c.Id); Assert.IsTrue(officer.Success); CollectionAssert.AreEquivalent(new[] { f.Alice, f.Bob }, officer.RecipientIds);
            var second = f.Service.InviteCommunity(f.Bob, c.Id, f.Cara); f.Service.RespondInvitation(f.Cara, second.Id, true);
            Assert.IsFalse(f.Service.Send(f.Cara, ChatChannel.CommunityOfficers, "denied", c.Id).Success);
            var restored = new SocialService(f.Directory, f.Store, f.Options); Assert.AreEqual(3, restored.GetCommunities(f.Cara).Single().Members.Count);
            restored.BanCommunityMember(f.Bob, c.Id, f.Cara); Assert.AreEqual(0, restored.GetCommunities(f.Cara).Count);
            Assert.ThrowsException<SocialException>(() => restored.InviteCommunity(f.Bob, c.Id, f.Cara));
            restored.UnbanCommunityMember(f.Bob, c.Id, f.Cara); Assert.IsNotNull(restored.InviteCommunity(f.Bob, c.Id, f.Cara));
            Assert.ThrowsException<SocialException>(() => restored.LeaveCommunity(f.Alice, c.Id));
            restored.ChangeCommunityRole(f.Alice, c.Id, f.Bob, CommunityRole.Owner); restored.LeaveCommunity(f.Alice, c.Id); Assert.AreEqual(f.Bob, restored.GetCommunities(f.Bob).Single().OwnerId);
        }
        [TestMethod]
        public void PartyAndRaidLeadershipSubgroupsAndInviteExpiryWork()
        {
            var f = new Fixture(); var party = f.Service.CreateGroup(f.Alice, SocialGroupKind.Party);
            var invite = f.Service.InviteGroup(f.Alice, party.Id, f.Bob); f.Service.RespondInvitation(f.Bob, invite.Id, true);
            Assert.IsFalse(f.Service.Send(f.Cara, ChatChannel.Party, "not a member", party.Id).Success);
            CollectionAssert.AreEquivalent(new[] { f.Alice, f.Bob }, f.Service.Send(f.Bob, ChatChannel.Party, "party", party.Id).RecipientIds);
            f.Service.TransferGroupLeadership(f.Alice, party.Id, f.Bob); f.Service.LeaveGroup(f.Bob, party.Id); Assert.AreEqual(f.Alice, f.Service.GetGroups(f.Alice).Single().LeaderId);
            var raid = f.Service.CreateGroup(f.Alice, SocialGroupKind.Raid, "Raid"); var ri = f.Service.InviteGroup(f.Alice, raid.Id, f.Bob); f.Service.RespondInvitation(f.Bob, ri.Id, true);
            f.Service.ChangeGroupRole(f.Alice, raid.Id, f.Bob, "Assistant"); f.Service.SetRaidSubgroup(f.Bob, raid.Id, f.Alice, 2);
            Assert.AreEqual(2, f.Service.GetGroups(f.Alice).Single(g => g.Kind == SocialGroupKind.Raid).Members.Single(m => m.AccountId == f.Alice).Subgroup);
            Assert.ThrowsException<SocialException>(() => f.Service.TransferGroupLeadership(f.Bob, raid.Id, f.Alice));
            var expired = f.Service.InviteGroup(f.Bob, raid.Id, f.Cara); f.Now += TimeSpan.FromDays(8); Assert.AreEqual(0, f.Service.GetInvites(f.Cara).Count);
            Assert.ThrowsException<SocialException>(() => f.Service.RespondInvitation(f.Cara, expired.Id, true));
        }
        [TestMethod]
        public void EmptyWebScopesResolveUniqueMembershipAndSlashDoesNotBroadcastPrivateText()
        {
            var f = new Fixture(); var community = f.Service.CreateCommunity(f.Alice, "Guild");
            f.Service.RespondInvitation(f.Bob, f.Service.InviteCommunity(f.Alice, community.Id, f.Bob).Id, true);
            var party = f.Service.CreateGroup(f.Alice, SocialGroupKind.Party);
            f.Service.RespondInvitation(f.Bob, f.Service.InviteGroup(f.Alice, party.Id, f.Bob).Id, true);
            Assert.IsTrue(f.Service.Send(f.Bob, ChatChannel.Community, "guild", "").Success);
            Assert.IsTrue(f.Service.Send(f.Bob, ChatChannel.Party, "party", "   ").Success);
            var command = f.Service.ExecuteSlash(f.Bob, "/p private party text", "");
            Assert.IsTrue(command.Success); Assert.AreEqual(ChatChannel.Party, command.Chat.Message.Channel);
            CollectionAssert.AreEquivalent(new[] { f.Alice, f.Bob }, command.Chat.RecipientIds);
            Assert.IsFalse(f.Service.ReadMessages(f.Cara).Any(message => message.Text == "private party text"));
            Assert.IsFalse(f.Service.ExecuteSlash(f.Cara, "/p not a member", "").Success);
        }
        [TestMethod]
        public void RegisteredLinksSlashAndModerationNeverTrustMarkupOrForgedReports()
        {
            var f = new Fixture(); f.Service.Links.Register("item", id => id == "123", _ => "Trusted sword");
            var item = f.Service.Send(f.Bob, ChatChannel.Trade, "Selling [item:123]"); Assert.IsTrue(item.Success); Assert.AreEqual("Trusted sword", item.Message.Links.Single().Label);
            Assert.IsFalse(f.Service.Send(f.Bob, ChatChannel.Trade, "Selling [item:999]").Success); Assert.IsFalse(f.Service.Send(f.Bob, ChatChannel.Global, "<color=red>bad</color>").Success);
            var report = f.Service.ReportMessage(f.Cara, item.Message.Id, "Please review"); Assert.AreEqual(f.Bob, report.Message.SenderId);
            Assert.ThrowsException<SocialException>(() => f.Service.ReportMessage(f.Cara, item.Message.Id, "duplicate"));
            Assert.ThrowsException<SocialException>(() => f.Service.SetChatMuted(f.Bob, f.Cara, f.Now.AddMinutes(5)));
            f.Service.SetChatMuted(f.Alice, f.Bob, f.Now.AddMinutes(5), "spam"); Assert.IsFalse(f.Service.Send(f.Bob, ChatChannel.Global, "blocked").Success);
            f.Now += TimeSpan.FromMinutes(6); Assert.IsTrue(f.Service.Send(f.Bob, ChatChannel.Global, "again").Success);
            Assert.AreEqual(1, f.Service.GetReports(f.Alice).Count); Assert.ThrowsException<SocialException>(() => f.Service.GetReports(f.Bob));
        }
        [TestMethod]
        public void RateHistoryAndReturnedSnapshotsAreBoundedAndIsolated()
        {
            var f = new Fixture(); var settings = f.Options; settings.MessagesPerSecond = 1; settings.MessageBurst = 2; settings.MaxStoredMessages = 3; settings.MaxMessagesPerInbox = 2;
            var service = new SocialService(f.Directory, f.Store, settings); Assert.IsTrue(service.Send(f.Alice, ChatChannel.Global, "one").Success); Assert.IsTrue(service.Send(f.Alice, ChatChannel.Global, "two").Success); Assert.IsFalse(service.Send(f.Alice, ChatChannel.Global, "three").Success);
            f.Now += TimeSpan.FromSeconds(1); Assert.IsTrue(service.Send(f.Alice, ChatChannel.Global, "three").Success);
            var history = service.ReadMessages(f.Bob, max: 2); Assert.AreEqual(2, history.Count); history[0].Text = "mutated copy"; Assert.AreNotEqual("mutated copy", service.ReadMessages(f.Bob, max: 2)[0].Text);
            var c = service.CreateCommunity(f.Bob, "Private"); c.Members.Clear(); Assert.AreEqual(1, service.GetCommunities(f.Bob).Single().Members.Count);
            var snapshot = service.GetSnapshot(); snapshot.Communities.Clear(); Assert.AreEqual(1, service.GetCommunities(f.Bob).Count);
        }
        [TestMethod]
        public void FailedAtomicSaveDoesNotCommitMembershipChanges()
        {
            var f = new Fixture(); var store = new FailingStore(); var service = new SocialService(f.Directory, store, f.Options);
            Assert.ThrowsException<InvalidOperationException>(() => service.CreateCommunity(f.Bob, "Not saved")); Assert.AreEqual(0, service.GetCommunities(f.Bob).Count);
        }
        [TestMethod]
        public void ConcurrentMessagesHaveUniqueOrderedIdsAndSafeRecipientHistories()
        {
            var f = new Fixture(); var ids = new ConcurrentBag<long>();
            Parallel.For(0, 50, index => { var sent = f.Service.Send(f.Alice, ChatChannel.Global, "message " + index); Assert.IsTrue(sent.Success); ids.Add(sent.Message.Id); });
            Assert.AreEqual(50, ids.Distinct().Count()); var history = f.Service.ReadMessages(f.Bob); Assert.AreEqual(50, history.Count); CollectionAssert.AreEqual(history.Select(m => m.Id).OrderBy(id => id).ToArray(), history.Select(m => m.Id).ToArray());
        }
        private sealed class FailingStore : ISocialStore
        { public SocialSnapshot Load() => new SocialSnapshot(); public void Save(SocialSnapshot snapshot) => throw new InvalidOperationException("simulated atomic-save failure"); }
    }
}
