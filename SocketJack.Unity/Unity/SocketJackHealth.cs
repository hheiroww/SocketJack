using System;
using System.Collections.Generic;
using SocketJack.Multiplayer.Serialization;
using UnityEngine;
using UnityEngine.Events;
namespace SocketJack.Multiplayer.Components
{
    [RequireComponent(typeof(SocketJackMemberSync)), DisallowMultipleComponent]
    public sealed class SocketJackHealth : NetworkBehaviour, INetworkMemberSource
    {
        [Min(1)] public float Maximum = 100;
        [Range(1, 65535)] public int MemberId = 50001;
        public UnityEvent<float> OnHealthChanged = new UnityEvent<float>();
        public UnityEvent OnDeath = new UnityEvent();
        private float health;
        public float Current => health;
        public bool IsDead => health <= 0;
        private void Awake() { health = Maximum; }
        public void Damage(float amount) { if (IsServer && Valid(amount)) SetHealth(Mathf.Max(0, health - amount)); }
        public void Heal(float amount) { if (IsServer && Valid(amount)) SetHealth(Mathf.Min(Maximum, health + amount)); }
        public void ResetHealth() { if (IsServer) SetHealth(Maximum); }
        private static bool Valid(float value) => !float.IsNaN(value) && !float.IsInfinity(value) && value >= 0;
        private void SetHealth(float value)
        {
            if (!Valid(value) || value > Maximum) throw new ArgumentOutOfRangeException(nameof(value));
            bool died = health > 0 && value <= 0; bool changed = health != value; health = value;
            if (changed) OnHealthChanged.Invoke(value); if (died) OnDeath.Invoke();
        }
        public IEnumerable<NetworkMemberBinding> CreateNetworkBindings(INetworkSerializer serializer)
        {
            yield return new NetworkMemberBinding(checked((ushort)MemberId), "socketjack.health.v1", typeof(float), () => health, value => SetHealth((float)value), maxBytes: 16);
        }
    }
}
