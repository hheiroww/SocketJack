#if UNITY_EDITOR
using SocketJack.Multiplayer.Web.Components;
using UnityEditor;
using UnityEngine;

namespace SocketJack.Multiplayer.Web.Editor
{
    [CustomEditor(typeof(SocketJackWebHost))]
    public sealed class SocketJackWebHostEditor : UnityEditor.Editor
    {
        private bool showSetupCode;
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var host = (SocketJackWebHost)target;
            if (!EditorApplication.isPlaying)
            { EditorGUILayout.HelpBox("A native listening host/server starts this website. Public account access requires TLS. Clients do not create a listener.", MessageType.Info); return; }
            if (!string.IsNullOrEmpty(host.LastError)) EditorGUILayout.HelpBox(host.LastError, MessageType.Error);
            using (new EditorGUI.DisabledScope(true)) EditorGUILayout.TextField("Website", host.WebsiteAddress);
            if (!string.IsNullOrEmpty(host.WebsiteAddress) && GUILayout.Button("Open server website")) Application.OpenURL(host.WebsiteAddress);
            if (string.IsNullOrEmpty(host.BootstrapCode)) return;
            EditorGUILayout.HelpBox("First operator setup uses this local one-time code. It is not stored in your scene or sent to other players.", MessageType.Info);
            showSetupCode = EditorGUILayout.Toggle("Show setup code", showSetupCode);
            using (new EditorGUI.DisabledScope(true))
            { if (showSetupCode) EditorGUILayout.TextField("Setup code", host.BootstrapCode); else EditorGUILayout.PasswordField("Setup code", host.BootstrapCode); }
        }
    }
}
#endif
