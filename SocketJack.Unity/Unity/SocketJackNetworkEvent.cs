using System;
using System.Globalization;
using UnityEngine;
using UnityEngine.Events;

namespace SocketJack.Multiplayer.Components
{
    public enum NetworkEventValueType { String = 0, Integer = 1, Float = 2, Boolean = 3, Vector3 = 4 }
    /// <summary>Inspector-configured typed command/event. Transient events require a spawned, ready object;
    /// use member synchronization for state that a late joiner must receive.</summary>
    [RequireComponent(typeof(NetworkIdentity))]
    public sealed class SocketJackNetworkEvent : NetworkBehaviour
    {
        [Range(1, 65535)] public int EventId = 1;
        public NetworkEventValueType ValueType = NetworkEventValueType.String;
        public bool RequireOwnership = true;
        public bool EchoCommandsToObservers;
        [Range(1, 8192)] public int MaxBytes = 256;
        [Min(0)] public float MaximumCommandDistance;
        public UnityEvent<string> OnCommand = new UnityEvent<string>();
        public UnityEvent<string> OnReceived = new UnityEvent<string>();
        public event Action<NetworkEventContext, object> Received;
        public Func<ulong, object, bool> CommandValidator { get; set; }
        public string CompatibilityId => "event.v1:" + EventId + ":" + ValueType + ":" + RequireOwnership + ":" + MaxBytes;
        private bool registered;
        private Type Type => ValueType == NetworkEventValueType.Integer ? typeof(int) : ValueType == NetworkEventValueType.Float ? typeof(float) :
            ValueType == NetworkEventValueType.Boolean ? typeof(bool) : ValueType == NetworkEventValueType.Vector3 ? typeof(Vector3) : typeof(string);
        protected override void OnNetworkSpawn()
        {
            Network.Events.Register(Identity.ObjectId, checked((ushort)EventId), Type, Network.Serializer, Dispatch, RequireOwnership, ValidateCommand, MaxBytes, EchoCommandsToObservers);
            registered = true;
        }
        protected override void OnNetworkDespawn()
        { if (registered) Network?.Events?.Unregister(Identity.ObjectId, (ushort)EventId); registered = false; }
        private bool ValidateCommand(ulong peerId, object value)
        {
            if (MaximumCommandDistance > 0)
            {
                bool inRange = false;
                foreach (var pair in Network.SpawnedObjects)
                {
                    var actor = pair.Value;
                    if (actor != null && actor.OwnerId == peerId && actor.RoomId == Identity.RoomId && Vector3.Distance(actor.transform.position, transform.position) <= MaximumCommandDistance)
                    { inRange = true; break; }
                }
                if (!inRange) return false;
            }
            return CommandValidator == null || CommandValidator(peerId, value);
        }
        private void Dispatch(NetworkEventContext context, object value)
        {
            string text = value is IFormattable formatted ? formatted.ToString(null, CultureInfo.InvariantCulture) : value?.ToString() ?? "";
            if (context.IsServerCommand) OnCommand.Invoke(text); else OnReceived.Invoke(text);
            Received?.Invoke(context, value);
        }
        public bool Send<T>(T value)
        {
            if (!IsNetworkReady || typeof(T) != Type) return false;
            return Network.Events.SendCommand(Identity.ObjectId, checked((ushort)EventId), value);
        }
        public bool Broadcast<T>(T value, NetworkEventAudience audience = NetworkEventAudience.Observers)
        {
            if (!IsServer || !IsNetworkReady || typeof(T) != Type) return false;
            return Network.Events.Emit(Identity.ObjectId, checked((ushort)EventId), value, audience);
        }
        public void SendString(string value) => Send(value);
        public void SendInteger(int value) => Send(value);
        public void SendFloat(float value) => Send(value);
        public void SendBoolean(bool value) => Send(value);
        public void SendVector(Vector3 value) => Send(value);
        public void BroadcastString(string value) => Broadcast(value);
        public void BroadcastInteger(int value) => Broadcast(value);
        public void BroadcastFloat(float value) => Broadcast(value);
        public void BroadcastBoolean(bool value) => Broadcast(value);
    }
}
