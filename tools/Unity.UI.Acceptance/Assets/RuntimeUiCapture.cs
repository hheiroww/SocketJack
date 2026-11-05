using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading.Tasks;
using SocketJack.Multiplayer.Chat;
using SocketJack.Multiplayer.Web;
using SocketJack.Multiplayer.Web.Components;
using SocketJack.Multiplayer.Voice.Components;
using SocketJack.Multiplayer.Voice.Web.Components;
using UnityEngine;

namespace SocketJack.Acceptance.UI
{
    /// <summary>Documentation capture of actual SDK UI with synthetic local accounts/messages/audio.
    /// No camera/microphone permission, OS input, screenshot composite or game-world simulation is used.</summary>
    public sealed class RuntimeUiCapture : MonoBehaviour
    {
        [Serializable] private sealed class Shot { public string file, channel, sha256; public int width, height; public long bytes; }
        [Serializable] private sealed class Receipt { public string result, unityVersion, renderer, error; public bool syntheticAccounts = true, physicalMicrophoneUsed; public Shot[] screenshots; }
        private readonly List<Shot> shots = new List<Shot>();
        private string output;
        private Exception failure;
        private SocketJackWebClientBehaviour a, b;
        private SocketJackMMOChatPanel chat;
        private SocketJackVoiceManager voiceAudio;
        private SocketJackWebHost host;
        private bool prepared;
        private void Awake() { Application.runInBackground = true; QualitySettings.vSyncCount = 0; Application.targetFrameRate = 60; output = Argument("-ui-capture-output"); Directory.CreateDirectory(output); }
        private IEnumerator Start()
        {
            var prepare = Prepare(); while (!prepare.IsCompleted) yield return null;
            if (prepare.IsFaulted) { failure = prepare.Exception; WriteReceipt(); yield break; }
            yield return new WaitForSecondsRealtime(.3f);
            yield return Capture("native-mmo-chat.png", "General");
            float deadline = Time.realtimeSinceStartup + 8;
            while (string.IsNullOrEmpty(chat.CommunityId) && Time.realtimeSinceStartup < deadline) yield return null;
            if (!string.IsNullOrEmpty(chat.CommunityId))
            {
                var community = SeedCommunity(); while (!community.IsCompleted) yield return null;
                if (community.IsFaulted) { failure = community.Exception; WriteReceipt(); yield break; }
                Set(chat, "tab", 4); Set(chat, "sendChannelIndex", 0); Set(chat, "sendSelectionInitialized", false);
                var social = chat.GetComponent<SocketJackSocialPanel>(); Set(social, "window", new Rect(914, 30, 500, 580)); social.Toggle();
                yield return new WaitForSecondsRealtime(.4f);
                yield return Capture("native-community-voice.png", "Community");
                social.Toggle(); Set(chat, "tab", 6); Set(chat, "sendSelectionInitialized", false);
                host.Server.PublishCombat("Training dummy takes 42 damage from Copper Sword.", new[] { a.User.Id, b.User.Id });
                host.Server.PublishCombat("Native A gains 18 experience. Party reward shared.", new[] { a.User.Id, b.User.Id });
                yield return new WaitForSecondsRealtime(.15f);
                yield return Capture("native-combat-feed.png", "Combat");
            }
            WriteReceipt();
        }
        private async Task Prepare()
        {
            float until = Time.realtimeSinceStartup + 25;
            while (true)
            {
                var first = GameObject.Find("Account voice A"); var second = GameObject.Find("Account voice B");
                if (first != null && second != null)
                {
                    a = first.GetComponent<SocketJackWebClientBehaviour>(); b = second.GetComponent<SocketJackWebClientBehaviour>();
                    if (a.IsSignedIn && b.IsSignedIn && a.IsLive && b.IsLive && first.GetComponent<SocketJackWebVoiceClient>().IsConnected && second.GetComponent<SocketJackWebVoiceClient>().IsConnected)
                    {
                        chat = first.GetComponent<SocketJackMMOChatPanel>(); voiceAudio = first.GetComponent<SocketJackVoiceManager>();
                        second.GetComponent<SocketJackMMOChatPanel>().enabled = false; second.GetComponent<SocketJackVoiceControls>().enabled = false; second.GetComponent<SocketJackSocialPanel>().enabled = false;
                        break;
                    }
                }
                if (Time.realtimeSinceStartup > until) throw new TimeoutException("Real account/voice UI did not become ready."); await Task.Delay(10);
            }
            var preset = SocketJackChatPreset.CreatePreset(ChatPresetKind.MMO); preset.Width = 860; preset.Height = 650; preset.FontSize = 16;
            Set(chat, "preset", preset); Set(chat, "offset", new Vector2(24, 24));
            Set(chat.GetComponent<SocketJackVoiceControls>(), "offset", new Vector2(24, 24));
            host = GameObject.Find("Canonical account voice host").GetComponent<SocketJackWebHost>();
            host.Server.RegisterGameLink("item", id => id == "copper-sword", id => "Copper Sword");
            host.Server.Presence.UpdateGamePresence(a.User, "capture-world", 1, System.Numerics.Vector3.Zero);
            host.Server.Presence.UpdateGamePresence(b.User, "capture-world", 1, new System.Numerics.Vector3(2, 0, 0));
            host.Server.PublishSystem("Welcome to the Eastern Reach. Choose a channel below.", new[] { a.User.Id, b.User.Id });
            await a.SendAsync("global", "Welcome, adventurers. Ready for the ruins?");
            await b.SendAsync("global", "Ready! I have supplies for the expedition.");
            await a.SendAsync("proximity", "Stay close. The scouts are just ahead.");
            await b.SendAsync("emote", "checks the map and points toward the bridge.");
            await b.SendAsync("yell", "Incoming from the north!");
            await a.SendAsync("global", "Today's reward: [item:copper-sword].");
            prepared = true;
        }
        private async Task SeedCommunity()
        {
            await a.SendAsync("community", "The expedition is ready. Meet at the north gate.", chat.CommunityId);
            await b.SendAsync("community", "Voice is clear. Keeping my microphone off until push-to-talk.", chat.CommunityId);
            await a.SendAsync("community", "We will share [item:copper-sword] after the encounter.", chat.CommunityId);
        }
        private IEnumerator Capture(string name, string channel)
        {
            if (voiceAudio.IsCapturing) { failure = new InvalidOperationException("Documentation capture never opens a physical microphone."); yield break; }
            yield return new WaitForEndOfFrame();
            string path = Path.Combine(output, name); ScreenCapture.CaptureScreenshot(path, 2);
            float deadline = Time.realtimeSinceStartup + 2;
            while ((!File.Exists(path) || new FileInfo(path).Length < 100) && Time.realtimeSinceStartup < deadline) yield return null;
            if (!File.Exists(path)) { failure = new InvalidOperationException("Unity did not write the rendered screenshot."); yield break; }
            using (var stream = File.OpenRead(path)) using (var sha = SHA256.Create())
                shots.Add(new Shot { file = name, channel = channel, bytes = stream.Length, width = Screen.width * 2, height = Screen.height * 2, sha256 = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant() });
        }
        private void WriteReceipt()
        {
            File.WriteAllText(Path.Combine(output, "capture-receipt.json"), JsonUtility.ToJson(new Receipt { result = failure == null && prepared && shots.Count == 3 ? "pass" : "fail", unityVersion = Application.unityVersion, renderer = SystemInfo.graphicsDeviceType.ToString(), screenshots = shots.ToArray(), physicalMicrophoneUsed = voiceAudio != null && voiceAudio.IsCapturing, error = failure?.ToString() ?? (shots.Count == 3 ? "" : "Not all screenshots were captured before the fixture ended.") }, true));
        }
        private static void Set(object target, string field, object value) { var member = target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new MissingFieldException(target.GetType().Name, field); member.SetValue(target, value); }
        private static string Argument(string key) { var args = Environment.GetCommandLineArgs(); int index = Array.IndexOf(args, key); if (index < 0 || index + 1 >= args.Length) throw new ArgumentException("Missing " + key); return args[index + 1]; }
    }
}
