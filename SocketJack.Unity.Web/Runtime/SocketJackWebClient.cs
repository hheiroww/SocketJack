using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using SocketJack.Multiplayer.Chat;

namespace SocketJack.Multiplayer.Web
{
    public sealed class WebChatMessage
    {
        public string Id { get; set; }
        public DateTime TimestampUtc { get; set; }
        public string SenderId { get; set; }
        public string SenderName { get; set; }
        public string Channel { get; set; }
        public string ScopeId { get; set; }
        public string TargetId { get; set; }
        public string Text { get; set; }
        public List<ChatLink> Links { get; set; } = new List<ChatLink>();
        /// <summary>Local receiving session identity. Never accepted from JSON or transmitted.</summary>
        [JsonIgnore] public long DeliveryEpoch { get; internal set; }
    }
    /// <summary>Inject a browser/platform bridge without adding Unity references to the portable client.</summary>
    public interface IWebApiSocket : IDisposable
    {
        Task ConnectAsync(Uri endpoint, CancellationToken token);
        Task SendAsync(string json, CancellationToken token);
        Task<string> ReceiveAsync(CancellationToken token);
    }
    /// <summary>Portable account/chat client. Tokens are kept in memory, never added to endpoint URLs.</summary>
    public sealed class SocketJackWebClient : IDisposable
    {
        private sealed class Pending
        {
            internal readonly TaskCompletionSource<JsonElement> Completion;
            internal IWebApiSocket Owner;
            internal Pending(bool inline) { Completion = new TaskCompletionSource<JsonElement>(inline ? TaskCreationOptions.None : TaskCreationOptions.RunContinuationsAsynchronously); }
        }
        // SemaphoreSlim's queued async waiters force worker-pool continuations. WebGL has no worker
        // pool; its bridge completes on Unity's context. Keep cancellation and exclusivity without
        // imposing a scheduler on that single-threaded path.
        private sealed class AsyncGate
        {
            private readonly object sync = new object();
            private readonly Queue<TaskCompletionSource<bool>> waiting = new Queue<TaskCompletionSource<bool>>();
            private bool entered;
            internal Task WaitAsync(CancellationToken token, bool inline)
            {
                if (token.IsCancellationRequested) return Task.FromCanceled(token);
                TaskCompletionSource<bool> turn;
                lock (sync)
                {
                    if (!entered) { entered = true; return Task.CompletedTask; }
                    if (waiting.Count >= 256) throw new InvalidOperationException("API operation backlog reached its bound.");
                    turn = new TaskCompletionSource<bool>(inline ? TaskCreationOptions.None : TaskCreationOptions.RunContinuationsAsynchronously); waiting.Enqueue(turn);
                }
                return WaitTurnAsync(turn, token, inline);
            }
            private static async Task WaitTurnAsync(TaskCompletionSource<bool> turn, CancellationToken token, bool capture)
            { using (token.Register(() => turn.TrySetCanceled())) await turn.Task.ConfigureAwait(capture); }
            internal void Release()
            {
                while (true)
                {
                    TaskCompletionSource<bool> next = null;
                    lock (sync)
                    {
                        if (!entered) throw new InvalidOperationException("API gate was released without ownership.");
                        while (waiting.Count > 0) { var candidate = waiting.Dequeue(); if (!candidate.Task.IsCompleted) { next = candidate; break; } }
                        if (next == null) { entered = false; return; }
                    }
                    if (next.TrySetResult(true)) return;
                }
            }
        }
        private readonly Uri endpoint;
        private readonly HttpClient http;
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private readonly ConcurrentDictionary<string, Pending> pending = new ConcurrentDictionary<string, Pending>();
        private readonly AsyncGate sendLock = new AsyncGate();
        private readonly AsyncGate realtimeGate = new AsyncGate();
        private IWebApiSocket socket;
        private CancellationTokenSource socketLifetime;
        private long nextRequest;
        private long sessionEpoch;
        private bool disposed;
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, MaxDepth = 24 };
        /// <summary>Optional UnityWebRequest bridge: endpoint, HTTP method, JSON body, bearer token, cancellation.</summary>
        public Func<Uri, string, string, string, CancellationToken, Task<string>> HttpRequestOverride { get; set; }
        public Func<IWebApiSocket> SocketFactory { get; set; }
        /// <summary>Enable for browser bridges that run on Unity's synchronization context. Native clients
        /// leave this off so network parsing and serialization continue away from the render thread.</summary>
        public bool PreserveSynchronizationContext { get; set; }
        public WebAccount User { get; private set; }
        public string Token { get; private set; }
        public bool IsLive { get; private set; }
        public Uri EndPoint => endpoint;
        public long SessionEpoch => Volatile.Read(ref sessionEpoch);
        /// <summary>Callbacks are raised on I/O continuations. Unity adapters must marshal them to the main thread.</summary>
        public event Action<WebChatMessage> ChatMessageReceived;
        public event Action<string, JsonElement> EventReceived;
        public event Action<string> ConnectionChanged;
        public SocketJackWebClient(Uri endpoint)
        {
            if (endpoint == null || !endpoint.IsAbsoluteUri || (endpoint.Scheme != "https" && endpoint.Scheme != "http") || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
                throw new ArgumentException("Use an absolute HTTP(S) server URL without credentials or query strings.", nameof(endpoint));
            if (endpoint.Scheme == "http" && !(endpoint.IsLoopback || IPAddress.TryParse(endpoint.Host, out var ip) && IPAddress.IsLoopback(ip)))
                throw new ArgumentException("Remote accounts require HTTPS.", nameof(endpoint));
            this.endpoint = new Uri(endpoint.GetLeftPart(UriPartial.Authority) + "/"); http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
        }
        public Task<WebAccount> RegisterAsync(string username, string password, string displayName, CancellationToken token = default)
            => AuthenticateAsync("register", new { username, password, displayName }, token);
        public Task<WebAccount> LoginAsync(string username, string password, CancellationToken token = default)
            => AuthenticateAsync("login", new { username, password }, token);
        public Task<WebAccount> BootstrapAsync(string code, string username, string password, string displayName, CancellationToken token = default)
            => AuthenticateAsync("bootstrap", new { code, username, password, displayName }, token);
        private async Task<WebAccount> AuthenticateAsync(string operation, object data, CancellationToken token)
        {
            Interlocked.Increment(ref sessionEpoch); StopRealtime(); Token = null; User = null;
            var result = await RequestAsync(operation, data, token).ConfigureAwait(PreserveSynchronizationContext);
            var session = JsonSerializer.Deserialize<WebAccountSession>(result.GetRawText(), Json);
            if (session?.User == null || string.IsNullOrEmpty(session.Token)) throw new InvalidDataException("Server did not return an account session.");
            Token = session.Token; User = session.User; return User;
        }
        public async Task<WebAccount> ResumeAsync(string tokenValue, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(tokenValue) || tokenValue.Length > 256) throw new ArgumentException("Invalid session token.", nameof(tokenValue));
            Interlocked.Increment(ref sessionEpoch); StopRealtime(); Token = null; User = null;
            var result = await RequestAsync("resume", new { token = tokenValue }, token).ConfigureAwait(PreserveSynchronizationContext);
            if (!result.TryGetProperty("user", out var user)) throw new InvalidDataException("Server did not return an account.");
            User = JsonSerializer.Deserialize<WebAccount>(user.GetRawText(), Json); Token = tokenValue; return User;
        }
        public async Task LogoutAsync(CancellationToken token = default)
        {
            Interlocked.Increment(ref sessionEpoch);
            try { if (Token != null) await RequestAsync("logout", new { }, token).ConfigureAwait(PreserveSynchronizationContext); }
            finally { StopRealtime(); Token = null; User = null; }
        }
        public async Task<byte[]> GetGameConnectionDataAsync(CancellationToken token = default)
            => await GetGameConnectionDataAsync("", token).ConfigureAwait(PreserveSynchronizationContext);
        public async Task<byte[]> GetGameConnectionDataAsync(string serverId, CancellationToken token = default)
        {
            var result = await RequestAsync("game/ticket", new { serverId }, token).ConfigureAwait(PreserveSynchronizationContext);
            return Encoding.ASCII.GetBytes(result.GetProperty("connectionData").GetString());
        }
        public async Task<WebChatMessage> SendChatAsync(string channel, string text, string scopeId = "", string targetUsername = "", string targetId = "", CancellationToken token = default)
        {
            var result = await RequestAsync("chat/send", new { channel, text, scopeId, targetUsername, targetId }, token).ConfigureAwait(PreserveSynchronizationContext);
            return JsonSerializer.Deserialize<WebChatMessage>(result.GetRawText(), Json);
        }
        public async Task<IReadOnlyList<WebChatMessage>> GetHistoryAsync(string channel, string scopeId = "", int limit = 100, string afterId = "", CancellationToken token = default)
        {
            var result = await RequestAsync("chat/history", new { channel, scopeId, limit, afterId }, token).ConfigureAwait(PreserveSynchronizationContext);
            return JsonSerializer.Deserialize<List<WebChatMessage>>(result.GetRawText(), Json) ?? new List<WebChatMessage>();
        }
        public async Task<JsonElement> RequestAsync(string operation, object data = null, CancellationToken token = default)
        {
            ThrowIfDisposed(); ValidateOperation(operation);
            var uri = new Uri(endpoint, "api/v1/" + operation); string method = data == null ? "GET" : "POST"; string text = data == null ? null : JsonSerializer.Serialize(data, Json);
            string responseText;
            if (HttpRequestOverride != null)
            {
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token))
                { linked.CancelAfter(TimeSpan.FromSeconds(15)); responseText = await HttpRequestOverride(uri, method, text, Token, linked.Token).ConfigureAwait(PreserveSynchronizationContext); }
            }
            else
            {
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token))
                using (var request = new HttpRequestMessage(new HttpMethod(method), uri))
                {
                    if (Token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
                    if (text != null) request.Content = new StringContent(text, Encoding.UTF8, "application/json");
                    using (var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(PreserveSynchronizationContext))
                    using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(PreserveSynchronizationContext))
                    using (var memory = new MemoryStream())
                    {
                        var bytes = new byte[8192]; int read;
                        while ((read = await stream.ReadAsync(bytes, 0, bytes.Length, linked.Token).ConfigureAwait(PreserveSynchronizationContext)) > 0) { if (memory.Length + read > 2 * 1024 * 1024) throw new InvalidDataException("API response exceeded its bound."); memory.Write(bytes, 0, read); }
                        responseText = Encoding.UTF8.GetString(memory.ToArray());
                    }
                }
            }
            if (responseText == null || responseText.Length > 2 * 1024 * 1024) throw new InvalidDataException("API response exceeded its bound.");
            return Unwrap(JsonSerializer.Deserialize<JsonElement>(responseText, Json));
        }
        public async Task StartRealtimeAsync(CancellationToken token = default)
        {
            await realtimeGate.WaitAsync(token, PreserveSynchronizationContext).ConfigureAwait(PreserveSynchronizationContext);
            try
            {
                ThrowIfDisposed(); if (User == null || string.IsNullOrEmpty(Token)) throw new InvalidOperationException("Sign in before starting realtime chat."); StopRealtime();
                long currentEpoch = SessionEpoch; string currentToken = Token;
                socketLifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, token); var current = socketLifetime;
                socket = SocketFactory != null ? SocketFactory() : new NativeApiSocket(); var currentSocket = socket;
                if (currentSocket == null) throw new InvalidOperationException("Socket factory returned null.");
                try
                {
                    var socketUri = new UriBuilder(endpoint) { Scheme = endpoint.Scheme == "https" ? "wss" : "ws", Path = "/ws/api", Query = "" }.Uri;
                    await currentSocket.ConnectAsync(socketUri, current.Token).ConfigureAwait(PreserveSynchronizationContext);
                    if (!ReferenceEquals(currentSocket, socket) || currentEpoch != SessionEpoch) throw new OperationCanceledException();
                    _ = ReceiveLoopAsync(currentSocket, current.Token, currentEpoch);
                    await LiveRequestAsync("resume", new { token = currentToken }, current.Token).ConfigureAwait(PreserveSynchronizationContext);
                    if (!ReferenceEquals(currentSocket, socket) || currentEpoch != SessionEpoch) throw new OperationCanceledException();
                    IsLive = true; ConnectionChanged?.Invoke("Live");
                }
                catch { if (ReferenceEquals(currentSocket, socket)) StopRealtime(); throw; }
            }
            finally { realtimeGate.Release(); }
        }
        public async Task<JsonElement> LiveRequestAsync(string operation, object data, CancellationToken token = default)
        {
            ValidateOperation(operation); if (socket == null) throw new InvalidOperationException("Realtime connection is not running.");
            if (pending.Count >= 64) throw new InvalidOperationException("Too many pending API requests.");
            string id = Interlocked.Increment(ref nextRequest).ToString(CultureInfo.InvariantCulture); var item = new Pending(PreserveSynchronizationContext) { Owner = socket }; pending[id] = item;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(12));
                using (timeout.Token.Register(() => item.Completion.TrySetCanceled()))
                {
                    try
                    {
                        string frame = JsonSerializer.Serialize(new { id, op = operation, data }, Json);
                        await sendLock.WaitAsync(timeout.Token, PreserveSynchronizationContext).ConfigureAwait(PreserveSynchronizationContext);
                        try { await item.Owner.SendAsync(frame, timeout.Token).ConfigureAwait(PreserveSynchronizationContext); } finally { sendLock.Release(); }
                        return await item.Completion.Task.ConfigureAwait(PreserveSynchronizationContext);
                    }
                    finally { pending.TryRemove(id, out _); }
                }
            }
        }
        private async Task ReceiveLoopAsync(IWebApiSocket current, CancellationToken token, long receivingEpoch)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    string text = await current.ReceiveAsync(token).ConfigureAwait(PreserveSynchronizationContext); if (text == null) break;
                    if (text.Length > 128 * 1024) throw new InvalidDataException("API event exceeded its bound."); var envelope = JsonSerializer.Deserialize<JsonElement>(text, Json);
                    if (envelope.TryGetProperty("id", out var id) && pending.TryRemove(id.GetString(), out var item)) { try { item.Completion.TrySetResult(Unwrap(envelope)); } catch (Exception error) { item.Completion.TrySetException(error); } }
                    else if (envelope.TryGetProperty("event", out var eventName) && envelope.TryGetProperty("data", out var payload))
                    {
                        if (!ReferenceEquals(current, socket) || receivingEpoch != SessionEpoch) continue;
                        string name = eventName.GetString();
                        if (name == "chat.message") { var message = JsonSerializer.Deserialize<WebChatMessage>(payload.GetRawText(), Json); if (message != null) { message.DeliveryEpoch = receivingEpoch; ChatMessageReceived?.Invoke(message); } }
                        EventReceived?.Invoke(name, payload.Clone());
                    }
                }
            }
            catch (Exception error) when (error is WebSocketException || error is IOException || error is OperationCanceledException || error is ObjectDisposedException) { }
            finally
            {
                if (ReferenceEquals(current, socket)) { IsLive = false; ConnectionChanged?.Invoke("Disconnected"); }
                foreach (var request in pending.Where(pair => ReferenceEquals(pair.Value.Owner, current)).ToArray())
                    if (pending.TryRemove(request.Key, out var item)) item.Completion.TrySetException(new IOException("Realtime connection closed."));
            }
        }
        public void StopRealtime()
        {
            IsLive = false; socketLifetime?.Cancel(); socketLifetime?.Dispose(); socketLifetime = null;
            socket?.Dispose(); socket = null;
            foreach (var item in pending.Values) item.Completion.TrySetCanceled(); pending.Clear();
        }
        private static JsonElement Unwrap(JsonElement response)
        {
            if (!response.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
                throw new WebAccountException(response.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String ? error.GetString() : "The server rejected this request.");
            return response.TryGetProperty("data", out var data) ? data.Clone() : JsonSerializer.Deserialize<JsonElement>("null");
        }
        private static void ValidateOperation(string operation)
        {
            if (string.IsNullOrEmpty(operation) || operation.Length > 80 || operation.StartsWith("/", StringComparison.Ordinal) || operation.Contains("..")) throw new ArgumentException("Invalid API operation.");
            foreach (char value in operation) if (!(value >= 'a' && value <= 'z' || value >= '0' && value <= '9' || value == '/' || value == '-')) throw new ArgumentException("Invalid API operation.");
        }
        private void ThrowIfDisposed() { if (disposed) throw new ObjectDisposedException(nameof(SocketJackWebClient)); }
        public void Dispose() { if (disposed) return; disposed = true; lifetime.Cancel(); StopRealtime(); http.Dispose(); lifetime.Dispose(); }

        private sealed class NativeApiSocket : IWebApiSocket
        {
            private readonly ClientWebSocket socket = new ClientWebSocket();
            public Task ConnectAsync(Uri endpoint, CancellationToken token) => socket.ConnectAsync(endpoint, token);
            public Task SendAsync(string json, CancellationToken token) => socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)), WebSocketMessageType.Text, true, token);
            public async Task<string> ReceiveAsync(CancellationToken token)
            {
                var bytes = new byte[4096]; using (var memory = new MemoryStream())
                {
                    WebSocketReceiveResult result;
                    do { result = await socket.ReceiveAsync(new ArraySegment<byte>(bytes), token).ConfigureAwait(false); if (result.MessageType == WebSocketMessageType.Close) return null; if (result.MessageType != WebSocketMessageType.Text || memory.Length + result.Count > 128 * 1024) throw new InvalidDataException("Invalid API event."); memory.Write(bytes, 0, result.Count); } while (!result.EndOfMessage);
                    return Encoding.UTF8.GetString(memory.ToArray());
                }
            }
            public void Dispose() => socket.Dispose();
        }
    }
}
