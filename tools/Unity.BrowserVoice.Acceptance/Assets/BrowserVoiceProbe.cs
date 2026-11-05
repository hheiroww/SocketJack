using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using SocketJack.Multiplayer.Components;
using SocketJack.Multiplayer.Voice;
using SocketJack.Multiplayer.Voice.Components;
using SocketJack.Multiplayer.Voice.Web.Components;
using SocketJack.Multiplayer.Web.Components;
using UnityEngine;

namespace SocketJack.Tests.BrowserVoice
{
    public sealed class BrowserVoiceProbe : MonoBehaviour
    {
        private SocketJackVoiceManager voice;
        private SocketJackBrowserMicrophone audio;
        private SocketJackWebClientBehaviour account;
        private SocketJackWebVoiceClient socket;
        private float[] pcm = new float[320];
        private double deadline, nextFrame, phaseAt, audioStart;
        private int phase, sent;
        private string progress = "initializing";
        private bool chatReply, sawPeak, finished;
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] private static extern void SJBrowserVoice_Result(string result);
        [DllImport("__Internal")] private static extern int SJBrowserVoice_AllowFakeCapture();
#endif
        private async void Start()
        {
            try
            {
                Application.runInBackground = true; deadline = Time.realtimeSinceStartupAsDouble + 90;
#if UNITY_WEBGL && !UNITY_EDITOR
                if (SJBrowserVoice_AllowFakeCapture() != 1) throw new InvalidOperationException("Use the isolated runner with its synthetic microphone. This fixture never opens a real microphone.");
#else
                if (Application.platform != RuntimePlatform.WebGLPlayer) throw new PlatformNotSupportedException("Run this fixture in an actual WebGL browser build.");
#endif
                string endpoint = "http://127.0.0.1:19885";
                foreach (string pair in new Uri(Application.absoluteURL).Query.TrimStart('?').Split('&')) if (pair.StartsWith("endpoint=", StringComparison.Ordinal)) endpoint = Uri.UnescapeDataString(pair.Substring(9));
                var root = new GameObject("Browser account voice fixture"); root.AddComponent<NetworkManager>();
                voice = root.AddComponent<SocketJackVoiceManager>(); audio = root.AddComponent<SocketJackBrowserMicrophone>();
                account = root.AddComponent<SocketJackWebClientBehaviour>(); account.ConfigureEndpoint(endpoint);
                socket = root.AddComponent<SocketJackWebVoiceClient>(); socket.enabled = false;
                root.AddComponent<SocketJackBrowserVoiceSocket>();
                account.MessageReceived += message => { if (message.Text == "native-text-reply") chatReply = true; };
                string password = Guid.NewGuid().ToString("N") + "aA7!";
                progress = "register"; await account.RegisterAsync("browser_probe", password, "Browser probe");
                progress = "logout"; await account.LogoutAsync(); progress = "login"; await account.LoginAsync("browser_probe", password);
                progress = "concurrent-api";
                await Task.WhenAll(account.Client.LiveRequestAsync("chat/history", new { channel = "global", limit = 10 }),
                    account.Client.LiveRequestAsync("communities", new { }));
                progress = "text-chat"; await account.SendAsync("global", "browser-text-probe");
                if (audio.IsCapturing || audio.CapturedFrames != 0) throw new InvalidOperationException("A microphone started without explicit opt-in.");
                socket.enabled = true; voice.Channel = VoiceChannel.Global; voice.SetPushToTalk(true); audio.EnableAudio();
                phase = 1; phaseAt = Time.realtimeSinceStartupAsDouble;
            }
            catch (Exception error) { Finish(false, error.GetType().Name + ": " + error.Message); }
        }
        private void Update()
        {
            if (finished) return;
            try
            {
                double now = Time.realtimeSinceStartupAsDouble;
                if (now >= deadline) throw new TimeoutException("Browser Voice deadline at phase " + phase + " (" + progress + "); " + socket?.LastError + "; " + voice?.LastError + "; " + audio?.LastError);
                if (phase == 0) return;
                if (!account.IsSignedIn || !account.IsLive || !socket.IsConnected) return;
                sawPeak |= audio.OutputPeak > .005f;
                if (phase == 1)
                {
                    if (now >= nextFrame && sent < 60)
                    {
                        for (int i = 0; i < pcm.Length; i++) pcm[i] = .2f * Mathf.Sin(2 * Mathf.PI * 660 * (sent * 320 + i) / 16000);
                        if (voice.SubmitPcm(pcm)) sent++; nextFrame = now + .02;
                    }
                    if (sent < 60 || !chatReply || voice.FramesReceived < 10 || audio.ScheduledPlaybackSamples < 3200 || !sawPeak || audio.AudioClockSeconds < .5) return;
                    if (audio.IsCapturing || audio.CapturedFrames != 0) throw new InvalidOperationException("Synthetic transmit/playback unexpectedly started the microphone.");
                    audioStart = audio.AudioClockSeconds; voice.SetPushToTalk(false); audio.StartCapture(); phase = 2; phaseAt = now;
                }
                else if (phase == 2 && audio.IsCapturing && now - phaseAt > .4)
                {
                    if (audio.CapturedFrames != 0) throw new InvalidOperationException("PTT-off microphone samples entered managed voice capture.");
                    voice.SetPushToTalk(true); phase = 3; phaseAt = now;
                }
                else if (phase == 3 && audio.CapturedFrames >= 15 && now - phaseAt > .5)
                {
                    audio.StopCapture(); if (audio.IsCapturing) throw new InvalidOperationException("Explicit microphone stop did not release capture.");
                    voice.SetPushToTalk(false); phase = 4; _ = Logout();
                }
            }
            catch (Exception error) { Finish(false, error.GetType().Name + ": " + error.Message); }
        }
        private async Task Logout()
        {
            try
            {
                await account.LogoutAsync();
                if (socket.IsConnected || voice.SpeakerCount != 0 || audio.IsCapturing) throw new InvalidOperationException("Logout retained voice/capture/playback state.");
                if (audio.AudioClockSeconds <= audioStart) throw new InvalidOperationException("Web Audio graph clock did not advance.");
                Finish(true, "login,concurrent-API,realtime-chat,binary-Opus-duplex,WebAudio-DSP,synthetic-PCM,fake-microphone-PTT,logout; no real microphone used");
            }
            catch (Exception error) { Finish(false, error.GetType().Name + ": " + error.Message); }
        }
        private void Finish(bool passed, string reason)
        {
            if (finished) return; finished = true; audio?.StopCapture(); socket?.Disconnect();
            string result = (passed ? "PASS " : "FAIL ") + reason; Debug.Log("SOCKETJACK_BROWSER_VOICE_" + result);
#if UNITY_WEBGL && !UNITY_EDITOR
            SJBrowserVoice_Result(result);
#endif
        }
    }
}
