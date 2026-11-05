using System;
using System.Threading.Tasks;
using SocketJack.Multiplayer.Components;
using UnityEngine;

namespace SocketJack.Multiplayer.Web.Components
{
    /// <summary>Small optional account-aware setup menu. Authentication is entered in the MMO chat panel;
    /// game admission uses a short-lived ticket after the server's website becomes ready.</summary>
    [DisallowMultipleComponent, RequireComponent(typeof(NetworkManager), typeof(SocketJackWebClientBehaviour))]
    public sealed class SocketJackWebSessionControls : MonoBehaviour
    {
        [SerializeField] private bool showMenu = true;
        [SerializeField, Tooltip("Empty selects the account endpoint's game server. Set an enrolled node ID when joining a leaf through its canonical hub.")]
        private string serverNodeId = "";
        [SerializeField] private string address = "127.0.0.1";
        private NetworkManager network;
        private SocketJackWebClientBehaviour account;
        private bool busy;
        private string status = "Start a listener, then sign in or create an account in the chat panel.";
        public string LastError { get; private set; } = "";
        private void Awake() { network = GetComponent<NetworkManager>(); account = GetComponent<SocketJackWebClientBehaviour>(); address = network.Address; }
        public void StartListener()
        {
            try
            {
                if (network.Topology == NetworkTopology.HostPeer) network.StartHostListener(); else network.StartServer();
                status = "The account website is starting. Sign in below, then join the authenticated game."; LastError = "";
            }
            catch (Exception error) { LastError = error.Message; }
        }
        public async Task JoinAsync()
        {
            if (busy) return; busy = true;
            try
            {
                if (network.IsServer) await account.ConnectLocalHostAsync(network, serverNodeId);
                else { network.Address = address.Trim(); await account.JoinGameAsync(network, serverNodeId); }
                LastError = ""; status = "Admission ticket sent. Waiting for the game baseline.";
            }
            catch (Exception error) { LastError = error.Message; }
            finally { busy = false; }
        }
        public void StopSession() { network.Shutdown(); status = "Session stopped. The account can remain signed in for chat."; }
        private void OnGUI()
        {
            if (!showMenu || network == null) return;
            GUILayout.BeginArea(new Rect(12, 12, Mathf.Min(470, Screen.width - 24), 175), GUI.skin.box);
            GUILayout.Label("SocketJack · multiplayer session");
            bool wasEnabled = GUI.enabled; GUI.enabled = wasEnabled && !busy;
            if (network.Server == null && network.Client == null)
            {
                address = GUILayout.TextField(address, 240);
                if (GetComponent("SocketJackWebHost") != null && GUILayout.Button("Start host / server listener")) StartListener();
            }
            if (network.Client == null && (!network.IsServer || network.Topology == NetworkTopology.HostPeer) && account.IsSignedIn && GUILayout.Button("Join authenticated game")) _ = JoinAsync();
            if ((network.Server != null || network.Client != null) && GUILayout.Button("Stop game session")) StopSession();
            GUI.enabled = wasEnabled;
            GUILayout.Label(network.IsClient ? "Connected · objects " + network.ObjectCount : status);
            if (!string.IsNullOrEmpty(LastError)) GUILayout.Label(LastError);
            GUILayout.EndArea();
        }
    }
}
