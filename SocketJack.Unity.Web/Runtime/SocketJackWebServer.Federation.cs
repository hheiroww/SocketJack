using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SocketJack.Multiplayer.Chat;
using SocketJack.Multiplayer.Transport;

namespace SocketJack.Multiplayer.Web
{
    public sealed partial class SocketJackWebServer
    {
        private sealed class Enrollment { internal DateTime Expiry; }
        private sealed class NodeState
        {
            internal FederationNode Node;
            internal ApiSocket Socket;
            internal DateTime LastSeen;
            internal DateTime Window;
            internal int Requests;
            internal readonly Dictionary<string, WebAccount> Accounts = new Dictionary<string, WebAccount>(StringComparer.Ordinal);
            internal readonly HashSet<string> Present = new HashSet<string>(StringComparer.Ordinal);
        }
        private readonly object federationLock = new object();
        private readonly Dictionary<string, Enrollment> enrollment = new Dictionary<string, Enrollment>(StringComparer.Ordinal);
        private readonly Dictionary<string, NodeState> nodes = new Dictionary<string, NodeState>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> presenceOwners = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> ticketAcknowledgments = new ConcurrentDictionary<string, TaskCompletionSource<bool>>();
        private FederationLeaf leaf;
        private IFederationStore federationStore;
        private HttpClient proxyHttp;
        public bool IsLinkedToHub => leaf != null && leaf.IsConnected;
        public string NodeId => options.NodeId;
        public string FederationError => leaf?.LastError ?? "";

        private void InitializeFederation()
        {
            if (string.IsNullOrEmpty(options.NodeId) || options.NodeId.Length > 48 || options.NodeId.Any(value => !(char.IsLetterOrDigit(value) || value == '-' || value == '_')))
                throw new ArgumentException("Use a node ID of 1–48 letters, digits, underscores or hyphens.");
            federationStore = store as IFederationStore;
            if (options.HubEndPoint == null) return;
            var hub = options.HubEndPoint;
            if (!hub.IsAbsoluteUri || hub.UserInfo.Length != 0 || hub.Query.Length != 0 || hub.Fragment.Length != 0 || hub.AbsolutePath != "/" ||
                !(hub.Scheme == "https" || hub.Scheme == "http" && hub.IsLoopback)) throw new ArgumentException("A linked-server hub needs an HTTPS origin, or HTTP loopback development origin.");
            if (federationStore == null) throw new ArgumentException("Linked-server hosting requires an IFederationStore.");
            proxyHttp = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = hub, Timeout = TimeSpan.FromSeconds(15) };
            leaf = new FederationLeaf(this, federationStore, hub, options.NodeId, options.ServerName, options.EnrollmentToken);
        }
        private void BeginFederation() { if (leaf != null) leaf.Start(); }
        private Task WaitForFederationAsync() => leaf == null ? Task.CompletedTask : leaf.WaitForInitialConnectionAsync();
        private async Task StopAllAsync()
        {
            if (leaf != null) await leaf.StopAsync().ConfigureAwait(false);
            await listener.StopAsync().ConfigureAwait(false);
            await StopRelayAsync().ConfigureAwait(false);
        }
        /// <summary>Supply main-thread snapshots. Linked servers publish them to the canonical hub; there is no Unity access on network callbacks.</summary>
        public void PublishNodePresence(FederationPresenceUpdate update)
        {
            if (update == null || update.Players == null || update.Players.Count > 4096 || update.RemovedAccountIds == null || update.RemovedAccountIds.Length > 4096) throw new ArgumentException("Invalid bounded node presence update.");
            if (leaf != null) leaf.UpdatePresence(update);
        }
        private object ExecuteNodeHttp(string operation, JsonElement data, WebAccount user, bool secure)
        {
            if (!secure) throw new WebAccountException("Linked servers require secure transport.", 403);
            if (federationStore == null) throw new WebAccountException("The account store does not support server enrollment.", 503);
            if (operation == "nodes/enroll")
            {
                string link = String(data, "linkToken", 256); string id = String(data, "nodeId", 48); string name = String(data, "name", 128, false);
                if (id == options.NodeId || id.Any(value => !(char.IsLetterOrDigit(value) || value == '-' || value == '_'))) throw new WebAccountException("Choose a unique linked-server ID.");
                lock (federationLock)
                {
                    string hash = LocalAccountProvider.HashTokenText(link);
                    if (!enrollment.TryGetValue(hash, out var grant) || grant.Expiry <= DateTime.UtcNow) throw new WebAccountException("Enrollment token is invalid or expired.", 403);
                    string nodeKey = LocalAccountProvider.RandomToken();
                    if (!federationStore.TryAddNode(new FederationNode { Id = id, Name = string.IsNullOrWhiteSpace(name) ? id : name, KeyHash = LocalAccountProvider.HashTokenText(nodeKey), Enabled = true })) throw new WebAccountException("That linked-server ID is already enrolled.", 409);
                    enrollment.Remove(hash); return new { nodeId = id, nodeKey };
                }
            }
            RequireAdministrator(user);
            if (operation == "nodes/issue-link")
            {
                lock (federationLock)
                {
                    var expired = enrollment.Where(pair => pair.Value.Expiry <= DateTime.UtcNow).Select(pair => pair.Key).ToArray(); foreach (var key in expired) enrollment.Remove(key);
                    if (enrollment.Count >= 32) throw new WebAccountException("Too many pending enrollment tokens.", 429);
                    string value = LocalAccountProvider.RandomToken(); enrollment.Add(LocalAccountProvider.HashTokenText(value), new Enrollment { Expiry = DateTime.UtcNow.AddMinutes(10) });
                    return new { linkToken = value, expiresInSeconds = 600 };
                }
            }
            if (operation == "nodes/revoke")
            {
                string id = String(data, "nodeId", 48); federationStore.RevokeNode(id);
                lock (federationLock) if (nodes.TryGetValue(id, out var node)) node.Socket.Cancellation.Cancel();
                return new { revoked = true };
            }
            if (operation == "nodes") { lock (federationLock) return nodes.Values.Select(node => new { nodeId = node.Node.Id, name = node.Node.Name, lastSeenUtc = node.LastSeen, players = node.Present.Count }).ToArray(); }
            throw new WebAccountException("Server operation was not found.", 404);
        }
        private void BindNodeSocket(ApiSocket socket, JsonElement data, bool secure)
        {
            if (!secure || federationStore == null || socket.User != null) throw new WebAccountException("Server authentication is unavailable.", 403);
            string id = String(data, "nodeId", 48), key = String(data, "nodeKey", 256); var node = federationStore.FindNode(id);
            byte[] expected = node == null ? new byte[32] : HexBytes(node.KeyHash), actual = LocalAccountProvider.HashToken(key);
            if (node == null || !node.Enabled || !CryptographicOperations.FixedTimeEquals(expected, actual)) throw new WebAccountException("Linked-server credentials are invalid.", 401);
            lock (federationLock)
            {
                if (nodes.TryGetValue(id, out var previous)) previous.Socket.Cancellation.Cancel();
                nodes[id] = new NodeState { Node = node, Socket = socket, LastSeen = DateTime.UtcNow, Window = DateTime.UtcNow };
                socket.NodeId = id; socket.Cancellation.CancelAfter(Timeout.InfiniteTimeSpan);
            }
        }
        private object ExecuteNodeSocket(ApiSocket socket, string operation, JsonElement data)
        {
            lock (federationLock)
            {
                if (!nodes.TryGetValue(socket.NodeId, out var node) || !ReferenceEquals(node.Socket, socket)) throw new WebAccountException("Linked-server connection was replaced.", 401);
                var now = DateTime.UtcNow; node.LastSeen = now;
                if (now - node.Window >= TimeSpan.FromMinutes(1)) { node.Window = now; node.Requests = 0; }
                if (++node.Requests > 12000) throw new WebAccountException("Linked-server message budget exceeded.", 429);
                if (operation == "nodes/voice-audience")
                {
                    string actor = String(data, "accountId", 64);
                    if (!node.Present.Contains(actor) || !presenceOwners.TryGetValue(actor, out string presenceNode) || presenceNode != socket.NodeId ||
                        !Presence.TryGetPresence(actor, out var active) || !active.HasGamePresence)
                        throw new WebAccountException("Voice speaker does not have an active game presence on this server.", 403);
                    var channel = ParseChannel(String(data, "channel", 32));
                    string scope = String(data, "scopeId", 64, false), target = String(data, "targetId", 64, false);
                    lock (socialLock) return social.ResolveAudience(actor, channel, scope, target).Where(node.Present.Contains).Take(4096).ToArray();
                }
                if (operation == "nodes/ack-ticket")
                {
                    string ack = socket.NodeId + ":" + String(data, "ticketId", 64);
                    if (ticketAcknowledgments.TryRemove(ack, out var completion)) completion.TrySetResult(true);
                    return new { acknowledged = true };
                }
                if (operation == "nodes/presence")
                {
                    var update = JsonSerializer.Deserialize<FederationPresenceUpdate>(data.GetRawText(), Json);
                    if (update?.Players == null || update.Players.Count > 64 || update.RemovedAccountIds == null || update.RemovedAccountIds.Length > 64) throw new WebAccountException("Presence batch exceeds its bounds.");
                    foreach (var removed in update.RemovedAccountIds)
                        if (presenceOwners.TryGetValue(removed, out string owner) && owner == socket.NodeId) { Presence.RemoveGamePresence(removed); presenceOwners.Remove(removed); node.Present.Remove(removed); }
                    foreach (var player in update.Players)
                    {
                        if (!node.Accounts.TryGetValue(player.AccountId, out var account))
                        {
                            if (node.Accounts.Count >= 8192) throw new WebAccountException("Linked-server account cache reached its bound.");
                            account = accounts.FindById(player.AccountId); if (account == null) throw new WebAccountException("Presence account was not found."); node.Accounts[player.AccountId] = account;
                        }
                        if (node.Present.Count >= 4096 && !node.Present.Contains(player.AccountId)) throw new WebAccountException("Linked-server player limit reached.");
                        Presence.UpdateGamePresence(account, player.WorldId ?? "", player.RoomId, new System.Numerics.Vector3(player.X, player.Y, player.Z));
                        presenceOwners[player.AccountId] = socket.NodeId; node.Present.Add(player.AccountId);
                    }
                    return new { accepted = update.Players.Count };
                }
                throw new WebAccountException("Unknown linked-server operation.", 404);
            }
        }
        private void RemoveNodeSocket(ApiSocket socket)
        {
            if (socket.NodeId == null) return;
            lock (federationLock)
            {
                if (!nodes.TryGetValue(socket.NodeId, out var node) || !ReferenceEquals(node.Socket, socket)) return;
                nodes.Remove(socket.NodeId);
                foreach (string id in node.Present) if (presenceOwners.TryGetValue(id, out string owner) && owner == socket.NodeId) { Presence.RemoveGamePresence(id); presenceOwners.Remove(id); }
            }
        }
        private async Task<string> IssueNodeGameTicketAsync(WebAccount account, string nodeId, CancellationToken token)
        {
            string ticket = "SJAT1:" + LocalAccountProvider.RandomToken(), ticketId = Guid.NewGuid().ToString("N"), key = nodeId + ":" + ticketId;
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (federationLock)
            {
                if (!nodes.TryGetValue(nodeId, out var node) || node.Socket.Cancellation.IsCancellationRequested) throw new WebAccountException("That game server is offline.", 503);
                if (ticketAcknowledgments.Count >= 1024) throw new WebAccountException("Game-ticket delivery is busy.", 429);
                ticketAcknowledgments[key] = completion;
                Queue(node.Socket, new { @event = "node.ticket", data = new { ticketId, connectionData = ticket, user = account, expiresUtc = DateTime.UtcNow.AddSeconds(60) } });
            }
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(5));
                using (deadline.Token.Register(() => completion.TrySetCanceled()))
                    try { await completion.Task.ConfigureAwait(false); return ticket; }
                    catch (OperationCanceledException) { throw new WebAccountException("The game server did not confirm admission in time.", 503); }
                    finally { ticketAcknowledgments.TryRemove(key, out _); }
            }
        }
        private void ImportNodeTicket(string ticket, WebAccount account, DateTime expires)
        {
            string key = TicketKey(Encoding.ASCII.GetBytes(ticket));
            if (key == null || account == null || expires <= DateTime.UtcNow || expires > DateTime.UtcNow.AddSeconds(65)) throw new InvalidDataException("Invalid hub game-ticket grant.");
            lock (ticketLock) { PruneTickets(); if (tickets.Count >= 8192) throw new InvalidDataException("Game-ticket cache reached its bound."); tickets[key] = new GameTicket { Account = LocalAccountProvider.Copy(account), Expires = expires }; }
        }
        private void BroadcastNodeRevocation(string accountId)
        {
            lock (federationLock)
                foreach (var node in nodes.Values) { node.Accounts.Remove(accountId); node.Present.Remove(accountId); Queue(node.Socket, new { @event = "node.account-revoked", data = new { accountId } }); }
        }
        private async Task<WebSocketHttpResponse> ProxyHttpAsync(WebSocketHttpRequest request, string operation, CancellationToken token)
        {
            try
            {
                if (operation == "bootstrap" || operation.StartsWith("nodes/", StringComparison.Ordinal) || request.QueryString.Length != 0) throw new WebAccountException("Manage linked-server enrollment at the hub.", 403);
                if (operation.Length == 0 || operation.Length > 80 || operation.Contains("..") || operation.Any(value => !(value >= 'a' && value <= 'z' || value >= '0' && value <= '9' || value == '/' || value == '-'))) throw new WebAccountException("Invalid API operation.");
                RequireRate(request.RemoteEndPoint.Address.ToString(), operation);
                using (var outbound = new HttpRequestMessage(new HttpMethod(request.Method), "api/v1/" + operation))
                {
                    string bearer = GetBearer(request); if (bearer != null) outbound.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
                    if (request.Body.Length > 0) outbound.Content = new StringContent(Encoding.UTF8.GetString(request.Body), Encoding.UTF8, "application/json");
                    using (var response = await proxyHttp.SendAsync(outbound, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
                    {
                        byte[] bytes = await ReadBoundedAsync(await response.Content.ReadAsStreamAsync().ConfigureAwait(false), 2 * 1024 * 1024, token).ConfigureAwait(false);
                        var result = new WebSocketHttpResponse { StatusCode = (int)response.StatusCode, ContentType = "application/json; charset=utf-8", Body = bytes }; AddHeaders(result, request, true); return result;
                    }
                }
            }
            catch (Exception error) { return Failure(error is HttpRequestException || error is OperationCanceledException ? new WebAccountException("The account/chat hub is unavailable.", 503) : error, request); }
        }
        private async Task ProxySocketAsync(WebSocket browser, CancellationToken token, string path = "/ws/api")
        {
            using (var upstream = new ClientWebSocket())
            using (var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                var uri = new UriBuilder(options.HubEndPoint) { Scheme = options.HubEndPoint.Scheme == "https" ? "wss" : "ws", Path = path, Query = "" }.Uri;
                await upstream.ConnectAsync(uri, lifetime.Token).ConfigureAwait(false);
                bool binary = path != "/ws/api";
                var toHub = PumpApiSocketAsync(browser, upstream, lifetime.Token, binary); var toBrowser = PumpApiSocketAsync(upstream, browser, lifetime.Token, binary);
                await Task.WhenAny(toHub, toBrowser).ConfigureAwait(false); lifetime.Cancel();
                try { await Task.WhenAll(toHub, toBrowser).ConfigureAwait(false); } catch (Exception error) when (error is OperationCanceledException || error is WebSocketException || error is IOException) { }
            }
        }
        private static async Task PumpApiSocketAsync(WebSocket input, WebSocket output, CancellationToken token, bool allowBinary)
        {
            var buffer = new byte[4096];
            while (!token.IsCancellationRequested)
            {
                using (var memory = new MemoryStream())
                {
                    WebSocketReceiveResult result;
                    do { result = await input.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false); if (result.MessageType == WebSocketMessageType.Close) return; if (result.MessageType != WebSocketMessageType.Text && !(allowBinary && result.MessageType == WebSocketMessageType.Binary) || memory.Length + result.Count > 128 * 1024) throw new InvalidDataException("Invalid API bridge frame."); memory.Write(buffer, 0, result.Count); } while (!result.EndOfMessage);
                    await output.SendAsync(new ArraySegment<byte>(memory.ToArray()), result.MessageType, true, token).ConfigureAwait(false);
                }
            }
        }
        private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximum, CancellationToken token)
        {
            using (stream) using (var memory = new MemoryStream())
            {
                var buffer = new byte[8192]; int count;
                while ((count = await stream.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) > 0) { if (memory.Length + count > maximum) throw new InvalidDataException("Hub response exceeded its bound."); memory.Write(buffer, 0, count); }
                return memory.ToArray();
            }
        }
        private static byte[] HexBytes(string value)
        {
            if (value == null || value.Length != 64) return new byte[32]; var bytes = new byte[32];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = byte.Parse(value.Substring(i * 2, 2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture); return bytes;
        }

        private sealed class FederationLeaf
        {
            private readonly SocketJackWebServer owner;
            private readonly IFederationStore store;
            private readonly Uri hub;
            private readonly string nodeId, name, enrollmentToken;
            private readonly object sync = new object();
            private readonly SemaphoreSlim send = new SemaphoreSlim(1, 1);
            private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> pending = new ConcurrentDictionary<string, TaskCompletionSource<JsonElement>>();
            private CancellationTokenSource cancellation;
            private Task running;
            private TaskCompletionSource<bool> initial;
            private ClientWebSocket socket;
            private FederationPresenceUpdate presence = new FederationPresenceUpdate();
            private long requestId;
            public bool IsConnected { get; private set; }
            public string LastError { get; private set; } = "";
            internal FederationLeaf(SocketJackWebServer owner, IFederationStore store, Uri hub, string nodeId, string name, string enrollmentToken)
            { this.owner = owner; this.store = store; this.hub = hub; this.nodeId = nodeId; this.name = name; this.enrollmentToken = enrollmentToken; }
            internal void UpdatePresence(FederationPresenceUpdate value)
            { var detached = JsonSerializer.Deserialize<FederationPresenceUpdate>(JsonSerializer.Serialize(value, Json), Json); lock (sync) presence = detached; }
            internal void Start()
            {
                lock (sync)
                {
                    if (running != null && !running.IsCompleted) return;
                    cancellation?.Dispose(); cancellation = new CancellationTokenSource(); initial = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); running = RunAsync(cancellation.Token);
                }
            }
            internal Task WaitForInitialConnectionAsync() { lock (sync) return initial?.Task ?? Task.CompletedTask; }
            internal async Task<string[]> ResolveVoiceAudienceAsync(string actor, ChatChannel channel, string scope, string target, CancellationToken token)
            {
                if (!IsConnected) throw new WebAccountException("The account hub is unavailable.", 503);
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    deadline.CancelAfter(TimeSpan.FromSeconds(3));
                    var result = await RequestAsync("nodes/voice-audience", new { accountId = actor, channel = ChannelName(channel), scopeId = scope ?? "", targetId = target ?? "" }, deadline.Token).ConfigureAwait(false);
                    if (result.ValueKind != JsonValueKind.Array || result.GetArrayLength() > 4096) throw new InvalidDataException("Invalid voice permission response.");
                    var audience = result.EnumerateArray().Select(value => value.GetString()).ToArray();
                    if (audience.Any(value => string.IsNullOrEmpty(value) || value.Length > 64)) throw new InvalidDataException("Invalid voice account identity.");
                    return audience;
                }
            }
            internal async Task StopAsync()
            {
                Task wait;
                lock (sync) { cancellation?.Cancel(); socket?.Abort(); wait = running; }
                if (wait != null) try { await wait.ConfigureAwait(false); } catch (OperationCanceledException) { }
            }
            private async Task RunAsync(CancellationToken token)
            {
                int delay = 1000;
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        string key = store.ReadNodeKey(hub, nodeId);
                        if (key == null)
                        {
                            if (string.IsNullOrEmpty(enrollmentToken)) throw new WebAccountException("Configure a one-time hub enrollment token before starting this linked server.", 403);
                            using (var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/nodes/enroll"))
                            {
                                request.Content = new StringContent(JsonSerializer.Serialize(new { nodeId, name, linkToken = enrollmentToken }, Json), Encoding.UTF8, "application/json");
                                using (var response = await owner.proxyHttp.SendAsync(request, token).ConfigureAwait(false))
                                {
                                    var body = JsonSerializer.Deserialize<JsonElement>(await ReadBoundedAsync(await response.Content.ReadAsStreamAsync().ConfigureAwait(false), 16384, token).ConfigureAwait(false), Json);
                                    var result = SocketJackWebClientUnwrap(body); key = result.GetProperty("nodeKey").GetString(); store.SaveNodeKey(hub, nodeId, key);
                                }
                            }
                        }
                        using (var current = new ClientWebSocket())
                        using (var connected = CancellationTokenSource.CreateLinkedTokenSource(token))
                        {
                            lock (sync) socket = current;
                            await current.ConnectAsync(new UriBuilder(hub) { Scheme = hub.Scheme == "https" ? "wss" : "ws", Path = "/ws/api", Query = "" }.Uri, connected.Token).ConfigureAwait(false);
                            var receiving = ReceiveAsync(current, connected.Token);
                            await RequestAsync("nodes/resume", new { nodeId, nodeKey = key }, connected.Token).ConfigureAwait(false);
                            IsConnected = true; LastError = ""; delay = 1000; initial.TrySetResult(true);
                            try
                            {
                                while (!connected.IsCancellationRequested && !receiving.IsCompleted)
                                {
                                    FederationPresenceUpdate snapshot; lock (sync) snapshot = presence;
                                    var sends = new List<Task>();
                                    for (int offset = 0; offset < Math.Max(1, Math.Max(snapshot.Players.Count, snapshot.RemovedAccountIds.Length)); offset += 32)
                                        sends.Add(RequestAsync("nodes/presence", new FederationPresenceUpdate { Players = snapshot.Players.Skip(offset).Take(32).ToList(), RemovedAccountIds = snapshot.RemovedAccountIds.Skip(offset).Take(32).ToArray() }, connected.Token));
                                    await Task.WhenAll(sends).ConfigureAwait(false); await Task.Delay(1000, connected.Token).ConfigureAwait(false);
                                }
                            }
                            finally { connected.Cancel(); current.Abort(); try { await receiving.ConfigureAwait(false); } catch (Exception error) when (error is OperationCanceledException || error is WebSocketException || error is IOException) { } }
                        }
                    }
                    catch (Exception error) when (error is HttpRequestException || error is WebSocketException || error is IOException || error is OperationCanceledException || error is WebAccountException)
                    {
                        LastError = error is WebAccountException ? error.Message : "The account/chat hub is unavailable.";
                        if (initial != null && !initial.Task.IsCompleted && error is WebAccountException accountError && accountError.StatusCode == 403) initial.TrySetException(error);
                    }
                    finally
                    {
                        IsConnected = false; foreach (var item in pending.Values) item.TrySetException(new IOException("Linked-server connection closed.")); pending.Clear();
                    }
                    if (!token.IsCancellationRequested) { await Task.Delay(delay, token).ConfigureAwait(false); delay = Math.Min(30000, delay * 2); }
                }
                initial?.TrySetCanceled();
            }
            private async Task<JsonElement> RequestAsync(string operation, object data, CancellationToken token)
            {
                if (pending.Count >= 256) throw new IOException("Linked-server request backlog reached its bound.");
                string id = Interlocked.Increment(ref requestId).ToString(System.Globalization.CultureInfo.InvariantCulture); var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously); pending[id] = completion;
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    timeout.CancelAfter(10000);
                    using (timeout.Token.Register(() => completion.TrySetCanceled()))
                        try
                        {
                            byte[] dataBytes = JsonSerializer.SerializeToUtf8Bytes(new { id, op = operation, data }, Json);
                            await send.WaitAsync(timeout.Token).ConfigureAwait(false);
                            try { await socket.SendAsync(new ArraySegment<byte>(dataBytes), WebSocketMessageType.Text, true, timeout.Token).ConfigureAwait(false); } finally { send.Release(); }
                            return await completion.Task.ConfigureAwait(false);
                        }
                        finally { pending.TryRemove(id, out _); }
                }
            }
            private async Task ReceiveAsync(ClientWebSocket current, CancellationToken token)
            {
                byte[] buffer = new byte[4096];
                while (!token.IsCancellationRequested)
                {
                    byte[] message;
                    using (var memory = new MemoryStream())
                    {
                        WebSocketReceiveResult result;
                        do { result = await current.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false); if (result.MessageType == WebSocketMessageType.Close) return; if (result.MessageType != WebSocketMessageType.Text || memory.Length + result.Count > 256 * 1024) throw new InvalidDataException("Invalid linked-server frame."); memory.Write(buffer, 0, result.Count); } while (!result.EndOfMessage);
                        message = memory.ToArray();
                    }
                    var envelope = JsonSerializer.Deserialize<JsonElement>(message, Json);
                    if (envelope.TryGetProperty("id", out var id) && pending.TryRemove(id.GetString(), out var completion))
                    { try { completion.TrySetResult(SocketJackWebClientUnwrap(envelope)); } catch (Exception error) { completion.TrySetException(error); } }
                    else if (envelope.TryGetProperty("event", out var eventName) && envelope.TryGetProperty("data", out var payload))
                    {
                        if (eventName.GetString() == "node.ticket")
                        {
                            owner.ImportNodeTicket(payload.GetProperty("connectionData").GetString(), JsonSerializer.Deserialize<WebAccount>(payload.GetProperty("user").GetRawText(), Json), payload.GetProperty("expiresUtc").GetDateTime());
                            // Enqueue the acknowledgment without awaiting its reply on the receive loop.
                            _ = RequestAsync("nodes/ack-ticket", new { ticketId = payload.GetProperty("ticketId").GetString() }, token).ContinueWith(task => { if (task.IsFaulted) LastError = "Game-ticket acknowledgment failed."; }, TaskScheduler.Default);
                        }
                        else if (eventName.GetString() == "node.account-revoked") owner.RevokeLiveAccount(payload.GetProperty("accountId").GetString());
                    }
                }
            }
        }
        private static JsonElement SocketJackWebClientUnwrap(JsonElement envelope)
        {
            if (!envelope.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True) throw new WebAccountException(envelope.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String ? error.GetString() : "The hub rejected this operation.", 403);
            return envelope.GetProperty("data").Clone();
        }
    }
}
