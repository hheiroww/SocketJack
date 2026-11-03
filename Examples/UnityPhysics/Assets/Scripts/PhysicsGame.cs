using System;
using System.Collections.Generic;
using System.IO;
using SocketJack.Multiplayer;
using SocketJack.Multiplayer.Components;
using SocketJack.Multiplayer.Transport;
using UnityEngine;

namespace SocketJack.Samples.Physics
{
    /// <summary>Small, executable Unity game. One binary can host, join, or run as a headless server.</summary>
    public sealed class PhysicsGame : MonoBehaviour
    {
        [Serializable] public sealed class RunMetrics
        {
            public string role, transport;
            public double seconds, meanPositionError;
            public int connectedPeers, objects, observedFrames;
            public int maxConnectedPeers, maxObjects;
            public bool clientConnected;
            public long inputsSent, inputsReceived, keyframesReceived, hardCorrections;
            public float peakPositionError;
            public float peakNetworkVelocityCorrection, peakVisualStep;
            public int roundTrips, pingSendRejected;
            public double rttMeanMilliseconds, rttP95Milliseconds, rttMaxMilliseconds;
            public string topology;
            public bool serverAuthority, automaticPortForwarding;
            public long ownerStatesSent;
        }
        private NetworkManager network;
        private GameObject template;
        private string address = "127.0.0.1", portText = "7777", role = "menu", metricsPath;
        private float duration, started;
        private bool autoplay, exiting;
        private int spawnOrdinal, observedFrames;
        private double errorSum;
        private long errorSamples;
        private readonly Dictionary<ulong, RunMetrics> totals = new Dictionary<ulong, RunMetrics>();
        private int maxObjects, maxConnectedPeers;
        private bool everConnected;
        private readonly List<double> roundTrips = new List<double>();
        private float nextPing;
        private int pingSendRejected;

        private void Start()
        {
            Application.runInBackground = true; Application.targetFrameRate = 120;
            QualitySettings.vSyncCount = 0;
            Time.fixedDeltaTime = 1f / 60;
            started = Time.realtimeSinceStartup;
            string[] args = Environment.GetCommandLineArgs();
            address = Argument(args, "-address", address); portText = Argument(args, "-port", portText);
            metricsPath = Argument(args, "-metrics", "");
            float.TryParse(Argument(args, "-duration", "0"), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out duration);
            autoplay = Has(args, "-autoplay");
            network = new GameObject("NetworkManager").AddComponent<NetworkManager>();
            network.Address = address; network.Port = int.Parse(portText);
            network.Transport = Has(args, "-tcp") ? TransportType.Tcp : TransportType.Udp;
            network.Topology = Has(args, "-host") || Has(args, "-peer") ? NetworkTopology.HostPeer : NetworkTopology.DedicatedServerClient;
            network.ServerAuthority = !Has(args, "-owner-authority");
            network.EnableAutomaticPortForwarding = !Has(args, "-no-upnp");
            network.SimulationTickRate = 60; network.SnapshotRate = 5;
            CreateWorld(); template = CreatePlayerTemplate(); network.RegisterPrefabs(template);
            network.PeerConnected += SpawnPlayer;
            network.ClientDisconnected += reason => Debug.Log("SOCKETJACK_PHYSICS_DISCONNECTED " + reason);
            if (Has(args, "-server")) Begin("server");
            else if (Has(args, "-client")) Begin("client");
            else if (Has(args, "-host")) Begin("host");
            else if (Has(args, "-peer")) Begin("peer");
        }
        private static bool Has(string[] args, string key) => Array.IndexOf(args, key) >= 0;
        private static string Argument(string[] args, string key, string fallback)
        { int index = Array.IndexOf(args, key); return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback; }

        private void Begin(string selected)
        {
            if (network.Server != null || network.Client != null) return;
            try
            {
                network.Address = address; network.Port = int.Parse(portText);
                if (selected == "server") network.StartServer();
                else if (selected == "host") network.StartHost();
                else if (selected == "peer") network.StartPeer();
                else network.StartClient();
                if (network.Server != null) network.Server.MessageReceived += message =>
                {
                    if (message.MessageId == 60002 && message.Payload.Length == 8)
                        network.Server.SendMessage(message.PeerId, 60002, message.Payload, DeliveryMode.Unreliable);
                };
                if (network.Client != null) network.Client.MessageReceived += message =>
                {
                    if (message.MessageId != 60002 || message.Payload.Length != 8) return;
                    double elapsed = (System.Diagnostics.Stopwatch.GetTimestamp() - BitConverter.ToInt64(message.Payload, 0)) *
                        1000d / System.Diagnostics.Stopwatch.Frequency;
                    if (elapsed >= 0 && elapsed < 60000) roundTrips.Add(elapsed);
                };
                role = selected;
                Debug.Log("SOCKETJACK_PHYSICS_ROLE " + role + " port=" +
                    (network.Server == null ? network.Port : network.Server.Transport.LocalEndPoint.Port));
            }
            catch (Exception error) { Debug.LogError(error); }
        }
        private void SpawnPlayer(ulong peer)
        {
            int index = spawnOrdinal++;
            network.Spawn(1, new Vector3((index % 6) * 2.2f - 5.5f, 0.65f, (index / 6) * 2.2f),
                Quaternion.identity, ownerId: peer);
        }
        private GameObject CreatePlayerTemplate()
        {
            var player = new GameObject("Player (SocketJackObject)"); player.SetActive(false);
            var collider = player.AddComponent<BoxCollider>(); collider.size = Vector3.one;
            var material = new PhysicMaterial("Sliding cube") { dynamicFriction = 0.08f, staticFriction = 0.1f,
                frictionCombine = PhysicMaterialCombine.Minimum, bounciness = 0 };
            collider.sharedMaterial = material;
            var body = player.AddComponent<Rigidbody>();
            body.mass = 1; body.drag = 0.6f; body.angularDrag = 0.8f;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            body.constraints = RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            var visible = GameObject.CreatePrimitive(PrimitiveType.Cube); visible.name = "Interpolated visual";
            visible.transform.SetParent(player.transform, false);
            var visualCollider = visible.GetComponent<Collider>(); visualCollider.enabled = false; Destroy(visualCollider);
            visible.GetComponent<Renderer>().material = NewMaterial(new Color(0.22f, 0.72f, 0.92f));
            player.AddComponent<NetworkIdentity>();
            var sync = player.AddComponent<SocketJackObject>(); sync.ReadKeyboard = !autoplay; sync.KeyframeRate = 5;
            sync.Visual = visible.transform;
            return player;
        }
        private void CreateWorld()
        {
            var floor = GameObject.CreatePrimitive(PrimitiveType.Cube); floor.name = "Physics floor";
            floor.transform.position = new Vector3(0, -0.5f, 0); floor.transform.localScale = new Vector3(60, 1, 60);
            floor.GetComponent<Renderer>().material = NewMaterial(new Color(0.16f, 0.18f, 0.21f));
            for (int i = 0; i < 8; i++)
            {
                var obstacle = GameObject.CreatePrimitive(PrimitiveType.Cube); obstacle.name = "Static physics obstacle";
                obstacle.transform.position = new Vector3((i % 4) * 5 - 8, 0.5f, (i / 4) * 15 - 7.5f);
                obstacle.transform.localScale = new Vector3(1.5f, 1, 1.5f);
                obstacle.GetComponent<Renderer>().material = NewMaterial(new Color(0.35f, 0.38f, 0.42f));
            }
            var cameraObject = new GameObject("Main Camera"); cameraObject.tag = "MainCamera";
            var camera = cameraObject.AddComponent<Camera>(); camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.07f, 0.08f, 0.1f);
            cameraObject.transform.position = new Vector3(0, 20, -17); cameraObject.transform.LookAt(Vector3.zero);
            var light = new GameObject("Sun").AddComponent<Light>(); light.type = LightType.Directional;
            light.intensity = 1.1f; light.transform.rotation = Quaternion.Euler(50, -30, 0);
            RenderSettings.ambientLight = new Color(0.45f, 0.45f, 0.5f);
        }
        private static Material NewMaterial(Color color)
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return null;
            var template = Resources.Load<Material>("SocketJackPhysicsStandard");
            Material material;
            if (template != null) material = new Material(template);
            else
            {
                var shader = Shader.Find("Standard");
                if (shader == null) return null; // A headless graphics device does not need a render material.
                material = new Material(shader);
            }
            material.color = color; return material;
        }

        private void Update()
        {
            if (network == null) return;
            observedFrames++;
            everConnected |= network.IsClient;
            if (network.IsClient && Time.unscaledTime >= nextPing)
            {
                nextPing = Time.unscaledTime + 0.5f;
                if (!network.Client.SendMessage(60002, BitConverter.GetBytes(System.Diagnostics.Stopwatch.GetTimestamp()), DeliveryMode.Unreliable)) pingSendRejected++;
            }
            if (network.IsServer) maxConnectedPeers = Math.Max(maxConnectedPeers, network.Server.Peers.Count);
            if (network.IsClient)
                foreach (var state in network.Client.Objects.Values)
                    if (network.TryGetIdentity(state.Id, out var identity) && identity != null)
                    {
                        var sync = identity.GetComponent<SocketJackObject>();
                        if (sync == null) continue;
                        errorSum += sync.LastPositionError; errorSamples++;
                        if (identity.IsOwner)
                        {
                            if (autoplay)
                            {
                                int phase = ((int)(Time.realtimeSinceStartup - started) / 2) % 4;
                                sync.ReadKeyboard = false;
                                sync.SetInput(phase == 0 ? 1 : phase == 2 ? -1 : 0, phase == 1 ? 1 : phase == 3 ? -1 : 0);
                            }
                            var renderer = identity.GetComponentInChildren<Renderer>();
                            if (renderer != null && renderer.material != null) renderer.material.color = new Color(0.95f, 0.65f, 0.2f);
                        }
                    }
            ObserveCounters();
            if (!exiting && duration > 0 && Time.realtimeSinceStartup - started >= duration)
            { exiting = true; WriteMetrics(); network.Shutdown(); Application.Quit(0); }
        }
        private void OnGUI()
        {
            GUILayout.BeginArea(new Rect(14, 14, 420, 260), GUI.skin.box);
            GUILayout.Label("SocketJack.Unity · local physics multiplayer");
            GUILayout.Label("WASD / arrow keys: slide · Space: jump");
            GUILayout.Label("15-byte owner intent · 5 Hz sparse 70-byte corrections");
            if (role == "menu")
            {
                int selectedTopology = GUILayout.Toolbar((int)network.Topology, new[] { "Host / Peer", "Server / Client" });
                if (selectedTopology != (int)network.Topology) network.Topology = (NetworkTopology)selectedTopology;
                bool selectedAuthority = GUILayout.Toggle(network.ServerAuthority, "Server authority");
                if (selectedAuthority != network.ServerAuthority) network.ServerAuthority = selectedAuthority;
                bool selectedMapping = GUILayout.Toggle(network.EnableAutomaticPortForwarding, "Automatic router forwarding (listening role only)");
                if (selectedMapping != network.EnableAutomaticPortForwarding) network.EnableAutomaticPortForwarding = selectedMapping;
                address = GUILayout.TextField(address); portText = GUILayout.TextField(portText);
                if (network.Topology == NetworkTopology.HostPeer)
                {
                    if (GUILayout.Button("Host + play")) Begin("host");
                    if (GUILayout.Button("Join host as peer")) Begin("peer");
                }
                else
                {
                    if (GUILayout.Button("Dedicated server")) Begin("server");
                    if (GUILayout.Button("Join server as client")) Begin("client");
                }
            }
            else
            {
                GUILayout.Label("Role: " + role + " · " + network.Transport + " · peer " + network.LocalPeerId);
                GUILayout.Label(network.Topology + " · server authority: " + network.ServerAuthority);
                GUILayout.Label("Router: " + network.PortMappingStatus);
                GUILayout.Label("Objects: " + (network.IsServer ? network.Server.Objects.Count : network.Client == null ? 0 : network.Client.Objects.Count));
                GUILayout.Label("Mean correction error: " + (errorSamples == 0 ? 0 : errorSum / errorSamples).ToString("F3") + " m");
            }
            GUILayout.EndArea();
        }
        private void WriteMetrics()
        {
            ObserveCounters();
            var metrics = new RunMetrics { role = role, transport = network.Transport.ToString(),
                seconds = Time.realtimeSinceStartup - started, observedFrames = observedFrames,
                clientConnected = everConnected, connectedPeers = network.IsServer ? network.Server.Peers.Count : 0,
                objects = network.IsServer ? network.Server.Objects.Count : network.Client == null ? 0 : network.Client.Objects.Count,
                meanPositionError = errorSamples == 0 ? 0 : errorSum / errorSamples,
                maxObjects = maxObjects, maxConnectedPeers = maxConnectedPeers, topology = network.Topology.ToString(),
                serverAuthority = network.ServerAuthority, automaticPortForwarding = network.EnableAutomaticPortForwarding };
            roundTrips.Sort(); metrics.roundTrips = roundTrips.Count; metrics.pingSendRejected = pingSendRejected;
            if (roundTrips.Count > 0)
            {
                double sum = 0; foreach (double rtt in roundTrips) sum += rtt;
                metrics.rttMeanMilliseconds = sum / roundTrips.Count;
                metrics.rttP95Milliseconds = roundTrips[Math.Max(0, (int)Math.Ceiling(roundTrips.Count * 0.95) - 1)];
                metrics.rttMaxMilliseconds = roundTrips[roundTrips.Count - 1];
            }
            foreach (var values in totals.Values)
            {
                metrics.inputsSent += values.inputsSent; metrics.inputsReceived += values.inputsReceived;
                metrics.keyframesReceived += values.keyframesReceived; metrics.hardCorrections += values.hardCorrections;
                metrics.ownerStatesSent += values.ownerStatesSent;
                metrics.peakPositionError = Mathf.Max(metrics.peakPositionError, values.peakPositionError);
                metrics.peakNetworkVelocityCorrection = Mathf.Max(metrics.peakNetworkVelocityCorrection, values.peakNetworkVelocityCorrection);
                metrics.peakVisualStep = Mathf.Max(metrics.peakVisualStep, values.peakVisualStep);
            }
            string json = JsonUtility.ToJson(metrics, true);
            if (!string.IsNullOrEmpty(metricsPath))
            {
                string fullPath = Path.GetFullPath(metricsPath); Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
                File.WriteAllText(fullPath, json);
            }
            Debug.Log("SOCKETJACK_PHYSICS_METRICS " + json);
        }
        private void ObserveCounters()
        {
            IEnumerable<NetworkObject> objects = network.IsServer ? network.Server.Objects.Values :
                network.Client == null ? (IEnumerable<NetworkObject>)new NetworkObject[0] : network.Client.Objects.Values;
            maxObjects = Math.Max(maxObjects, network.IsServer ? network.Server.Objects.Count : network.Client == null ? 0 : network.Client.Objects.Count);
            foreach (var state in objects)
                if (network.TryGetIdentity(state.Id, out var identity) && identity != null)
                {
                    var sync = identity.GetComponent<SocketJackObject>(); if (sync == null) continue;
                    if (!totals.TryGetValue(state.Id, out var values)) { values = new RunMetrics(); totals.Add(state.Id, values); }
                    values.inputsSent = sync.InputsSent; values.inputsReceived = sync.InputsReceived;
                    values.keyframesReceived = sync.KeyframesReceived; values.hardCorrections = sync.HardCorrections;
                    values.ownerStatesSent = sync.OwnerStatesSent;
                    values.peakPositionError = sync.PeakPositionError;
                    values.peakNetworkVelocityCorrection = sync.PeakNetworkVelocityCorrection; values.peakVisualStep = sync.PeakVisualStep;
                }
        }
        private void OnApplicationQuit() { if (!exiting && network != null) WriteMetrics(); }
    }
}
