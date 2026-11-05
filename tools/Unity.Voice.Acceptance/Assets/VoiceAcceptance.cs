using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using SocketJack.Multiplayer.Components;
using SocketJack.Multiplayer.Transport;
using SocketJack.Multiplayer.Voice.Components;
using UnityEngine;

namespace SocketJack.Tests.Voice
{
    public sealed class VoiceAcceptance : MonoBehaviour
    {
        [Serializable] private sealed class Receipt
        {
            public string runId, result, error, role, transport, unityVersion, runtimeSha256, voiceSha256, opusSha256;
            public bool playing, microphoneNeverOpened, ownershipChanged, roomReentry;
            public long sent, received, relayed, rejected, playbackSamples;
            public int speakers;
            public float seconds;
        }
        [Serializable] private sealed class Ready { public string runId; public int port; }
        private NetworkManager manager;
        private SocketJackVoiceManager voice;
        private readonly List<ulong> peers = new List<ulong>(), objects = new List<ulong>();
        private string role, readyPath, receiptPath, runId;
        private float createdAt, runningAt = -1, nextFrame;
        private int frame;
        private bool finished, transferred, hidden, restored, ownershipChanged, microphoneOpened;
        private long sent, received, relayed, rejected, playbackSamples;
        private int speakers;
        private void OnEnable() => Application.logMessageReceived += OnLog;
        private void OnDisable() => Application.logMessageReceived -= OnLog;
        private void OnLog(string text, string trace, LogType type) { if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert) Finish(new InvalidOperationException(text + "\n" + trace)); }
        private void Start()
        {
            try
            {
                Application.runInBackground = true; Application.targetFrameRate = 120; QualitySettings.vSyncCount = 0;
                AudioListener.volume = 0; // Exercise the DSP callback without making the fixture audible.
                new GameObject("Voice acceptance listener").AddComponent<AudioListener>();
                role = Argument("-voice-role", "client"); readyPath = Argument("-voice-ready", ""); receiptPath = Argument("-voice-receipt", ""); runId = Argument("-voice-run-id", "");
                var go = new GameObject("Voice acceptance manager"); manager = go.AddComponent<NetworkManager>(); voice = go.AddComponent<SocketJackVoiceManager>();
                voice.AllowTrustedGuests = true; voice.SetPushToTalk(true);
                manager.EnableAutomaticPortForwarding = false; manager.Address = "127.0.0.1"; manager.Port = int.Parse(Argument("-voice-port", "0"));
                manager.Transport = (TransportType)Enum.Parse(typeof(TransportType), Argument("-voice-transport", "Udp"));
                manager.RegisterPrefabs(Resources.Load<GameObject>("VoiceAcceptancePlayer"));
                if (role == "server")
                {
                    manager.PeerConnected += peer => { peers.Add(peer); if (peers.Count == 2) foreach (ulong owner in peers) objects.Add(manager.Spawn(1, Vector3.zero, Quaternion.identity, owner).ObjectId); };
                    manager.StartServer(); File.WriteAllText(readyPath, JsonUtility.ToJson(new Ready { runId = runId, port = manager.Server.Transport.LocalEndPoint.Port }));
                }
                else manager.StartClient();
                createdAt = Time.realtimeSinceStartup;
            }
            catch (Exception error) { Finish(error); }
        }
        private void Update()
        {
            if (finished || manager == null) return;
            try
            {
                Require(Time.realtimeSinceStartup - createdAt < 40, "Native voice session timed out."); microphoneOpened |= voice.IsCapturing;
                if (runningAt < 0) { if (manager.ObjectCount != 2) return; runningAt = Time.realtimeSinceStartup; }
                float elapsed = Time.realtimeSinceStartup - runningAt;
                if (role == "server")
                {
                    if (!transferred && elapsed > 3) { transferred = true; manager.TransferOwnership(objects[0], peers[1]); manager.TransferOwnership(objects[1], peers[0]); }
                    if (!hidden && elapsed > 6) { hidden = true; manager.Server.SetPeerRoom(peers[1], 1); }
                    if (!restored && elapsed > 7) { restored = true; manager.Server.SetPeerRoom(peers[1], 0); }
                }
                else
                {
                    voice.Channel = elapsed < 4 ? SocketJack.Multiplayer.Voice.VoiceChannel.Proximity : SocketJack.Multiplayer.Voice.VoiceChannel.Global;
                    if (manager.Client.Objects.Values.Any(value => value.AuthorityEpoch > 1)) ownershipChanged = true;
                    for (int i = 0; i < 3 && elapsed >= nextFrame; i++)
                    {
                        var pcm = new float[320]; for (int sample = 0; sample < pcm.Length; sample++) pcm[sample] = (float)(.2 * Math.Sin(2 * Math.PI * 440 * (frame * 320 + sample) / 16000));
                        voice.SubmitPcm(pcm); frame++; nextFrame += .02f;
                    }
                }
                sent = Math.Max(sent, voice.FramesSent); received = Math.Max(received, voice.FramesReceived); relayed = Math.Max(relayed, voice.FramesRelayed); rejected = Math.Max(rejected, voice.RejectedFrames);
                playbackSamples = Math.Max(playbackSamples, voice.PlaybackSamples); speakers = Math.Max(speakers, voice.SpeakerCount);
                if (elapsed >= (role == "server" ? 13 : 10)) Finish(null);
            }
            catch (Exception error) { Finish(error); }
        }
        private void Finish(Exception error)
        {
            if (finished) return; finished = true;
            try
            {
                if (error == null)
                {
                    Require(!microphoneOpened, "The synthetic fixture must not record a microphone.");
                    if (role == "server") Require(relayed > 300 && transferred && restored, "Voice relay/ownership/room transition did not complete.");
                    else { Require(sent > 150 && received > 100 && speakers > 0 && ownershipChanged, "Voice capture adapter, relay, playback stream or ownership transfer failed."); Require(playbackSamples > 1000, "Actual Unity audio DSP callback did not play nonzero decoded PCM."); }
                }
            }
            catch (Exception verify) { error = verify; }
            var receipt = new Receipt { runId = runId, result = error == null ? "PASS" : "FAIL", error = error?.ToString(), role = role,
                transport = manager == null ? "" : manager.Transport.ToString(), unityVersion = Application.unityVersion, playing = Application.isPlaying,
                runtimeSha256 = Hash("SocketJack.Unity.dll"), voiceSha256 = Hash("SocketJack.Unity.Voice.dll"), opusSha256 = Hash("Concentus.dll"), microphoneNeverOpened = !microphoneOpened,
                ownershipChanged = role == "server" ? transferred : ownershipChanged, roomReentry = restored, sent = sent, received = received, relayed = relayed, rejected = rejected,
                playbackSamples = playbackSamples, speakers = speakers, seconds = runningAt < 0 ? 0 : Time.realtimeSinceStartup - runningAt };
            if (!string.IsNullOrEmpty(receiptPath)) File.WriteAllText(receiptPath, JsonUtility.ToJson(receipt, true));
            if (manager != null) manager.Shutdown();
            if (error == null) Debug.Log("NATIVE_VOICE_PASS " + role + " " + receipt.transport + " opusAudioSamples=" + playbackSamples); else Debug.LogException(error);
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
            if (!File.Exists(path)) return ""; using (var stream = File.OpenRead(path)) using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
        private static string Argument(string name, string fallback) { var args = Environment.GetCommandLineArgs(); int i = Array.IndexOf(args, name); return i < 0 || i + 1 >= args.Length ? fallback : args[i + 1]; }
        private static void Require(bool value, string error) { if (!value) throw new InvalidOperationException(error); }
    }
}
