using UnityEngine;

namespace SocketJack.Multiplayer.Voice.Components
{
    /// <summary>Optional compact microphone opt-in and channel/mute controls. Place on a scene UI object.</summary>
    [DisallowMultipleComponent, RequireComponent(typeof(SocketJackVoiceManager))]
    public sealed class SocketJackVoiceControls : MonoBehaviour
    {
        [SerializeField] private bool visible = true;
        [SerializeField] private Vector2 offset = new Vector2(12, 12);
        private SocketJackVoiceManager voice;
        private void Awake() => voice = GetComponent<SocketJackVoiceManager>();
        public void Toggle() => visible = !visible;
        private void OnGUI()
        {
            if (!visible || voice == null) return;
            GUILayout.BeginArea(new Rect(Mathf.Max(0, Screen.width - 264 - offset.x), Mathf.Max(0, Screen.height - 190 - offset.y), 264, 190), GUI.skin.box);
            GUILayout.Label("Voice · " + (voice.IsCapturing ? voice.IsTalking ? "transmitting" : "hold push-to-talk" : "microphone off"));
            GUILayout.BeginHorizontal();
            if (!voice.IsCapturing && GUILayout.Button("Enable microphone")) voice.StartCapture();
            if (voice.IsCapturing && GUILayout.Button("Stop microphone")) voice.StopCapture();
            GUILayout.EndHorizontal();
            int channel = GUILayout.SelectionGrid((int)voice.Channel, new[] { "Nearby", "Party", "Raid", "Community", "Whisper", "Global" }, 3);
            if (channel != (int)voice.Channel) voice.Channel = (VoiceChannel)channel;
            voice.MutePlayback = GUILayout.Toggle(voice.MutePlayback, "Mute incoming voice");
            GUILayout.Label("V: push-to-talk · Input System: SetPushToTalk");
            if (voice.Channel == VoiceChannel.Whisper) GUILayout.Label("Select the target sender in account chat.");
            if (!string.IsNullOrEmpty(voice.LastError)) GUILayout.Label(voice.LastError);
            GUILayout.EndArea();
        }
    }
}
