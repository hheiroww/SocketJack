using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SocketJack.Multiplayer.Components;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace SocketJack.Multiplayer.Voice.Components
{
    /// <summary>Optional scene-level voice. Microphone capture is opt-in; unknown accounts are denied unless
    /// trusted guest mode is explicitly enabled. Call session methods only on Unity's main thread.</summary>
    [DefaultExecutionOrder(-200)]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkManager))]
    public sealed class SocketJackVoiceManager : MonoBehaviour
    {
        private sealed class DenyMembership : IVoiceMembershipProvider
        { public bool TryGetParticipant(ulong peer, out VoiceParticipant value) { value = null; return false; } public IEnumerable<ulong> ResolveAudience(VoiceParticipant speaker, VoiceChannel channel, string scope, string target) => Array.Empty<ulong>(); }
        private sealed class Capture
        { internal float[] Pcm; internal byte[] Encoded; internal int Generation; internal ulong Object, View; internal uint Epoch; internal VoiceChannel Channel; internal string Scope, Target; internal bool ResetStream, External; }
        private sealed class Speaker
        { internal uint Stream; internal VoiceJitterBuffer Jitter; internal VoicePcmBuffer Pcm; internal float[] Frame; internal VoicePlayback Playback; internal double LastPacket; internal string Key; internal bool Spatial; internal Vector3 Position; }
        [Header("Authenticated voice routing")]
        [SerializeField, Tooltip("Optional component implementing IVoiceMembershipProvider. Assign before starting a server.")]
        private MonoBehaviour membershipProvider = null;
        [SerializeField, Tooltip("Only for an explicitly trusted game/demo. This bypasses account membership and allows Proximity/Global.")]
        private bool allowTrustedGuests;
        [SerializeField, Min(1)] private float proximityRange = 30;
        [SerializeField, Range(1, 128)] private int maxSpeakers = 32;
        [Header("Transmit selection")]
        [SerializeField] private NetworkIdentity transmitObject = null;
        [SerializeField] private VoiceChannel channel = VoiceChannel.Proximity;
        [SerializeField] private string scopeId = "", targetAccountId = "";
        [Header("Microphone (opt-in)")]
        [SerializeField] private bool startMicrophone = false;
#if !(UNITY_WEBGL && !UNITY_EDITOR)
        [SerializeField] private string microphoneDevice = "";
#endif
        [SerializeField] private bool pushToTalk = true;
        [SerializeField] private bool readKeyboard = true;
        [SerializeField] private KeyCode pushToTalkKey = KeyCode.V;
        [SerializeField, Range(0, 1)] private float playbackVolume = 1;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly object speakersGate = new object();
        private readonly Dictionary<string, Speaker> speakers = new Dictionary<string, Speaker>(StringComparer.Ordinal);
        private readonly ConcurrentQueue<Capture> raw = new ConcurrentQueue<Capture>(), encoded = new ConcurrentQueue<Capture>();
        private readonly ConcurrentQueue<string> errors = new ConcurrentQueue<string>();
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private int rawCount, encodedCount, generation, lastTransmittedGeneration = int.MinValue;
        private bool manualTalk, previousTalk, startingCapture, mutePlayback;
        private long retiredPlaybackSamples, retiredPlayedFrames;
#if !(UNITY_WEBGL && !UNITY_EDITOR)
        private float[] microphoneData;
        private AudioClip microphoneClip;
        private int microphoneRead;
#endif
        private AudioClip silence;
        private NetworkManager manager;
        private NetworkServer boundServer;
        private NetworkClient boundClient;
        private VoiceOptions options;
        private NetworkVoiceServer relay;
        private NetworkVoiceClient client;
#if !(UNITY_WEBGL && !UNITY_EDITOR)
        private Task codecPump;
#endif
#if UNITY_WEBGL && !UNITY_EDITOR
        private OpusVoiceCodec browserEncoder;
        private Capture browserPrevious;
        private int browserSpeakerCursor;
#endif
        private IVoiceMembershipProvider runtimeMembership;
        private Func<byte[], VoiceChannel, string, string, bool, bool> externalTransmitter;
        /// <summary>Optional account/socket transport. Arguments are encoded Opus, channel, scope, target and
        /// codec-stream reset. Called on the main thread; return false for disconnected/backpressured audio.</summary>
        public Func<byte[], VoiceChannel, string, string, bool, bool> EncodedTransmitter
        { get => externalTransmitter; set { if (externalTransmitter == value) return; externalTransmitter = value; Interlocked.Increment(ref generation); ClearIncomingAudio(); } }
        /// <summary>Optional platform microphone bridge. Capture remains opt-in and must feed SubmitPcm only
        /// while IsTalking. These callbacks run on Unity's main thread.</summary>
        public Action PlatformStartCapture { get; set; }
        public Action PlatformStopCapture { get; set; }
        public Func<bool> PlatformIsCapturing { get; set; }
        /// <summary>Browser Web Audio playback override, called on the main thread with one decoded 16 kHz
        /// mono frame, a stable speaker key, spatial flag, trusted position and gain.</summary>
        public Action<string, float[], bool, Vector3, float> PlatformPlayback { get; set; }
        public Action<string> PlatformRemoveSpeaker { get; set; }
        public long PlatformPlaybackSamples { get; private set; }
        public IVoiceMembershipProvider MembershipProvider { get => runtimeMembership; set { if (manager != null && manager.IsServer) throw new InvalidOperationException("Configure membership before starting the server."); runtimeMembership = value; } }
        public bool AllowTrustedGuests { get => allowTrustedGuests; set { if (manager != null && manager.IsServer) throw new InvalidOperationException("Configure guest mode before starting the server."); allowTrustedGuests = value; } }
        public VoiceChannel Channel { get => channel; set { if (channel != value) { channel = value; Interlocked.Increment(ref generation); } } }
        public string ScopeId { get => scopeId; set { value = value ?? ""; if (scopeId != value) { scopeId = value; Interlocked.Increment(ref generation); } } }
        public string TargetAccountId { get => targetAccountId; set { value = value ?? ""; if (targetAccountId != value) { targetAccountId = value; Interlocked.Increment(ref generation); } } }
        public bool MutePlayback { get => mutePlayback; set { if (mutePlayback == value) return; mutePlayback = value; if (value) { lock (speakersGate) foreach (string key in speakers.Keys.ToArray()) RemoveSpeaker(key); } } }
        public NetworkIdentity TransmitObject { get => transmitObject; set { transmitObject = value; Interlocked.Increment(ref generation); } }
#if UNITY_WEBGL && !UNITY_EDITOR
        public bool IsCapturing => PlatformIsCapturing?.Invoke() ?? false;
#else
        public bool IsCapturing => microphoneClip != null || (PlatformIsCapturing?.Invoke() ?? false);
#endif
        public bool IsTalking => previousTalk;
        public string LastError { get; private set; } = "";
        public int SpeakerCount { get { lock (speakersGate) return speakers.Count; } }
        public long FramesSent => client?.FramesSent ?? 0;
        public long FramesRelayed => relay?.ForwardedFrames ?? 0;
        public long RejectedFrames => relay?.RejectedFrames ?? 0;
        public long FramesReceived { get; private set; }
        public long PlaybackSamples { get { lock (speakersGate) return retiredPlaybackSamples + PlatformPlaybackSamples + speakers.Values.Sum(value => value.Playback == null ? 0 : value.Playback.NonzeroSamples); } }
        public long PlayedFrames { get { lock (speakersGate) return retiredPlayedFrames + speakers.Values.Sum(value => value.Jitter.DecodedPackets); } }
        public void SetPushToTalk(bool pressed) => manualTalk = pressed;
        public void ResetCaptureStream() => Interlocked.Increment(ref generation);
        private void Awake()
        {
            manager = GetComponent<NetworkManager>(); options = new VoiceOptions { MaxSpeakers = Mathf.Clamp(maxSpeakers, 1, 128), ProximityRange = Mathf.Max(1, proximityRange) };
            silence = AudioClip.Create("SocketJack voice carrier", 1024, 1, 48000, false);
#if UNITY_WEBGL && !UNITY_EDITOR
            options.MaxSpeakers = Math.Min(options.MaxSpeakers, 8); browserEncoder = new OpusVoiceCodec(options);
#else
            codecPump = Task.Run(() => PumpCodecs(lifetime.Token));
#endif
        }
        private void Start() { if (startMicrophone) StartCapture(); }
        private void Update()
        {
            BindSessions(); relay?.Update(clock.Elapsed.TotalSeconds);
            bool talking = !pushToTalk || manualTalk;
#if ENABLE_LEGACY_INPUT_MANAGER
            if (readKeyboard && pushToTalk) talking |= Input.GetKey(pushToTalkKey);
#endif
            if (talking != previousTalk) { previousTalk = talking; Interlocked.Increment(ref generation); }
#if !(UNITY_WEBGL && !UNITY_EDITOR)
            if (microphoneClip != null) CaptureMicrophone(talking);
#endif
#if UNITY_WEBGL && !UNITY_EDITOR
            PumpBrowserCodecs();
#endif
            for (int i = 0; i < 4 && encoded.TryDequeue(out var item); i++)
            {
                Interlocked.Decrement(ref encodedCount);
                if (item.Generation != Volatile.Read(ref generation)) continue;
                if (item.External)
                {
                    try { if (externalTransmitter != null && externalTransmitter(item.Encoded, item.Channel, item.Scope, item.Target, item.ResetStream || item.Generation != lastTransmittedGeneration)) lastTransmittedGeneration = item.Generation; }
                    catch (Exception error) when (error is ArgumentException || error is InvalidOperationException) { LastError = error.Message; }
                    continue;
                }
                if (client == null || boundClient == null || !boundClient.Objects.TryGetValue(item.Object, out var obj) || obj.AuthorityEpoch != item.Epoch || obj.ViewId != item.View || obj.OwnerId != boundClient.LocalPeerId) continue;
                try
                {
                    if (item.ResetStream || item.Generation != lastTransmittedGeneration) client.ResetStream();
                    if (client.SendEncoded(item.Object, item.Encoded, item.Channel, item.Scope, item.Target)) lastTransmittedGeneration = item.Generation;
                }
                catch (ArgumentException error) { LastError = error.Message; }
            }
            if (errors.TryDequeue(out var errorText)) LastError = errorText;
            double now = clock.Elapsed.TotalSeconds;
            lock (speakersGate)
                foreach (string peer in speakers.Where(pair => now - pair.Value.LastPacket > options.StreamTimeoutSeconds).Select(pair => pair.Key).ToArray()) RemoveSpeaker(peer);
        }
        private void BindSessions()
        {
            if (!ReferenceEquals(boundServer, manager.Server))
            {
                relay?.Dispose(); relay = null; boundServer = manager.Server;
                if (boundServer != null)
                {
                    var provider = runtimeMembership ?? membershipProvider as IVoiceMembershipProvider;
                    if (provider == null && allowTrustedGuests) provider = new TrustedGuestVoiceMembershipProvider(() => boundServer.Peers, peer =>
                    {
                        var identity = manager.SpawnedObjects.Values.FirstOrDefault(value => value.OwnerId == peer);
                        var position = identity == null ? Vector3.zero : identity.transform.position; return new System.Numerics.Vector3(position.x, position.y, position.z);
                    });
                    relay = new NetworkVoiceServer(boundServer, provider ?? new DenyMembership(), options);
                    if (provider == null) LastError = "Voice denies unknown accounts. Configure an authenticated membership provider, or explicitly opt in to trusted guests.";
                }
            }
            if (!ReferenceEquals(boundClient, manager.Client))
            {
                client?.Dispose(); client = null; boundClient = manager.Client; Interlocked.Increment(ref generation);
                lock (speakersGate) foreach (string peer in speakers.Keys.ToArray()) RemoveSpeaker(peer);
                if (boundClient != null) { client = new NetworkVoiceClient(boundClient, options); client.FrameReceived += Receive; }
            }
        }
        private void Receive(VoiceFrame frame)
        {
            if (externalTransmitter != null) return;
            ReceiveAuthorizedAudio("game:" + frame.PeerId, frame.StreamId, frame.Sequence, frame.Channel,
                new Vector3(frame.Position.X, frame.Position.Y, frame.Position.Z), frame.EncodedAudio);
        }
        /// <summary>Revoke queued/playing account audio immediately on sign-out or account/transport change.</summary>
        public void ClearIncomingAudio() { lock (speakersGate) foreach (string key in speakers.Keys.ToArray()) RemoveSpeaker(key); }
        public void RevokeSpeaker(string speakerKey) { if (speakerKey == null) return; lock (speakersGate) RemoveSpeaker(speakerKey); }
        /// <summary>Feed canonical audio from a separately authenticated transport on the main thread.
        /// speakerKey and position must come from its trusted server response, never an unchecked player message.</summary>
        public void ReceiveAuthorizedAudio(string speakerKey, uint streamId, uint sequence, VoiceChannel voiceChannel, Vector3 position, byte[] opus)
        {
            if (!isActiveAndEnabled || mutePlayback || string.IsNullOrEmpty(speakerKey) || speakerKey.Length > 128 || streamId == 0 || !OpusVoiceCodec.IsValidPacket(opus, options.SampleRate, options.MaxEncodedBytes)) return;
            lock (speakersGate)
            {
                if (speakers.TryGetValue(speakerKey, out var speaker) && speaker.Stream != streamId)
                {
                    if (unchecked((int)(streamId - speaker.Stream)) <= 0) return;
                    speaker.Stream = streamId; speaker.Jitter.Reset(); speaker.Pcm.Clear(); if (speaker.Playback != null) speaker.Playback.ConfigureSpatial(voiceChannel == VoiceChannel.Proximity, options.ProximityRange);
                    PlatformRemoveSpeaker?.Invoke(speakerKey);
                }
                if (speaker == null)
                {
                    if (speakers.Count >= options.MaxSpeakers) return;
                    var pcm = new VoicePcmBuffer(options.SampleRate, options.JitterFrames); VoicePlayback playback = null;
#if UNITY_WEBGL && !UNITY_EDITOR
                    if (PlatformPlayback == null)
#endif
                    {
                        var go = new GameObject("SocketJack voice " + speakerKey); go.transform.SetParent(transform, false); playback = go.AddComponent<VoicePlayback>();
                        playback.Configure(pcm, silence, AudioSettings.outputSampleRate, voiceChannel == VoiceChannel.Proximity, options.ProximityRange, playbackVolume);
                    }
                    speaker = new Speaker { Stream = streamId, Key = speakerKey, Jitter = new VoiceJitterBuffer(options), Pcm = pcm, Frame = new float[options.SamplesPerFrame], Playback = playback };
                    speakers.Add(speakerKey, speaker);
                }
                speaker.LastPacket = clock.Elapsed.TotalSeconds;
                speaker.Spatial = voiceChannel == VoiceChannel.Proximity; speaker.Position = position;
                if (speaker.Playback != null) speaker.Playback.transform.position = position;
                if (speaker.Jitter.Enqueue(sequence, opus, speaker.LastPacket)) FramesReceived++;
            }
        }
        private void RemoveSpeaker(string peer)
        { if (!speakers.TryGetValue(peer, out var speaker)) return; speakers.Remove(peer); speaker.Pcm.Clear(); retiredPlayedFrames += speaker.Jitter.DecodedPackets; PlatformRemoveSpeaker?.Invoke(peer); if (speaker.Playback != null) { speaker.Playback.StopPlayback(); retiredPlaybackSamples += speaker.Playback.NonzeroSamples; Destroy(speaker.Playback.gameObject); } }
        /// <summary>Explicit game-provided mono capture adapter. Call on the main thread with exactly 320 samples
        /// (16 kHz, 20 ms). This does not open the microphone and is also useful for deterministic audio fixtures.</summary>
        public bool SubmitPcm(float[] monoPcm)
        {
            if (!isActiveAndEnabled) return false;
            if (monoPcm == null || monoPcm.Length != options.SamplesPerFrame) throw new ArgumentException("Provide one 20 ms mono frame at 16 kHz.");
            bool external = externalTransmitter != null;
            if (!external && (client == null || boundClient == null || !boundClient.IsConnected)) return false;
            var identity = transmitObject != null ? transmitObject : manager.SpawnedObjects.Values.FirstOrDefault(value => value.IsOwner && value.IsNetworkReady);
            NetworkObject obj = null;
            if (!external && (identity == null || !identity.IsOwner || !boundClient.Objects.TryGetValue(identity.ObjectId, out obj))) return false;
            if (Interlocked.Increment(ref rawCount) > 4) { Interlocked.Decrement(ref rawCount); return false; }
            raw.Enqueue(new Capture { Pcm = (float[])monoPcm.Clone(), Generation = Volatile.Read(ref generation), External = external, Object = obj?.Id ?? 0, View = obj?.ViewId ?? 0, Epoch = obj?.AuthorityEpoch ?? 0, Channel = channel, Scope = scopeId, Target = targetAccountId }); return true;
        }
        private async Task PumpCodecs(CancellationToken token)
        {
            var encoder = new OpusVoiceCodec(options);
            Capture previous = null;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    for (int i = 0; i < 4 && raw.TryDequeue(out var item); i++)
                        EncodeCapture(item, encoder, ref previous);
                    double now = clock.Elapsed.TotalSeconds;
                    Speaker[] playback; lock (speakersGate) playback = speakers.Values.ToArray();
                    foreach (var speaker in playback)
                        for (int i = 0; i < 3 && speaker.Jitter.TryDecode(speaker.Frame, now); i++) speaker.Pcm.Write(speaker.Frame);
                    await Task.Delay(5, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { if (errors.IsEmpty) errors.Enqueue(error.Message); }
        }
        private void EncodeCapture(Capture item, OpusVoiceCodec encoder, ref Capture previous)
        {
            Interlocked.Decrement(ref rawCount); if (item.Generation != Volatile.Read(ref generation)) return;
            try
            {
                if (previous == null || previous.Generation != item.Generation || previous.Object != item.Object || previous.Epoch != item.Epoch || previous.View != item.View || previous.Channel != item.Channel || previous.Scope != item.Scope || previous.Target != item.Target)
                { encoder.Reset(); item.ResetStream = true; }
                previous = item; item.Encoded = encoder.Encode(item.Pcm); item.Pcm = null;
                if (Interlocked.Increment(ref encodedCount) <= 4) encoded.Enqueue(item); else Interlocked.Decrement(ref encodedCount);
            }
            catch (Exception error) { if (errors.IsEmpty) errors.Enqueue(error.Message); }
        }
#if UNITY_WEBGL && !UNITY_EDITOR
        private void PumpBrowserCodecs()
        {
            // WebGL without shared-memory threads has no managed ThreadPool. Bound codec work to two
            // milliseconds between frames, at most two encodes and eight fair speaker checks per update.
            long started = Stopwatch.GetTimestamp(); double budget = Stopwatch.Frequency * .002;
            for (int i = 0; i < 2 && Stopwatch.GetTimestamp() - started < budget && raw.TryDequeue(out var item); i++) EncodeCapture(item, browserEncoder, ref browserPrevious);
            Speaker[] playback; lock (speakersGate) playback = speakers.Values.ToArray();
            for (int i = 0; i < playback.Length && Stopwatch.GetTimestamp() - started < budget; i++)
            {
                if (browserSpeakerCursor >= playback.Length) browserSpeakerCursor = 0;
                var speaker = playback[browserSpeakerCursor++];
                if (speaker.Jitter.TryDecode(speaker.Frame, clock.Elapsed.TotalSeconds))
                {
                    if (PlatformPlayback != null)
                    {
                        PlatformPlayback(speaker.Key, speaker.Frame, speaker.Spatial, speaker.Position, playbackVolume);
                        for (int sample = 0; sample < speaker.Frame.Length; sample++) if (Math.Abs(speaker.Frame[sample]) > .0001f) PlatformPlaybackSamples++;
                    }
                    else speaker.Pcm.Write(speaker.Frame);
                }
            }
        }
#endif
        public void StartCapture()
        {
            if (startingCapture || IsCapturing) return;
            if (PlatformStartCapture != null) { PlatformStartCapture(); return; }
#if UNITY_WEBGL && !UNITY_EDITOR
            LastError = "Add SocketJackBrowserMicrophone for explicit browser capture; native Microphone APIs are unavailable in WebGL.";
#else
            StartCoroutine(RequestMicrophone());
#endif
        }
#if !(UNITY_WEBGL && !UNITY_EDITOR)
        private IEnumerator RequestMicrophone()
        {
            startingCapture = true;
            if (!Application.HasUserAuthorization(UserAuthorization.Microphone)) yield return Application.RequestUserAuthorization(UserAuthorization.Microphone);
            if (!Application.HasUserAuthorization(UserAuthorization.Microphone)) { LastError = "Microphone permission was not granted."; startingCapture = false; yield break; }
            try
            {
                if (Microphone.devices.Length == 0) throw new InvalidOperationException("No microphone device is available.");
                string device = string.IsNullOrWhiteSpace(microphoneDevice) ? null : microphoneDevice;
                microphoneClip = Microphone.Start(device, true, 1, options.SampleRate);
                if (microphoneClip == null) throw new InvalidOperationException("Microphone capture could not start.");
                microphoneData = new float[(microphoneClip.frequency / 50) * microphoneClip.channels]; microphoneRead = 0; LastError = "";
            }
            catch (Exception error) { LastError = error.Message; }
            startingCapture = false;
        }
        private void CaptureMicrophone(bool talking)
        {
            string device = string.IsNullOrWhiteSpace(microphoneDevice) ? null : microphoneDevice;
            int position = Microphone.GetPosition(device); if (position < 0) return;
            int frame = microphoneClip.frequency / 50, available = (position - microphoneRead + microphoneClip.samples) % microphoneClip.samples;
            if (!talking) { microphoneRead = position; return; }
            if (available > frame * 4) { microphoneRead = (position - frame * 4 + microphoneClip.samples) % microphoneClip.samples; available = frame * 4; }
            for (int count = 0; count < 4 && available >= frame; count++)
            {
                if (!microphoneClip.GetData(microphoneData, microphoneRead)) { LastError = "Microphone PCM could not be read."; return; }
                var mono = new float[options.SamplesPerFrame]; int channels = microphoneClip.channels;
                for (int i = 0; i < mono.Length; i++) { int source = Math.Min(frame - 1, (int)((long)i * frame / mono.Length)); float sum = 0; for (int c = 0; c < channels; c++) sum += microphoneData[source * channels + c]; mono[i] = Mathf.Clamp(sum / channels, -1, 1); }
                SubmitPcm(mono); microphoneRead = (microphoneRead + frame) % microphoneClip.samples; available -= frame;
            }
        }
#endif
        public void StopCapture()
        {
            StopAllCoroutines(); startingCapture = false; Interlocked.Increment(ref generation);
            PlatformStopCapture?.Invoke();
#if !(UNITY_WEBGL && !UNITY_EDITOR)
            if (microphoneClip != null) { Microphone.End(string.IsNullOrWhiteSpace(microphoneDevice) ? null : microphoneDevice); Destroy(microphoneClip); microphoneClip = null; } microphoneData = null;
#endif
        }
        private void OnApplicationPause(bool paused) { if (paused) StopCapture(); }
        private void OnDisable()
        { StopCapture(); relay?.Dispose(); client?.Dispose(); relay = null; client = null; boundServer = null; boundClient = null; lock (speakersGate) foreach (string peer in speakers.Keys.ToArray()) RemoveSpeaker(peer); }
        private void OnDestroy()
        { StopCapture(); lifetime.Cancel(); relay?.Dispose(); client?.Dispose(); lock (speakersGate) foreach (string peer in speakers.Keys.ToArray()) RemoveSpeaker(peer); if (silence != null) Destroy(silence); }
    }

    /// <summary>Audio thread adapter. Opus decoding runs in the manager's bounded background codec pump.</summary>
    [RequireComponent(typeof(AudioSource))]
    public sealed class VoicePlayback : MonoBehaviour
    {
        private VoicePcmBuffer pcm;
        private int outputRate;
        private long nonzero;
        public long NonzeroSamples => Interlocked.Read(ref nonzero);
        internal void Configure(VoicePcmBuffer value, AudioClip carrier, int outputSampleRate, bool spatial, float range, float volume)
        {
            pcm = value; outputRate = outputSampleRate;
            var source = GetComponent<AudioSource>(); source.clip = carrier; source.loop = true; source.playOnAwake = false; source.volume = volume;
            source.spatialBlend = spatial ? 1 : 0; source.minDistance = 1; source.maxDistance = range; source.dopplerLevel = 0; source.Play();
        }
        internal void ConfigureSpatial(bool spatial, float range) { var source = GetComponent<AudioSource>(); source.spatialBlend = spatial ? 1 : 0; source.maxDistance = range; }
        internal void StopPlayback() { GetComponent<AudioSource>().Stop(); pcm?.Clear(); }
        private void OnAudioFilterRead(float[] data, int channels)
        {
            var buffer = pcm; if (buffer == null || outputRate < 8000) { Array.Clear(data, 0, data.Length); return; }
            buffer.Read(data, channels, outputRate); long count = 0; for (int i = 0; i < data.Length; i++) if (Math.Abs(data[i]) > .0001f) count++;
            Interlocked.Add(ref nonzero, count);
        }
    }
}
