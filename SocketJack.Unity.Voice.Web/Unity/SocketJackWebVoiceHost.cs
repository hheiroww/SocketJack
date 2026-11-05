using System;
using SocketJack.Multiplayer.Components;
using SocketJack.Multiplayer.Voice.Components;
using SocketJack.Multiplayer.Web;
using SocketJack.Multiplayer.Web.Components;
using UnityEngine;

namespace SocketJack.Multiplayer.Voice.Web.Components
{
    /// <summary>Native account hub/leaf voice role and authenticated game-voice membership adapter.</summary>
    [DisallowMultipleComponent, DefaultExecutionOrder(-150)]
    [RequireComponent(typeof(SocketJackWebHost))]
    public sealed class SocketJackWebVoiceHost : MonoBehaviour
    {
        private SocketJackWebHost host;
        private SocketJackWebServer bound;
        private AccountVoiceRelay relay;
        private WebVoiceMembershipProvider membership;
        public AccountVoiceRelay Relay => relay;
        private void Awake()
        {
            host = GetComponent<SocketJackWebHost>();
            var manager = GetComponent<NetworkManager>(); var voice = GetComponent<SocketJackVoiceManager>();
            if (manager != null && voice != null)
            {
                membership = new WebVoiceMembershipProvider(() => host.Server,
                    peer => host.TryGetAuthenticatedAccount(peer, out var account) ? account : null,
                    () => manager.Server == null ? Array.Empty<ulong>() : manager.Server.Peers);
                voice.MembershipProvider = membership;
            }
        }
        private void OnEnable() { if (membership != null) membership.Enabled = true; host.Started += Attach; if (host.Server != null) Attach(host.Server); }
        private void OnDisable() { host.Started -= Attach; if (relay != null) relay.Enabled = false; if (membership != null) membership.Enabled = false; }
        private void OnDestroy() { relay?.Dispose(); membership?.Dispose(); }
        private void Update() { if (bound != null && !ReferenceEquals(bound, host.Server)) { relay?.Dispose(); relay = null; bound = null; membership?.Invalidate(); } }
        private void Attach(SocketJackWebServer server)
        {
            if (ReferenceEquals(bound, server)) { if (relay != null) relay.Enabled = true; return; }
            relay?.Dispose(); membership?.Invalidate(); bound = server; relay = new AccountVoiceRelay(server);
        }
    }
}
