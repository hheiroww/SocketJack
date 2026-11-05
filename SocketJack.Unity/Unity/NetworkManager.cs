using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using SocketJack.Multiplayer.Transport;
using SocketJack.Multiplayer.Serialization;
using UnityEngine;
using RuntimeObject = SocketJack.Multiplayer.NetworkObject;

namespace SocketJack.Multiplayer.Components
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
        [SerializeField] private int maxVisibleObjectsPerPeer = 4096;
        [SerializeField] private GameObject[] prefabs = Array.Empty<GameObject>();
        [Header("Member synchronization serializer")]
        [SerializeField, InspectorName("Serializer")] private NetworkSerializerKind serializerKind = NetworkSerializerKind.BinaryFormatterSocketJack;
        [SerializeField, Tooltip("Must implement INetworkSerializerProvider. Used by the Custom Provider serializer option.")]
        private MonoBehaviour serializerProvider;
        private INetworkSerializer runtimeSerializer;
        private NetworkMemberSyncOptions memberSyncOptions = new NetworkMemberSyncOptions();
        private readonly Dictionary<ulong, NetworkIdentity> instances = new Dictionary<ulong, NetworkIdentity>();
        private readonly List<ulong> removed = new List<ulong>();
        private sealed class PhysicsRelay
        {
            internal byte[] Payload;
            internal ulong ObjectId;
            internal int Room;
            internal ulong View;
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
        private bool shuttingDown;
        private INetworkObjectPool objectPool;
        public INetworkObjectPool ObjectPool { get => objectPool; set { RequireStopped(); objectPool = value; } }
        /// <summary>Optional transport provider, configured before startup (for example WebSocket/relay).</summary>
        public Func<bool, IGameTransport> TransportFactory { get; set; }
        public string CompatibilityId { get; set; } = "";
        public int ObjectCount => instances.Count;
        public IReadOnlyDictionary<ulong, NetworkIdentity> SpawnedObjects => instances;

        public NetworkServer Server { get; private set; }
        public NetworkClient Client { get; private set; }
        public NetworkMemberServer MemberServer { get; private set; }
        public NetworkMemberClient MemberClient { get; private set; }
        public NetworkEventHub Events { get; private set; }
        public NetworkLatencyProbe Latency { get; private set; }
        /// <summary>Optional runtime serializer override. Configure while stopped, before any schema is bound.</summary>
        public INetworkSerializer Serializer
        {
            get
            {
                if (runtimeSerializer != null) return runtimeSerializer;
                if (serializerKind == NetworkSerializerKind.BinaryFormatterSocketJack) return runtimeSerializer = CreateDefaultSerializer();
                if (!(serializerProvider is INetworkSerializerProvider provider)) throw new InvalidOperationException("Custom serializer provider must implement INetworkSerializerProvider.");
                return runtimeSerializer = provider.CreateSerializer() ?? throw new InvalidOperationException("Serializer provider returned null.");
            }
            set { RequireStopped(); runtimeSerializer = value ?? throw new ArgumentNullException(nameof(value)); }
        }
        public NetworkSerializerKind SerializerKind { get => serializerKind; set { RequireStopped(); serializerKind = value; runtimeSerializer = null; } }
        public MonoBehaviour SerializerProvider { get => serializerProvider; set { RequireStopped(); serializerProvider = value; runtimeSerializer = null; } }
        /// <summary>Configure before starting. Coordinators snapshot these bounded work/traffic limits.</summary>
        public NetworkMemberSyncOptions MemberSyncOptions { get => memberSyncOptions; set { RequireStopped(); memberSyncOptions = value ?? throw new ArgumentNullException(nameof(value)); } }
        public Func<RuntimeObject, ushort, object, bool> MemberValueValidator { get; set; }
        public bool IsServer => Server != null && Server.IsRunning;
        public bool IsClient => Client != null && Client.IsConnected;
        public ulong LocalPeerId => Client == null ? 0 : Client.LocalPeerId;
        public TransportType Transport { get => transport; set { RequireStopped(); transport = value; } }
        public string Address { get => address; set { RequireStopped(); address = value; } }
        public int Port { get => port; set { RequireStopped(); port = value; } }
        public int MaxPlayers { get => maxPlayers; set { RequireStopped(); if (value < 1 || value > 65535) throw new ArgumentOutOfRangeException(nameof(value)); maxPlayers = value; } }
        public int SimulationTickRate { get => tickRate; set { RequireStopped(); tickRate = value; } }
        public int SnapshotRate { get => snapshotRate; set { RequireStopped(); snapshotRate = value; } }
        public int MaxVisibleObjectsPerPeer { get => maxVisibleObjectsPerPeer; set { RequireStopped(); if (value < 1 || value > 100000) throw new ArgumentOutOfRangeException(nameof(value)); maxVisibleObjectsPerPeer = value; } }
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
        public event Action ListenerStarted;
        public event Action SessionStopped;
        public event Action<NetworkIdentity> ObjectSpawned;
        public event Action<ulong> ObjectDespawned;

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
#if UNITY_WEBGL && !UNITY_EDITOR
            throw new PlatformNotSupportedException("A browser can join a server but cannot host a listening game/web service.");
#else
            RequireStopped();
            CheckPlatform();
            ValidateMemberPrefabs();
            var endpoint = new IPEndPoint(IPAddress.Any, port);
            var serverTransport = TransportFactory == null ? GameTransport.Create(transport, NewTransportOptions()) : TransportFactory(true);
            try
            {
                Server = new NetworkServer(serverTransport, NewSessionOptions());
                if (Events == null) Events = new NetworkEventHub(); Events.AttachServer(Server);
                if (Latency == null) Latency = new NetworkLatencyProbe(); Latency.AttachServer(Server);
                MemberServer = new NetworkMemberServer(Server, memberSyncOptions);
                MemberServer.BindingError += OnMemberError;
                MemberServer.OwnerValueValidator = (obj, member, value) => MemberValueValidator == null || MemberValueValidator(obj, member, value);
                MemberServer.ApplyOwnerValue = obj => Client == null || !Client.IsConnected || obj.OwnerId != Client.LocalPeerId;
                Server.InputReceived += OnServerInput;
                Server.ObjectChanged += OnServerObjectChanged;
                Server.OwnershipChanged += (obj, previous) => { if (instances.TryGetValue(obj.Id, out var identity)) identity.Bind(this, obj); };
                Server.OwnerStateValidator = ValidateOwnerState;
                Server.PeerConnected += peer => PeerConnected?.Invoke(peer);
                Server.PeerDisconnected += (peer, reason) => { physicsRelays.Remove(peer); PeerDisconnected?.Invoke(peer, reason); };
                Server.Start(endpoint);
                SpawnSceneObjects();
                BeginPortMapping();
                ListenerStarted?.Invoke();
            }
            catch { MemberServer?.Dispose(); MemberServer = null; Server?.Dispose(); Server = null; serverTransport.Dispose(); throw; }
#endif
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
            StartHostListener();
            try { ConnectLocalHost(connectionData); }
            catch { Shutdown(); throw; }
        }
        /// <summary>Start the host listener first, allowing an embedded account service to issue a local game ticket.</summary>
        public void StartHostListener()
        {
            if (topology != NetworkTopology.HostPeer) throw new InvalidOperationException("Select Host / Peer topology to start a host.");
            StartListener();
        }
        public void ConnectLocalHost(byte[] connectionData = null)
        {
            if (topology != NetworkTopology.HostPeer || !IsServer || Client != null)
                throw new InvalidOperationException("Start a host listener before connecting its one local player.");
            StartClientCore(connectionData, IPAddress.Loopback);
        }
        public void StartPeer(byte[] connectionData = null)
        {
            if (topology != NetworkTopology.HostPeer) throw new InvalidOperationException("Select Host / Peer topology to start a peer.");
            RequireStopped(); CheckPlatform(); StartClientCore(connectionData);
        }

        private void StartClientCore(byte[] connectionData, IPAddress target = null)
        {
            ValidateMemberPrefabs();
            var clientTransport = TransportFactory == null ? GameTransport.Create(transport, NewTransportOptions()) : TransportFactory(false);
            try
            {
                Client = new NetworkClient(clientTransport, NewSessionOptions());
                if (Events == null) Events = new NetworkEventHub(); Events.AttachClient(Client);
                if (Latency == null) Latency = new NetworkLatencyProbe(); Latency.AttachClient(Client);
                MemberClient = new NetworkMemberClient(Client, memberSyncOptions);
                MemberClient.ApplyValues = !IsServer;
                MemberClient.BindingError += OnMemberError;
                MemberClient.BaselineReady += id => { if (!IsServer && instances.TryGetValue(id, out var identity)) NotifyReady(identity); };
                Client.Connected += () => ClientConnected?.Invoke();
                Client.Disconnected += reason => ClientDisconnected?.Invoke(reason);
                Client.ObjectSpawned += OnObjectSpawned;
                Client.ObjectChanged += OnObjectChanged;
                Client.OwnershipChanged += (obj, previous) => { if (!IsServer && instances.TryGetValue(obj.Id, out var identity)) identity.Bind(this, obj); };
                Client.ObjectDespawned += OnObjectDespawned;
                Client.MessageReceived += OnClientMessage;
                int targetPort = target != null && Server != null ? Server.Transport.LocalEndPoint.Port : port;
                IPAddress destination = target;
                if (destination == null && !IPAddress.TryParse(address, out destination))
                {
                    if (transport != TransportType.WebSocket || TransportFactory == null) throw new ArgumentException("Use an IP address, or a WebSocket endpoint provider for hostname/WSS connections.");
                    destination = IPAddress.Loopback; // URI-based provider owns hostname resolution and certificate validation.
                }
                Client.Connect(new IPEndPoint(destination, targetPort), connectionData);
            }
            catch { MemberClient?.Dispose(); MemberClient = null; Client?.Dispose(); Client = null; clientTransport.Dispose(); throw; }
        }

        /// <summary>Instantiate and replicate a registered prefab. IDs start at one.</summary>
        public NetworkIdentity Spawn(uint prefabId, Vector3 position, Quaternion rotation, ulong ownerId = 0, int roomId = 0)
        {
            if (!IsServer) throw new InvalidOperationException("Only the server can spawn objects.");
            var prefab = FindPrefab(prefabId);
            var instance = objectPool == null ? Instantiate(prefab, position, rotation) : objectPool.Rent(prefabId, prefab, position, rotation);
            // Inactive prefab Rigidbodies have not joined the physics scene yet. Activate and
            // initialize their pose before reading the authoritative spawn baseline.
            instance.SetActive(true);
            var body = instance.GetComponent<Rigidbody>();
            if (body != null) { body.position = position; body.rotation = rotation; }
            var body2D = instance.GetComponent<Rigidbody2D>();
            if (body2D != null) { body2D.position = position; body2D.rotation = rotation.eulerAngles.z; }
            return BindInstance(instance, prefabId, ownerId, roomId);
        }
        private NetworkIdentity BindInstance(GameObject instance, uint prefabId, ulong ownerId, int roomId)
        {
            var identity = instance.GetComponent<NetworkIdentity>() ?? instance.AddComponent<NetworkIdentity>();
            foreach (var behaviour in instance.GetComponents<NetworkBehaviour>()) behaviour.NotifyNetworkReset();
            var controller = instance.GetComponent<INetworkStateController>();
            bool objectServerAuthority = controller == null ? serverAuthority : controller.ResolveServerAuthority(serverAuthority);
            RuntimeObject state = null;
            try
            {
                var memberSync = instance.GetComponent<SocketJackMemberSync>();
                var schema = memberSync == null ? null : memberSync.CreateSchema(Serializer);
                state = Server.Spawn(prefabId, ownerId, roomId, controller == null ? Array.Empty<byte>() : controller.CaptureState(), objectServerAuthority);
                identity.Bind(this, state); instances.Add(state.Id, identity);
                if (schema != null) { MemberServer.RegisterObject(state.Id, schema); memberSync.MarkBound(); }
            }
            catch { if (state != null) { Server.Despawn(state.Id); instances.Remove(state.Id); MemberServer.RemoveObject(state.Id); } Destroy(instance); throw; }
            instance.SetActive(true);
            NotifySpawn(identity); NotifyReady(identity);
            return identity;
        }

        public void Despawn(ulong objectId)
        {
            if (!IsServer) throw new InvalidOperationException("Only the server can despawn objects.");
            Server.Despawn(objectId);
            RemoveInstance(objectId);
        }
        public void SpawnSceneObjects(int roomId = 0)
        {
            if (!IsServer) throw new InvalidOperationException("Only the server binds scene objects.");
            var ids = new HashSet<uint>();
            foreach (var marker in FindSceneObjects())
            {
                if (marker.SceneObjectId == 0 || marker.SceneObjectId > 0x7fffffffu || !ids.Add(marker.SceneObjectId))
                    throw new InvalidOperationException("Scene network objects require unique saved IDs in the range 1..2147483647.");
                var identity = marker.GetComponent<NetworkIdentity>();
                if (identity.Manager == this && identity.ObjectId != 0) continue;
                marker.gameObject.SetActive(true); BindInstance(marker.gameObject, marker.SceneObjectId | 0x80000000u, 0, roomId);
            }
        }
        private static NetworkSceneObject[] FindSceneObjects()
        {
#if UNITY_6000_0_OR_NEWER
            return UnityEngine.Object.FindObjectsByType<NetworkSceneObject>(FindObjectsInactive.Include, FindObjectsSortMode.None);
#else
            return UnityEngine.Object.FindObjectsOfType<NetworkSceneObject>(true);
#endif
        }

        public bool TryGetIdentity(ulong objectId, out NetworkIdentity identity) => instances.TryGetValue(objectId, out identity);
        public void TransferOwnership(ulong objectId, ulong ownerId, bool? authoritativeServer = null)
        {
            if (!IsServer) throw new InvalidOperationException("Only the server can transfer ownership.");
            Server.TransferOwnership(objectId, ownerId, authoritativeServer);
        }

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
                        var controller = pair.Value.GetComponent<INetworkStateController>();
                        if (!Server.Objects.TryGetValue(pair.Key, out var current) || (!current.ServerAuthority && current.OwnerId != 0)) continue;
                        if (controller != null && controller.CaptureEligible(Time.unscaledTimeAsDouble))
                            Server.SetState(pair.Key, controller.CaptureState());
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
                MemberServer?.Update(Time.unscaledDeltaTime);
            }
            Client?.Update(Time.unscaledDeltaTime, maxEventsPerFrame);
            CaptureOwnerTransforms();
            MemberClient?.Update(Time.unscaledDeltaTime);
            Events?.Update();
            Latency?.Update(Time.unscaledDeltaTime);
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
                mappingTask = activeMapper.RequestMappingAsync(Server.Transport.LocalEndPoint.Port, transport == TransportType.WebSocket ? TransportType.Tcp : transport, mappingCancellation.Token);
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
            if (IsServer)
            {
                // Host renders the authoritative world once, but its local client still submits owner members.
                if (instances.TryGetValue(state.Id, out var existing)) BindClientMembers(existing);
                return;
            }
            GameObject prefab = null, sceneInstance = null;
            try
            {
                if ((state.PrefabId & 0x80000000u) == 0) prefab = FindPrefab(state.PrefabId);
                else
                {
                    foreach (var marker in FindSceneObjects()) if (marker.SceneObjectId == (state.PrefabId & 0x7fffffffu))
                    { if (sceneInstance != null) throw new InvalidOperationException("Duplicate network scene object ID."); sceneInstance = marker.gameObject; }
                    if (sceneInstance == null) throw new InvalidOperationException("Network scene object is missing. Load the matching scene before acknowledging readiness.");
                }
            }
            catch (InvalidOperationException error)
            {
                Debug.LogError(error.Message);
                Client.Stop();
                return;
            }
            var instance = sceneInstance != null ? sceneInstance : objectPool == null ? Instantiate(prefab) : objectPool.Rent(state.PrefabId, prefab, Vector3.zero, Quaternion.identity);
            instance.SetActive(true);
            var identity = instance.GetComponent<NetworkIdentity>() ?? instance.AddComponent<NetworkIdentity>();
            foreach (var behaviour in instance.GetComponents<NetworkBehaviour>()) behaviour.NotifyNetworkReset();
            identity.Bind(this, state);
            instances.Add(state.Id, identity);
            instance.SetActive(true);
            BindClientMembers(identity);
            instance.GetComponent<INetworkStateController>()?.ApplyState(state.State, true);
            NotifySpawn(identity);
            if (!MemberClient.IsRegistered(identity.ObjectId) || MemberClient.IsReady(identity.ObjectId)) NotifyReady(identity);
        }

        private void OnObjectChanged(RuntimeObject state)
        {
            if (IsServer || !instances.TryGetValue(state.Id, out var identity) || identity == null) return;
            identity.Bind(this, state);
            if (!state.ServerAuthority && identity.IsOwner) return;
            identity.GetComponent<INetworkStateController>()?.ApplyState(state.State, false);
        }

        private void OnServerObjectChanged(RuntimeObject state)
        {
            if (state.ServerAuthority || !instances.TryGetValue(state.Id, out var identity) || identity == null) return;
            identity.Bind(this, state);
            if (!identity.HasStateAuthority) identity.GetComponent<INetworkStateController>()?.ApplyState(state.State, false);
        }
        private bool ValidateOwnerState(RuntimeObject state, byte[] bytes)
        {
            if (!instances.TryGetValue(state.Id, out var identity) || identity == null) return false;
            var controller = identity.GetComponent<INetworkStateController>();
            if (controller != null) { if (!controller.ValidateState(bytes)) return false; }
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
                if (pair.Value == null || !pair.Value.IsOwner || pair.Value.ServerAuthority) continue;
                var controller = pair.Value.GetComponent<INetworkStateController>();
                if (controller != null && !controller.UsesInput) Client.SendOwnerState(pair.Key, controller.CaptureState());
            }
        }

        private void OnServerInput(NetworkInput input)
        {
            if (instances.TryGetValue(input.ObjectId, out var identity) && identity != null)
            {
                var controller = identity.GetComponent<INetworkStateController>();
                if (controller != null && controller.UsesInput)
                {
                    ulong sourceView = Server.GetViewId(input.PeerId, input.ObjectId);
                    if (identity.LastOwnerInputView != sourceView) { controller.ResetPrediction(); identity.LastOwnerInputView = sourceView; }
                    if (!controller.ReceiveOwnerInput(input.Payload)) return;
                    int room = Server.GetPeerRoom(input.PeerId);
                    foreach (ulong peer in new List<ulong>(Server.Peers))
                    {
                        if (peer == input.PeerId) continue;
                        int peerRoom;
                        try { peerRoom = Server.GetPeerRoom(peer); }
                        catch (KeyNotFoundException) { continue; }
                        if (peerRoom == room && Server.CanObserve(peer, input.ObjectId))
                        {
                            using (var stream = new MemoryStream(input.Payload.Length + 28)) using (var writer = new BinaryWriter(stream))
                            {
                                writer.Write(input.ObjectId); writer.Write(identity.AuthorityEpoch); writer.Write(sourceView);
                                writer.Write(Server.GetViewId(peer, input.ObjectId)); writer.Write(input.Payload);
                                QueuePhysicsRelay(peer, input.ObjectId, room, stream.ToArray());
                            }
                        }
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
            queue.Enqueue(new PhysicsRelay { Payload = payload, ObjectId = objectId, Room = room, View = Server.GetViewId(peer, objectId) });
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
                    if (relay.Room != currentRoom || Server.GetViewId(peer, relay.ObjectId) != relay.View || !Server.Objects.TryGetValue(relay.ObjectId, out var obj) || obj.RoomId != currentRoom)
                    { queue.Dequeue(); continue; }
                    if (!Server.SendMessage(peer, SocketJackObject.InputRelayMessageId, relay.Payload, DeliveryMode.ReliableUnordered)) break;
                    queue.Dequeue();
                }
                if (queue.Count == 0) physicsRelays.Remove(peer);
            }
        }

        private void OnClientMessage(NetworkMessage message)
        {
            if (IsServer || message.MessageId != SocketJackObject.InputRelayMessageId || message.Payload.Length <= 28 || message.Payload.Length > 64) return;
            ulong objectId;
            uint epoch;
            ulong sourceView, targetView;
            using (var stream = new MemoryStream(message.Payload, false))
            using (var reader = new BinaryReader(stream)) { objectId = reader.ReadUInt64(); epoch = reader.ReadUInt32(); sourceView = reader.ReadUInt64(); targetView = reader.ReadUInt64(); }
            if (!instances.TryGetValue(objectId, out var identity) || identity == null) return;
            var controller = identity.GetComponent<INetworkStateController>();
            if (controller == null || !controller.UsesInput || identity.AuthorityEpoch != epoch || !Client.Objects.TryGetValue(objectId, out var state) || state.ViewId != targetView) return;
            if (identity.LastRelaySourceView != sourceView) { controller.ResetPrediction(); identity.LastRelaySourceView = sourceView; }
            var input = new byte[message.Payload.Length - 28]; Buffer.BlockCopy(message.Payload, 28, input, 0, input.Length);
            controller.ReceiveRelayedInput(input);
        }

        private void OnObjectDespawned(ulong objectId) { if (!IsServer) RemoveInstance(objectId); }
        private void RemoveInstance(ulong id)
        {
            MemberServer?.RemoveObject(id); MemberClient?.RemoveObject(id);
            if (!instances.TryGetValue(id, out var identity)) return;
            instances.Remove(id);
            if (identity != null)
            {
                NotifyDespawn(identity);
                uint prefabId = identity.PrefabId;
                identity.GetComponent<INetworkStateController>()?.ResetPrediction(); identity.GetComponent<SocketJackMemberSync>()?.ResetBinding(); identity.Unbind();
                if (identity.GetComponent<NetworkSceneObject>() != null)
                    identity.gameObject.SetActive(false);
                else if (objectPool != null) objectPool.Return(prefabId, identity.gameObject);
                else Destroy(identity.gameObject);
            }
            ObjectDespawned?.Invoke(id);
        }
        private GameObject FindPrefab(uint id)
        {
            if (id == 0 || id > prefabs.Length || prefabs[id - 1] == null)
                throw new InvalidOperationException("Unknown network prefab ID " + id + ". Register the same prefab list in every build.");
            return prefabs[id - 1];
        }
        private TransportOptions NewTransportOptions() => new TransportOptions { MaxPeers = maxPlayers, ConnectionValidator = ConnectionValidator };
        private NetworkSessionOptions NewSessionOptions() => new NetworkSessionOptions { TickRate = tickRate, SnapshotRate = Math.Min(snapshotRate, tickRate), MaxPeers = maxPlayers, ServerAuthority = serverAuthority, MaxMessageBytes = 44 + NetworkMemberSyncOptions.AbsoluteMaxMemberBytes, CompatibilityId = BuildCompatibilityId(), MaxVisibleObjectsPerPeer = maxVisibleObjectsPerPeer };
        private void ValidateMemberPrefabs()
        {
            foreach (var prefab in prefabs) if (prefab != null) prefab.GetComponent<SocketJackMemberSync>()?.CreateSchema(Serializer);
        }
        private string BuildCompatibilityId()
        {
            var text = new System.Text.StringBuilder("SocketJack.Unity/second-release/session3\n");
            text.Append(CompatibilityId).Append('\n').Append(Serializer.ProtocolId).Append('\n');
            var sceneCoordinator = GetComponent<NetworkSceneCoordinator>();
            if (sceneCoordinator != null) foreach (string scene in sceneCoordinator.SceneCatalog) text.Append("scene:").Append(scene).Append('\n');
            for (int i = 0; i < prefabs.Length; i++)
            {
                var prefab = prefabs[i]; var controller = prefab.GetComponent<INetworkStateController>();
                text.Append(i + 1).Append(':').Append(prefab.name).Append(':').Append(controller?.GetType().FullName).Append(':');
                if (controller is SocketJackObject physics) text.Append(physics.PrecisionProfile.ProtocolId);
                if (controller is SocketJackObject2D physics2D) text.Append(physics2D.PrecisionProfile.ProtocolId);
                if (controller is NetworkTransform transformSync) text.Append(transformSync.PrecisionProfile.ProtocolId);
                var members = prefab.GetComponent<SocketJackMemberSync>()?.CreateSchema(Serializer);
                text.Append(':').Append(members?.Fingerprint.ToString("x16", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
                foreach (var networkEvent in prefab.GetComponents<SocketJackNetworkEvent>()) text.Append(networkEvent.CompatibilityId).Append('\n');
                var adaptive = prefab.GetComponent<SocketJackAdaptiveObject>(); if (adaptive != null && adaptive.enabled) text.Append(adaptive.CompatibilityId).Append('\n');
            }
            using (var sha = System.Security.Cryptography.SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(text.ToString()))).Replace("-", "").ToLowerInvariant();
        }
        private void NotifySpawn(NetworkIdentity identity)
        {
            foreach (var behaviour in identity.GetComponents<NetworkBehaviour>())
                try { behaviour.NotifyNetworkSpawn(); } catch (Exception error) { Debug.LogException(error, behaviour); }
            ObjectSpawned?.Invoke(identity);
        }
        private void NotifyReady(NetworkIdentity identity)
        {
            if (identity == null || identity.IsNetworkReady) return;
            identity.IsNetworkReady = true;
            foreach (var behaviour in identity.GetComponents<NetworkBehaviour>())
                try { behaviour.NotifyNetworkReady(); } catch (Exception error) { Debug.LogException(error, behaviour); }
        }
        internal void NotifyOwnershipChanged(NetworkIdentity identity, ulong previousOwner)
        {
            identity.GetComponent<INetworkStateController>()?.ResetPrediction();
            foreach (var queue in physicsRelays.Values)
            {
                int count = queue.Count;
                while (count-- > 0) { var item = queue.Dequeue(); if (item.ObjectId != identity.ObjectId) queue.Enqueue(item); }
            }
            foreach (var behaviour in identity.GetComponents<NetworkBehaviour>())
                try { behaviour.NotifyOwnershipChanged(previousOwner); } catch (Exception error) { Debug.LogException(error, behaviour); }
        }
        private void NotifyDespawn(NetworkIdentity identity)
        {
            identity.IsNetworkReady = false;
            foreach (var behaviour in identity.GetComponents<NetworkBehaviour>())
                try { behaviour.NotifyNetworkDespawn(); } catch (Exception error) { Debug.LogException(error, behaviour); }
        }
        private void BindClientMembers(NetworkIdentity identity)
        {
            try
            {
                var component = identity.GetComponent<SocketJackMemberSync>();
                var schema = component == null ? null : component.CreateSchema(Serializer);
                if (schema != null) { MemberClient.RegisterObject(identity.ObjectId, schema); component.MarkBound(); }
            }
            catch (Exception error) { OnMemberError(identity.ObjectId, error); Client.Stop(); }
        }
        private void OnMemberError(ulong objectId, Exception error) => Debug.LogException(new InvalidOperationException("Member synchronization failed for object " + objectId, error), this);

        public static SocketJackBinaryFormatter CreateDefaultSerializer()
        { var formatter = new SocketJackBinaryFormatter(); RegisterUnityTypes(formatter); return formatter; }
        /// <summary>Register these stable codecs before ProtocolId is read. Custom providers opt in explicitly.
        /// IDs 256..261 are reserved for Unity Vector2/3/4, Quaternion, Color and Color32.</summary>
        public static void RegisterUnityTypes(SocketJackBinaryFormatter formatter)
        {
            if (formatter == null) throw new ArgumentNullException(nameof(formatter));
            formatter.Register<Vector2>(256, "unity.vector2.f32.v1", (w, v) => { w.Write(v.x); w.Write(v.y); }, r => new Vector2(r.ReadSingle(), r.ReadSingle()));
            formatter.Register<Vector3>(257, "unity.vector3.f32.v1", (w, v) => { w.Write(v.x); w.Write(v.y); w.Write(v.z); }, r => new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()));
            formatter.Register<Vector4>(258, "unity.vector4.f32.v1", (w, v) => { w.Write(v.x); w.Write(v.y); w.Write(v.z); w.Write(v.w); }, r => new Vector4(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()));
            formatter.Register<Quaternion>(259, "unity.quaternion.f32.v1", (w, v) => { w.Write(v.x); w.Write(v.y); w.Write(v.z); w.Write(v.w); }, r => new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()));
            formatter.Register<Color>(260, "unity.color.f32.v1", (w, v) => { w.Write(v.r); w.Write(v.g); w.Write(v.b); w.Write(v.a); }, r => new Color(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()));
            formatter.Register<Color32>(261, "unity.color32.u8.v1", (w, v) => { w.Write(v.r); w.Write(v.g); w.Write(v.b); w.Write(v.a); }, r => new Color32(r.ReadByte(), r.ReadByte(), r.ReadByte(), r.ReadByte()));
        }
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
                int controllers = 0;
                foreach (var component in prefabs[i].GetComponents<MonoBehaviour>()) if (component is INetworkStateController) controllers++;
                if (controllers > 1) throw new InvalidOperationException("A network prefab must have at most one movement synchronization controller.");
            }
        }
        private void CheckPlatform()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            if (transport != TransportType.WebSocket || TransportFactory == null)
                throw new PlatformNotSupportedException("Select WebSocket and configure the SocketJack browser transport provider for WebGL.");
#endif
        }
        public void Shutdown()
        {
            if (shuttingDown) return;
            bool wasRunning = Server != null || Client != null;
            shuttingDown = true;
            try
            {
            StopPortMapping();
            Events?.Dispose(); Events = null;
            Latency?.Dispose(); Latency = null;
            MemberClient?.Dispose(); MemberClient = null;
            MemberServer?.Dispose(); MemberServer = null;
            Client?.Dispose(); Client = null;
            Server?.Dispose(); Server = null;
            foreach (var id in new List<ulong>(instances.Keys)) RemoveInstance(id);
            instances.Clear(); captureAccumulator = 0;
            ownerTransformAccumulator = 0;
            physicsRelays.Clear(); relayCursor = 0;
            if (wasRunning) SessionStopped?.Invoke();
            }
            finally { shuttingDown = false; }
        }
        private void OnDestroy() => Shutdown();
        private void OnApplicationQuit() => Shutdown();
    }
}
