using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using SocketJack.Multiplayer.Chat;
using UnityEngine;
using UnityEngine.Events;

namespace SocketJack.Multiplayer.Web.Components
{
    /// <summary>Optional account-backed MMO chat overlay. Add to a scene UI object, not a network player prefab.
    /// Web callbacks arrive through the behaviour's main-thread event pump. Passwords/tokens are never persisted here.</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(SocketJackWebClientBehaviour), typeof(SocketJackSocialPanel))]
    public sealed class SocketJackMMOChatPanel : MonoBehaviour
    {
        [Serializable] public sealed class RegisteredLinkEvent : UnityEvent<string, string> { }
        [SerializeField] private SocketJackChatPreset preset = null;
        [SerializeField] private bool visible = true;
        [SerializeField] private Vector2 offset = new Vector2(12, 12);
        [SerializeField] private string endpoint = "http://127.0.0.1:7778";
        [SerializeField] private RegisteredLinkEvent onRegisteredLink = new RegisteredLinkEvent();
        private SocketJackWebClientBehaviour web;
        private SocketJackChatPreset fallback;
        private readonly List<WebChatMessage> messages = new List<WebChatMessage>();
        private readonly HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
        private Vector2 scroll;
        private string input = "", username = "", password = "", displayName = "", whisperTarget = "", status = "";
        private string selectedMessage, selectedSender;
        private string whisperTargetId = "";
        private int tab, sendChannelIndex;
        private bool busy, autoScroll = true, historyRequested, sendSelectionInitialized;
        private GUIStyle textStyle, headingStyle, linkStyle, tabStyle;
        private readonly Dictionary<ChatChannel, GUIStyle> channelStyles = new Dictionary<ChatChannel, GUIStyle>();
        private int styleFont;
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, MaxDepth = 24 };
        private string communityId = "", partyId = "", raidId = "";
        public SocketJackWebClientBehaviour Web => web;
        public string SelectedSenderAccountId => selectedSender;
        public string CommunityId { get => communityId; set { value = value ?? ""; if (communityId != value) { communityId = value; historyRequested = true; } } }
        public string PartyId { get => partyId; set { value = value ?? ""; if (partyId != value) { partyId = value; historyRequested = true; } } }
        public string RaidId { get => raidId; set { value = value ?? ""; if (raidId != value) { raidId = value; historyRequested = true; } } }
        public RegisteredLinkEvent OnRegisteredLink => onRegisteredLink;
        private SocketJackChatPreset Settings => preset != null ? preset : fallback;

        private void Awake()
        {
            web = GetComponent<SocketJackWebClientBehaviour>();
            fallback = ScriptableObject.CreateInstance<SocketJackChatPreset>(); fallback.hideFlags = HideFlags.DontSave;
        }
        private void OnEnable()
        {
            if (web == null) web = GetComponent<SocketJackWebClientBehaviour>();
            web.MessageReceived += Receive; web.StatusChanged += OnStatus; web.SignedIn += OnSignedIn; web.SignedOut += OnSignedOut;
        }
        private void OnDisable()
        { if (web != null) { web.MessageReceived -= Receive; web.StatusChanged -= OnStatus; web.SignedIn -= OnSignedIn; web.SignedOut -= OnSignedOut; } password = ""; }
        private void OnDestroy() { if (fallback != null) Destroy(fallback); }
        public void Toggle() => visible = !visible;
        private void OnStatus(string value) => status = value;
        private void OnSignedIn(WebAccount account)
        { messages.Clear(); ids.Clear(); password = input = whisperTarget = whisperTargetId = communityId = partyId = raidId = ""; selectedMessage = selectedSender = null; status = "Signed in as " + account.DisplayName; historyRequested = true; }
        private void OnSignedOut()
        { password = input = whisperTarget = whisperTargetId = communityId = partyId = raidId = ""; messages.Clear(); ids.Clear(); selectedMessage = selectedSender = null; historyRequested = false; }
        private void Receive(WebChatMessage message)
        {
            if (message == null || string.IsNullOrEmpty(message.Id) || !ids.Add(message.Id)) return;
            messages.Add(message); messages.Sort((a, b) => CompareIds(a.Id, b.Id));
            while (messages.Count > Mathf.Clamp(Settings.MessageCapacity, 50, 1000)) { ids.Remove(messages[0].Id); messages.RemoveAt(0); }
            if (autoScroll) scroll.y = float.MaxValue;
        }
        private void RefreshHistory()
        {
            if (!web.IsSignedIn || busy) return;
            Observe(async () =>
            {
                string account = web.User.Id;
                foreach (var channel in VisibleChannels())
                {
                    var history = await web.GetHistoryAsync(channel.ToString(), Scope(channel), 100);
                    if (this == null || !web.IsSignedIn || web.User.Id != account) return;
                    foreach (var message in history) Receive(message);
                }
            });
        }
        private void Update() { if (historyRequested && !busy && web != null && web.IsSignedIn) { historyRequested = false; RefreshHistory(); } }
        private void OnGUI()
        {
            if (!visible || web == null || Settings == null) return;
            var settings = Settings; EnsureStyles(settings.FontSize);
            float width = Mathf.Min(settings.Width, Screen.width - 2 * offset.x), height = Mathf.Min(settings.Height, Screen.height - 2 * offset.y);
            var area = new Rect(offset.x, Mathf.Max(offset.y, Screen.height - height - offset.y), Mathf.Max(280, width), Mathf.Max(160, height));
            Color oldColor = GUI.color; GUI.color = settings.Background; GUI.DrawTexture(area, Texture2D.whiteTexture); GUI.color = oldColor;
            GUILayout.BeginArea(new Rect(area.x + 7, area.y + 5, area.width - 14, area.height - 10));
            GUILayout.BeginHorizontal(); GUILayout.Label("SocketJack", headingStyle, GUILayout.Width(74));
            GUILayout.Label(web.IsSignedIn ? web.User.DisplayName + (web.IsLive ? " · live" : " · HTTP") : "Sign in to chat", textStyle);
            var social = GetComponent<SocketJackSocialPanel>(); if (social != null && web.IsSignedIn && GUILayout.Button("Social", GUILayout.Width(48))) social.Toggle();
            if (web.IsSignedIn && GUILayout.Button("Out", GUILayout.Width(34))) Observe(() => web.LogoutAsync()); GUILayout.EndHorizontal();
            if (!web.IsSignedIn) { DrawAccount(settings.ShowAccountControls); GUILayout.EndArea(); return; }
            DrawTabs();
            scroll = GUILayout.BeginScrollView(scroll, GUILayout.ExpandHeight(true));
            foreach (var message in messages) if (Accepts(message)) DrawMessage(message, settings.ShowTimestamps);
            GUILayout.EndScrollView();
            if (!string.IsNullOrEmpty(selectedMessage)) DrawMessageActions();
            DrawSendBar();
            GUILayout.BeginHorizontal(); autoScroll = GUILayout.Toggle(autoScroll, "Follow", GUILayout.Width(65));
            GUILayout.Label(string.IsNullOrEmpty(status) ? "/help · /w name text · /me action" : status, textStyle); GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }
        private void DrawAccount(bool show)
        {
            if (!show) { GUILayout.Label("Sign in through your game's account UI.", textStyle); return; }
            GUILayout.Label("Server", textStyle); endpoint = GUILayout.TextField(endpoint, 240);
            GUILayout.BeginHorizontal(); GUILayout.Label("User", GUILayout.Width(45)); username = GUILayout.TextField(username, 64); GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal(); GUILayout.Label("Pass", GUILayout.Width(45)); password = GUILayout.PasswordField(password, '*', 128); GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal(); GUILayout.Label("Name", GUILayout.Width(45)); displayName = GUILayout.TextField(displayName, 64); GUILayout.EndHorizontal();
            using (new GUIEnabled(!busy))
            {
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Sign in")) Authenticate(false);
                if (GUILayout.Button("Create account")) Authenticate(true); GUILayout.EndHorizontal();
            }
            if (!string.IsNullOrEmpty(status)) GUILayout.Label(status, textStyle);
        }
        private void Authenticate(bool register)
        {
            string secret = password; password = "";
            Observe(async () => { web.ConfigureEndpoint(endpoint.Trim()); if (register) await web.RegisterAsync(username.Trim(), secret, displayName.Trim()); else await web.LoginAsync(username.Trim(), secret); });
        }
        private void DrawTabs()
        {
            if (Settings.Tabs == null || Settings.Tabs.Length == 0) return;
            int selected = GUILayout.Toolbar(Mathf.Clamp(tab, 0, Settings.Tabs.Length - 1), Settings.Tabs.Select(t => t.Name ?? "Chat").ToArray(), tabStyle);
            if (selected != tab) { tab = selected; sendSelectionInitialized = false; selectedMessage = selectedSender = null; historyRequested = true; }
        }
        private ChatChannel[] VisibleChannels()
        {
            var tabs = Settings.Tabs; if (tabs == null || tabs.Length == 0) return new[] { ChatChannel.Global };
            return tabs[Mathf.Clamp(tab, 0, tabs.Length - 1)].VisibleChannels ?? new[] { tabs[Mathf.Clamp(tab, 0, tabs.Length - 1)].SendChannel };
        }
        private bool Accepts(WebChatMessage message) => Enum.TryParse(message.Channel, true, out ChatChannel channel) && VisibleChannels().Contains(channel) &&
            (!(channel == ChatChannel.Community || channel == ChatChannel.CommunityOfficers) || CommunityId.Length == 0 || message.ScopeId == CommunityId);
        private void DrawMessage(WebChatMessage message, bool time)
        {
            Enum.TryParse(message.Channel, true, out ChatChannel channel);
            MmoChatPresets.Channels.TryGetValue(channel, out var channelStyle);
            if (!channelStyles.TryGetValue(channel, out var style))
            {
                style = new GUIStyle(textStyle);
                var appearance = Settings.ChannelColors?.FirstOrDefault(value => value != null && value.Channel == channel);
                if (appearance != null) style.normal.textColor = appearance.Color;
                else if (channelStyle != null && ColorUtility.TryParseHtmlString(channelStyle.ColorHex, out var color)) style.normal.textColor = color;
                channelStyles[channel] = style;
            }
            string prefix = (time ? message.TimestampUtc.ToLocalTime().ToString("HH:mm") + " " : "") + "[" + (channelStyle?.Label ?? message.Channel) + "] " + message.SenderName + ": ";
            if (GUILayout.Button(prefix + message.Text, style))
            {
                selectedMessage = message.Id; selectedSender = message.SenderId;
                if (channel == ChatChannel.Whisper)
                {
                    whisperTargetId = message.SenderId == web.User.Id ? message.TargetId : message.SenderId;
                    whisperTarget = message.SenderId == web.User.Id ? message.TargetId : message.SenderName;
                }
            }
            if (message.Links != null && message.Links.Count > 0)
            {
                GUILayout.BeginHorizontal(); GUILayout.Space(10);
                foreach (var link in message.Links.Take(16))
                    if (link != null && GUILayout.Button(link.Label, linkStyle, GUILayout.ExpandWidth(false))) onRegisteredLink.Invoke(link.Kind, link.Id);
                GUILayout.EndHorizontal();
            }
        }
        private void DrawMessageActions()
        {
            GUILayout.BeginHorizontal(); GUILayout.Label("Selected message", textStyle, GUILayout.Width(110));
            if (!string.IsNullOrEmpty(selectedSender) && selectedSender != web.User.Id && selectedSender != Guid.Empty.ToString("N") && GUILayout.Button("Ignore sender", GUILayout.Width(95)))
                Observe(async () => { await web.Client.RequestAsync("chat/ignore", new { targetId = selectedSender, ignore = true }); status = "Sender ignored."; });
            if (GUILayout.Button("Report", GUILayout.Width(50)) && long.TryParse(selectedMessage, out long id))
                Observe(async () => { await web.Client.RequestAsync("chat/report", new { messageId = id, reason = "Reported from in-game chat." }); status = "Report sent."; });
            if (GUILayout.Button("×", GUILayout.Width(22))) selectedMessage = selectedSender = null; GUILayout.EndHorizontal();
        }
        private void DrawSendBar()
        {
            var channels = VisibleChannels().Where(c => c != ChatChannel.System && c != ChatChannel.Combat).Distinct().ToArray();
            if (channels.Length == 0) { GUILayout.Label("Server updates", textStyle); return; }
            if (!sendSelectionInitialized)
            {
                var tabs = Settings.Tabs; var preferred = tabs != null && tabs.Length > 0 ? tabs[Mathf.Clamp(tab, 0, tabs.Length - 1)].SendChannel : ChatChannel.Global;
                sendChannelIndex = Mathf.Max(0, Array.IndexOf(channels, preferred)); sendSelectionInitialized = true;
            }
            if (channels.Length > 1) sendChannelIndex = GUILayout.Toolbar(Mathf.Clamp(sendChannelIndex, 0, channels.Length - 1), channels.Select(c => MmoChatPresets.Channels[c].Command).ToArray());
            var channel = channels[Mathf.Clamp(sendChannelIndex, 0, channels.Length - 1)];
            if (channel == ChatChannel.Whisper)
            {
                GUILayout.BeginHorizontal(); GUILayout.Label("To", GUILayout.Width(22)); string target = GUILayout.TextField(whisperTarget, 64);
                if (target != whisperTarget) { whisperTarget = target; whisperTargetId = ""; } GUILayout.EndHorizontal();
            }
            GUILayout.BeginHorizontal(); GUI.SetNextControlName("SocketJackChatInput"); input = GUILayout.TextField(input, 512, GUILayout.ExpandWidth(true));
            bool enter = Event.current.type == EventType.KeyDown && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter) && GUI.GetNameOfFocusedControl() == "SocketJackChatInput";
            using (new GUIEnabled(!busy && !string.IsNullOrWhiteSpace(input)))
                if (GUILayout.Button("Send", GUILayout.Width(48)) || enter) { if (enter) Event.current.Use(); SendInput(channel); }
            GUILayout.EndHorizontal();
        }
        private void SendInput(ChatChannel channel)
        {
            if (busy || string.IsNullOrWhiteSpace(input)) return;
            if (input.Trim().Equals("/social", StringComparison.OrdinalIgnoreCase)) { GetComponent<SocketJackSocialPanel>()?.Toggle(); input = ""; return; }
            string text = input, account = web.User.Id;
            Observe(async () =>
            {
                WebChatMessage sent = null;
                string response = null;
                if (text.TrimStart().StartsWith("/", StringComparison.Ordinal))
                {
                    var command = await web.Client.RequestAsync("chat/command", new { text, scopeId = CommandScope(text) });
                    if (command.TryGetProperty("message", out var value) && value.ValueKind == JsonValueKind.Object)
                        sent = JsonSerializer.Deserialize<WebChatMessage>(value.GetRawText(), Json);
                    if (command.TryGetProperty("response", out var reply) && reply.ValueKind == JsonValueKind.String) response = reply.GetString();
                }
                else sent = await web.SendAsync(channel.ToString(), text, Scope(channel), channel == ChatChannel.Whisper && whisperTargetId.Length == 0 ? whisperTarget : "", channel == ChatChannel.Whisper ? whisperTargetId : "");
                if (this == null || !web.IsSignedIn || web.User.Id != account) return;
                if (sent != null) Receive(sent);
                if (!string.IsNullOrEmpty(response)) status = response;
                if (input == text) input = "";
            });
        }
        private string CommandScope(string text)
        {
            string command = text.TrimStart().Split(new[] { ' ' }, 2)[0].ToLowerInvariant();
            switch (command)
            {
                case "/p": case "/party": return PartyId;
                case "/r": case "/raid": return RaidId;
                case "/c": case "/community": case "/guild": case "/co": case "/officers": return CommunityId;
                default: return "";
            }
        }
        private string Scope(ChatChannel channel) => channel == ChatChannel.Community || channel == ChatChannel.CommunityOfficers ? CommunityId : channel == ChatChannel.Party ? PartyId : channel == ChatChannel.Raid ? RaidId : "";
        private async void Observe(Func<Task> action)
        {
            if (busy || web == null) return; busy = true;
            try { await action(); }
            catch (Exception error) { if (this != null) status = error.Message; }
            finally { if (this != null) busy = false; }
        }
        private void EnsureStyles(int font)
        {
            if (textStyle != null && styleFont == font) return; styleFont = Mathf.Clamp(font, 10, 22);
            textStyle = new GUIStyle(GUI.skin.label) { fontSize = styleFont, wordWrap = true, richText = false }; textStyle.normal.textColor = Settings.Text;
            headingStyle = new GUIStyle(textStyle) { fontStyle = FontStyle.Bold };
            tabStyle = new GUIStyle(GUI.skin.button) { fontSize = styleFont, richText = false }; tabStyle.onNormal.textColor = Settings.SelectedTab;
            channelStyles.Clear();
            linkStyle = new GUIStyle(GUI.skin.button) { fontSize = styleFont, richText = false }; linkStyle.normal.textColor = new Color(.5f, .8f, 1);
        }
        private static int CompareIds(string a, string b) => long.TryParse(a, out var x) && long.TryParse(b, out var y) ? x.CompareTo(y) : string.CompareOrdinal(a, b);
        private readonly struct GUIEnabled : IDisposable
        { private readonly bool previous; internal GUIEnabled(bool enabled) { previous = GUI.enabled; GUI.enabled = previous && enabled; } public void Dispose() { GUI.enabled = previous; } }
    }
}
