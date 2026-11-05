using System;
using SocketJack.Multiplayer.Chat;
using UnityEngine;

namespace SocketJack.Multiplayer.Web.Components
{
    public enum ChatPresetKind { MMO, Cooperative, Arena, Custom }
    /// <summary>Editable MMO-style chat layout. Message content remains plain text; colors are local UI settings.</summary>
    [CreateAssetMenu(menuName = "SocketJack/MMO chat preset", fileName = "SocketJackChatPreset")]
    public sealed class SocketJackChatPreset : ScriptableObject
    {
        [Serializable] public sealed class Tab
        {
            public string Name;
            public ChatChannel SendChannel;
            public ChatChannel[] VisibleChannels;
            public Tab(string name, ChatChannel send, params ChatChannel[] visible) { Name = name; SendChannel = send; VisibleChannels = visible; }
        }
        [Serializable] public sealed class ChannelAppearance { public ChatChannel Channel; public Color Color = Color.white; }
        [Range(10, 22)] public int FontSize = 12;
        [Range(300, 900)] public float Width = 480;
        [Range(180, 650)] public float Height = 290;
        [Range(50, 1000)] public int MessageCapacity = 300;
        public bool ShowTimestamps = true;
        public bool ShowAccountControls = true;
        public Color Background = new Color(.035f, .045f, .07f, .94f);
        public Color Text = new Color(.9f, .92f, .96f);
        public Color SelectedTab = new Color(.35f, .65f, .95f);
        public ChannelAppearance[] ChannelColors = Array.Empty<ChannelAppearance>();
        [SerializeField] private ChatPresetKind presetKind = ChatPresetKind.MMO;
        public ChatPresetKind Kind => presetKind;
        public Tab[] Tabs =
        {
            new Tab("General", ChatChannel.Global, ChatChannel.Global, ChatChannel.Zone, ChatChannel.Proximity, ChatChannel.Yell, ChatChannel.Emote, ChatChannel.System),
            new Tab("Trade", ChatChannel.Trade, ChatChannel.Trade),
            new Tab("LFG", ChatChannel.LookingForGroup, ChatChannel.LookingForGroup),
            new Tab("Group", ChatChannel.Party, ChatChannel.Party, ChatChannel.Raid),
            new Tab("Community", ChatChannel.Community, ChatChannel.Community, ChatChannel.CommunityOfficers),
            new Tab("Whispers", ChatChannel.Whisper, ChatChannel.Whisper),
            new Tab("Combat", ChatChannel.Combat, ChatChannel.Combat, ChatChannel.System)
        };
        public static SocketJackChatPreset CreatePreset(ChatPresetKind kind)
        { var preset = CreateInstance<SocketJackChatPreset>(); preset.ApplyPreset(kind); return preset; }
        /// <summary>Explicitly replace tab/color definitions. Other window settings remain editable.
        /// Custom keeps the current definitions; selecting an Inspector enum never resets an asset implicitly.</summary>
        public void ApplyPreset(ChatPresetKind kind)
        {
            if (!Enum.IsDefined(typeof(ChatPresetKind), kind)) throw new ArgumentOutOfRangeException(nameof(kind));
            presetKind = kind; if (kind == ChatPresetKind.Custom) return;
            ChannelColors = Array.Empty<ChannelAppearance>();
            if (kind == ChatPresetKind.Cooperative)
                Tabs = new[] { new Tab("Team", ChatChannel.Party, ChatChannel.Party, ChatChannel.Proximity, ChatChannel.Whisper, ChatChannel.System),
                    new Tab("World", ChatChannel.Global, ChatChannel.Global, ChatChannel.Zone, ChatChannel.System), new Tab("Combat", ChatChannel.Combat, ChatChannel.Combat, ChatChannel.System) };
            else if (kind == ChatPresetKind.Arena)
                Tabs = new[] { new Tab("Match", ChatChannel.Zone, ChatChannel.Zone, ChatChannel.Yell, ChatChannel.System), new Tab("Team", ChatChannel.Party, ChatChannel.Party, ChatChannel.Whisper),
                    new Tab("Combat", ChatChannel.Combat, ChatChannel.Combat, ChatChannel.System), new Tab("General", ChatChannel.Global, ChatChannel.Global, ChatChannel.System) };
            else
                Tabs = new[] { new Tab("General", ChatChannel.Global, ChatChannel.Global, ChatChannel.Zone, ChatChannel.Proximity, ChatChannel.Yell, ChatChannel.Emote, ChatChannel.System),
                    new Tab("Trade", ChatChannel.Trade, ChatChannel.Trade), new Tab("LFG", ChatChannel.LookingForGroup, ChatChannel.LookingForGroup),
                    new Tab("Group", ChatChannel.Party, ChatChannel.Party, ChatChannel.Raid), new Tab("Community", ChatChannel.Community, ChatChannel.Community, ChatChannel.CommunityOfficers),
                    new Tab("Whispers", ChatChannel.Whisper, ChatChannel.Whisper), new Tab("Combat", ChatChannel.Combat, ChatChannel.Combat, ChatChannel.System) };
        }
        [ContextMenu("Apply MMO preset")] private void ApplyMMO() => ApplyPreset(ChatPresetKind.MMO);
        [ContextMenu("Apply cooperative preset")] private void ApplyCooperative() => ApplyPreset(ChatPresetKind.Cooperative);
        [ContextMenu("Apply arena preset")] private void ApplyArena() => ApplyPreset(ChatPresetKind.Arena);
    }
}
