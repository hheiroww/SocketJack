using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SocketJack.Multiplayer.Chat;
using SocketJack.Multiplayer.Transport;

namespace SocketJack.Multiplayer.Web
{
    /// <summary>Optional embedded HTTP/WS account/chat service. It never reads Unity objects from I/O callbacks.</summary>
    public sealed partial class SocketJackWebServer : IDisposable, IAsyncDisposable
    {
        private sealed class ApiSocket
        {
            internal readonly Guid Id = Guid.NewGuid();
            internal WebSocket Socket;
            internal CancellationTokenSource Cancellation;
            internal readonly ConcurrentQueue<byte[]> Outbox = new ConcurrentQueue<byte[]>();
            internal readonly SemaphoreSlim Available = new SemaphoreSlim(0);
            internal string Token;
            internal WebAccount User;
            internal int Queued;
            internal string Address;
            internal string NodeId;
        }
        private sealed class RateEntry { internal DateTime Window; internal int Count; }
        private sealed class GameTicket { internal WebAccount Account; internal DateTime Expires; internal bool Claimed; }
        private readonly object socialLock = new object(), stateLock = new object(), rateLock = new object(), ticketLock = new object();
        private readonly SocketJackWebOptions options;
        private readonly IAccountStore store;
        private readonly IAccountProvider accounts;
        private readonly bool ownsAccounts;
        private readonly SocialService social;
        private readonly WebSocketListener listener;
        private readonly ConcurrentDictionary<Guid, ApiSocket> sockets = new ConcurrentDictionary<Guid, ApiSocket>();
        private readonly Dictionary<string, RateEntry> rates = new Dictionary<string, RateEntry>(StringComparer.Ordinal);
        private readonly Dictionary<string, GameTicket> tickets = new Dictionary<string, GameTicket>(StringComparer.Ordinal);
        private readonly Stopwatch uptime = Stopwatch.StartNew();
        private readonly Dictionary<string, (byte[] Bytes, string Type)> website = new Dictionary<string, (byte[], string)>(StringComparer.Ordinal);
        private WebServerStatus status;
        private JsonElement diagnostics;
        private bool disposed;
        private long requests, rejected, chatMessages;
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = false, MaxDepth = 24 };

        public WebPresenceDirectory Presence { get; }
        public IAccountProvider Accounts => accounts;
        public IPEndPoint LocalEndPoint => listener.LocalEndPoint;
        public bool IsRunning => listener.IsRunning;
        public int WebSocketClients => sockets.Count;
        public string BootstrapCode => (accounts as LocalAccountProvider)?.BootstrapCode;
        public event Action<ChatMessage> ChatAccepted;
        public event Action<string> AccountRevoked;
        public event Action<Exception> ServerError;

        /// <summary>Trusted game-server feed. Call on the canonical account hub with explicit recipients;
        /// this operation is deliberately absent from player-facing HTTP/WebSocket commands.</summary>
        public ChatMessage PublishCombat(string text, IEnumerable<string> recipientIds, string scopeId = "")
            => PublishTrusted(text, recipientIds, scopeId, true);
        public ChatMessage PublishSystem(string text, IEnumerable<string> recipientIds, string scopeId = "")
            => PublishTrusted(text, recipientIds, scopeId, false);
        private ChatMessage PublishTrusted(string text, IEnumerable<string> recipientIds, string scopeId, bool combat)
        {
            if (options.HubEndPoint != null) throw new InvalidOperationException("Publish trusted feeds on the canonical account hub.");
            ChatSendResult result;
            lock (socialLock) result = combat ? social.SendCombat(NormalizeLinks(text), recipientIds, scopeId) : social.SendSystem(NormalizeLinks(text), recipientIds, scopeId);
            if (!result.Success) throw new WebAccountException(result.Error ?? "The trusted feed was not accepted.");
            BroadcastChat(result); return result.Message;
        }

        public SocketJackWebServer(IAccountStore accountStore, ISocialStore socialStore, SocketJackWebOptions options = null, IAccountProvider accountProvider = null)
        {
            store = accountStore ?? throw new ArgumentNullException(nameof(accountStore)); this.options = options ?? new SocketJackWebOptions();
            ValidateOptions(this.options);
            accounts = accountProvider ?? new LocalAccountProvider(store); ownsAccounts = accountProvider == null;
            Presence = new WebPresenceDirectory();
            social = new SocialService(Presence, socialStore, new SocialOptions
            {
                LookupAccount = id => ToChatAccount(accounts.FindById(id)),
                LookupAccountByName = name => ToChatAccount(accounts.FindByUsername(name)),
                IsModerator = id => IsAdministrator(accounts.FindById(id))
            });
            social.Links.Register("player", id => accounts.FindById(id) != null, id => accounts.FindById(id)?.DisplayName);
            listener = new WebSocketListener(new WebSocketListenerOptions
            {
                Certificate = this.options.Certificate, AllowedOrigins = this.options.AllowedOrigins,
                MaxConnections = this.options.MaxConnections, MaxBodyBytes = this.options.MaxApiBytes, MaxResponseBytes = 2 * 1024 * 1024
            });
            listener.HttpHandler = HandleHttpAsync; listener.UpgradeAuthorizer = AuthorizeUpgradeAsync; listener.WebSocketAccepted = HandleSocketAsync;
            listener.HandlerError += error => ServerError?.Invoke(error);
            status = new WebServerStatus { Name = this.options.ServerName, Version = typeof(SocketJackWebServer).Assembly.GetName().Version.ToString(), IsSecure = this.options.Certificate != null };
            diagnostics = JsonSerializer.Deserialize<JsonElement>("{}");
            LoadWebsite();
            InitializeFederation();
        }
        public void Start() { ThrowIfDisposed(); BeginRelay(); try { listener.Start(options.ListenEndPoint); BeginFederation(); } catch { _ = StopRelayAsync(); throw; } }
        public async Task StartAsync() { Start(); await WaitForFederationAsync().ConfigureAwait(false); }
        public Task StopAsync() => StopAllAsync();
        public void UpdateStatus(WebServerStatus snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            // Serialization creates a detached snapshot rather than retaining a caller-owned mutable object.
            var copy = JsonSerializer.Deserialize<WebServerStatus>(JsonSerializer.Serialize(snapshot, Json), Json);
            lock (stateLock) status = copy;
        }
        public void UpdateDiagnostics(object snapshot)
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot ?? new { }, Json);
            if (bytes.Length > 64 * 1024) throw new ArgumentException("Diagnostic snapshot exceeds its bound.");
            lock (stateLock) diagnostics = JsonSerializer.Deserialize<JsonElement>(bytes, Json);
        }
        public void RegisterGameLinkValidator(Func<ChatLink, bool> validator) => options.LinkValidator = validator;
        /// <summary>Register trusted catalog snapshots; callbacks execute on I/O continuations, never touch Unity objects.</summary>
        public void RegisterGameLink(string kind, Func<string, bool> validateId, Func<string, string> labelResolver)
        { lock (socialLock) social.Links.Register(kind, validateId, labelResolver); }
        public string IssueGameTicket(WebAccount account)
        {
            if (account == null) throw new ArgumentNullException(nameof(account));
            lock (ticketLock)
            {
                PruneTickets(); if (tickets.Count >= 8192) throw new WebAccountException("Game-ticket capacity reached.", 429);
                string value = LocalAccountProvider.RandomToken(); tickets.Add(LocalAccountProvider.HashTokenText(value), new GameTicket { Account = LocalAccountProvider.Copy(account), Expires = DateTime.UtcNow.AddSeconds(60) }); return "SJAT1:" + value;
            }
        }
        /// <summary>Compose this main-thread-safe, bounded validator with the game's existing admission rule.</summary>
        public bool ValidateGameConnection(IPEndPoint endpoint, byte[] connectionData)
        {
            string key = TicketKey(connectionData); if (key == null) return false;
            lock (ticketLock)
            {
                if (!tickets.TryGetValue(key, out var ticket) || ticket.Claimed || ticket.Expires <= DateTime.UtcNow) return false;
                ticket.Claimed = true; return true;
            }
        }
        /// <summary>Call after admission using NetworkServer.GetConnectionData(peerId); a ticket binds exactly once.</summary>
        public WebAccount BindAcceptedGameConnection(byte[] connectionData)
        {
            string key = TicketKey(connectionData); if (key == null) return null;
            lock (ticketLock)
            {
                if (!tickets.TryGetValue(key, out var ticket) || !ticket.Claimed || ticket.Expires <= DateTime.UtcNow) return null;
                tickets.Remove(key); return LocalAccountProvider.Copy(ticket.Account);
            }
        }
        private void PruneTickets() { var remove = tickets.Where(pair => pair.Value.Expires <= DateTime.UtcNow).Select(pair => pair.Key).ToArray(); foreach (var key in remove) tickets.Remove(key); }
        private static string TicketKey(byte[] data)
        {
            if (data == null || data.Length < 10 || data.Length > 128) return null;
            string text = Encoding.ASCII.GetString(data); return text.StartsWith("SJAT1:", StringComparison.Ordinal) ? LocalAccountProvider.HashTokenText(text.Substring(6)) : null;
        }
        private async Task<WebSocketHttpResponse> HandleHttpAsync(WebSocketHttpRequest request, CancellationToken token)
        {
            Interlocked.Increment(ref requests);
            if (!OriginAllowed(request)) return Response(new { ok = false, data = (object)null, error = "Origin is not allowed." }, 403, request);
            if (request.Method == "OPTIONS") return Response(new { ok = true, data = (object)null, error = (string)null }, 200, request);
            if (request.Method == "GET" && website.TryGetValue(request.Path, out var asset))
            {
                var response = new WebSocketHttpResponse { Body = asset.Bytes, ContentType = asset.Type };
                AddHeaders(response, request, false); return response;
            }
            if (!request.Path.StartsWith("/api/v1/", StringComparison.Ordinal)) return Response(new { ok = false, data = (object)null, error = "Not found." }, 404, request);
            string operation = request.Path.Substring(8);
            if (options.HubEndPoint != null && operation != "status") return await ProxyHttpAsync(request, operation, token).ConfigureAwait(false);
            try
            {
                RequireRate(request.RemoteEndPoint.Address.ToString(), operation);
                if (request.Method != "POST" && request.Method != "GET") throw new WebAccountException("Unsupported method.", 405);
                bool read = operation == "status" || operation == "chat/preset" || operation == "me" || operation == "communities" || operation == "groups" || operation == "diagnostics" || operation == "nodes";
                if (!read && request.Method != "POST") throw new WebAccountException("This operation requires POST.", 405);
                var data = request.Body.Length == 0 ? EmptyObject() : JsonSerializer.Deserialize<JsonElement>(request.Body, Json);
                if (data.ValueKind != JsonValueKind.Object) throw new WebAccountException("A JSON object is required.");
                string bearer = GetBearer(request); var user = accounts.Resume(bearer);
                object result = await ExecuteAsync(operation, data, user, bearer, CredentialTransportAllowed(request), IPAddress.IsLoopback(request.RemoteEndPoint.Address), token).ConfigureAwait(false);
                return Response(new { ok = true, data = result, error = (string)null }, 200, request);
            }
            catch (Exception error) { return Failure(error, request); }
        }
        private Task<bool> AuthorizeUpgradeAsync(WebSocketHttpRequest request, CancellationToken token)
        {
            bool allowed = (request.Path == "/ws/api" || HasAuthenticatedHandler(request.Path) || options.EnableRelay && request.Path == "/ws/relay") && string.IsNullOrEmpty(request.QueryString);
            try { if (allowed) RequireRate(request.RemoteEndPoint.Address.ToString(), "socket"); } catch (WebAccountException) { allowed = false; }
            return Task.FromResult(allowed);
        }
        private async Task HandleSocketAsync(WebSocketHttpRequest request, WebSocket socket, CancellationToken token)
        {
            if (request.Path == "/ws/relay")
            {
                if (options.HubEndPoint != null) await ProxySocketAsync(socket, token, request.Path).ConfigureAwait(false);
                else if (webSocketRelay != null) await webSocketRelay.HandleWebSocketAsync(request, socket, token).ConfigureAwait(false);
                return;
            }
            if (request.Path != "/ws/api") { await HandleAuthenticatedSocketAsync(request, socket, token).ConfigureAwait(false); return; }
            if (options.HubEndPoint != null) { await ProxySocketAsync(socket, token).ConfigureAwait(false); return; }
            var client = new ApiSocket { Socket = socket, Cancellation = CancellationTokenSource.CreateLinkedTokenSource(token), Address = request.RemoteEndPoint.Address.ToString() };
            client.Cancellation.CancelAfter(TimeSpan.FromSeconds(10)); sockets[client.Id] = client;
            var sending = SendLoopAsync(client); var buffer = new byte[4096];
            try
            {
                while (!client.Cancellation.IsCancellationRequested && socket.State == WebSocketState.Open)
                {
                    byte[] frame; using (var message = new MemoryStream())
                    {
                        WebSocketReceiveResult result;
                        do
                        {
                            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), client.Cancellation.Token).ConfigureAwait(false);
                            if (result.MessageType == WebSocketMessageType.Close) return;
                            if (result.MessageType != WebSocketMessageType.Text || message.Length + result.Count > options.MaxApiBytes) throw new WebAccountException("Invalid or oversized API frame.");
                            message.Write(buffer, 0, result.Count);
                        } while (!result.EndOfMessage);
                        frame = message.ToArray();
                    }
                    string id = "";
                    try
                    {
                        if (client.NodeId == null) RequireRate(client.Address, "socket-message"); var envelope = JsonSerializer.Deserialize<JsonElement>(frame, Json);
                        id = String(envelope, "id", 64, false); string operation = String(envelope, "op", 80); var data = envelope.TryGetProperty("data", out var payload) ? payload : EmptyObject();
                        if (operation == "login" || operation == "register" || operation == "bootstrap") RequireRate(client.Address, operation);
                        if (data.ValueKind != JsonValueKind.Object) throw new WebAccountException("A JSON object is required.");
                        if (operation == "nodes/resume")
                        {
                            BindNodeSocket(client, data, CredentialTransportAllowed(request));
                            Queue(client, new { id, ok = true, data = new { linked = true }, error = (string)null }); continue;
                        }
                        if (client.NodeId != null)
                        {
                            object nodeResult = ExecuteNodeSocket(client, operation, data);
                            Queue(client, new { id, ok = true, data = nodeResult, error = (string)null }); continue;
                        }
                        if (client.Token != null)
                        {
                            var refreshed = accounts.Resume(client.Token);
                            if (refreshed == null && client.User != null) Presence.Disconnect(client.User.Id);
                            client.User = refreshed;
                        }
                        object result = await ExecuteAsync(operation, data, client.User, client.Token, CredentialTransportAllowed(request), IPAddress.IsLoopback(request.RemoteEndPoint.Address), client.Cancellation.Token).ConfigureAwait(false);
                        if (operation == "login" || operation == "register" || operation == "bootstrap")
                        {
                            var session = (WebAccountSession)result; BindSocket(client, session.User, session.Token);
                        }
                        else if (operation == "resume")
                        {
                            string value = String(data, "token", 256); var user = accounts.Resume(value); if (user == null) throw new WebAccountException("Session is invalid or expired.", 401); BindSocket(client, user, value); result = new { user };
                        }
                        else if (operation == "logout") { if (client.User != null) Presence.Disconnect(client.User.Id); client.User = null; client.Token = null; client.Cancellation.CancelAfter(TimeSpan.FromSeconds(10)); }
                        Queue(client, new { id, ok = true, data = result, error = (string)null });
                    }
                    catch (Exception error) { Queue(client, new { id, ok = false, data = (object)null, error = PublicError(error) }); }
                }
            }
            catch (Exception error) when (error is OperationCanceledException || error is WebSocketException || error is IOException || error is WebAccountException || error is JsonException) { }
            finally
            {
                sockets.TryRemove(client.Id, out _); if (client.User != null) Presence.Disconnect(client.User.Id);
                RemoveNodeSocket(client);
                client.Cancellation.Cancel(); client.Available.Release();
                try { await sending.ConfigureAwait(false); } catch (OperationCanceledException) { } catch (WebSocketException) { } catch (IOException) { }
                try { if (socket.State == WebSocketState.Open) await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Disconnected", CancellationToken.None).ConfigureAwait(false); } catch { }
                client.Available.Dispose(); client.Cancellation.Dispose();
            }
        }
        private void BindSocket(ApiSocket client, WebAccount account, string token)
        {
            if (!Presence.TryConnect(account, options.MaxWebSocketsPerAccount + (client.User?.Id == account.Id ? 1 : 0))) throw new WebAccountException("Too many live connections for this account.", 429);
            if (client.User != null) Presence.Disconnect(client.User.Id);
            client.User = account; client.Token = token; client.Cancellation.CancelAfter(Timeout.InfiniteTimeSpan);
        }
        private async Task SendLoopAsync(ApiSocket client)
        {
            while (!client.Cancellation.IsCancellationRequested)
            {
                await client.Available.WaitAsync(client.Cancellation.Token).ConfigureAwait(false);
                while (client.Outbox.TryDequeue(out byte[] data))
                {
                    Interlocked.Decrement(ref client.Queued);
                    await client.Socket.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Text, true, client.Cancellation.Token).ConfigureAwait(false);
                }
            }
        }
        private void Queue(ApiSocket client, object envelope)
        {
            if (client.Cancellation.IsCancellationRequested) return;
            var data = JsonSerializer.SerializeToUtf8Bytes(envelope, Json);
            if (data.Length > 128 * 1024 || Interlocked.Increment(ref client.Queued) > options.MaxQueuedEventsPerSocket) { client.Cancellation.Cancel(); return; }
            client.Outbox.Enqueue(data); try { client.Available.Release(); } catch (ObjectDisposedException) { }
        }
        private void BroadcastSocialChanged()
        {
            foreach (var client in sockets.Values) if (client.User != null && accounts.Resume(client.Token) != null) Queue(client, new { @event = "social.changed", data = new { } });
        }
        private void BroadcastChat(ChatSendResult result)
        {
            var recipients = new HashSet<string>(result.RecipientIds, StringComparer.Ordinal);
            foreach (var client in sockets.Values)
                if (client.User != null && recipients.Contains(client.User.Id) && accounts.Resume(client.Token) != null)
                    Queue(client, new { @event = "chat.message", data = ProjectMessage(result.Message) });
            Interlocked.Increment(ref chatMessages); ChatAccepted?.Invoke(result.Message);
        }
        private async Task<object> ExecuteAsync(string operation, JsonElement data, WebAccount user, string bearer, bool secure, bool loopback, CancellationToken token)
        {
            if (operation == "status") return StatusSnapshot();
            if (operation == "chat/preset") return MmoChatPresets.Channels.Select(pair => new { id = ChannelName(pair.Key), name = pair.Value.Label, color = pair.Value.ColorHex, command = pair.Value.Command }).ToArray();
            if (operation == "nodes" || operation.StartsWith("nodes/", StringComparison.Ordinal)) return ExecuteNodeHttp(operation, data, user, secure);
            if (operation == "register" || operation == "login" || operation == "bootstrap")
            {
                if (!secure) throw new WebAccountException("Account credentials require HTTPS/WSS or loopback development mode.", 403);
                if (operation == "register") { if (!options.EnableRegistration) throw new WebAccountException("Account registration is disabled.", 403); return await accounts.RegisterAsync(String(data, "username", 64), String(data, "password", 128), String(data, "displayName", 64, false), token).ConfigureAwait(false); }
                if (operation == "login") return await accounts.SignInAsync(String(data, "username", 64), String(data, "password", 128), token).ConfigureAwait(false);
                if (!loopback || !(accounts is LocalAccountProvider local)) throw new WebAccountException("Operator setup is available only from this server's loopback address.", 403);
                return await local.BootstrapAsync(String(data, "code", 256), String(data, "username", 64), String(data, "password", 128), String(data, "displayName", 64, false), token).ConfigureAwait(false);
            }
            if (operation == "resume") { if (!secure) throw new WebAccountException("Account sessions require secure transport.", 403); var resumed = accounts.Resume(String(data, "token", 256)); if (resumed == null) throw new WebAccountException("Session is invalid or expired.", 401); return new { user = resumed }; }
            if (user == null) throw new WebAccountException("Sign in to continue.", 401); Presence.Touch(user);
            if (operation == "me") return user;
            if (operation == "logout")
            {
                accounts.SignOut(bearer);
                foreach (var client in sockets.Values) if (client.Token == bearer) client.Cancellation.Cancel();
                RevokeExternalToken(bearer);
                RevokeAccountRelays(user.Id);
                return new { signedOut = true };
            }
            if (operation == "game/ticket")
            {
                string nodeId = String(data, "serverId", 64, false);
                string ticket = nodeId.Length == 0 || nodeId == options.NodeId ? IssueGameTicket(user) : await IssueNodeGameTicketAsync(user, nodeId, token).ConfigureAwait(false);
                return new { connectionData = ticket, expiresInSeconds = 60 };
            }
            if (operation.StartsWith("relay/", StringComparison.Ordinal)) return ExecuteRelayOperation(operation, data, user);
            if (operation == "diagnostics")
            {
                RequireAdministrator(user); JsonElement snapshot; lock (stateLock) snapshot = diagnostics;
                return new { requests = Interlocked.Read(ref requests), rejectedRequests = Interlocked.Read(ref rejected), chatMessages = Interlocked.Read(ref chatMessages), webSocketClients = sockets.Count, uptimeSeconds = uptime.Elapsed.TotalSeconds, game = snapshot };
            }
            if (operation == "accounts/reset-password")
            {
                RequireAdministrator(user); var target = ResolveTarget(data); if (!(accounts is LocalAccountProvider local)) throw new WebAccountException("Password recovery is handled by the configured identity provider.");
                await local.ResetPasswordAsync(target.Id, String(data, "password", 128), token).ConfigureAwait(false); RevokeLiveAccount(target.Id); return new { reset = true };
            }
            lock (socialLock)
            {
                switch (operation)
                {
                    case "communities": return new { communities = social.GetCommunities(user.Id).Select(ProjectCommunity).ToArray(), invitations = social.GetInvites(user.Id).Where(invite => invite.Kind == SocialInviteKind.Community).Select(ProjectInvite).ToArray() };
                    case "communities/browse": return social.FindCommunities(user.Id, String(data, "search", 64, false), 50).Select(ProjectCommunity).ToArray();
                    case "communities/join": { var result = social.JoinCommunity(user.Id, String(data, "communityId", 64)); BroadcastSocialChanged(); return ProjectCommunity(result); }
                    case "groups": return new { groups = social.GetGroups(user.Id).Select(ProjectGroup).ToArray(), invitations = social.GetInvites(user.Id).Where(invite => invite.Kind == SocialInviteKind.Group).Select(ProjectInvite).ToArray() };
                    case "communities/create": { var result = social.CreateCommunity(user.Id, String(data, "name", 64), String(data, "description", 500, false), Boolean(data, "isOpen")); BroadcastSocialChanged(); return ProjectCommunity(result); }
                    case "communities/invite": { var result = social.InviteCommunity(user.Id, String(data, "communityId", 64), ResolveTarget(data).Id); BroadcastSocialChanged(); return ProjectInvite(result); }
                    case "groups/create": { string kind = String(data, "kind", 16, false); if (kind.Length == 0) kind = String(data, "type", 16, false); var result = social.CreateGroup(user.Id, kind.Equals("raid", StringComparison.OrdinalIgnoreCase) ? SocialGroupKind.Raid : SocialGroupKind.Party, String(data, "name", 64, false)); BroadcastSocialChanged(); return ProjectGroup(result); }
                    case "groups/invite": { var result = social.InviteGroup(user.Id, String(data, "groupId", 64), ResolveTarget(data).Id); BroadcastSocialChanged(); return ProjectInvite(result); }
                    case "communities/respond": case "groups/respond": { var result = social.RespondInvitation(user.Id, String(data, "invitationId", 64), Boolean(data, "accept")); BroadcastSocialChanged(); return ProjectInvite(result); }
                    case "communities/role": { if (!Enum.TryParse(String(data, "role", 32), true, out CommunityRole role)) throw new WebAccountException("Unknown Community role."); var result = social.ChangeCommunityRole(user.Id, String(data, "communityId", 64), ResolveTarget(data).Id, role); BroadcastSocialChanged(); return ProjectCommunity(result); }
                    case "communities/remove": social.RemoveCommunityMember(user.Id, String(data, "communityId", 64), ResolveTarget(data).Id); BroadcastSocialChanged(); return new { removed = true };
                    case "communities/leave": social.LeaveCommunity(user.Id, String(data, "communityId", 64)); BroadcastSocialChanged(); return new { left = true };
                    case "groups/remove": social.RemoveGroupMember(user.Id, String(data, "groupId", 64), ResolveTarget(data).Id); BroadcastSocialChanged(); return new { removed = true };
                    case "groups/leader": { var result = social.TransferGroupLeadership(user.Id, String(data, "groupId", 64), ResolveTarget(data).Id); BroadcastSocialChanged(); return ProjectGroup(result); }
                    case "groups/role": { var result = social.ChangeGroupRole(user.Id, String(data, "groupId", 64), ResolveTarget(data).Id, String(data, "role", 32)); BroadcastSocialChanged(); return ProjectGroup(result); }
                    case "groups/subgroup": { var result = social.SetRaidSubgroup(user.Id, String(data, "groupId", 64), ResolveTarget(data).Id, (int)Long(data, "subgroup", 0)); BroadcastSocialChanged(); return ProjectGroup(result); }
                    case "groups/leave": social.LeaveGroup(user.Id, String(data, "groupId", 64)); BroadcastSocialChanged(); return new { left = true };
                    case "chat/history":
                    {
                        ChatChannel channel = ParseChannel(String(data, "channel", 32, false)); string scope = String(data, "scopeId", 64, false);
                        long after = Long(data, "afterId", 0); int limit = (int)Math.Max(1, Math.Min(200, Long(data, "limit", 100)));
                        return social.ReadMessages(user.Id, after, 200).Where(message => message.Channel == channel && (scope.Length == 0 || message.ScopeId == scope)).Take(limit).Select(ProjectMessage).ToArray();
                    }
                    case "chat/send":
                    {
                        string text = NormalizeLinks(String(data, "text", 512)); var requestedChannel = ParseChannel(String(data, "channel", 32));
                        string target = String(data, "targetId", 64, false); string username = String(data, "targetUsername", 64, false); if (target.Length == 0 && username.Length != 0) target = ResolveTarget(data).Id;
                        var result = social.Send(user.Id, requestedChannel, text, String(data, "scopeId", 64, false), target);
                        if (!result.Success) throw new WebAccountException(result.Error ?? "Message was not accepted."); BroadcastChat(result); return ProjectMessage(result.Message);
                    }
                    case "chat/command":
                    {
                        var result = social.ExecuteSlash(user.Id, NormalizeLinks(String(data, "text", 512)), String(data, "scopeId", 64, false));
                        if (!result.Success) throw new WebAccountException(result.Error ?? "Command was not accepted.");
                        if (result.Chat != null) { BroadcastChat(result.Chat); return new { response = result.Response, message = ProjectMessage(result.Chat.Message) }; }
                        BroadcastSocialChanged(); return new { response = result.Response, message = (object)null };
                    }
                    case "chat/ignore": social.SetIgnored(user.Id, ResolveTarget(data).Id, !data.TryGetProperty("ignore", out var ignored) || ignored.ValueKind != JsonValueKind.False); return new { updated = true };
                    case "chat/report":
                    {
                        long messageId = Long(data, "messageId", 0); if (messageId == 0) throw new WebAccountException("Select a message to report.");
                        social.ReportMessage(user.Id, messageId, String(data, "reason", 240)); return new { reported = true };
                    }
                    case "chat/moderate":
                    {
                        RequireAdministrator(user); var targetAccount = ResolveTarget(data); string action = String(data, "action", 32);
                        if (targetAccount.Id == user.Id && action == "ban") throw new WebAccountException("Operators cannot ban their own active account.");
                        if (action == "ban" || action == "unban") { store.SetBanned(targetAccount.Id, action == "ban"); if (action == "ban") RevokeLiveAccount(targetAccount.Id); }
                        else if (action == "mute" || action == "unmute") social.SetChatMuted(user.Id, targetAccount.Id, action == "mute" ? DateTime.UtcNow.AddHours(1) : DateTime.UtcNow, String(data, "reason", 240, false));
                        else throw new WebAccountException("Unknown moderation action.");
                        BroadcastSocialChanged(); return new { updated = true };
                    }
                    default: throw new WebAccountException("Operation was not found.", 404);
                }
            }
        }
        private void RevokeLiveAccount(string accountId)
        {
            lock (ticketLock) { var keys = tickets.Where(pair => pair.Value.Account.Id == accountId).Select(pair => pair.Key).ToArray(); foreach (var key in keys) tickets.Remove(key); }
            Presence.Remove(accountId); foreach (var client in sockets.Values) if (client.User?.Id == accountId) client.Cancellation.Cancel(); AccountRevoked?.Invoke(accountId);
            RevokeExternalAccount(accountId);
            RevokeAccountRelays(accountId);
            BroadcastNodeRevocation(accountId);
        }
        private WebAccount ResolveTarget(JsonElement data)
        {
            string id = String(data, "targetId", 64, false); string username = String(data, "targetUsername", 64, false);
            var account = id.Length != 0 ? accounts.FindById(id) : accounts.FindByUsername(username);
            return account ?? throw new WebAccountException("Player account was not found.", 404);
        }
        private void ValidateLinks(string text)
        {
            var matches = System.Text.RegularExpressions.Regex.Matches(text, "\\[(item|player|location):([A-Za-z0-9_.:-]{1,128})\\|([^\\]\\r\\n]{1,100})\\]");
            foreach (System.Text.RegularExpressions.Match match in matches)
            {
                var link = new ChatLink { Kind = match.Groups[1].Value, Id = match.Groups[2].Value, Label = match.Groups[3].Value, Start = match.Index, Length = match.Length };
                bool valid = link.Kind == "player" ? accounts.FindById(link.Id) != null : options.LinkValidator != null && options.LinkValidator(link);
                if (!valid) throw new WebAccountException("A game link is invalid or unavailable.");
            }
        }
        private static string NormalizeLinks(string text)
            => System.Text.RegularExpressions.Regex.Replace(text, "\\[(item|player|location):([A-Za-z0-9_.-]{1,64})\\|([^\\]\\r\\n]{1,100})\\]", match => "[" + match.Groups[1].Value + ":" + match.Groups[2].Value + "]");
        private WebServerStatus StatusSnapshot()
        {
            lock (stateLock)
            {
                var copy = JsonSerializer.Deserialize<WebServerStatus>(JsonSerializer.Serialize(status, Json), Json); copy.WebSocketClients = sockets.Count; copy.UptimeSeconds = uptime.Elapsed.TotalSeconds; copy.IsSecure = options.Certificate != null; return copy;
            }
        }
        private void RequireRate(string address, string operation)
        {
            bool authentication = operation == "login" || operation == "register" || operation == "bootstrap"; string key = (authentication ? "auth:" : "api:") + address;
            lock (rateLock)
            {
                var now = DateTime.UtcNow;
                if (!rates.TryGetValue(key, out var entry))
                {
                    if (rates.Count >= 8192) { var old = rates.Where(pair => now - pair.Value.Window > TimeSpan.FromMinutes(1)).Select(pair => pair.Key).ToArray(); foreach (var item in old) rates.Remove(item); if (rates.Count >= 8192) throw new WebAccountException("Server request capacity reached.", 429); }
                    entry = new RateEntry { Window = now }; rates.Add(key, entry);
                }
                if (now - entry.Window >= TimeSpan.FromMinutes(1)) { entry.Window = now; entry.Count = 0; }
                if (++entry.Count > (authentication ? options.AuthenticationPerMinutePerAddress : options.RequestsPerMinutePerAddress)) throw new WebAccountException("Too many requests. Try again shortly.", 429);
            }
        }
        private bool CredentialTransportAllowed(WebSocketHttpRequest request) => options.Certificate != null || IPAddress.IsLoopback(request.RemoteEndPoint.Address);
        private bool OriginAllowed(WebSocketHttpRequest request)
        {
            if (!request.Headers.TryGetValue("Origin", out string origin)) return true;
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https") || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0) return false;
            string canonical = uri.GetLeftPart(UriPartial.Authority);
            if (options.AllowedOrigins.Length > 0) return options.AllowedOrigins.Contains(canonical, StringComparer.OrdinalIgnoreCase);
            return request.Headers.TryGetValue("Host", out string host) && string.Equals(canonical, (options.Certificate == null ? "http://" : "https://") + host, StringComparison.OrdinalIgnoreCase);
        }
        private WebSocketHttpResponse Response(object value, int code, WebSocketHttpRequest request)
        {
            var response = new WebSocketHttpResponse { StatusCode = code, ContentType = "application/json; charset=utf-8", Body = JsonSerializer.SerializeToUtf8Bytes(value, Json) }; AddHeaders(response, request, true); return response;
        }
        private void AddHeaders(WebSocketHttpResponse response, WebSocketHttpRequest request, bool api)
        {
            response.Headers["X-Content-Type-Options"] = "nosniff"; response.Headers["Referrer-Policy"] = "same-origin";
            response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
            response.Headers["Cache-Control"] = api ? "no-store" : "no-cache";
            if (request.Headers.TryGetValue("Origin", out string origin) && OriginAllowed(request))
            {
                response.Headers["Access-Control-Allow-Origin"] = origin; response.Headers["Vary"] = "Origin";
                response.Headers["Access-Control-Allow-Headers"] = "Authorization, Content-Type"; response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
            }
        }
        private WebSocketHttpResponse Failure(Exception error, WebSocketHttpRequest request)
        {
            Interlocked.Increment(ref rejected); int code = error is WebAccountException account ? account.StatusCode : error is SocialException ? 403 : error is JsonException || error is ArgumentException ? 400 : 500;
            if (code == 500) ServerError?.Invoke(error); return Response(new { ok = false, data = (object)null, error = PublicError(error) }, code, request);
        }
        private static string PublicError(Exception error) => error is WebAccountException || error is SocialException ? error.Message : error is JsonException || error is ArgumentException ? "Invalid request data." : "The server could not complete this request.";
        private static string GetBearer(WebSocketHttpRequest request) => request.Headers.TryGetValue("Authorization", out string value) && value.StartsWith("Bearer ", StringComparison.Ordinal) && value.Length <= 263 ? value.Substring(7) : null;
        private static bool IsAdministrator(WebAccount user) => user != null && (user.Role == "admin" || user.Role == "operator");
        private static void RequireAdministrator(WebAccount user) { if (!IsAdministrator(user)) throw new WebAccountException("Operator permission is required.", 403); }
        private static ChatAccount ToChatAccount(WebAccount user) => user == null ? null : new ChatAccount { AccountId = user.Id, DisplayName = user.DisplayName };
        private static JsonElement EmptyObject() => JsonSerializer.Deserialize<JsonElement>("{}");
        private static string String(JsonElement data, string name, int maximum, bool required = true)
        {
            if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty(name, out var value)) { if (required) throw new WebAccountException("Missing field: " + name); return ""; }
            if (value.ValueKind == JsonValueKind.Null && !required) return "";
            if (value.ValueKind != JsonValueKind.String) throw new WebAccountException("Invalid field: " + name);
            string text = value.GetString(); if (text.Length > maximum || required && text.Length == 0) throw new WebAccountException("Invalid field: " + name); return text;
        }
        private static bool Boolean(JsonElement data, string name) => data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
        private static long Long(JsonElement data, string name, long fallback)
        {
            if (!data.TryGetProperty(name, out var value)) return fallback;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number)) return number;
            if (value.ValueKind == JsonValueKind.String)
            {
                string text = value.GetString(); if (text.Length == 0) return fallback;
                if (long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed)) return parsed;
            }
            throw new WebAccountException("Invalid field: " + name);
        }
        private static ChatChannel ParseChannel(string name)
        {
            switch (name.ToLowerInvariant())
            {
                case "global": case "": return ChatChannel.Global; case "trade": return ChatChannel.Trade; case "zone": return ChatChannel.Zone;
                case "lfg": return ChatChannel.LookingForGroup; case "party": return ChatChannel.Party; case "raid": return ChatChannel.Raid;
                case "community": return ChatChannel.Community; case "officer": return ChatChannel.CommunityOfficers; case "proximity": case "say": return ChatChannel.Proximity;
                case "yell": return ChatChannel.Yell; case "whisper": return ChatChannel.Whisper; case "emote": return ChatChannel.Emote; case "system": return ChatChannel.System; case "combat": return ChatChannel.Combat;
                default: throw new WebAccountException("Unknown chat channel.");
            }
        }
        private static string ChannelName(ChatChannel channel)
        { return channel == ChatChannel.LookingForGroup ? "lfg" : channel == ChatChannel.CommunityOfficers ? "officer" : channel.ToString().ToLowerInvariant(); }
        private static object ProjectMessage(ChatMessage value) => new { id = value.Id.ToString(CultureInfo.InvariantCulture), timestampUtc = value.TimestampUtc, value.SenderId, value.SenderName, channel = ChannelName(value.Channel), value.ScopeId, value.TargetId, value.Text, value.Links };
        private static object ProjectCommunity(Community value) => new { value.Id, value.Name, value.Description, value.OwnerId, value.IsOpen, members = value.Members.Select(member => new { userId = member.AccountId, member.DisplayName, role = member.Role.ToString().ToLowerInvariant() }).ToArray() };
        private static object ProjectGroup(SocialGroup value) => new { value.Id, value.Name, value.Description, kind = value.Kind.ToString().ToLowerInvariant(), value.LeaderId, members = value.Members.Select(member => new { userId = member.AccountId, member.DisplayName, member.Role, member.Subgroup }).ToArray() };
        private static object ProjectInvite(SocialInvite value) => new { value.Id, value.Name, value.ScopeId, value.InviterId, value.TargetId, value.ExpiresUtc, kind = value.Kind.ToString().ToLowerInvariant() };
        private void LoadWebsite()
        {
            var assembly = typeof(SocketJackWebServer).Assembly;
            foreach (var asset in new[] { ("index.html", "text/html; charset=utf-8"), ("app.css", "text/css; charset=utf-8"), ("app.js", "application/javascript; charset=utf-8"), ("logo.png", "image/png") })
            {
                using (var stream = assembly.GetManifestResourceStream("SocketJack.Multiplayer.Web.Website." + asset.Item1))
                {
                    if (stream == null) throw new InvalidOperationException("The web module is missing its bundled website asset.");
                    using (var memory = new MemoryStream()) { stream.CopyTo(memory); var value = (memory.ToArray(), asset.Item2); website["/" + asset.Item1] = value; if (asset.Item1 == "index.html") website["/"] = value; }
                }
            }
        }
        private static void ValidateOptions(SocketJackWebOptions value)
        {
            if (value.ListenEndPoint == null || value.AllowedOrigins == null || string.IsNullOrWhiteSpace(value.ServerName) || value.ServerName.Length > 128 || value.MaxConnections < 1 || value.MaxConnections > 65535 || value.MaxWebSocketsPerAccount < 1 || value.MaxWebSocketsPerAccount > 32 || value.MaxQueuedEventsPerSocket < 1 || value.MaxQueuedEventsPerSocket > 1024 || value.MaxApiBytes < 1024 || value.MaxApiBytes > 65536 || value.RequestsPerMinutePerAddress < 1 || value.AuthenticationPerMinutePerAddress < 1) throw new ArgumentException("Invalid web-host configuration.");
            if (!IPAddress.IsLoopback(value.ListenEndPoint.Address) && value.Certificate == null) throw new ArgumentException("A network-accessible account website requires a TLS certificate. Use loopback for local development.");
        }
        private void ThrowIfDisposed() { if (disposed) throw new ObjectDisposedException(nameof(SocketJackWebServer)); }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            var stopped = StopAllAsync(); listener.Dispose();
            if (ownsAccounts && accounts is IDisposable disposable)
                _ = stopped.ContinueWith(_ => disposable.Dispose(), TaskScheduler.Default);
        }
        public async ValueTask DisposeAsync() { if (disposed) { await StopAllAsync().ConfigureAwait(false); return; } disposed = true; await StopAllAsync().ConfigureAwait(false); await listener.DisposeAsync().ConfigureAwait(false); if (ownsAccounts && accounts is IDisposable disposable) disposable.Dispose(); }
    }
}
