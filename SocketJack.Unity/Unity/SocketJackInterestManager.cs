using System.Collections.Generic;
using UnityEngine;

namespace SocketJack.Multiplayer.Components
{
    /// <summary>Optional server-only spatial visibility. All movement/member/event permission uses the same view.</summary>
    [DefaultExecutionOrder(-5000), RequireComponent(typeof(NetworkManager)), DisallowMultipleComponent]
    public sealed class SocketJackInterestManager : MonoBehaviour
    {
        public NetworkSpatialDimension Dimension = NetworkSpatialDimension.XZ;
        [Min(1)] public float Radius = 60;
        [Min(0)] public float Hysteresis = 8;
        [Min(1)] public float CellSize = 32;
        [Range(1, 4096)] public int MaxVisibleObjects = 256;
        private NetworkManager network;
        private readonly HashSet<ulong> tracked = new HashSet<ulong>(), current = new HashSet<ulong>(), anchors = new HashSet<ulong>();
        public SpatialInterestIndex Index { get; private set; }
        private void Awake()
        {
            network = GetComponent<NetworkManager>(); network.MaxVisibleObjectsPerPeer = MaxVisibleObjects;
            network.ListenerStarted += Started; network.SessionStopped += Stopped;
            network.PeerDisconnected += Disconnected;
        }
        private void Started()
        {
            Index = new SpatialInterestIndex(new SpatialInterestOptions { Dimension = Dimension, Radius = Radius, Hysteresis = Hysteresis, CellSize = CellSize });
            network.Server.InterestProvider = Index; Update();
        }
        private void Stopped() { Index = null; tracked.Clear(); current.Clear(); anchors.Clear(); }
        private void Disconnected(ulong peer, string reason) => Index?.RemovePeer(peer);
        private void Update()
        {
            if (Index == null || !network.IsServer) return;
            current.Clear(); anchors.Clear();
            // Registration order is authoritative spawn order. The first owned object anchors a player unless explicitly marked.
            foreach (var pair in network.SpawnedObjects)
            {
                var identity = pair.Value; if (identity == null) continue;
                var p = identity.transform.position; var position = new System.Numerics.Vector3(p.x, p.y, p.z);
                var settings = identity.GetComponent<SocketJackInterestObject>();
                Index.SetObjectPosition(pair.Key, identity.RoomId, position, identity.OwnerId, settings != null && settings.AlwaysVisible);
                current.Add(pair.Key);
                if (identity.OwnerId != 0 && ((settings != null && settings.ObserverAnchor) || anchors.Add(identity.OwnerId)))
                    Index.SetPeerPosition(identity.OwnerId, position);
            }
            foreach (ulong id in tracked) if (!current.Contains(id)) Index.RemoveObject(id);
            tracked.Clear(); foreach (ulong id in current) tracked.Add(id);
        }
        private void OnDestroy()
        {
            if (network != null) { network.ListenerStarted -= Started; network.SessionStopped -= Stopped; network.PeerDisconnected -= Disconnected; }
        }
    }
}
