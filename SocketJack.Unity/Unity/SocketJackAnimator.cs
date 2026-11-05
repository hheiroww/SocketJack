using System;
using System.Collections.Generic;
using SocketJack.Multiplayer.Serialization;
using UnityEngine;
namespace SocketJack.Multiplayer.Components
{
    /// <summary>Selected Animator parameters are state; explicit triggers are transient network events.
    /// Configure matching controllers and parameter lists on every build.</summary>
    [RequireComponent(typeof(SocketJackMemberSync)), DisallowMultipleComponent]
    public sealed class SocketJackAnimator : NetworkBehaviour, INetworkMemberSource
    {
        [Serializable] public sealed class Parameter
        {
            public string Name;
            public AnimatorControllerParameterType Type = AnimatorControllerParameterType.Float;
            public NetworkMemberAuthority Authority = NetworkMemberAuthority.Server;
            public NetworkPrecisionPreset Precision = NetworkPrecisionPreset.FullPrecision;
        }
        public Animator Animator;
        public Parameter[] Parameters = Array.Empty<Parameter>();
        [Range(1, 65535)] public int BaseMemberId = 10000;
        [Range(1, 65535)] public int TriggerEventId = 2001;
        public NetworkMemberAuthority TriggerAuthority = NetworkMemberAuthority.Server;
        [Min(.016f)] public float SendInterval = .1f;
        private void Awake() { if (Animator == null) Animator = GetComponent<Animator>(); }
        protected override void OnNetworkSpawn()
        {
            Network.Events.Register<string>(Identity.ObjectId, checked((ushort)TriggerEventId), Network.Serializer, TriggerReceived,
                true, (peer, name) => TriggerAuthority == NetworkMemberAuthority.Owner && HasParameter(name, AnimatorControllerParameterType.Trigger), maxBytes: 256);
        }
        protected override void OnNetworkDespawn() => Network?.Events?.Unregister(Identity.ObjectId, (ushort)TriggerEventId);
        public bool SetTrigger(string name)
        {
            if (!IsNetworkReady || !HasParameter(name, AnimatorControllerParameterType.Trigger)) return false;
            if (IsServer) { Animator.SetTrigger(name); return Network.Events.Emit(Identity.ObjectId, (ushort)TriggerEventId, name); }
            return TriggerAuthority == NetworkMemberAuthority.Owner && IsOwner && Network.Events.SendCommand(Identity.ObjectId, (ushort)TriggerEventId, name);
        }
        private void TriggerReceived(NetworkEventContext context, string name)
        {
            if (!HasParameter(name, AnimatorControllerParameterType.Trigger)) return;
            if (context.IsServerCommand) { Animator.SetTrigger(name); Network.Events.Emit(Identity.ObjectId, (ushort)TriggerEventId, name); }
            else if (!IsServer) Animator.SetTrigger(name); // Host shares the server Animator instance.
        }
        private bool HasParameter(string name, AnimatorControllerParameterType type)
        { foreach (var parameter in Parameters) if (parameter != null && parameter.Name == name && parameter.Type == type) return true; return false; }
        public IEnumerable<NetworkMemberBinding> CreateNetworkBindings(INetworkSerializer serializer)
        {
            var animator = Animator != null ? Animator : GetComponent<Animator>();
            if (animator == null) throw new InvalidOperationException("SocketJackAnimator needs an assigned Animator.");
            var names = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < Parameters.Length; i++)
            {
                var parameter = Parameters[i];
                if (parameter == null || string.IsNullOrWhiteSpace(parameter.Name) || !names.Add(parameter.Name)) throw new InvalidOperationException("Animator parameters need unique names.");
                if (parameter.Type == AnimatorControllerParameterType.Trigger) continue;
                string name = parameter.Name; Type type; Func<object> capture; Action<object> apply;
                if (parameter.Type == AnimatorControllerParameterType.Float) { type = typeof(float); capture = () => animator.GetFloat(name); apply = value => animator.SetFloat(name, (float)value); }
                else if (parameter.Type == AnimatorControllerParameterType.Int) { type = typeof(int); capture = () => animator.GetInteger(name); apply = value => animator.SetInteger(name, (int)value); }
                else if (parameter.Type == AnimatorControllerParameterType.Bool) { type = typeof(bool); capture = () => animator.GetBool(name); apply = value => animator.SetBool(name, (bool)value); }
                else throw new InvalidOperationException("Unsupported Animator parameter kind.");
                INetworkSerializer codec = type == typeof(float) && parameter.Precision != NetworkPrecisionPreset.FullPrecision
                    ? new UnityQuantizedNetworkSerializer(NetworkPrecisionProfile.ForPreset(parameter.Precision)) : null;
                yield return new NetworkMemberBinding(checked((ushort)(BaseMemberId + i)), "socketjack.animator.v1:" + name, type, capture, apply, parameter.Authority, SendInterval, true, 64, codec);
            }
        }
    }
}
