using System;
using UnityEngine;
#if UNITY_WEBGL && !UNITY_EDITOR
using System.Collections.Generic;
using System.Runtime.InteropServices;
#endif

namespace SocketJack.Multiplayer.Voice.Components
{
    /// <summary>Web Audio capture/playback bridge for single-threaded WebGL. Adding this component
    /// never opens a microphone. Call StartCapture from an explicit user action; the browser owns
    /// its permission prompt. Audio playback may require a click/tap through EnableAudio. PTT-off
    /// microphone samples are discarded before entering managed code. Native players use the
    /// ordinary VoiceManager microphone and audio pipeline.</summary>
    [DisallowMultipleComponent, RequireComponent(typeof(SocketJackVoiceManager)), DefaultExecutionOrder(-300)]
    public sealed class SocketJackBrowserMicrophone : MonoBehaviour
    {
        [SerializeField] private Transform listenerTransform;
        private SocketJackVoiceManager voice;
        private bool capturing = false;
        public bool IsCapturing => capturing;
        public long CapturedFrames { get; private set; }
        public string LastError { get; private set; } = "";
        public Transform ListenerTransform { get => listenerTransform; set => listenerTransform = value; }
        public long ScheduledPlaybackSamples
        {
            get
            {
#if UNITY_WEBGL && !UNITY_EDITOR
                return id == 0 ? 0 : (long)SJMic_PlaybackSamples(id);
#else
                return 0;
#endif
            }
        }
        public double AudioClockSeconds
        {
            get
            {
#if UNITY_WEBGL && !UNITY_EDITOR
                return id == 0 ? 0 : SJMic_AudioTime(id);
#else
                return 0;
#endif
            }
        }
        /// <summary>Current browser Web Audio graph peak; this does not measure physical speakers.</summary>
        public float OutputPeak
        {
            get
            {
#if UNITY_WEBGL && !UNITY_EDITOR
                return id == 0 ? 0 : SJMic_OutputPeak(id);
#else
                return 0;
#endif
            }
        }
        private void Awake() { voice = GetComponent<SocketJackVoiceManager>(); }
        private void OnEnable()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            id = ++nextId; Instances.Add(id, this);
            SJMic_Create(id, StateCallback, PcmCallback);
            capturingStatus = () => capturing;
            voice.PlatformStartCapture = StartPlatformCapture; voice.PlatformStopCapture = StopPlatformCapture; voice.PlatformIsCapturing = capturingStatus;
            voice.PlatformPlayback = Play; voice.PlatformRemoveSpeaker = Remove;
#endif
        }
        /// <summary>Resume browser output after a user gesture. This never requests microphone access.</summary>
        public void EnableAudio()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            if (id != 0) SJMic_Resume(id);
#endif
        }
        public void StartCapture() => voice.StartCapture();
        public void StopCapture() => voice.StopCapture();
        private void StartPlatformCapture()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            if (!isActiveAndEnabled || id == 0) throw new InvalidOperationException("Enable the browser microphone bridge before starting capture.");
            LastError = ""; SJMic_StartCapture(id);
#endif
        }
        private void StopPlatformCapture()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            capturing = false; if (id != 0) SJMic_StopCapture(id);
#endif
        }
        private void Update()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            if (id == 0) return;
            SJMic_SetTalking(id, voice.IsTalking ? 1 : 0);
            var listener = listenerTransform; if (listener == null) { var camera = Camera.main; listener = camera == null ? transform : camera.transform; }
            var p = listener.position; var f = listener.forward; var u = listener.up;
            SJMic_SetListener(id, p.x, p.y, -p.z, f.x, f.y, -f.z, u.x, u.y, -u.z);
#endif
        }
        private void OnApplicationPause(bool pause) { if (pause) StopCapture(); }
        private void OnDisable()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            if (voice.PlatformStopCapture == StopPlatformCapture) voice.StopCapture(); else StopPlatformCapture();
            voice.ClearIncomingAudio();
            if (voice.PlatformStartCapture == StartPlatformCapture) voice.PlatformStartCapture = null;
            if (voice.PlatformStopCapture == StopPlatformCapture) voice.PlatformStopCapture = null;
            if (voice.PlatformIsCapturing == capturingStatus) voice.PlatformIsCapturing = null;
            if (voice.PlatformPlayback == Play) voice.PlatformPlayback = null;
            if (voice.PlatformRemoveSpeaker == Remove) voice.PlatformRemoveSpeaker = null;
            if (id != 0) { Instances.Remove(id); SJMic_Destroy(id); id = 0; }
#endif
        }
#if UNITY_WEBGL && !UNITY_EDITOR
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Notify(int id, int state);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Samples(int id, IntPtr data, int count);
        private static readonly Notify StateCallback = StateChanged;
        private static readonly Samples PcmCallback = ReceivedPcm;
        private static readonly Dictionary<int, SocketJackBrowserMicrophone> Instances = new Dictionary<int, SocketJackBrowserMicrophone>();
        private static int nextId;
        private int id;
        private readonly float[] capture = new float[320];
        private Func<bool> capturingStatus;
        [DllImport("__Internal")] private static extern void SJMic_Create(int id, Notify state, Samples samples);
        [DllImport("__Internal")] private static extern void SJMic_Resume(int id);
        [DllImport("__Internal")] private static extern void SJMic_StartCapture(int id);
        [DllImport("__Internal")] private static extern void SJMic_StopCapture(int id);
        [DllImport("__Internal")] private static extern void SJMic_SetTalking(int id, int value);
        [DllImport("__Internal")] private static extern void SJMic_SetListener(int id, float x, float y, float z, float fx, float fy, float fz, float ux, float uy, float uz);
        [DllImport("__Internal")] private static extern void SJMic_Play(int id, string key, float[] data, int count, int spatial, float x, float y, float z, float volume);
        [DllImport("__Internal")] private static extern void SJMic_RemoveSpeaker(int id, string key);
        [DllImport("__Internal")] private static extern void SJMic_Destroy(int id);
        [DllImport("__Internal")] private static extern double SJMic_PlaybackSamples(int id);
        [DllImport("__Internal")] private static extern double SJMic_AudioTime(int id);
        [DllImport("__Internal")] private static extern float SJMic_OutputPeak(int id);
        private void Play(string key, float[] data, bool spatial, Vector3 position, float volume)
        {
            if (id == 0 || data == null || data.Length != 320 || voice.MutePlayback) return;
            SJMic_Play(id, key, data, data.Length, spatial ? 1 : 0, position.x, position.y, -position.z, Mathf.Clamp01(volume));
        }
        private void Remove(string key) { if (id != 0) SJMic_RemoveSpeaker(id, key); }
        [AOT.MonoPInvokeCallback(typeof(Notify))] private static void StateChanged(int id, int state)
        {
            if (!Instances.TryGetValue(id, out var component)) return;
            component.capturing = state == 1;
            component.LastError = state == 0 || state == 1 ? "" : state == 2 ? "Browser microphone capture requires HTTPS or localhost and media-device support." :
                state == 3 ? "Browser microphone permission was denied or unavailable." : "Browser microphone capture stopped because the audio device was unavailable.";
        }
        [AOT.MonoPInvokeCallback(typeof(Samples))] private static void ReceivedPcm(int id, IntPtr pointer, int count)
        {
            if (count != 320 || !Instances.TryGetValue(id, out var component) || !component.capturing || !component.isActiveAndEnabled || !component.voice.IsTalking) return;
            Marshal.Copy(pointer, component.capture, 0, count); if (component.voice.SubmitPcm(component.capture)) component.CapturedFrames++;
        }
#endif
    }
}
