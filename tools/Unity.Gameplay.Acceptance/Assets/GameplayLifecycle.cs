using SocketJack.Multiplayer.Components;
using UnityEngine;

namespace SocketJack.Tests.Gameplay
{
    public sealed class GameplayLifecycle : NetworkBehaviour
    {
        public int SpawnCount, ReadyCount, OwnershipCount, DespawnCount, ResetCount;
        public bool TriggerSeen;
        public float ReadyHealth;
        public float AdaptiveScore = .123456f;
        protected override void OnNetworkReset()
        {
            ResetCount++; SpawnCount = ReadyCount = OwnershipCount = DespawnCount = 0; TriggerSeen = false; ReadyHealth = -1; AdaptiveScore = .123456f;
            var animator = GetComponent<Animator>();
            if (animator != null) { animator.Rebind(); animator.Update(0); animator.SetFloat("Speed", 0); animator.SetInteger("Phase", 0); animator.SetBool("Open", false); animator.ResetTrigger("Pulse"); }
            GameplayProbe.Record("reset", 0, !IsNetworkSpawned && !IsNetworkReady);
        }
        protected override void OnNetworkSpawn()
        {
            SpawnCount++;
            GameplayProbe.Record("spawn", Identity.ObjectId, IsNetworkSpawned && !IsNetworkReady && Network != null && Identity.ObjectId != 0, gameObject.GetInstanceID());
        }
        protected override void OnNetworkReady()
        {
            ReadyCount++; var health = GetComponent<SocketJackHealth>(); ReadyHealth = health == null ? -1 : health.Current;
            GameplayProbe.Record("ready", Identity.ObjectId, IsNetworkSpawned && IsNetworkReady && SpawnCount == 1 && ReadyCount == 1, gameObject.GetInstanceID());
        }
        protected override void OnOwnershipChanged(ulong previous)
        { OwnershipCount++; GameplayProbe.Record("ownership", Identity.ObjectId, IsNetworkSpawned && previous != Identity.OwnerId, gameObject.GetInstanceID()); }
        protected override void OnNetworkDespawn()
        { DespawnCount++; GameplayProbe.Record("despawn", Identity.ObjectId, !IsNetworkSpawned && !IsNetworkReady && DespawnCount == 1, gameObject.GetInstanceID()); }
        private void Update()
        { var animator = GetComponent<Animator>(); if (animator != null && animator.GetCurrentAnimatorStateInfo(0).IsName("Triggered")) TriggerSeen = true; }
    }
}
