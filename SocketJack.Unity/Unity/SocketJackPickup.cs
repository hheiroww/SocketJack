using System;
using UnityEngine;
using UnityEngine.Events;
namespace SocketJack.Multiplayer.Components
{
    [RequireComponent(typeof(NetworkIdentity)), DisallowMultipleComponent]
    public sealed class SocketJackPickup : NetworkBehaviour
    {
        [Serializable] public sealed class PickupEvent : UnityEvent<NetworkIdentity> { }
        public PickupEvent OnCollected = new PickupEvent();
        public bool DespawnOnCollect = true;
        private bool collected;
        protected override void OnNetworkSpawn() { collected = false; }
        private void OnTriggerEnter(Collider other) => Collect(other.GetComponentInParent<NetworkIdentity>());
        private void OnTriggerEnter2D(Collider2D other) => Collect(other.GetComponentInParent<NetworkIdentity>());
        private void Collect(NetworkIdentity actor)
        {
            if (!IsServer || collected || actor == null || actor.Manager != Network || actor.OwnerId == 0 || actor.RoomId != Identity.RoomId) return;
            collected = true; OnCollected.Invoke(actor);
            if (DespawnOnCollect && IsNetworkSpawned) Network.Despawn(Identity.ObjectId);
        }
    }
}
