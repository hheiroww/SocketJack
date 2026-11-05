using System;
using UnityEngine;

namespace SocketJack.Multiplayer.Components
{
    /// <summary>Gates local cameras, audio and game input without disabling remote network simulation.</summary>
    [DisallowMultipleComponent]
    public sealed class NetworkLocalPlayer : NetworkBehaviour
    {
        [SerializeField, Tooltip("Local-only cameras, audio listeners and game input scripts. Never select a networking component.")]
        private Behaviour[] localBehaviours = Array.Empty<Behaviour>();
        [SerializeField, Tooltip("Local-only child objects. Do not select the network root or an ancestor.")]
        private GameObject[] localObjects = Array.Empty<GameObject>();
        private bool active;

        public void Configure(Behaviour[] behaviours, GameObject[] objects = null)
        {
            if (IsNetworkSpawned) throw new InvalidOperationException("Configure local-player targets before spawning.");
            localBehaviours = behaviours == null ? Array.Empty<Behaviour>() : (Behaviour[])behaviours.Clone();
            localObjects = objects == null ? Array.Empty<GameObject>() : (GameObject[])objects.Clone();
            Apply(false, true);
        }
        private void Awake() => Apply(false, true);
        protected override void OnNetworkReady() => Apply(IsOwner, true);
        protected override void OnOwnershipChanged(ulong previousOwnerId) => Apply(IsNetworkReady && IsOwner, true);
        protected override void OnNetworkDespawn() => Apply(false, true);
        private void LateUpdate() => Apply(IsNetworkReady && IsOwner, false);
        private void OnDisable() => Apply(false, true);

        private void Apply(bool enabledForOwner, bool force)
        {
            if (!force && active == enabledForOwner) return;
            active = enabledForOwner;
            foreach (var target in localBehaviours)
                if (target != null && IsSafeLocalBehaviour(target)) target.enabled = enabledForOwner;
            foreach (var target in localObjects)
                if (target != null && target.transform.IsChildOf(transform) && target != gameObject)
                    target.SetActive(enabledForOwner);
        }
        public static bool IsSafeLocalBehaviour(Behaviour target)
        {
            if (target == null || target is NetworkBehaviour || target is NetworkIdentity || target is NetworkManager ||
                target is SocketJackObject || target is NetworkTransform || target is SocketJackMemberSync) return false;
            // This also protects optional motors without taking a hard dependency on their assemblies.
            var type = target.GetType();
            return type.Namespace != typeof(NetworkIdentity).Namespace;
        }
    }
}
