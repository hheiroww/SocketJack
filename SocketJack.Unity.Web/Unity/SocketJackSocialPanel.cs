using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SocketJack.Multiplayer.Web.Components
{
    /// <summary>Optional runtime Community/party/raid UI. Buttons reflect current membership;
    /// the server remains authoritative for every action and role change.</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(SocketJackWebClientBehaviour))]
    public sealed class SocketJackSocialPanel : MonoBehaviour
    {
        private sealed class Member { internal string Id, Name, Role; internal int Subgroup; }
        private sealed class Group
        {
            internal string Id, Name, Description, OwnerId, Kind; internal bool Open;
            internal List<Member> Members = new List<Member>();
        }
        private sealed class Invite { internal string Id, Name, Scope; }
        [SerializeField] private bool visible;
        [SerializeField] private Rect window = new Rect(510, 120, 460, 460);
        private SocketJackWebClientBehaviour web;
        private SocketJackWebClient eventsClient;
        private List<Group> communities = new List<Group>(), groups = new List<Group>(), publicCommunities = new List<Group>();
        private List<Invite> communityInvites = new List<Invite>(), groupInvites = new List<Invite>();
        private string selectedCommunity = "", selectedGroup = "", selectedMember = "", inviteUser = "", communityName = "", description = "", search = "", status = "";
        private Vector2 scroll; private bool isOpen, busy; private int section, roleChoice, subgroup = 1, dirty;
        private float nextRefresh;
        private void Awake() { web = GetComponent<SocketJackWebClientBehaviour>(); }
        private void OnEnable()
        {
            if (web == null) web = GetComponent<SocketJackWebClientBehaviour>();
            web.SignedIn += OnSignedIn; web.SignedOut += OnSignedOut;
            if (web.IsSignedIn) OnSignedIn(web.User);
        }
        private void OnDisable()
        {
            if (web != null) { web.SignedIn -= OnSignedIn; web.SignedOut -= OnSignedOut; }
            if (eventsClient != null) eventsClient.EventReceived -= OnServerEvent; eventsClient = null;
        }
        public void Toggle() { visible = !visible; if (visible) Interlocked.Exchange(ref dirty, 1); }
        private void OnSignedIn(WebAccount user)
        {
            if (eventsClient != null) eventsClient.EventReceived -= OnServerEvent;
            eventsClient = web.Client; if (eventsClient != null) eventsClient.EventReceived += OnServerEvent;
            Interlocked.Exchange(ref dirty, 1);
        }
        private void OnSignedOut() { communities.Clear(); groups.Clear(); publicCommunities.Clear(); communityInvites.Clear(); groupInvites.Clear(); selectedCommunity = selectedGroup = selectedMember = ""; }
        private void OnServerEvent(string kind, JsonElement value) { if (kind == "social.changed") Interlocked.Exchange(ref dirty, 1); }
        private void Update()
        {
            if (!web.IsSignedIn || busy) return;
            if (Interlocked.Exchange(ref dirty, 0) != 0 || visible && Time.unscaledTime >= nextRefresh)
            { nextRefresh = Time.unscaledTime + 10; Observe(Refresh); }
        }
        private async Task Refresh()
        {
            string account = web.User.Id;
            var communityData = await web.Client.RequestAsync("communities", new { });
            var groupData = await web.Client.RequestAsync("groups", new { });
            if (this == null || !web.IsSignedIn || web.User.Id != account) return;
            communities = ReadGroups(communityData, "communities", true); groups = ReadGroups(groupData, "groups", false);
            communityInvites = ReadInvites(communityData); groupInvites = ReadInvites(groupData);
            if (!communities.Any(c => c.Id == selectedCommunity)) selectedCommunity = communities.FirstOrDefault()?.Id ?? "";
            if (!groups.Any(g => g.Id == selectedGroup)) selectedGroup = groups.FirstOrDefault()?.Id ?? "";
            var chat = GetComponent<SocketJackMMOChatPanel>();
            if (chat != null) { chat.CommunityId = selectedCommunity; chat.PartyId = groups.FirstOrDefault(g => g.Kind.Equals("party", StringComparison.OrdinalIgnoreCase))?.Id ?? ""; chat.RaidId = groups.FirstOrDefault(g => g.Kind.Equals("raid", StringComparison.OrdinalIgnoreCase))?.Id ?? ""; }
        }
        private void OnGUI()
        {
            if (!visible || web == null || !web.IsSignedIn) return;
            window.width = Mathf.Clamp(window.width, 340, Mathf.Max(340, Screen.width)); window.height = Mathf.Clamp(window.height, 280, Mathf.Max(280, Screen.height));
            window.x = Mathf.Clamp(window.x, 0, Mathf.Max(0, Screen.width - window.width)); window.y = Mathf.Clamp(window.y, 0, Mathf.Max(0, Screen.height - window.height));
            window = GUI.Window(GetInstanceID(), window, DrawWindow, "Communities & groups");
        }
        private void DrawWindow(int id)
        {
            GUILayout.BeginHorizontal(); section = GUILayout.Toolbar(section, new[] { "Communities", "Party / Raid", "Invitations" }); if (GUILayout.Button("×", GUILayout.Width(24))) visible = false; GUILayout.EndHorizontal();
            scroll = GUILayout.BeginScrollView(scroll);
            bool previous = GUI.enabled; GUI.enabled = previous && !busy;
            if (section == 0) DrawCommunities(); else if (section == 1) DrawGroups(); else DrawInvitations();
            GUI.enabled = previous; GUILayout.EndScrollView();
            GUILayout.Label(busy ? "Working…" : status);
            GUILayout.BeginHorizontal(); if (!busy && GUILayout.Button("Refresh")) Observe(Refresh); GUILayout.Label(web.User.DisplayName); GUILayout.EndHorizontal();
            GUI.DragWindow(new Rect(0, 0, window.width, 20));
        }
        private void DrawCommunities()
        {
            GUILayout.Label("Your communities", GUI.skin.box);
            foreach (var c in communities)
                if (GUILayout.Toggle(c.Id == selectedCommunity, c.Name + " (" + c.Members.Count + ")", GUI.skin.button) && c.Id != selectedCommunity)
                { selectedCommunity = c.Id; selectedMember = ""; var chat = GetComponent<SocketJackMMOChatPanel>(); if (chat != null) chat.CommunityId = c.Id; }
            var current = communities.FirstOrDefault(c => c.Id == selectedCommunity);
            if (current != null)
            {
                GUILayout.Label(current.Description);
                string myRole = current.Members.FirstOrDefault(m => m.Id == web.User.Id)?.Role ?? "Member";
                bool owner = current.OwnerId == web.User.Id || Administrator, officer = owner || myRole.Equals("Officer", StringComparison.OrdinalIgnoreCase);
                DrawMembers(current);
                if (officer) DrawInvite("communities/invite");
                var member = current.Members.FirstOrDefault(m => m.Id == selectedMember);
                if (member != null && member.Id != web.User.Id)
                {
                    if (owner)
                    {
                        roleChoice = GUILayout.Toolbar(roleChoice, new[] { "Member", "Officer", "Owner" });
                        if (GUILayout.Button(roleChoice == 2 ? "Transfer community ownership" : "Set selected member role"))
                            Act("communities/role", new { communityId = current.Id, targetId = member.Id, role = new[] { "Member", "Officer", "Owner" }[roleChoice] });
                    }
                    if (officer && !member.Role.Equals("Owner", StringComparison.OrdinalIgnoreCase) && (owner || member.Role.Equals("Member", StringComparison.OrdinalIgnoreCase)))
                        if (GUILayout.Button("Remove selected member")) Act("communities/remove", new { communityId = current.Id, targetId = member.Id });
                }
                if (GUILayout.Button("Leave " + current.Name)) Act("communities/leave", new { communityId = current.Id });
            }
            GUILayout.Space(8); GUILayout.Label("Create a community", GUI.skin.box);
            communityName = LabelField("Name", communityName, 64); description = LabelField("Description", description, 500); isOpen = GUILayout.Toggle(isOpen, "Open membership");
            if (GUILayout.Button("Create community")) Act("communities/create", new { name = communityName, description, isOpen });
            GUILayout.Space(8); GUILayout.Label("Discover open communities", GUI.skin.box); search = LabelField("Search", search, 64);
            if (GUILayout.Button("Browse")) Observe(async () => { var response = await web.Client.RequestAsync("communities/browse", new { search }); if (this != null) publicCommunities = ReadGroups(response, "communities", true); });
            foreach (var available in publicCommunities.Where(c => communities.All(mine => mine.Id != c.Id)))
            { GUILayout.BeginHorizontal(); GUILayout.Label(available.Name); if (GUILayout.Button("Join", GUILayout.Width(55))) Act("communities/join", new { communityId = available.Id }); GUILayout.EndHorizontal(); }
        }
        private void DrawGroups()
        {
            GUILayout.Label("Your party and raid", GUI.skin.box);
            foreach (var group in groups)
                if (GUILayout.Toggle(group.Id == selectedGroup, group.Name + " · " + group.Kind + " (" + group.Members.Count + ")", GUI.skin.button) && group.Id != selectedGroup) { selectedGroup = group.Id; selectedMember = ""; }
            var current = groups.FirstOrDefault(g => g.Id == selectedGroup);
            if (current != null)
            {
                bool leader = current.OwnerId == web.User.Id || Administrator;
                bool manager = leader || current.Members.Any(m => m.Id == web.User.Id && m.Role.Equals("Assistant", StringComparison.OrdinalIgnoreCase));
                DrawMembers(current); if (manager) DrawInvite("groups/invite");
                var member = current.Members.FirstOrDefault(m => m.Id == selectedMember);
                if (member != null)
                {
                    if (leader && member.Id != web.User.Id && GUILayout.Button("Transfer leadership to selected member")) Act("groups/leader", new { groupId = current.Id, targetId = member.Id });
                    if (current.Kind.Equals("raid", StringComparison.OrdinalIgnoreCase) && manager)
                    {
                        subgroup = Mathf.RoundToInt(GUILayout.HorizontalSlider(subgroup, 1, 8)); GUILayout.Label("Raid subgroup: " + subgroup);
                        if (GUILayout.Button("Move selected member to subgroup")) Act("groups/subgroup", new { groupId = current.Id, targetId = member.Id, subgroup });
                        if (leader && member.Id != current.OwnerId && GUILayout.Button(member.Role.Equals("Assistant", StringComparison.OrdinalIgnoreCase) ? "Demote assistant" : "Promote raid assistant"))
                            Act("groups/role", new { groupId = current.Id, targetId = member.Id, role = member.Role.Equals("Assistant", StringComparison.OrdinalIgnoreCase) ? "Member" : "Assistant" });
                    }
                    if (leader && member.Id != current.OwnerId && GUILayout.Button("Remove selected member")) Act("groups/remove", new { groupId = current.Id, targetId = member.Id });
                }
                if (GUILayout.Button("Leave group")) Act("groups/leave", new { groupId = current.Id });
            }
            GUILayout.Space(8); GUILayout.Label("Start a group", GUI.skin.box); communityName = LabelField("Name", communityName, 64);
            GUILayout.BeginHorizontal(); if (GUILayout.Button("Create party")) Act("groups/create", new { kind = "party", name = communityName }); if (GUILayout.Button("Create raid")) Act("groups/create", new { kind = "raid", name = communityName }); GUILayout.EndHorizontal();
        }
        private void DrawMembers(Group group)
        {
            foreach (var member in group.Members.Take(200))
                if (GUILayout.Toggle(member.Id == selectedMember, member.Name + " · " + member.Role + (member.Subgroup > 0 ? " · subgroup " + member.Subgroup : ""), GUI.skin.button)) selectedMember = member.Id;
            if (group.Members.Count > 200) GUILayout.Label("Showing the first 200 members.");
        }
        private void DrawInvite(string operation)
        {
            inviteUser = LabelField("Invite username", inviteUser, 64);
            // Build the request when clicked, after this frame's text field has updated.
            if (GUILayout.Button("Invite"))
            {
                if (operation.StartsWith("communities", StringComparison.Ordinal)) Act(operation, new { communityId = selectedCommunity, targetUsername = inviteUser });
                else Act(operation, new { groupId = selectedGroup, targetUsername = inviteUser });
            }
        }
        private void DrawInvitations()
        {
            if (communityInvites.Count + groupInvites.Count == 0) GUILayout.Label("No pending invitations.");
            foreach (var pair in communityInvites.Select(i => new { Invite = i, Prefix = "communities" }).Concat(groupInvites.Select(i => new { Invite = i, Prefix = "groups" })))
            {
                GUILayout.BeginHorizontal(); GUILayout.Label(pair.Invite.Name);
                if (GUILayout.Button("Accept", GUILayout.Width(65))) Act(pair.Prefix + "/respond", new { invitationId = pair.Invite.Id, accept = true });
                if (GUILayout.Button("Decline", GUILayout.Width(65))) Act(pair.Prefix + "/respond", new { invitationId = pair.Invite.Id, accept = false }); GUILayout.EndHorizontal();
            }
        }
        private bool Administrator => web.User != null && string.Equals(web.User.Role, "admin", StringComparison.OrdinalIgnoreCase);
        private void Act(string operation, object data) => Observe(async () => { await web.Client.RequestAsync(operation, data); await Refresh(); status = "Updated."; });
        private async void Observe(Func<Task> action)
        {
            if (busy || web == null || !web.IsSignedIn) return; busy = true;
            try { await action(); }
            catch (Exception error) { if (this != null) status = error.Message; }
            finally { if (this != null) busy = false; }
        }
        private static string LabelField(string label, string value, int max)
        { GUILayout.BeginHorizontal(); GUILayout.Label(label, GUILayout.Width(105)); value = GUILayout.TextField(value, max); GUILayout.EndHorizontal(); return value; }
        private static List<Group> ReadGroups(JsonElement data, string name, bool community)
        {
            var result = new List<Group>(); if (!data.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array) return result;
            foreach (var value in array.EnumerateArray().Take(1000))
            {
                var group = new Group { Id = Text(value, "id"), Name = Text(value, "name"), Description = Text(value, "description"), OwnerId = Text(value, community ? "ownerId" : "leaderId"), Kind = Text(value, "kind"), Open = value.TryGetProperty("isOpen", out var open) && open.ValueKind == JsonValueKind.True };
                if (value.TryGetProperty("members", out var members) && members.ValueKind == JsonValueKind.Array)
                    foreach (var member in members.EnumerateArray().Take(1000)) group.Members.Add(new Member { Id = Text(member, "userId"), Name = Text(member, "displayName"), Role = Text(member, "role"), Subgroup = member.TryGetProperty("subgroup", out var subgroup) && subgroup.TryGetInt32(out int number) ? number : 0 });
                result.Add(group);
            }
            return result;
        }
        private static List<Invite> ReadInvites(JsonElement data)
        {
            var result = new List<Invite>(); if (data.TryGetProperty("invitations", out var invites) && invites.ValueKind == JsonValueKind.Array)
                foreach (var invite in invites.EnumerateArray().Take(100)) result.Add(new Invite { Id = Text(invite, "id"), Name = Text(invite, "name"), Scope = Text(invite, "scopeId") });
            return result;
        }
        private static string Text(JsonElement value, string name) => value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() ?? "" : "";
    }
}
