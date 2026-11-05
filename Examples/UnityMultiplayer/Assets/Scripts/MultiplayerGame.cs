using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SocketJack.Multiplayer;
using SocketJack.Multiplayer.Components;
using SocketJack.Multiplayer.Serialization;
using SocketJack.Multiplayer.Transport;
using SocketJack.Multiplayer.Web.Components;
using SocketJack.Multiplayer.Voice.Components;
using SocketJack.Multiplayer.Voice.Web.Components;
using UnityEngine;

namespace SocketJack.Samples.Multiplayer
{
    /// <summary>Native account-backed 3D/2D example. Creates only demo-owned objects; no project settings,
    /// router mappings, credentials or microphone capture are changed automatically.</summary>
    public sealed class MultiplayerGame : MonoBehaviour
    {
        public bool Use2D;
        public Material MaterialTemplate;
        private NetworkManager network;
        private SocketJackWebHost host;
        private SocketJackWebClientBehaviour account;
        private GameObject template;
        private NetworkPrefabRegistry registry;
        private readonly List<Material> materials = new List<Material>();
        private string address = "127.0.0.1", status = "Start a host, then create an account in the chat panel.";
        private bool busy;
        private void Start()
        {
            Application.runInBackground = true;
            var managerObject = new GameObject("SocketJack complete demo services");
            network = managerObject.AddComponent<NetworkManager>();
            network.EnableAutomaticPortForwarding = false; network.Topology = NetworkTopology.HostPeer;
            network.SimulationTickRate = 60; network.SnapshotRate = 5;
            managerObject.AddComponent<NetworkObjectPool>(); managerObject.AddComponent<SocketJackAdaptiveNetworking>();
            var interest = managerObject.AddComponent<SocketJackInterestManager>();
            interest.Dimension = Use2D ? NetworkSpatialDimension.XY : NetworkSpatialDimension.XZ;
            host = managerObject.AddComponent<SocketJackWebHost>(); host.RequireGameAccount = true;
            host.Configure("SocketJack multiplayer demo", Use2D ? "demo-2d" : "demo-3d", 7778, "");
            account = managerObject.AddComponent<SocketJackWebClientBehaviour>();
            managerObject.AddComponent<SocketJackMMOChatPanel>();
            managerObject.AddComponent<SocketJackVoiceManager>(); managerObject.AddComponent<SocketJackVoiceControls>();
            managerObject.AddComponent<SocketJackWebVoiceHost>(); managerObject.AddComponent<SocketJackWebVoiceClient>();
            template = CreatePlayer();
            registry = ScriptableObject.CreateInstance<NetworkPrefabRegistry>();
            registry.SetEntries(new[] { new NetworkPrefabRegistry.Entry(1, template, Use2D ? "demo.player.2d.v1" : "demo.player.3d.v1") });
            managerObject.AddComponent<NetworkPlayerSpawner>().Configure(network, registry);
            CreateWorld();
        }
        private GameObject CreatePlayer()
        {
            var player = new GameObject(Use2D ? "Network square prefab" : "Network cube prefab"); player.SetActive(false);
            player.AddComponent<NetworkIdentity>(); player.AddComponent<SocketJackAdaptiveObject>();
            var visual = GameObject.CreatePrimitive(PrimitiveType.Cube); visual.name = "Smoothed visual";
            visual.transform.SetParent(player.transform, false);
            if (Use2D) visual.transform.localScale = new Vector3(1, 1, 0.2f);
            var collider = visual.GetComponent<Collider>(); collider.enabled = false; Destroy(collider);
            visual.GetComponent<Renderer>().material = CreateMaterial(new Color(.75f, .8f, .87f));
            if (Use2D)
            {
                player.AddComponent<BoxCollider2D>(); var body = player.AddComponent<Rigidbody2D>();
                body.interpolation = RigidbodyInterpolation2D.Interpolate; body.freezeRotation = true;
                var sync = player.AddComponent<SocketJackObject2D>(); sync.PrecisionPreset = NetworkPrecisionPreset.Balanced; sync.Visual = visual.transform;
            }
            else
            {
                player.AddComponent<BoxCollider>(); var body = player.AddComponent<Rigidbody>();
                body.interpolation = RigidbodyInterpolation.Interpolate; body.constraints = RigidbodyConstraints.FreezeRotation;
                var sync = player.AddComponent<SocketJackObject>(); sync.PrecisionPreset = NetworkPrecisionPreset.Balanced; sync.Visual = visual.transform;
            }
            player.AddComponent<SocketJackHealth>();
            player.AddComponent<NetworkInputBridge>();
            var anchor = player.AddComponent<SocketJackInterestObject>(); anchor.ObserverAnchor = true;
            return player;
        }
        private void CreateWorld()
        {
            var floor = GameObject.CreatePrimitive(PrimitiveType.Cube); floor.name = "Shared static floor";
            floor.transform.SetParent(transform, false);
            floor.transform.position = new Vector3(0, -.5f, 0); floor.transform.localScale = new Vector3(40, 1, Use2D ? 1 : 40);
            floor.GetComponent<Renderer>().material = CreateMaterial(new Color(.18f, .2f, .23f));
            if (Use2D) { floor.GetComponent<Collider>().enabled = false; Destroy(floor.GetComponent<Collider>()); floor.AddComponent<BoxCollider2D>(); }
            var camera = new GameObject("Demo camera").AddComponent<Camera>(); camera.tag = "MainCamera";
            camera.transform.SetParent(transform, false);
            camera.gameObject.AddComponent<AudioListener>(); camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(.07f, .08f, .1f);
            if (Use2D) { camera.orthographic = true; camera.orthographicSize = 8; camera.transform.position = new Vector3(4, 3, -20); }
            else { camera.transform.position = new Vector3(8, 16, -15); camera.transform.LookAt(new Vector3(4, 0, 0)); }
            var light = new GameObject("Demo light").AddComponent<Light>(); light.type = LightType.Directional; light.transform.rotation = Quaternion.Euler(55, -25, 0);
            light.transform.SetParent(transform, false);
            RenderSettings.ambientLight = new Color(.5f, .5f, .5f);
        }
        private Material CreateMaterial(Color color)
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return null;
            var pipeline = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
            var source = MaterialTemplate;
            bool created = false;
            if (source == null)
            {
                string kind = pipeline == null ? "" : pipeline.GetType().FullName;
                string shaderName = pipeline == null ? "Standard" : kind.Contains("UniversalRenderPipelineAsset") ? "Universal Render Pipeline/Unlit" : kind.Contains("HDRenderPipelineAsset") ? "HDRP/Unlit" : null;
                if (shaderName == null) source = pipeline.defaultMaterial;
                else { var shader = Shader.Find(shaderName); if (shader != null && shader.isSupported) { source = new Material(shader); created = true; } }
            }
            if (source == null || source.shader == null || !source.shader.isSupported) throw new InvalidOperationException("Assign a compatible material template and retain its shader in the build.");
            var material = created ? source : new Material(source); materials.Add(material);
            foreach (string property in new[] { "_Color", "_BaseColor", "_UnlitColor" }) if (material.HasProperty(property)) material.SetColor(property, color);
            return material;
        }
        private void OnGUI()
        {
            if (network == null) return;
            GUILayout.BeginArea(new Rect(12, 12, 500, 190), GUI.skin.box);
            GUILayout.Label("SocketJack · complete " + (Use2D ? "2D" : "3D") + " multiplayer demo");
            GUILayout.Label("WASD/arrows move · Space jumps · chat below · voice is opt-in");
            using (new GUIEnabled(!busy))
            {
                if (network.Server == null && network.Client == null)
                {
                    address = GUILayout.TextField(address, 240);
                    int transport = GUILayout.SelectionGrid(network.Transport == TransportType.Tcp ? 1 : 0, new[] { "UDP", "TCP" }, 2);
                    network.Transport = transport == 1 ? TransportType.Tcp : TransportType.Udp;
                    GUILayout.BeginHorizontal();
                    if (GUILayout.Button("Start local host listener")) Run(() => { network.Address = address; network.StartHostListener(); status = "Website starts on port 7778. Sign in below, then Join authenticated game."; return Task.CompletedTask; });
                    if (GUILayout.Button("Dedicated server")) Run(() => { network.Topology = NetworkTopology.DedicatedServerClient; network.StartServer(); status = "Server listening. Connect clients and sign in below."; return Task.CompletedTask; });
                    GUILayout.EndHorizontal();
                }
                if (network.Client == null && account.IsSignedIn && GUILayout.Button("Join authenticated game"))
                    Run(async () => { if (network.IsServer) await account.ConnectLocalHostAsync(network); else { network.Address = address; await account.JoinGameAsync(network); } status = "Ticket accepted; waiting for the game baseline."; });
                if ((network.Server != null || network.Client != null) && GUILayout.Button("Stop game session")) Run(() => { network.Shutdown(); status = "Stopped. Sign out before changing account servers."; return Task.CompletedTask; });
            }
            GUILayout.Label(network.IsClient ? "Connected · objects " + network.ObjectCount : status);
            if (!string.IsNullOrEmpty(host.LastError)) GUILayout.Label(host.LastError);
            GUILayout.EndArea();
        }
        private async void Run(Func<Task> action)
        { busy = true; try { await action(); } catch (Exception error) { status = error.Message; } finally { busy = false; } }
        private void OnDestroy() { if (network != null) { network.Shutdown(); Destroy(network.gameObject); } if (template != null) Destroy(template); if (registry != null) Destroy(registry); foreach (var material in materials) if (material != null) Destroy(material); }
        private readonly struct GUIEnabled : IDisposable
        {
            private readonly bool previous;
            public GUIEnabled(bool enabled) { previous = GUI.enabled; GUI.enabled = previous && enabled; }
            public void Dispose() { GUI.enabled = previous; }
        }
    }
}
