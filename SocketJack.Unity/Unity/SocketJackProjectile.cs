using UnityEngine;
namespace SocketJack.Multiplayer.Components
{
    [RequireComponent(typeof(NetworkIdentity)), DisallowMultipleComponent]
    public sealed class SocketJackProjectile : NetworkBehaviour
    {
        [Min(0)] public float Damage = 10;
        [Min(.01f)] public float Lifetime = 5;
        private float expires;
        private bool hit;
        protected override void OnNetworkSpawn() { hit = false; expires = Time.unscaledTime + Lifetime; }
        private void Update() { if (IsServer && IsNetworkSpawned && Time.unscaledTime >= expires) Network.Despawn(Identity.ObjectId); }
        private void OnCollisionEnter(Collision collision) => Hit(collision.collider.GetComponentInParent<SocketJackHealth>());
        private void OnCollisionEnter2D(Collision2D collision) => Hit(collision.collider.GetComponentInParent<SocketJackHealth>());
        private void Hit(SocketJackHealth health)
        {
            if (!IsServer || hit || health == null || health.Network != Network || health.Identity.RoomId != Identity.RoomId || (Identity.OwnerId != 0 && health.Identity.OwnerId == Identity.OwnerId)) return;
            hit = true; health.Damage(Damage); if (IsNetworkSpawned) Network.Despawn(Identity.ObjectId);
        }
    }
}
