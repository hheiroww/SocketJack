using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using SocketJack.Multiplayer.Transport;

namespace SocketJack.Multiplayer.Web
{
    public sealed partial class SocketJackWebServer
    {
        private sealed class RelaySession
        {
            internal string Id, OwnerId, Name, Mode;
            internal DateTime Expires;
        }
        private sealed class RelayTicket
        {
            internal string SessionId, AccountId;
            internal WebSocketRelayRole Role;
            internal DateTime Expires;
        }
        private sealed class AllocationOwner
        {
            internal string AccountId, SessionId;
            internal DatagramRelayAllocation Allocation;
        }
        private readonly object relayLock = new object();
        private readonly Dictionary<string, RelaySession> relaySessions = new Dictionary<string, RelaySession>(StringComparer.Ordinal);
        private readonly Dictionary<string, RelayTicket> relayTickets = new Dictionary<string, RelayTicket>(StringComparer.Ordinal);
        private readonly Dictionary<ulong, AllocationOwner> relayAllocations = new Dictionary<ulong, AllocationOwner>();
        private WebSocketRelayService webSocketRelay;
        private DatagramRelayServer datagramRelay;
        public IPEndPoint DatagramRelayEndPoint => datagramRelay?.LocalEndPoint;
        public int RelayAllocationCount => datagramRelay?.AllocationCount ?? 0;
        private void BeginRelay()
        {
            if (!options.EnableRelay || options.HubEndPoint != null) return;
            if (webSocketRelay != null || datagramRelay != null) throw new InvalidOperationException("Relay is already running or still stopping.");
            int port = options.DatagramRelayPort;
            if (port == -1) port = options.ListenEndPoint.Port == 0 ? 0 : options.ListenEndPoint.Port + 1;
            if (port < 0 || port > 65535) throw new ArgumentException("Configure an available UDP relay port.");
            webSocketRelay = new WebSocketRelayService(); webSocketRelay.AuthorizeTicket = ConsumeRelayTicket;
            datagramRelay = new DatagramRelayServer();
            try { datagramRelay.Start(new IPEndPoint(options.ListenEndPoint.Address, port)); }
            catch { webSocketRelay.Dispose(); webSocketRelay = null; datagramRelay.Dispose(); datagramRelay = null; throw; }
        }
        private async Task StopRelayAsync()
        {
            WebSocketRelayService ws; DatagramRelayServer udp;
            lock (relayLock)
            {
                ws = webSocketRelay; webSocketRelay = null; udp = datagramRelay; datagramRelay = null;
                relayTickets.Clear(); relaySessions.Clear(); relayAllocations.Clear();
            }
            if (ws != null) await ws.StopAsync().ConfigureAwait(false);
            if (udp != null) await udp.StopAsync().ConfigureAwait(false);
        }
        private object ExecuteRelayOperation(string operation, JsonElement data, WebAccount user)
        {
            if (!options.EnableRelay || webSocketRelay == null || datagramRelay == null) throw new WebAccountException("Relay hosting is not enabled on this server.", 503);
            lock (relayLock)
            {
                PruneRelayState();
                if (operation == "relay/create")
                {
                    if (relaySessions.Count >= 256 || relaySessions.Values.Count(session => session.OwnerId == user.Id) >= 4) throw new WebAccountException("Relay hosting quota reached.", 429);
                    string mode = String(data, "transport", 16, false).ToLowerInvariant(); if (mode == "") mode = "websocket";
                    if (mode != "udp" && mode != "websocket" && mode != "wss") throw new WebAccountException("Choose udp or websocket relay transport.");
                    var session = new RelaySession { Id = Guid.NewGuid().ToString("N"), OwnerId = user.Id, Name = String(data, "name", 64, false), Mode = mode == "udp" ? "udp" : "websocket", Expires = DateTime.UtcNow.AddHours(2) };
                    relaySessions[session.Id] = session;
                    try { return GrantRelay(session, user, true); }
                    catch { CloseRelaySession(session.Id); throw; }
                }
                string id = String(data, "sessionId", 64);
                if (!relaySessions.TryGetValue(id, out var current)) throw new WebAccountException("Relay session is not available.", 404);
                if (operation == "relay/join") return GrantRelay(current, user, false);
                if (operation == "relay/close")
                {
                    if (current.OwnerId != user.Id && !IsAdministrator(user)) throw new WebAccountException("Only the host can close this relay session.", 403);
                    CloseRelaySession(current.Id); return new { closed = true };
                }
                if (operation == "relay/renew")
                {
                    string allocationId = String(data, "allocationId", 32, false);
                    if (allocationId.Length != 0)
                    {
                        if (!ulong.TryParse(allocationId, NumberStyles.None, CultureInfo.InvariantCulture, out ulong parsed) || !relayAllocations.TryGetValue(parsed, out var allocation) || allocation.SessionId != current.Id || allocation.AccountId != user.Id) throw new WebAccountException("That relay allocation does not belong to this account.", 403);
                        allocation.Allocation = datagramRelay.Renew(parsed, TimeSpan.FromMinutes(30));
                        if (current.OwnerId == user.Id) current.Expires = DateTime.UtcNow.AddHours(2);
                        return ProjectAllocation(allocation.Allocation);
                    }
                    if (current.OwnerId != user.Id) throw new WebAccountException("Only the host can renew this relay session.", 403);
                    current.Expires = DateTime.UtcNow.AddHours(2); return new { sessionId = current.Id, expiresUtc = current.Expires };
                }
                throw new WebAccountException("Unknown relay operation.", 404);
            }
        }
        private object GrantRelay(RelaySession session, WebAccount user, bool host)
        {
            if (session.Mode == "udp")
            {
                if (relayAllocations.Count >= 8192 || relayAllocations.Values.Count(value => value.AccountId == user.Id) >= 16) throw new WebAccountException("Relay allocation quota reached.", 429);
                var allocation = datagramRelay.CreateAllocation(session.Id, host, TimeSpan.FromMinutes(30));
                relayAllocations[allocation.AllocationId] = new AllocationOwner { AccountId = user.Id, SessionId = session.Id, Allocation = allocation };
                return ProjectAllocation(allocation);
            }
            if (relayTickets.Count >= 8192 || relayTickets.Values.Count(ticket => ticket.AccountId == user.Id) >= 16) throw new WebAccountException("Relay ticket quota reached.", 429);
            string value = LocalAccountProvider.RandomToken(); var grant = new RelayTicket { AccountId = user.Id, SessionId = session.Id, Role = host ? WebSocketRelayRole.Host : WebSocketRelayRole.Client, Expires = DateTime.UtcNow.AddSeconds(60) };
            relayTickets.Add(LocalAccountProvider.HashTokenText(value), grant);
            return new { sessionId = session.Id, transport = "websocket", isHost = host, ticket = value, relayPath = "/ws/relay", expiresUtc = grant.Expires, sessionExpiresUtc = session.Expires };
        }
        private object ProjectAllocation(DatagramRelayAllocation allocation)
        {
            string host = options.PublicRelayHost;
            if (string.IsNullOrWhiteSpace(host))
            {
                if (IPAddress.Any.Equals(options.ListenEndPoint.Address) || IPAddress.IPv6Any.Equals(options.ListenEndPoint.Address)) throw new WebAccountException("Configure the public UDP relay host address.", 503);
                host = options.ListenEndPoint.Address.ToString();
            }
            return new { allocationId = allocation.AllocationId.ToString(CultureInfo.InvariantCulture), allocation.SessionId, allocation.IsHost, secret = Convert.ToBase64String(allocation.Secret), allocation.ExpiresUtc, host, port = datagramRelay.LocalEndPoint.Port, transport = "udp", maxNativeDatagramBytes = DatagramRelayServer.MaximumPayloadBytes, reservedRelayHeaderBytes = DatagramRelayServer.HeaderBytes };
        }
        private RelayAccess ConsumeRelayTicket(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 256) return null;
            lock (relayLock)
            {
                string key = LocalAccountProvider.HashTokenText(value);
                if (!relayTickets.TryGetValue(key, out var ticket)) return null;
                relayTickets.Remove(key);
                if (ticket.Expires <= DateTime.UtcNow || !relaySessions.TryGetValue(ticket.SessionId, out var session) || session.Expires <= DateTime.UtcNow || accounts.FindById(ticket.AccountId) == null) return null;
                return new RelayAccess(ticket.SessionId, ticket.Role, new DateTimeOffset(ticket.Expires), ticket.AccountId);
            }
        }
        private void PruneRelayState()
        {
            var now = DateTime.UtcNow;
            foreach (var key in relayTickets.Where(pair => pair.Value.Expires <= now).Select(pair => pair.Key).ToArray()) relayTickets.Remove(key);
            foreach (var id in relayAllocations.Where(pair => pair.Value.Allocation.ExpiresUtc <= now).Select(pair => pair.Key).ToArray()) { datagramRelay?.Revoke(id); relayAllocations.Remove(id); }
            foreach (var id in relaySessions.Where(pair => pair.Value.Expires <= now).Select(pair => pair.Key).ToArray()) CloseRelaySession(id);
        }
        private void CloseRelaySession(string id)
        {
            relaySessions.Remove(id); webSocketRelay?.RevokeSession(id); datagramRelay?.CloseSession(id);
            foreach (var ticket in relayTickets.Where(pair => pair.Value.SessionId == id).Select(pair => pair.Key).ToArray()) relayTickets.Remove(ticket);
            foreach (var allocation in relayAllocations.Where(pair => pair.Value.SessionId == id).Select(pair => pair.Key).ToArray()) relayAllocations.Remove(allocation);
        }
        private void RevokeAccountRelays(string accountId)
        {
            lock (relayLock)
            {
                foreach (var id in relaySessions.Values.Where(session => session.OwnerId == accountId).Select(session => session.Id).ToArray()) CloseRelaySession(id);
                foreach (var ticket in relayTickets.Where(pair => pair.Value.AccountId == accountId).Select(pair => pair.Key).ToArray()) relayTickets.Remove(ticket);
                foreach (var id in relayAllocations.Where(pair => pair.Value.AccountId == accountId).Select(pair => pair.Key).ToArray()) { datagramRelay?.Revoke(id); relayAllocations.Remove(id); }
            }
        }
    }
}
