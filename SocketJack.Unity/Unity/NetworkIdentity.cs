using System;
using UnityEngine;

namespace SocketJack.Multiplayer.Components
{
    /// <summary>Runtime identity of an object spawned by NetworkManager.</summary>
    [DisallowMultipleComponent]
    public sealed class NetworkIdentity : MonoBehaviour
    {
        public NetworkManager Manager { get; private set; }
        public ulong ObjectId { get; private set; }
        public ulong OwnerId { get; private set; }
        public uint PrefabId { get; private set; }
        public int RoomId { get; private set; }
        public bool ServerAuthority { get; private set; } = true;
        public uint AuthorityEpoch { get; private set; }
        public bool IsNetworkReady { get; internal set; }
        internal ulong LastOwnerInputView, LastRelaySourceView;
        internal void Unbind()
        { Manager = null; ObjectId = OwnerId = 0; PrefabId = 0; AuthorityEpoch = 0; RoomId = 0; IsNetworkReady = false; LastOwnerInputView = LastRelaySourceView = 0; }
        public bool IsServer => Manager != null && Manager.IsServer;
        public bool IsOwner => Manager != null && Manager.IsClient && OwnerId == Manager.LocalPeerId;
        public bool HasStateAuthority => (ServerAuthority || OwnerId == 0) ? IsServer : IsOwner;
        internal void Bind(NetworkManager manager, NetworkObject state)
        {
            ulong previousOwner = OwnerId; uint previousEpoch = AuthorityEpoch;
            Manager = manager; ObjectId = state.Id; PrefabId = state.PrefabId; OwnerId = state.OwnerId; RoomId = state.RoomId;
            ServerAuthority = state.ServerAuthority; AuthorityEpoch = state.AuthorityEpoch;
            if (previousEpoch != 0 && previousEpoch != AuthorityEpoch) manager.NotifyOwnershipChanged(this, previousOwner);
        }
        /// <summary>Send owner input to authoritative game logic; this never modifies replicated state directly.</summary>
        public bool SendInput(byte[] payload)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            return IsOwner && Manager.Client.SendInput(ObjectId, payload);
        }
    }
}
