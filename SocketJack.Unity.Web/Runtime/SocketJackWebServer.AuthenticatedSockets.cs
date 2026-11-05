using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
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
        private sealed class AuthenticatedSocket
        {
            internal string AccountId, Token;
            internal CancellationTokenSource Cancellation;
        }
        private readonly object handlersLock = new object();
        private readonly Dictionary<string, Func<WebAccount, WebSocket, CancellationToken, Task>> authenticatedHandlers = new Dictionary<string, Func<WebAccount, WebSocket, CancellationToken, Task>>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<Guid, AuthenticatedSocket> authenticatedSockets = new ConcurrentDictionary<Guid, AuthenticatedSocket>();
        /// <summary>Additional bounded WebSocket role, e.g. voice. First text frame is {"token":"..."}; all later frames belong to the handler.
        /// Authentication is repeated periodically and logout/ban/recovery immediately revokes its socket.</summary>
        public void RegisterAuthenticatedWebSocketHandler(string path, Func<WebAccount, WebSocket, CancellationToken, Task> handler)
        {
            if (handler == null || path == null || !System.Text.RegularExpressions.Regex.IsMatch(path, "^/ws/[a-z][a-z0-9-]{0,31}$") || path == "/ws/api" || path == "/ws/relay") throw new ArgumentException("Use a dedicated bounded /ws/name path.");
            lock (handlersLock) { if (authenticatedHandlers.Count >= 8 || authenticatedHandlers.ContainsKey(path)) throw new InvalidOperationException("Duplicate handler or custom socket capacity reached."); authenticatedHandlers[path] = handler; }
        }
        public WebAccount AuthorizeSession(string token) => accounts.Resume(token);
        public string[] ResolveVoiceAudience(string accountId, ChatChannel channel, string scopeId = null, string targetId = null)
        { lock (socialLock) return social.ResolveAudience(accountId, channel, scopeId, targetId).ToArray(); }
        /// <summary>Resolve native game-voice permissions through the canonical hub on an enrolled leaf.
        /// Call from a bounded background worker; failures and the three-second deadline deny delivery.</summary>
        public Task<string[]> ResolveVoiceAudienceAsync(string accountId, ChatChannel channel, string scopeId = null, string targetId = null, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            return leaf == null ? Task.FromResult(ResolveVoiceAudience(accountId, channel, scopeId, targetId)) : leaf.ResolveVoiceAudienceAsync(accountId, channel, scopeId, targetId, token);
        }
        private bool HasAuthenticatedHandler(string path) { lock (handlersLock) return authenticatedHandlers.ContainsKey(path); }
        private async Task HandleAuthenticatedSocketAsync(WebSocketHttpRequest request, WebSocket socket, CancellationToken token)
        {
            Func<WebAccount, WebSocket, CancellationToken, Task> handler; lock (handlersLock) authenticatedHandlers.TryGetValue(request.Path, out handler);
            if (handler == null) return;
            if (options.HubEndPoint != null) { await ProxySocketAsync(socket, token, request.Path).ConfigureAwait(false); return; }
            if (!CredentialTransportAllowed(request)) return;
            WebAccount user = null; Guid id = Guid.NewGuid();
            using (var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                lifetime.CancelAfter(TimeSpan.FromSeconds(10));
                using (lifetime.Token.Register(() => { if (lifetime.IsCancellationRequested) socket.Abort(); }))
                {
                    try
                    {
                        byte[] bytes = new byte[2048]; using (var memory = new MemoryStream())
                        {
                            WebSocketReceiveResult result;
                            do { result = await socket.ReceiveAsync(new ArraySegment<byte>(bytes), lifetime.Token).ConfigureAwait(false); if (result.MessageType != WebSocketMessageType.Text || memory.Length + result.Count > bytes.Length) return; memory.Write(bytes, 0, result.Count); } while (!result.EndOfMessage);
                            var authentication = JsonSerializer.Deserialize<JsonElement>(memory.ToArray(), Json); string bearer = String(authentication, "token", 256);
                            var authorized = accounts.Resume(bearer); if (authorized == null || !Presence.TryConnect(authorized, options.MaxWebSocketsPerAccount)) return;
                            user = authorized;
                            lifetime.CancelAfter(Timeout.InfiniteTimeSpan);
                            authenticatedSockets[id] = new AuthenticatedSocket { AccountId = user.Id, Token = bearer, Cancellation = lifetime };
                            var watchdog = WatchSessionAsync(bearer, lifetime);
                            try { await handler(user, socket, lifetime.Token).ConfigureAwait(false); }
                            finally { lifetime.Cancel(); try { await watchdog.ConfigureAwait(false); } catch (OperationCanceledException) { } }
                        }
                    }
                    catch (Exception error) when (error is OperationCanceledException || error is WebSocketException || error is IOException || error is JsonException || error is WebAccountException) { }
                    finally { authenticatedSockets.TryRemove(id, out _); if (user != null) Presence.Disconnect(user.Id); }
                }
            }
        }
        private async Task WatchSessionAsync(string token, CancellationTokenSource lifetime)
        {
            while (!lifetime.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), lifetime.Token).ConfigureAwait(false);
                if (accounts.Resume(token) == null) { lifetime.Cancel(); return; }
            }
        }
        private void RevokeExternalToken(string token) { foreach (var client in authenticatedSockets.Values) if (client.Token == token) client.Cancellation.Cancel(); }
        private void RevokeExternalAccount(string accountId) { foreach (var client in authenticatedSockets.Values) if (client.AccountId == accountId) client.Cancellation.Cancel(); }
    }
}
