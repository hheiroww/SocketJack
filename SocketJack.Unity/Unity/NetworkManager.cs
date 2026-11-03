using System;
using System.Collections.Generic;
using System.Net;
using SocketJack.Unity.Transport;
using UnityEngine;
using RuntimeObject = SocketJack.Unity.NetworkObject;

namespace SocketJack.Unity.Components
{
    /// <summary>Scene entry point. Register prefabs in the same order in every build.
    /// All networking and callbacks are pumped by Update on Unity's main thread.</summary>
    [DisallowMultipleComponent]
    public sealed class NetworkManager : MonoBehaviour
    {
        [SerializeField] private TransportType transport = TransportType.Udp;
        [SerializeField] private string address = "127.0.0.1";
        [SerializeField] private int port = 7777;
        [SerializeField] private int maxPlayers = 4096;
        [SerializeField] private int tickRate = 30;
        [SerializeField] private int maxEventsPerFrame = 4096;
        [SerializeField] private GameObject[] prefabs = Array.Empty<GameObject>();
        private readonly Dictionary<ulong, NetworkIdentity> instances = new Dictionary<ulong, NetworkIdentity>();
        private readonly List<ulong> removed = new List<ulong>();
        private float captureAccumulator;

        public NetworkServer Server { get; private set; }
        public NetworkClient Client { get; private set; }
        public bool IsServer => Server != null && Server.IsRunning;
        public bool IsClient => Client != null && Client.IsConnected;
        public ulong LocalPeerId => Client == null ? 0 : Client.LocalPeerId;
        public TransportType Transport { get => transport; set { RequireStopped(); transport = value; } }
        public string Address { get => address; set { RequireStopped(); address = value; } }
        public int Port { get => port; set { RequireStopped(); port = value; } }
        /// <summary>Set before StartServer/StartHost. Validate short-lived game tickets here.</summary>
        public Func<IPEndPoint, byte[], bool> ConnectionValidator { get; set; }
        public event Action<NetworkInput> InputReceived;
        public event Action<ulong> PeerConnected;
        public event Action<ulong, string> PeerDisconnected;
        public event Action ClientConnected;
        public event Action<string> ClientDisconnected;

        public void RegisterPrefabs(params GameObject[] registeredPrefabs)
        {
            RequireStopped();
            prefabs = registeredPrefabs == null ? Array.Empty<GameObject>() : (GameObject[])registeredPrefabs.Clone();
        }

        public void StartServer()
        {
            RequireStopped();
            CheckPlatform();
            var endpoint = new IPEndPoint(IPAddress.Any, port);
            var serverTransport = GameTransport.Create(transport, NewTransportOptions());
            try
            {
                Server = new NetworkServer(serverTransport, NewSessionOptions());
                Server.InputReceived += input => InputReceived?.Invoke(input);
                Server.PeerConnected += peer => PeerConnected?.Invoke(peer);
                Server.PeerDisconnected += (peer, reason) => PeerDisconnected?.Invoke(peer, reason);
                Server.Start(endpoint);
            }
            catch { Server?.Dispose(); Server = null; serverTransport.Dispose(); throw; }
        }

        public void StartClient(byte[] connectionData = null)
        {
            RequireStopped();
            CheckPlatform();
            StartClientCore(connectionData);
        }

        public void StartHost(byte[] connectionData = null)
        {
            StartServer();
            try { StartClientCore(connectionData, IPAddress.Loopback); }
            catch { Shutdown(); throw; }
        }

        private void StartClientCore(byte[] connectionData, IPAddress target = null)
        {
            var clientTransport = GameTransport.Create(transport, NewTransportOptions());
            try
            {
                Client = new NetworkClient(clientTransport, NewSessionOptions());
                Client.Connected += () => ClientConnected?.Invoke();
                Client.Disconnected += reason => ClientDisconnected?.Invoke(reason);
                Client.ObjectSpawned += OnObjectSpawned;
                Client.ObjectChanged += OnObjectChanged;
                Client.ObjectDespawned += OnObjectDespawned;
                int targetPort = target != null && Server != null ? Server.Transport.LocalEndPoint.Port : port;
                Client.Connect(new IPEndPoint(target ?? IPAddress.Parse(address), targetPort), connectionData);
            }
            catch { Client?.Dispose(); Client = null; clientTransport.Dispose(); throw; }
        }

        /// <summary>Instantiate and replicate a registered prefab. IDs start at one.</summary>
        public NetworkIdentity Spawn(uint prefabId, Vector3 position, Quaternion rotation, ulong ownerId = 0, int roomId = 0)
        {
            if (!IsServer) throw new InvalidOperationException("Only the server can spawn objects.");
            var prefab = FindPrefab(prefabId);
            var instance = Instantiate(prefab, position, rotation);
            var identity = instance.GetComponent<NetworkIdentity>() ?? instance.AddComponent<NetworkIdentity>();
            var synchronizer = instance.GetComponent<NetworkTransform>();
            RuntimeObject state;
            try { state = Server.Spawn(prefabId, ownerId, roomId, synchronizer == null ? Array.Empty<byte>() : synchronizer.CaptureState()); }
            catch { Destroy(instance); throw; }
            identity.Bind(this, state);
            instances.Add(state.Id, identity);
            return identity;
        }

        public void Despawn(ulong objectId)
        {
            if (!IsServer) throw new InvalidOperationException("Only the server can despawn objects.");
            Server.Despawn(objectId);
            RemoveInstance(objectId);
        }

        public bool TryGetIdentity(ulong objectId, out NetworkIdentity identity) => instances.TryGetValue(objectId, out identity);

        private void Update()
        {
            if (IsServer)
            {
                captureAccumulator += Time.unscaledDeltaTime;
                if (captureAccumulator >= 1f / tickRate)
                {
                    captureAccumulator %= 1f / tickRate;
                    foreach (var pair in instances)
                    {
                        if (pair.Value == null) continue;
                        var synchronizer = pair.Value.GetComponent<NetworkTransform>();
                        if (synchronizer != null && Server.Objects.ContainsKey(pair.Key))
                            Server.SetState(pair.Key, synchronizer.CaptureState());
                    }
                }
                Server.Update(Time.unscaledDeltaTime, maxEventsPerFrame);
                removed.Clear();
                foreach (var pair in instances)
                {
                    if (pair.Value == null || !Server.Objects.TryGetValue(pair.Key, out var state)) removed.Add(pair.Key);
                    else pair.Value.Bind(this, state);
                }
                foreach (var id in removed) RemoveInstance(id);
            }
            Client?.Update(Time.unscaledDeltaTime, maxEventsPerFrame);
        }

        private void OnObjectSpawned(RuntimeObject state)
        {
            if (IsServer) return; // Host renders the authoritative world once.
            GameObject prefab;
            try { prefab = FindPrefab(state.PrefabId); }
            catch (InvalidOperationException error)
            {
                Debug.LogError(error.Message);
                Client.Stop();
                return;
            }
            var instance = Instantiate(prefab);
            var identity = instance.GetComponent<NetworkIdentity>() ?? instance.AddComponent<NetworkIdentity>();
            identity.Bind(this, state);
            instances.Add(state.Id, identity);
            instance.GetComponent<NetworkTransform>()?.ApplyState(state.State, true);
        }

        private void OnObjectChanged(RuntimeObject state)
        {
            if (IsServer || !instances.TryGetValue(state.Id, out var identity) || identity == null) return;
            identity.Bind(this, state);
            identity.GetComponent<NetworkTransform>()?.ApplyState(state.State, false);
        }

        private void OnObjectDespawned(ulong objectId) { if (!IsServer) RemoveInstance(objectId); }
        private void RemoveInstance(ulong id)
        {
            if (!instances.TryGetValue(id, out var identity)) return;
            instances.Remove(id);
            if (identity != null) Destroy(identity.gameObject);
        }
        private GameObject FindPrefab(uint id)
        {
            if (id == 0 || id > prefabs.Length || prefabs[id - 1] == null)
                throw new InvalidOperationException("Unknown network prefab ID " + id + ". Register the same prefab list in every build.");
            return prefabs[id - 1];
        }
        private TransportOptions NewTransportOptions() => new TransportOptions { MaxPeers = maxPlayers, ConnectionValidator = ConnectionValidator };
        private NetworkSessionOptions NewSessionOptions() => new NetworkSessionOptions { TickRate = tickRate, SnapshotRate = Math.Min(10, tickRate), MaxPeers = maxPlayers };
        private void RequireStopped()
        {
            if (Server != null || Client != null) throw new InvalidOperationException("Call Shutdown before starting or reconfiguring networking.");
            if (tickRate < 1 || tickRate > 240 || maxEventsPerFrame < 1) throw new InvalidOperationException("Invalid network tick or event budget.");
            for (int i = 0; i < prefabs.Length; i++)
                if (prefabs[i] == null) throw new InvalidOperationException("Network prefab slot " + (i + 1) + " is empty.");
        }
        private static void CheckPlatform()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            throw new PlatformNotSupportedException("Raw UDP/TCP sockets are unavailable in WebGL. Supply a browser bridge via IGameTransport.");
#endif
        }
        public void Shutdown()
        {
            Client?.Dispose(); Client = null;
            Server?.Dispose(); Server = null;
            foreach (var pair in instances) if (pair.Value != null) Destroy(pair.Value.gameObject);
            instances.Clear(); captureAccumulator = 0;
        }
        private void OnDestroy() => Shutdown();
        private void OnApplicationQuit() => Shutdown();
    }
}
