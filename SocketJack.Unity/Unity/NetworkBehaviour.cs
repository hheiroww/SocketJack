using UnityEngine;

namespace SocketJack.Multiplayer.Components
{
    /// <summary>Optional gameplay lifecycle. Identity binding precedes Spawn; all initial baselines precede Ready.</summary>
    [RequireComponent(typeof(NetworkIdentity))]
    public abstract class NetworkBehaviour : MonoBehaviour
    {
        private NetworkIdentity identity;
        public NetworkIdentity Identity => identity != null ? identity : (identity = GetComponent<NetworkIdentity>());
        public NetworkManager Network => Identity == null ? null : Identity.Manager;
        public bool IsNetworkSpawned { get; private set; }
        public bool IsNetworkReady { get; private set; }
        public bool IsServer => Identity != null && Identity.IsServer;
        public bool IsOwner => Identity != null && Identity.IsOwner;
        public bool HasStateAuthority => Identity != null && Identity.HasStateAuthority;

        internal void NotifyNetworkSpawn()
        {
            if (IsNetworkSpawned) return;
            IsNetworkSpawned = true;
            IsNetworkReady = false;
            OnNetworkSpawn();
        }
        internal void NotifyNetworkReset() { OnNetworkReset(); }
        internal void NotifyNetworkReady()
        {
            if (!IsNetworkSpawned || IsNetworkReady) return;
            IsNetworkReady = true;
            OnNetworkReady();
        }
        internal void NotifyOwnershipChanged(ulong previousOwnerId)
        {
            if (IsNetworkSpawned) OnOwnershipChanged(previousOwnerId);
        }
        internal void NotifyNetworkDespawn()
        {
            if (!IsNetworkSpawned) return;
            IsNetworkReady = false;
            IsNetworkSpawned = false;
            OnNetworkDespawn();
        }
        protected virtual void OnNetworkSpawn() { }
        protected virtual void OnNetworkReset() { }
        protected virtual void OnNetworkReady() { }
        protected virtual void OnOwnershipChanged(ulong previousOwnerId) { }
        protected virtual void OnNetworkDespawn() { }
    }
}
