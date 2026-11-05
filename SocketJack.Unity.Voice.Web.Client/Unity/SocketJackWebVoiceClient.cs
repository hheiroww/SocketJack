using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using SocketJack.Multiplayer.Voice.Components;
using SocketJack.Multiplayer.Web;
using SocketJack.Multiplayer.Web.Components;
using UnityEngine;

namespace SocketJack.Multiplayer.Voice.Web.Components
{
    /// <summary>Account-backed audio on a separate /ws/voice socket. Capture/playback remain in
    /// SocketJackVoiceManager; the game socket remains independent.</summary>
    [DisallowMultipleComponent, DefaultExecutionOrder(-100)]
    [RequireComponent(typeof(SocketJackVoiceManager), typeof(SocketJackWebClientBehaviour))]
    public sealed class SocketJackWebVoiceClient : MonoBehaviour
    {
        private sealed class Pending { internal AccountVoiceFrame Frame; internal int Epoch; }
        private sealed class SeenSession { internal ulong Id; internal float Last; }
        [SerializeField] private bool connectWhenSignedIn = true;
        [SerializeField] private bool followChatScope = true;
        private readonly Queue<Pending> incoming = new Queue<Pending>();
        private readonly Dictionary<string, SeenSession> speakerSessions = new Dictionary<string, SeenSession>(StringComparer.Ordinal);
        private SocketJackVoiceManager voice;
        private SocketJackWebClientBehaviour account;
        private SocketJackMMOChatPanel chat;
        private AccountVoiceClient client;
        private int epoch;
        private bool connecting, stoppedByUser, wasConnected;
        private float nextConnect;
        private float nextPrune;
        private Func<byte[], VoiceChannel, string, string, bool, bool> transmitter;
        private CancellationTokenSource lifetime;
        public bool IsConnected => client != null && client.IsConnected;
        public string LastError { get; private set; } = "";
        public Func<Uri, CancellationToken, Task<WebSocket>> SocketConnector { get; set; }
        public AccountVoiceClient Client => client;
        private void Awake() { voice = GetComponent<SocketJackVoiceManager>(); account = GetComponent<SocketJackWebClientBehaviour>(); chat = GetComponent<SocketJackMMOChatPanel>(); transmitter = Transmit; }
        private void OnEnable() { lifetime = new CancellationTokenSource(); stoppedByUser = false; voice.EncodedTransmitter = transmitter; account.SignedIn += SignedIn; account.SignedOut += SignedOut; if (connectWhenSignedIn && account.IsSignedIn) BeginConnect(); }
        private void OnDisable() { account.SignedIn -= SignedIn; account.SignedOut -= SignedOut; voice.StopCapture(); Disconnect(); if (voice.EncodedTransmitter == transmitter) voice.EncodedTransmitter = null; lifetime.Cancel(); lifetime.Dispose(); lifetime = null; }
        private void SignedIn(WebAccount value) { Disconnect(); stoppedByUser = false; if (connectWhenSignedIn) BeginConnect(); }
        private void SignedOut() { voice.StopCapture(); Disconnect(); }
        private async void BeginConnect() { try { await ConnectAsync(); } catch (Exception error) { if (this != null) LastError = error.Message; } }
        public async Task ConnectAsync()
        {
            if (!isActiveAndEnabled || !account.IsSignedIn) throw new InvalidOperationException("Enable the component and sign in before connecting voice.");
            if (connecting || IsConnected) return; stoppedByUser = false; connecting = true; nextConnect = Time.unscaledTime + 5; int current = ++epoch; string accountId = account.User.Id;
            client?.Dispose(); voice.ClearIncomingAudio(); speakerSessions.Clear();
            var created = new AccountVoiceClient(account.Client) { SocketConnector = SocketConnector };
            created.FrameReceived += frame => { lock (incoming) { while (incoming.Count >= 128) incoming.Dequeue(); incoming.Enqueue(new Pending { Frame = frame, Epoch = current }); } };
            client = created;
            try
            {
                await created.ConnectAsync(lifetime.Token);
                if (this == null || current != epoch || !account.IsSignedIn || account.User.Id != accountId) { created.Dispose(); return; }
                voice.ResetCaptureStream(); voice.EncodedTransmitter = transmitter; wasConnected = true; LastError = "";
            }
            catch { created.Dispose(); if (ReferenceEquals(client, created)) client = null; throw; }
            finally { if (current == epoch) connecting = false; }
        }
        public void Disconnect()
        {
            epoch++; stoppedByUser = true; connecting = false; wasConnected = false; client?.Dispose(); client = null;
            if (voice != null) { voice.ResetCaptureStream(); voice.ClearIncomingAudio(); }
            // Keep the explicitly selected account transport while disconnected. It drops audio rather
            // than silently falling back to the game socket or a less secure transport.
            if (voice != null && isActiveAndEnabled) voice.EncodedTransmitter = transmitter;
            lock (incoming) incoming.Clear();
            speakerSessions.Clear();
        }
        private bool Transmit(byte[] bytes, VoiceChannel channel, string scope, string target, bool reset)
        { if (connecting || client == null || !client.IsConnected) return false; if (reset) client.ResetStream(); return client.SendEncoded(bytes, channel, scope, target); }
        private void Update()
        {
            bool live = IsConnected;
            if (wasConnected && !live)
            {
                voice.ResetCaptureStream(); voice.ClearIncomingAudio(); speakerSessions.Clear(); lock (incoming) incoming.Clear();
            }
            wasConnected = live;
            if (connectWhenSignedIn && !stoppedByUser && account.IsSignedIn && !IsConnected && !connecting && Time.unscaledTime >= nextConnect) BeginConnect();
            if (Time.unscaledTime >= nextPrune)
            {
                nextPrune = Time.unscaledTime + 1; var stale = new List<string>();
                foreach (var pair in speakerSessions) if (Time.unscaledTime - pair.Value.Last > 5) stale.Add(pair.Key);
                foreach (string key in stale) speakerSessions.Remove(key);
            }
            if (followChatScope && chat != null)
            {
                if (voice.Channel == VoiceChannel.Party) voice.ScopeId = chat.PartyId;
                else if (voice.Channel == VoiceChannel.Raid) voice.ScopeId = chat.RaidId;
                else if (voice.Channel == VoiceChannel.Community) voice.ScopeId = chat.CommunityId;
                if (voice.Channel == VoiceChannel.Whisper && !string.IsNullOrEmpty(chat.SelectedSenderAccountId)) voice.TargetAccountId = chat.SelectedSenderAccountId;
            }
            for (int i = 0; i < 128; i++)
            {
                Pending pending; lock (incoming) { if (incoming.Count == 0) break; pending = incoming.Dequeue(); }
                if (pending.Epoch != epoch || !account.IsSignedIn || !IsConnected) continue;
                var frame = pending.Frame;
                if (speakerSessions.TryGetValue(frame.AccountId, out var previousSession) && previousSession.Id != frame.SessionId) voice.RevokeSpeaker("account:" + frame.AccountId);
                if (speakerSessions.Count >= 1024 && !speakerSessions.ContainsKey(frame.AccountId)) continue;
                if (previousSession == null) speakerSessions[frame.AccountId] = previousSession = new SeenSession();
                previousSession.Id = frame.SessionId; previousSession.Last = Time.unscaledTime;
                voice.ReceiveAuthorizedAudio("account:" + frame.AccountId, frame.StreamId, frame.Sequence, frame.Channel,
                    new Vector3(frame.Position.X, frame.Position.Y, frame.Position.Z), frame.EncodedAudio);
            }
        }
    }
}
