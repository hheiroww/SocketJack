using System;
using UnityEngine;

namespace SocketJack.Unity.Components
{
    /// <summary>Runtime identity of an object spawned by NetworkManager.</summary>
    [DisallowMultipleComponent]
    public sealed class NetworkIdentity : MonoBehaviour
    {
        public NetworkManager Manager { get; private set; }
        public ulong ObjectId { get; private set; }
        public ulong OwnerId { get; private set; }
        public int RoomId { get; private set; }
        public bool ServerAuthority { get; private set; } = true;
        public bool IsServer => Manager != null && Manager.IsServer;
        public bool IsOwner => Manager != null && Manager.IsClient && OwnerId == Manager.LocalPeerId;
        public bool HasStateAuthority => (ServerAuthority || OwnerId == 0) ? IsServer : IsOwner;
        internal void Bind(NetworkManager manager, NetworkObject state)
        { Manager = manager; ObjectId = state.Id; OwnerId = state.OwnerId; RoomId = state.RoomId; ServerAuthority = state.ServerAuthority; }
        /// <summary>Send owner input to authoritative game logic; this never modifies replicated state directly.</summary>
        public bool SendInput(byte[] payload)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            return IsOwner && Manager.Client.SendInput(ObjectId, payload);
        }
    }
}
