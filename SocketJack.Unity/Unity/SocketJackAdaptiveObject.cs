using System;
using SocketJack.Multiplayer.Serialization;
using UnityEngine;
namespace SocketJack.Multiplayer.Components
{
    /// <summary>Opt in to the server-selected Full/Balanced/Compact family. Physics continues at full local precision.</summary>
    [DisallowMultipleComponent, RequireComponent(typeof(NetworkIdentity))]
    public sealed class SocketJackAdaptiveObject : NetworkBehaviour
    {
        public NetworkPrecisionPreset LowestAllowedPrecision = NetworkPrecisionPreset.Compact;
        private NetworkPrecisionPreset selected = NetworkPrecisionPreset.Balanced;
        private uint policyEpoch, policyRevision;
        private ulong policyView;
        public NetworkPrecisionProfile CurrentProfile => NetworkPrecisionProfile.ForPreset((NetworkPrecisionPreset)Math.Min((int)selected, (int)LowestAllowedPrecision));
        public string CompatibilityId => NetworkPrecisionFamily.ProtocolId + "/policy2:" + LowestAllowedPrecision;
        internal void Select(NetworkPrecisionPreset preset)
        {
            if (preset < NetworkPrecisionPreset.FullPrecision || preset > NetworkPrecisionPreset.Compact) throw new ArgumentOutOfRangeException(nameof(preset));
            selected = (NetworkPrecisionPreset)Math.Min((int)preset, (int)LowestAllowedPrecision);
        }
        internal void SelectPolicy(uint epoch, ulong view, uint revision, NetworkPrecisionPreset preset)
        {
            if (revision == 0 || (policyEpoch == epoch && policyView == view && policyRevision != 0 && unchecked((int)(revision - policyRevision)) <= 0)) return;
            policyEpoch = epoch; policyView = view; policyRevision = revision; Select(preset);
        }
        protected override void OnNetworkReset() { selected = NetworkPrecisionPreset.Balanced; policyEpoch = policyRevision = 0; policyView = 0; }
        internal NetworkPrecisionProfile DecodeProfile(byte[] bytes, bool transform, NetworkPrecisionProfile fixedProfile)
            => NetworkPrecisionFamily.ForState(bytes, transform, fixedProfile, isActiveAndEnabled);
    }
}
