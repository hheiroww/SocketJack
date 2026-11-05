using System.Collections.Generic;
using SocketJack.Multiplayer.Serialization;
using UnityEngine;
namespace SocketJack.Multiplayer.Components
{
    [RequireComponent(typeof(SocketJackMemberSync), typeof(SocketJackNetworkEvent)), DisallowMultipleComponent]
    public sealed class SocketJackDoor : NetworkBehaviour, INetworkMemberSource
    {
        [Range(1, 65535)] public int StateMemberId = 50002;
        [Range(1, 65535)] public int CommandEventId = 1001;
        public Transform DoorVisual;
        public Vector3 ClosedEuler, OpenEuler = new Vector3(0, 90, 0);
        [Min(0)] public float InteractionDistance = 3, Cooldown = .3f, Smoothing = 12;
        public bool IsOpen { get; private set; }
        private SocketJackNetworkEvent command;
        private double nextInteraction;
        protected override void OnNetworkReset() { IsOpen = false; nextInteraction = 0; }
        private void Awake()
        {
            command = GetComponent<SocketJackNetworkEvent>(); command.EventId = CommandEventId; command.ValueType = NetworkEventValueType.Boolean;
            command.RequireOwnership = false; command.MaximumCommandDistance = InteractionDistance;
            command.Received += Received;
            command.CommandValidator = (peer, value) => Time.unscaledTimeAsDouble >= nextInteraction;
        }
        private void Received(NetworkEventContext context, object value)
        {
            if (!context.IsServerCommand || !IsServer) return;
            nextInteraction = Time.unscaledTimeAsDouble + Cooldown; IsOpen = !IsOpen;
        }
        public void Interact() => command.Send(true);
        private void Update()
        {
            if (DoorVisual == null) return;
            var target = Quaternion.Euler(IsOpen ? OpenEuler : ClosedEuler);
            DoorVisual.localRotation = Quaternion.Slerp(DoorVisual.localRotation, target, 1 - Mathf.Exp(-Smoothing * Time.deltaTime));
        }
        public IEnumerable<NetworkMemberBinding> CreateNetworkBindings(INetworkSerializer serializer)
        { yield return new NetworkMemberBinding(checked((ushort)StateMemberId), "socketjack.door.open.v1", typeof(bool), () => IsOpen, value => IsOpen = (bool)value, maxBytes: 8); }
    }
}
