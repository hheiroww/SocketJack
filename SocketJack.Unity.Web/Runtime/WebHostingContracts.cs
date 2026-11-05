using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using SocketJack.Multiplayer.Chat;

namespace SocketJack.Multiplayer.Web
{
    public sealed class SocketJackWebOptions
    {
        public IPEndPoint ListenEndPoint { get; set; } = new IPEndPoint(IPAddress.Loopback, 7778);
        public X509Certificate2 Certificate { get; set; }
        public string[] AllowedOrigins { get; set; } = Array.Empty<string>();
        public string ServerName { get; set; } = "SocketJack Multiplayer";
        public int MaxConnections { get; set; } = 4096;
        public int MaxWebSocketsPerAccount { get; set; } = 4;
        public int MaxQueuedEventsPerSocket { get; set; } = 128;
        public int MaxApiBytes { get; set; } = 16384;
        public int RequestsPerMinutePerAddress { get; set; } = 240;
        public int AuthenticationPerMinutePerAddress { get; set; } = 12;
        public bool EnableRegistration { get; set; } = true;
        public Func<ChatLink, bool> LinkValidator { get; set; }
        public string NodeId { get; set; } = "local";
        public Uri HubEndPoint { get; set; }
        public string EnrollmentToken { get; set; }
        public bool EnableRelay { get; set; }
        public int DatagramRelayPort { get; set; } = -1;
        public string PublicRelayHost { get; set; }
    }
    public sealed class WebServerStatus
    {
        public string Name { get; set; } = "SocketJack Multiplayer";
        public string Version { get; set; } = "";
        public int Players { get; set; }
        public int Objects { get; set; }
        public int TickRate { get; set; }
        public string Transport { get; set; } = "UDP";
        public int WebSocketClients { get; set; }
        public double UptimeSeconds { get; set; }
        public bool IsSecure { get; set; }
    }
    /// <summary>Thread-safe snapshots populated by authenticated web sessions and the game's main-thread adapter.</summary>
    public sealed class WebPresenceDirectory : IChatMembershipProvider
    {
        private sealed class Entry
        {
            internal WebAccount Account;
            internal int WebSockets;
            internal DateTime LastRequest;
            internal ChatPresence Game;
            internal DateTime LastGameUpdate;
        }
        private readonly object sync = new object();
        private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly int maximum;
        public WebPresenceDirectory(int maximum = 100000) { if (maximum < 1) throw new ArgumentOutOfRangeException(nameof(maximum)); this.maximum = maximum; }
        public void Touch(WebAccount account)
        {
            if (account == null) return;
            lock (sync) { var entry = Get(account); entry.LastRequest = DateTime.UtcNow; }
        }
        public void Connect(WebAccount account) { lock (sync) { var entry = Get(account); entry.WebSockets++; entry.LastRequest = DateTime.UtcNow; } }
        public bool TryConnect(WebAccount account, int maximum)
        {
            lock (sync) { var entry = Get(account); if (entry.WebSockets >= maximum) return false; entry.WebSockets++; entry.LastRequest = DateTime.UtcNow; return true; }
        }
        public void Disconnect(string accountId) { lock (sync) if (entries.TryGetValue(accountId, out var entry)) { entry.WebSockets = Math.Max(0, entry.WebSockets - 1); entry.LastRequest = DateTime.UtcNow; } }
        public int WebSocketCount(string accountId) { lock (sync) return entries.TryGetValue(accountId, out var entry) ? entry.WebSockets : 0; }
        public void UpdateGamePresence(WebAccount account, string worldId, int roomId, System.Numerics.Vector3 position)
        {
            if (account == null || worldId == null || worldId.Length > 128 || !Finite(position.X) || !Finite(position.Y) || !Finite(position.Z)) throw new ArgumentException("Game presence needs an authenticated account and finite position.");
            lock (sync)
            {
                var entry = Get(account); entry.LastGameUpdate = DateTime.UtcNow;
                entry.Game = new ChatPresence { AccountId = account.Id, DisplayName = account.DisplayName, IsOnline = true, HasGamePresence = true, WorldId = worldId, RoomId = roomId, Position = position };
            }
        }
        public void RemoveGamePresence(string accountId) { lock (sync) if (entries.TryGetValue(accountId, out var entry)) entry.Game = null; }
        public void Remove(string accountId) { lock (sync) entries.Remove(accountId); }
        public bool TryGetPresence(string accountId, out ChatPresence presence)
        {
            lock (sync)
            {
                if (accountId != null && entries.TryGetValue(accountId, out var entry)) { presence = Snapshot(entry); return presence.IsOnline; }
                presence = null; return false;
            }
        }
        public IEnumerable<ChatPresence> GetOnlinePresences()
        {
            lock (sync)
            {
                var result = new List<ChatPresence>(); foreach (var entry in entries.Values) { var presence = Snapshot(entry); if (presence.IsOnline) result.Add(presence); } return result;
            }
        }
        private Entry Get(WebAccount account)
        {
            if (!entries.TryGetValue(account.Id, out var entry))
            {
                if (entries.Count >= maximum)
                {
                    var remove = new List<string>(); foreach (var pair in entries) if (!Snapshot(pair.Value).IsOnline) remove.Add(pair.Key);
                    foreach (var key in remove) entries.Remove(key); if (entries.Count >= maximum) throw new WebAccountException("Presence capacity reached.", 503);
                }
                entry = new Entry { Account = LocalAccountProvider.Copy(account) }; entries.Add(account.Id, entry);
            }
            else entry.Account = LocalAccountProvider.Copy(account);
            return entry;
        }
        private static ChatPresence Snapshot(Entry entry)
        {
            var now = DateTime.UtcNow;
            if (entry.Game != null && now - entry.LastGameUpdate < TimeSpan.FromSeconds(5))
                return new ChatPresence { AccountId = entry.Game.AccountId, DisplayName = entry.Account.DisplayName, IsOnline = true, HasGamePresence = true, WorldId = entry.Game.WorldId, RoomId = entry.Game.RoomId, Position = entry.Game.Position };
            return new ChatPresence { AccountId = entry.Account.Id, DisplayName = entry.Account.DisplayName, IsOnline = entry.WebSockets > 0 || now - entry.LastRequest < TimeSpan.FromSeconds(30) };
        }
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
