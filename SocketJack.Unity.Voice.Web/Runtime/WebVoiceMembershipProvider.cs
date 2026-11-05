using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SocketJack.Multiplayer.Chat;
using SocketJack.Multiplayer.Web;

namespace SocketJack.Multiplayer.Voice.Web
{
    /// <summary>Account-backed game-session voice without account-store I/O on the session owner thread.
    /// Mapping/presence callbacks run on that owner thread. At most two background permission queries refresh
    /// 256 cached scopes at 10 Hz; unknown/expired permission snapshots deny audio after 200 ms.</summary>
    public sealed class WebVoiceMembershipProvider : IVoiceMembershipProvider, IDisposable
    {
        private sealed class Entry { internal HashSet<string> Accounts = new HashSet<string>(StringComparer.Ordinal); internal double Refresh, Expires, Used; internal bool Pending; }
        private readonly Func<SocketJackWebServer> server;
        private readonly Func<ulong, WebAccount> accountForPeer;
        private readonly Func<IEnumerable<ulong>> peers;
        private readonly object gate = new object();
        private readonly Dictionary<string, Entry> cache = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly SemaphoreSlim work = new SemaphoreSlim(2, 2);
        private readonly Stopwatch clock = Stopwatch.StartNew();
        public bool Enabled { get; set; } = true;
        public WebVoiceMembershipProvider(Func<SocketJackWebServer> server, Func<ulong, WebAccount> authenticatedAccountForPeer, Func<IEnumerable<ulong>> connectedPeers)
        { this.server = server ?? throw new ArgumentNullException(nameof(server)); accountForPeer = authenticatedAccountForPeer ?? throw new ArgumentNullException(nameof(authenticatedAccountForPeer)); peers = connectedPeers ?? throw new ArgumentNullException(nameof(connectedPeers)); }
        public bool TryGetParticipant(ulong peerId, out VoiceParticipant participant)
        {
            participant = null; var current = server(); var account = accountForPeer(peerId);
            if (!Enabled || current == null || account == null || !current.Presence.TryGetPresence(account.Id, out var presence) || !presence.IsOnline) return false;
            participant = new VoiceParticipant { PeerId = peerId, AccountId = account.Id, DisplayName = account.DisplayName, Position = presence.Position }; return true;
        }
        public IEnumerable<ulong> ResolveAudience(VoiceParticipant speaker, VoiceChannel channel, string scope, string target)
        {
            var currentServer = server(); if (!Enabled || currentServer == null) return Array.Empty<ulong>();
            ChatChannel chat = channel == VoiceChannel.Proximity ? ChatChannel.Proximity : channel == VoiceChannel.Party ? ChatChannel.Party : channel == VoiceChannel.Raid ? ChatChannel.Raid : channel == VoiceChannel.Community ? ChatChannel.Community : channel == VoiceChannel.Whisper ? ChatChannel.Whisper : ChatChannel.Global;
            string key = speaker.AccountId + ":" + (int)channel + ":" + scope + ":" + target;
            HashSet<string> audience; double now = clock.Elapsed.TotalSeconds;
            lock (gate)
            {
                if (!cache.TryGetValue(key, out var entry))
                {
                    if (cache.Count >= 256) { var stale = cache.FirstOrDefault(value => !value.Value.Pending && now - value.Value.Used > 5); if (stale.Key == null) return Array.Empty<ulong>(); cache.Remove(stale.Key); }
                    cache.Add(key, entry = new Entry());
                }
                entry.Used = now;
                if (!entry.Pending && now >= entry.Refresh && work.Wait(0))
                {
                    entry.Pending = true; string actor = speaker.AccountId;
                    _ = Task.Run(async () =>
                    {
                        var values = new HashSet<string>(StringComparer.Ordinal);
                        try { values = new HashSet<string>(await currentServer.ResolveVoiceAudienceAsync(actor, chat, scope, target).ConfigureAwait(false), StringComparer.Ordinal); }
                        catch (Exception) { }
                        finally
                        {
                            lock (gate)
                                if (cache.TryGetValue(key, out var active) && ReferenceEquals(active, entry))
                                { entry.Accounts = values; entry.Pending = false; entry.Refresh = clock.Elapsed.TotalSeconds + .1; entry.Expires = clock.Elapsed.TotalSeconds + .2; }
                            work.Release();
                        }
                    });
                }
                if (now >= entry.Expires) return Array.Empty<ulong>(); audience = entry.Accounts;
            }
            return (peers() ?? Array.Empty<ulong>()).Where(peer => { var account = accountForPeer(peer); return account != null && audience.Contains(account.Id); }).ToArray();
        }
        public void Invalidate() { lock (gate) cache.Clear(); }
        public void Dispose() { Enabled = false; Invalidate(); }
    }
}
