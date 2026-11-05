using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using SocketJack.Multiplayer.Components;
using SocketJack.Multiplayer.Voice;
using SocketJack.Multiplayer.Voice.Components;
using SocketJack.Multiplayer.Voice.Web;
using SocketJack.Multiplayer.Voice.Web.Components;
using SocketJack.Multiplayer.Web;
using SocketJack.Multiplayer.Web.Components;
using UnityEngine;

namespace SocketJack.Tests.Voice
{
    /// <summary>Exact Unity account/socket adapters, real canonical hub + enrolled leaf and synthetic audio.</summary>
    public sealed class AccountVoiceAcceptance : MonoBehaviour
    {
        [Serializable] private sealed class Receipt
        {
            public string result, error, runId, unityVersion, runtimeSha256, voiceSha256, voiceClientSha256, voiceServerSha256;
            public int globalA, globalB, communityA, communityB;
            public long playbackA, playbackB;
            public bool microphoneNeverOpened, canonicalSenderIds, enrolledLeaf, nativeHostBridge, mainThreadAdapter, reconnectPlayback, signoutClearsPlayback;
        }
        private NetworkManager network;
        private SocketJackWebHost host;
        private SocketJackWebVoiceHost hostVoice;
        private SocketJackWebServer leaf;
        private SqliteAccountStore leafStore;
        private AccountVoiceRelay leafVoice;
        private SocketJackWebClientBehaviour a, b;
        private SocketJackVoiceManager audioA, audioB;
        private SocketJackWebVoiceClient voiceA, voiceB;
        private SocketJackMMOChatPanel chatA, chatB;
        private bool finished, streaming;
        private float nextFrame;
        private int frame, thread;
        private readonly Receipt receipt = new Receipt { microphoneNeverOpened = true, canonicalSenderIds = true, mainThreadAdapter = true };
        private void OnEnable() => Application.logMessageReceived += Log;
        private void OnDisable() => Application.logMessageReceived -= Log;
        private void Log(string value, string trace, LogType type) { if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert) _ = Finish(new InvalidOperationException(value + "\n" + trace)); }
        private async void Start()
        {
            Application.runInBackground = true; Application.targetFrameRate = 120; QualitySettings.vSyncCount = 0; AudioListener.volume = 0;
            thread = System.Threading.Thread.CurrentThread.ManagedThreadId; receipt.runId = Argument("-voice-account-run-id"); receipt.unityVersion = Application.unityVersion;
            new GameObject("Account voice listener").AddComponent<AudioListener>();
            try { await Run(); await Finish(null); } catch (Exception error) { await Finish(error); }
        }
        private async Task Run()
        {
            string data = Path.Combine(Argument("-voice-account-data"), receipt.runId); Directory.CreateDirectory(data);
            var server = new GameObject("Canonical account voice host"); network = server.AddComponent<NetworkManager>(); network.Port = 0; network.EnableAutomaticPortForwarding = false;
            server.AddComponent<SocketJackVoiceManager>(); host = server.AddComponent<SocketJackWebHost>(); host.Configure("Native voice hub", "voice-hub", 0, Path.Combine(data, "hub")); hostVoice = server.AddComponent<SocketJackWebVoiceHost>(); network.StartServer();
            await Until(() => host.Server != null && host.Server.IsRunning && hostVoice.Relay != null);
            var origin = new Uri("http://127.0.0.1:" + host.Server.LocalEndPoint.Port + "/");
            using (var http = new HttpClient { BaseAddress = origin })
            {
                var admin = await Call(http, "bootstrap", new { code = host.BootstrapCode, username = "operator", password = Password() });
                var grant = await Call(http, "nodes/issue-link", new { }, admin.GetProperty("token").GetString()); string enrollment = grant.GetProperty("linkToken").GetString();
                await Task.Run(async () =>
                {
                    leafStore = new SqliteAccountStore(Path.Combine(data, "leaf", "accounts.sqlite"));
                    leaf = new SocketJackWebServer(leafStore, leafStore, new SocketJackWebOptions { ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0), NodeId = "voice-leaf", HubEndPoint = origin, EnrollmentToken = enrollment });
                    leafVoice = new AccountVoiceRelay(leaf); await leaf.StartAsync();
                });
            }
            receipt.enrolledLeaf = leaf.IsLinkedToHub; receipt.nativeHostBridge = hostVoice.Relay != null;
            CreateClient("Account voice A", origin, out a, out audioA, out voiceA, out chatA);
            CreateClient("Account voice B", new Uri("http://127.0.0.1:" + leaf.LocalEndPoint.Port + "/"), out b, out audioB, out voiceB, out chatB);
            await a.RegisterAsync("native_voice_a", Password(), "Native A"); await b.RegisterAsync("native_voice_b", Password(), "Native B");
            await Until(() => voiceA.IsConnected && voiceB.IsConnected);
            voiceA.Client.FrameReceived += value => Observe(value, true); voiceB.Client.FrameReceived += value => Observe(value, false);
            audioA.Channel = audioB.Channel = VoiceChannel.Global; streaming = true; nextFrame = Time.realtimeSinceStartup;
            await Delay(3);
            var community = await a.Client.RequestAsync("communities/create", new { name = "Native voice community" }); string id = community.GetProperty("id").GetString();
            var invite = await a.Client.RequestAsync("communities/invite", new { communityId = id, targetId = b.User.Id }); await b.Client.RequestAsync("communities/respond", new { invitationId = invite.GetProperty("id").GetString(), accept = true });
            chatA.CommunityId = chatB.CommunityId = id; audioA.Channel = audioB.Channel = VoiceChannel.Community;
            await Delay(2); audioA.MutePlayback = true; await Delay(.3); audioA.MutePlayback = false; await Delay(1);
            long beforeReconnect = audioB.PlayedFrames; voiceA.Disconnect(); await voiceA.ConnectAsync(); await Delay(1);
            receipt.reconnectPlayback = audioB.PlayedFrames - beforeReconnect > 20; await Delay(1);
            streaming = false;
            Require(receipt.globalA > 70 && receipt.globalB > 70 && receipt.communityA > 100 && receipt.communityB > 100, "Global/community account audio did not cross both exact Unity client bridges.");
            Require(receipt.playbackA > 1000 && receipt.playbackB > 1000, "The exact account bridge did not reach native Unity audio DSP.");
            Require(receipt.canonicalSenderIds && receipt.microphoneNeverOpened && receipt.enrolledLeaf && receipt.nativeHostBridge && receipt.mainThreadAdapter && receipt.reconnectPlayback, "Account identity/permissions, reconnect, thread or microphone boundary failed.");
            voiceA.Disconnect(); voiceB.Disconnect(); receipt.signoutClearsPlayback = audioA.SpeakerCount == 0 && audioB.SpeakerCount == 0; Require(receipt.signoutClearsPlayback, "Disconnect retained private account playback buffers.");
        }
        private static void CreateClient(string name, Uri endpoint, out SocketJackWebClientBehaviour account, out SocketJackVoiceManager audio, out SocketJackWebVoiceClient voice, out SocketJackMMOChatPanel chat)
        {
            var go = new GameObject(name); go.AddComponent<NetworkManager>(); audio = go.AddComponent<SocketJackVoiceManager>(); audio.SetPushToTalk(true);
            account = go.AddComponent<SocketJackWebClientBehaviour>(); account.ConfigureEndpoint(endpoint.AbsoluteUri); chat = go.AddComponent<SocketJackMMOChatPanel>(); go.AddComponent<SocketJackVoiceControls>(); voice = go.AddComponent<SocketJackWebVoiceClient>();
        }
        private void Observe(AccountVoiceFrame value, bool first)
        {
            receipt.mainThreadAdapter &= System.Threading.Thread.CurrentThread.ManagedThreadId == thread;
            receipt.canonicalSenderIds &= value.AccountId == (first ? b.User.Id : a.User.Id);
            if (value.Channel == VoiceChannel.Global) { if (first) receipt.globalA++; else receipt.globalB++; }
            if (value.Channel == VoiceChannel.Community) { if (first) receipt.communityA++; else receipt.communityB++; }
        }
        private void Update()
        {
            if (finished) return;
            if (audioA != null) { receipt.microphoneNeverOpened &= !audioA.IsCapturing; receipt.playbackA = Math.Max(receipt.playbackA, audioA.PlaybackSamples); }
            if (audioB != null) { receipt.microphoneNeverOpened &= !audioB.IsCapturing; receipt.playbackB = Math.Max(receipt.playbackB, audioB.PlaybackSamples); }
            if (!streaming) return;
            for (int count = 0; count < 3 && Time.realtimeSinceStartup >= nextFrame; count++)
            {
                var pcm = new float[320]; for (int i = 0; i < pcm.Length; i++) pcm[i] = (float)(.2 * Math.Sin(2 * Math.PI * 440 * (frame * 320 + i) / 16000));
                audioA.SubmitPcm(pcm); audioB.SubmitPcm(pcm); frame++; nextFrame += .02f;
            }
        }
        private async Task Until(Func<bool> predicate)
        { float deadline = Time.realtimeSinceStartup + 25; while (!predicate()) { Require(!finished && Time.realtimeSinceStartup < deadline, "Native account voice condition timed out."); await Task.Delay(10); } }
        private async Task Delay(double seconds) { float until = Time.realtimeSinceStartup + (float)seconds; await Until(() => Time.realtimeSinceStartup >= until); }
        private async Task Finish(Exception error)
        {
            if (finished) return; finished = true; streaming = false;
            receipt.result = error == null ? "PASS" : "FAIL"; receipt.error = error?.ToString();
            receipt.runtimeSha256 = Hash("SocketJack.Unity.dll"); receipt.voiceSha256 = Hash("SocketJack.Unity.Voice.dll"); receipt.voiceClientSha256 = Hash("SocketJack.Unity.Voice.Web.Client.dll"); receipt.voiceServerSha256 = Hash("SocketJack.Unity.Voice.Web.dll");
            File.WriteAllText(Argument("-voice-account-receipt"), JsonUtility.ToJson(receipt, true));
            if (audioA != null) audioA.StopCapture(); if (audioB != null) audioB.StopCapture(); voiceA?.Disconnect(); voiceB?.Disconnect();
            try { if (a != null) await a.LogoutAsync(); if (b != null) await b.LogoutAsync(); if (host != null) await host.StopWebsiteAsync(); if (leaf != null) await leaf.DisposeAsync(); leafVoice?.Dispose(); leafStore?.Dispose(); network?.Shutdown(); } catch { }
            if (error == null) Debug.Log("NATIVE_ACCOUNT_VOICE_PASS exact Unity bridges, linked Global/Community, native audio DSP, no microphone"); else Debug.LogException(error);
#if UNITY_EDITOR
            UnityEditor.EditorApplication.Exit(error == null ? 0 : 1);
#else
            Application.Quit(error == null ? 0 : 1);
#endif
        }
        private static string Hash(string name)
        {
            string path = Path.Combine(Application.dataPath, "Managed", name);
#if UNITY_EDITOR
            path = Path.Combine(Application.dataPath, "Plugins", name);
#endif
            using (var file = File.OpenRead(path)) using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "").ToLowerInvariant();
        }
        private static async Task<JsonElement> Call(HttpClient client, string operation, object body, string bearer = null)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/" + operation))
            {
                request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"); if (bearer != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
                using (var response = await client.SendAsync(request)) { var result = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync()); Require(result.GetProperty("ok").GetBoolean(), "Fixture account setup failed."); return result.GetProperty("data").Clone(); }
            }
        }
        private static string Password() => Guid.NewGuid().ToString("N") + "aA7!";
        private static string Argument(string name) { var args = Environment.GetCommandLineArgs(); int i = Array.IndexOf(args, name); if (i < 0 || i + 1 >= args.Length) throw new ArgumentException("Missing " + name); return args[i + 1]; }
        private static void Require(bool condition, string error) { if (!condition) throw new InvalidOperationException(error); }
    }
}
