using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using SocketJack.Unity.Transport;
using UnityEngine;
using RuntimeObject = SocketJack.Unity.NetworkObject;

namespace SocketJack.Unity.Components
{
    public enum NetworkTopology
    {
        [InspectorName("Host / Peer")] HostPeer = 0,
        [InspectorName("Dedicated Server / Client")] DedicatedServerClient = 1
    }
    /// <summary>Scene entry point. Register prefabs in the same order in every build.
    /// All networking and callbacks are pumped by Update on Unity's main thread.</summary>
    [DisallowMultipleComponent]
    public sealed class NetworkManager : MonoBehaviour
    {
        [Header("Session topology and authority")]
        [SerializeField] private NetworkTopology topology = NetworkTopology.DedicatedServerClient;
        [SerializeField] private bool serverAuthority = true;
        [SerializeField, HideInInspector] private NetworkTopology previousTopology = NetworkTopology.DedicatedServerClient;
        [Header("Transport (independent of topology)")]
        [SerializeField] private bool enableAutomaticPortForwarding = true;
        [SerializeField] private TransportType transport = TransportType.Udp;
        [SerializeField] private string address = "127.0.0.1";
        [SerializeField] private int port = 7777;
        [SerializeField] private int maxPlayers = 4096;
        [SerializeField] private int tickRate = 30;
        [SerializeField] private int snapshotRate = 5;
        [SerializeField] private int maxEventsPerFrame = 4096;
        [SerializeField] private GameObject[] prefabs = Array.Empty<GameObject>();
        private readonly Dictionary<ulong, NetworkIdentity> instances = new Dictionary<ulong, NetworkIdentity>();
        private readonly List<ulong> removed = new List<ulong>();
        private sealed class PhysicsRelay
        {
            internal byte[] Payload;
            internal ulong ObjectId;
            internal int Room;
        }
        private readonly Dictionary<ulong, Queue<PhysicsRelay>> physicsRelays = new Dictionary<ulong, Queue<PhysicsRelay>>();
        private int relayCursor;
        private const int MaxPhysicsRelaysPerPeer = 256;
        private float captureAccumulator;
        private float ownerTransformAccumulator;
        private CancellationTokenSource mappingCancellation;
        private Task<PortMappingLease> mappingTask;
        private PortMappingLease mappingLease;
        private INatPortMapper activeMapper;
        private bool ownsMapper;

        public NetworkServer Server { get; private set; }
        public NetworkClient Client { get; private set; }
        public bool IsServer => Server != null && Server.IsRunning;
        public bool IsClient => Client != null && Client.IsConnected;
        public ulong LocalPeerId => Client == null ? 0 : Client.LocalPeerId;
        public TransportType Transport { get => transport; set { RequireStopped(); transport = value; } }
        public string Address { get => address; set { RequireStopped(); address = value; } }
        public int Port { get => port; set { RequireStopped(); port = value; } }
        public int SimulationTickRate { get => tickRate; set { RequireStopped(); tickRate = value; } }
        public int SnapshotRate { get => snapshotRate; set { RequireStopped(); snapshotRate = value; } }
        public NetworkTopology Topology
        {
            get => topology;
            set { RequireStopped(); topology = previousTopology = value; if (value == NetworkTopology.DedicatedServerClient) serverAuthority = true; }
        }
        public bool ServerAuthority { get => serverAuthority; set { RequireStopped(); serverAuthority = value; } }
        public bool EnableAutomaticPortForwarding { get => enableAutomaticPortForwarding; set { RequireStopped(); enableAutomaticPortForwarding = value; } }
        /// <summary>Optional caller-owned mapper, useful for platform integrations and deterministic tests.</summary>
        public INatPortMapper PortMapper { get; set; }
        public string PortMappingStatus { get; private set; } = "Not listening";
        public bool IsPortMapped { get; private set; }
        public string ExternalEndpoint { get; private set; } = "";
        /// <summary>Additional game validation for opted-in owner-authoritative state. Physics/transform codecs are always checked first.</summary>
        public Func<RuntimeObject, byte[], bool> OwnerStateValidator { get; set; }
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
            if (topology != NetworkTopology.DedicatedServerClient) throw new InvalidOperationException("Select Dedicated Server / Client topology to start a dedicated server.");
            StartListener();
        }
        private void StartListener()
        {
            RequireStopped();
            CheckPlatform();
            var endpoint = new IPEndPoint(IPAddress.Any, port);
            var serverTransport = GameTransport.Create(transport, NewTransportOptions());
            try
            {
                Server = new NetworkServer(serverTransport, NewSessionOptions());
                Server.InputReceived += OnServerInput;
                Server.ObjectChanged += OnServerObjectChanged;
                Server.OwnerStateValidator = ValidateOwnerState;
                Server.PeerConnected += peer => PeerConnected?.Invoke(peer);
                Server.PeerDisconnected += (peer, reason) => { physicsRelays.Remove(peer); PeerDisconnected?.Invoke(peer, reason); };
                Server.Start(endpoint);
                BeginPortMapping();
            }
            catch { Server?.Dispose(); Server = null; serverTransport.Dispose(); throw; }
        }

        public void StartClient(byte[] connectionData = null)
        {
            if (topology != NetworkTopology.DedicatedServerClient) throw new InvalidOperationException("Use StartPeer with Host / Peer topology.");
            RequireStopped();
            CheckPlatform();
            StartClientCore(connectionData);
        }

        public void StartHost(byte[] connectionData = null)
        {
            if (topology != NetworkTopology.HostPeer) throw new InvalidOperationException("Select Host / Peer topology to start a host.");
            StartListener();
            try { StartClientCore(connectionData, IPAddress.Loopback); }
            catch { Shutdown(); throw; }
        }
        public void StartPeer(byte[] connectionData = null)
        {
            if (topology != NetworkTopology.HostPeer) throw new InvalidOperationException("Select Host / Peer topology to start a peer.");
            RequireStopped(); CheckPlatform(); StartClientCore(connectionData);
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
                Client.MessageReceived += OnClientMessage;
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
            // Inactive prefab Rigidbodies have not joined the physics scene yet. Activate and
            // initialize their pose before reading the authoritative spawn baseline.
            instance.SetActive(true);
            var body = instance.GetComponent<Rigidbody>();
            if (body != null) { body.position = position; body.rotation = rotation; }
            var identity = instance.GetComponent<NetworkIdentity>() ?? instance.AddComponent<NetworkIdentity>();
            var synchronizer = instance.GetComponent<NetworkTransform>();
            var physicsObject = instance.GetComponent<SocketJackObject>();
            bool objectServerAuthority = physicsObject == null ? serverAuthority : physicsObject.ResolveServerAuthority(serverAuthority);
            RuntimeObject state;
            try { state = Server.Spawn(prefabId, ownerId, roomId, physicsObject != null ? physicsObject.CaptureState() :
                synchronizer == null ? Array.Empty<byte>() : synchronizer.CaptureState(), objectServerAuthority); }
            catch { Destroy(instance); throw; }
            identity.Bind(this, state);
            instances.Add(state.Id, identity);
            instance.SetActive(true);
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
            UpdatePortMapping();
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
                        var physicsObject = pair.Value.GetComponent<SocketJackObject>();
                        if (!Server.Objects.TryGetValue(pair.Key, out var current) || (!current.ServerAuthority && current.OwnerId != 0)) continue;
                        if (physicsObject != null && physicsObject.CaptureEligible(Time.unscaledTimeAsDouble))
                            Server.SetState(pair.Key, physicsObject.CaptureState());
                        else if (physicsObject == null && synchronizer != null && Server.Objects.ContainsKey(pair.Key))
                            Server.SetState(pair.Key, synchronizer.CaptureState());
                    }
                }
                FlushPhysicsRelays();
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
            CaptureOwnerTransforms();
        }

        private void BeginPortMapping()
        {
            if (!enableAutomaticPortForwarding) { PortMappingStatus = "Disabled"; return; }
            try
            {
                mappingCancellation = new CancellationTokenSource();
                activeMapper = PortMapper ?? new UpnpPortMapper(); ownsMapper = PortMapper == null;
                PortMappingStatus = "Discovering router";
                // Only a bound listener reaches this path. A peer/client never opens or maps a service port.
                mappingTask = activeMapper.RequestMappingAsync(Server.Transport.LocalEndPoint.Port, transport, mappingCancellation.Token);
            }
            catch { PortMappingStatus = "Failed: automatic router mapping could not start"; }
        }
        private void UpdatePortMapping()
        {
            if (mappingTask != null && mappingTask.IsCompleted)
            {
                var completed = mappingTask; mappingTask = null;
                try
                {
                    mappingLease = completed.GetAwaiter().GetResult();
                    IsPortMapped = mappingLease.IsMapped;
                    ExternalEndpoint = mappingLease.ExternalEndpoint == null ? "" : mappingLease.ExternalEndpoint.ToString();
                    PortMappingStatus = mappingLease.Status + (string.IsNullOrEmpty(mappingLease.Error) ? "" : ": " + mappingLease.Error);
                }
                catch { IsPortMapped = false; PortMappingStatus = "Failed: automatic router mapping did not complete"; }
            }
            if (mappingLease != null && !string.IsNullOrEmpty(mappingLease.RenewalError))
            { IsPortMapped = false; PortMappingStatus = "Renewal stopped: " + mappingLease.RenewalError; }
        }
        private void StopPortMapping()
        {
            mappingCancellation?.Cancel(); mappingCancellation?.Dispose(); mappingCancellation = null;
            var pending = mappingTask; mappingTask = null;
            if (pending != null)
                _ = pending.ContinueWith(task =>
                {
                    if (task.Status == TaskStatus.RanToCompletion) task.Result?.Dispose();
                    else { var observed = task.Exception; }
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            mappingLease?.Dispose(); mappingLease = null;
            if (ownsMapper && activeMapper is IDisposable disposable) disposable.Dispose();
            activeMapper = null; ownsMapper = false;
            IsPortMapped = false; ExternalEndpoint = ""; PortMappingStatus = "Not listening";
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
            instance.SetActive(true);
            var physicsObject = instance.GetComponent<SocketJackObject>();
            if (physicsObject != null) physicsObject.ApplyState(state.State, true);
            else instance.GetComponent<NetworkTransform>()?.ApplyState(state.State, true);
        }

        private void OnObjectChanged(RuntimeObject state)
        {
            if (IsServer || !instances.TryGetValue(state.Id, out var identity) || identity == null) return;
            identity.Bind(this, state);
            if (!state.ServerAuthority && identity.IsOwner) return;
            var physicsObject = identity.GetComponent<SocketJackObject>();
            if (physicsObject != null) physicsObject.ApplyState(state.State, false);
            else identity.GetComponent<NetworkTransform>()?.ApplyState(state.State, false);
        }

        private void OnServerObjectChanged(RuntimeObject state)
        {
            if (state.ServerAuthority || !instances.TryGetValue(state.Id, out var identity) || identity == null) return;
            identity.Bind(this, state);
            var physicsObject = identity.GetComponent<SocketJackObject>();
            if (physicsObject != null) { if (!physicsObject.HasStateAuthority) physicsObject.ApplyState(state.State, false); }
            else if (!identity.IsOwner) identity.GetComponent<NetworkTransform>()?.ApplyState(state.State, false);
        }
        private bool ValidateOwnerState(RuntimeObject state, byte[] bytes)
        {
            if (!instances.TryGetValue(state.Id, out var identity) || identity == null) return false;
            if (identity.GetComponent<SocketJackObject>() != null)
            {
                try { Physics.PhysicsBinarySerializer.DeserializeSnapshot(bytes); }
                catch (Exception error) when (error is ArgumentException || error is IOException || error is InvalidDataException) { return false; }
            }
            else if (identity.GetComponent<NetworkTransform>() != null)
            {
                if (!NetworkTransform.IsValidState(bytes)) return false;
            }
            else return OwnerStateValidator != null && OwnerStateValidator(state, bytes);
            return OwnerStateValidator == null || OwnerStateValidator(state, bytes);
        }
        private void CaptureOwnerTransforms()
        {
            if (!IsClient) return;
            ownerTransformAccumulator += Time.unscaledDeltaTime;
            if (ownerTransformAccumulator < 1f / snapshotRate) return;
            ownerTransformAccumulator %= 1f / snapshotRate;
            foreach (var pair in instances)
            {
                if (pair.Value == null || !pair.Value.IsOwner || pair.Value.ServerAuthority || pair.Value.GetComponent<SocketJackObject>() != null) continue;
                var synchronizer = pair.Value.GetComponent<NetworkTransform>();
                if (synchronizer != null) Client.SendOwnerState(pair.Key, synchronizer.CaptureState());
            }
        }

        private void OnServerInput(NetworkInput input)
        {
            if (instances.TryGetValue(input.ObjectId, out var identity) && identity != null)
            {
                var physicsObject = identity.GetComponent<SocketJackObject>();
                if (physicsObject != null)
                {
                    if (!physicsObject.ReceiveOwnerInput(input.Payload)) return;
                    byte[] relay;
                    using (var stream = new MemoryStream(input.Payload.Length + 8))
                    using (var writer = new BinaryWriter(stream))
                    { writer.Write(input.ObjectId); writer.Write(input.Payload); relay = stream.ToArray(); }
                    int room = Server.GetPeerRoom(input.PeerId);
                    foreach (ulong peer in new List<ulong>(Server.Peers))
                    {
                        if (peer == input.PeerId) continue;
                        int peerRoom;
                        try { peerRoom = Server.GetPeerRoom(peer); }
                        catch (KeyNotFoundException) { continue; }
                        if (peerRoom == room) QueuePhysicsRelay(peer, input.ObjectId, room, relay);
                    }
                }
            }
            InputReceived?.Invoke(input);
        }

        private void QueuePhysicsRelay(ulong peer, ulong objectId, int room, byte[] payload)
        {
            if (!physicsRelays.TryGetValue(peer, out var queue))
            {
                if (Server.SendMessage(peer, SocketJackObject.InputRelayMessageId, payload, DeliveryMode.ReliableUnordered)) return;
                queue = new Queue<PhysicsRelay>(); physicsRelays.Add(peer, queue);
            }
            if (queue.Count >= MaxPhysicsRelaysPerPeer)
            {
                physicsRelays.Remove(peer);
                Server.Disconnect(peer, "Physics input relay queue exceeded");
                return;
            }
            // Payloads are immutable 23-byte object-ID + intent frames shared by recipients.
            queue.Enqueue(new PhysicsRelay { Payload = payload, ObjectId = objectId, Room = room });
        }
        private void FlushPhysicsRelays()
        {
            if (physicsRelays.Count == 0) return;
            var peers = new List<ulong>(physicsRelays.Keys);
            int budget = 256;
            for (int scanned = 0; scanned < peers.Count && budget > 0; scanned++)
            {
                if (relayCursor >= peers.Count) relayCursor = 0;
                ulong peer = peers[relayCursor++];
                if (!physicsRelays.TryGetValue(peer, out var queue)) continue;
                int currentRoom;
                try { currentRoom = Server.GetPeerRoom(peer); }
                catch (KeyNotFoundException) { physicsRelays.Remove(peer); continue; }
                int peerBudget = 16;
                while (queue.Count > 0 && peerBudget-- > 0 && budget-- > 0)
                {
                    var relay = queue.Peek();
                    // Visibility may have changed while native sending was backpressured.
                    // Unsent input is revoked with the room/object instead of leaking into a new view.
                    if (relay.Room != currentRoom || !Server.Objects.TryGetValue(relay.ObjectId, out var obj) || obj.RoomId != currentRoom)
                    { queue.Dequeue(); continue; }
                    if (!Server.SendMessage(peer, SocketJackObject.InputRelayMessageId, relay.Payload, DeliveryMode.ReliableUnordered)) break;
                    queue.Dequeue();
                }
                if (queue.Count == 0) physicsRelays.Remove(peer);
            }
        }

        private void OnClientMessage(NetworkMessage message)
        {
            if (IsServer || message.MessageId != SocketJackObject.InputRelayMessageId || message.Payload.Length <= 8 || message.Payload.Length > 64) return;
            ulong objectId;
            using (var stream = new MemoryStream(message.Payload, false))
            using (var reader = new BinaryReader(stream)) objectId = reader.ReadUInt64();
            if (!instances.TryGetValue(objectId, out var identity) || identity == null) return;
            var physicsObject = identity.GetComponent<SocketJackObject>();
            if (physicsObject == null) return;
            var input = new byte[message.Payload.Length - 8]; Buffer.BlockCopy(message.Payload, 8, input, 0, input.Length);
            physicsObject.ReceiveRelayedInput(input);
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
        private NetworkSessionOptions NewSessionOptions() => new NetworkSessionOptions { TickRate = tickRate, SnapshotRate = Math.Min(snapshotRate, tickRate), MaxPeers = maxPlayers, ServerAuthority = serverAuthority };
        private void OnValidate()
        {
            if (topology != previousTopology && topology == NetworkTopology.DedicatedServerClient) serverAuthority = true;
            previousTopology = topology;
        }
        private void RequireStopped()
        {
            if (Server != null || Client != null) throw new InvalidOperationException("Call Shutdown before starting or reconfiguring networking.");
            if (tickRate < 1 || tickRate > 240 || snapshotRate < 1 || snapshotRate > tickRate || maxEventsPerFrame < 1)
                throw new InvalidOperationException("Invalid network tick, snapshot rate or event budget.");
            for (int i = 0; i < prefabs.Length; i++)
            {
                if (prefabs[i] == null) throw new InvalidOperationException("Network prefab slot " + (i + 1) + " is empty.");
                if (prefabs[i].GetComponent<SocketJackObject>() != null && prefabs[i].GetComponent<NetworkTransform>() != null)
                    throw new InvalidOperationException("A network prefab cannot combine SocketJackObject and NetworkTransform.");
            }
        }
        private static void CheckPlatform()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            throw new PlatformNotSupportedException("Raw UDP/TCP sockets are unavailable in WebGL. Supply a browser bridge via IGameTransport.");
#endif
        }
        public void Shutdown()
        {
            StopPortMapping();
            Client?.Dispose(); Client = null;
            Server?.Dispose(); Server = null;
            foreach (var pair in instances) if (pair.Value != null) Destroy(pair.Value.gameObject);
            instances.Clear(); captureAccumulator = 0;
            ownerTransformAccumulator = 0;
            physicsRelays.Clear(); relayCursor = 0;
        }
        private void OnDestroy() => Shutdown();
        private void OnApplicationQuit() => Shutdown();
    }
}
