using System;
using System.Collections.Generic;
using UnityEngine;

namespace SocketJack.Multiplayer.Components
{
    /// <summary>Server-side automatic player spawning with stable prefab registration.</summary>
    [DefaultExecutionOrder(-10000), DisallowMultipleComponent, RequireComponent(typeof(NetworkManager))]
    public sealed class NetworkPlayerSpawner : MonoBehaviour
    {
        [SerializeField] private NetworkManager network;
        [SerializeField] private NetworkPrefabRegistry registry;
        [SerializeField, Min(1)] private uint playerPrefabId = 1;
        [SerializeField] private NetworkSpawnPoint[] spawnPoints = Array.Empty<NetworkSpawnPoint>();
        [SerializeField] private Vector3 fallbackOrigin = new Vector3(0, 1, 0);
        [SerializeField, Min(0)] private float fallbackSpacing = 2;
        [SerializeField] private bool spawnOnConnect = true;
        private readonly Dictionary<ulong, ulong> players = new Dictionary<ulong, ulong>();
        private int nextSpawn;

        public NetworkPrefabRegistry Registry => registry;
        public NetworkManager Network => network != null ? network : (network = GetComponent<NetworkManager>());
        public bool SpawnOnConnect { get => spawnOnConnect; set => spawnOnConnect = value; }
        public bool TryGetPlayer(ulong peerId, out NetworkIdentity identity)
        {
            identity = null;
            return players.TryGetValue(peerId, out ulong objectId) && Network.TryGetIdentity(objectId, out identity) && identity != null && identity.OwnerId == peerId;
        }
        public void Configure(NetworkManager manager, NetworkPrefabRegistry prefabRegistry, uint prefabId = 1,
            NetworkSpawnPoint[] points = null)
        {
            if (manager == null || prefabRegistry == null || prefabId < 1) throw new ArgumentException("Manager, registry and valid prefab ID are required.");
            if (manager.IsServer || manager.Client != null) throw new InvalidOperationException("Configure spawning before starting the session.");
            network = manager; registry = prefabRegistry; playerPrefabId = prefabId;
            spawnPoints = points == null ? Array.Empty<NetworkSpawnPoint>() : (NetworkSpawnPoint[])points.Clone();
            registry.ApplyTo(network);
        }
        private void Awake()
        {
            if (network == null) network = GetComponent<NetworkManager>();
            if (registry != null) registry.ApplyTo(network);
        }
        private void OnEnable()
        {
            Network.PeerConnected += OnPeerConnected;
            Network.PeerDisconnected += OnPeerDisconnected;
        }
        private void OnDisable()
        {
            if (network == null) return;
            network.PeerConnected -= OnPeerConnected;
            network.PeerDisconnected -= OnPeerDisconnected;
        }
        private void OnPeerConnected(ulong peerId)
        { if (spawnOnConnect && (Network.GetComponent<NetworkSceneCoordinator>() == null || Network.GetComponent<NetworkSceneCoordinator>().IsPeerReady(peerId))) SpawnPlayer(peerId); }
        private void OnPeerDisconnected(ulong peerId, string reason) => players.Remove(peerId);

        public NetworkIdentity SpawnPlayer(ulong peerId)
        {
            if (!Network.IsServer) throw new InvalidOperationException("Only the server can spawn players.");
            if (players.TryGetValue(peerId, out var existing) && Network.TryGetIdentity(existing, out var identity) && identity.OwnerId == peerId) return identity;
            bool anyPoint = false; foreach (var candidate in spawnPoints) if (candidate != null && candidate.isActiveAndEnabled) { anyPoint = true; break; }
            if (!anyPoint)
            {
#if UNITY_6000_0_OR_NEWER
                spawnPoints = UnityEngine.Object.FindObjectsByType<NetworkSpawnPoint>(FindObjectsSortMode.None);
#else
                spawnPoints = UnityEngine.Object.FindObjectsOfType<NetworkSpawnPoint>();
#endif
            }
            NetworkSpawnPoint point = null;
            for (int i = 0; i < spawnPoints.Length; i++)
            {
                var candidate = spawnPoints[nextSpawn++ % spawnPoints.Length];
                if (candidate != null && candidate.isActiveAndEnabled) { point = candidate; break; }
            }
            int room = point == null ? Network.Server.GetPeerRoom(peerId) : point.RoomId;
            if (point != null) Network.Server.SetPeerRoom(peerId, room);
            var position = point == null ? fallbackOrigin + Vector3.right * ((nextSpawn++ % 8) * fallbackSpacing) : point.transform.position;
            var rotation = point == null ? Quaternion.identity : point.transform.rotation;
            var spawned = Network.Spawn(playerPrefabId, position, rotation, peerId, room);
            players[peerId] = spawned.ObjectId;
            return spawned;
        }
        public NetworkIdentity RespawnPlayer(ulong peerId)
        {
            if (!Network.IsServer) throw new InvalidOperationException("Only the server can respawn players.");
            if (players.TryGetValue(peerId, out var existing) && Network.TryGetIdentity(existing, out _)) Network.Despawn(existing);
            players.Remove(peerId);
            return SpawnPlayer(peerId);
        }
    }
}
