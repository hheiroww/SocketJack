using System;
using UnityEngine;

namespace SocketJack.Multiplayer.Components
{
    /// <summary>Menu-ready connection actions. Bind these methods to your existing game's buttons.</summary>
    [DisallowMultipleComponent, RequireComponent(typeof(NetworkManager))]
    public sealed class NetworkSessionControls : MonoBehaviour
    {
        [SerializeField] private NetworkManager network;
        public string LastError { get; private set; } = "";
        public NetworkManager Network => network != null ? network : (network = GetComponent<NetworkManager>());
        public void Configure(NetworkManager manager) => network = manager != null ? manager : throw new ArgumentNullException(nameof(manager));
        public void Host() => Run(() => { Network.Topology = NetworkTopology.HostPeer; Network.StartHost(); });
        public void Join() => Run(() => { if (Network.Topology == NetworkTopology.HostPeer) Network.StartPeer(); else Network.StartClient(); });
        public void StartDedicatedServer() => Run(() => { Network.Topology = NetworkTopology.DedicatedServerClient; Network.StartServer(); });
        public void SetAddress(string address) => Run(() => Network.Address = address);
        public void SetPort(string port) => Run(() =>
        {
            if (!int.TryParse(port, out int value) || value < 1 || value > 65535) throw new ArgumentException("Port must be between 1 and 65535.");
            Network.Port = value;
        });
        public void Stop() => Run(() => Network.Shutdown());
        private void Run(Action action)
        {
            try { action(); LastError = ""; }
            catch (Exception error) { LastError = error.Message; Debug.LogError("SocketJack session: " + error.Message, this); }
        }
    }
}
