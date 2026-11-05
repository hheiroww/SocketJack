using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SocketJack.Multiplayer.Transport
{
    public sealed class WebSocketListenerOptions
    {
        public int MaxConnections { get; set; } = 256;
        public int MaxHeaderBytes { get; set; } = 8192;
        public int MaxBodyBytes { get; set; } = 65536;
        public int MaxResponseBytes { get; set; } = 1024 * 1024;
        public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(10);
        public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);
        public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(20);
        /// <summary>Caller-owned server certificate. Without TLS, use loopback or a trusted TLS terminator; never send Internet passwords over HTTP.</summary>
        public X509Certificate2 Certificate { get; set; }
        /// <summary>Empty means same origin only, with native clients lacking Origin also allowed. Wildcards are rejected.</summary>
        public string[] AllowedOrigins { get; set; } = Array.Empty<string>();
        internal WebSocketListenerOptions Snapshot()
        {
            if (MaxConnections < 1 || MaxConnections > 65535 || MaxHeaderBytes < 256 || MaxHeaderBytes > 65536 ||
                MaxBodyBytes < 0 || MaxBodyBytes > 1024 * 1024 || MaxResponseBytes < 0 || MaxResponseBytes > 4 * 1024 * 1024 ||
                HandshakeTimeout <= TimeSpan.Zero || HandshakeTimeout > TimeSpan.FromMinutes(2) || RequestTimeout <= TimeSpan.Zero || RequestTimeout > TimeSpan.FromMinutes(2) ||
                KeepAliveInterval <= TimeSpan.Zero || KeepAliveInterval > TimeSpan.FromMinutes(5) || AllowedOrigins == null || AllowedOrigins.Length > 128)
                throw new ArgumentOutOfRangeException(nameof(WebSocketListenerOptions));
            var copy = (WebSocketListenerOptions)MemberwiseClone();
            copy.AllowedOrigins = AllowedOrigins.Select(Origin).ToArray(); return copy;
        }
        internal static string Origin(string value)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https") ||
                uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0 || value.IndexOf('*') >= 0)
                throw new ArgumentException("Origins must be explicit HTTP(S) origins without paths, credentials or wildcards.");
            return uri.GetLeftPart(UriPartial.Authority);
        }
    }

    public sealed class WebSocketHttpRequest
    {
        private readonly byte[] body;
        public string Method { get; }
        public string RawTarget { get; }
        public string Path { get; }
        public string QueryString { get; }
        public IReadOnlyDictionary<string, string> Headers { get; }
        public byte[] Body => (byte[])body.Clone();
        public IPEndPoint RemoteEndPoint { get; }
        public bool IsWebSocketRequest { get; }
        internal WebSocketHttpRequest(string method, string target, Dictionary<string, string> headers, byte[] body, IPEndPoint endpoint, bool upgrade)
        {
            Method = method; RawTarget = target; var uri = new Uri("http://localhost" + target, UriKind.Absolute);
            Path = uri.AbsolutePath; QueryString = uri.Query; Headers = new ReadOnlyDictionary<string, string>(headers);
            this.body = body; RemoteEndPoint = endpoint; IsWebSocketRequest = upgrade;
        }
    }
    public sealed class WebSocketHttpResponse
    {
        public int StatusCode { get; set; } = 200;
        public string ContentType { get; set; } = "application/octet-stream";
        public IDictionary<string, string> Headers { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public byte[] Body { get; set; } = Array.Empty<byte>();
        public static WebSocketHttpResponse Text(string text, string contentType = "text/plain; charset=utf-8", int statusCode = 200)
            => new WebSocketHttpResponse { StatusCode = statusCode, ContentType = contentType, Body = Encoding.UTF8.GetBytes(text ?? "") };
    }

    /// <summary>Optional bounded native HTTP/1.1 + RFC6455 listener. HTTP requests use Connection: close;
    /// GET, POST and OPTIONS with an explicit bounded Content-Length are supported, without chunking,
    /// pipelining or HTTP/2. WebSocket framing is handled by System.Net.WebSockets, not a custom parser.
    /// Handlers run on asynchronous I/O continuations, never Unity's game thread: marshal game operations.
    /// HTTP authentication does not authorize upgrades; configure UpgradeAuthorizer separately.</summary>
    public sealed class WebSocketListener : IDisposable, IAsyncDisposable
    {
        private sealed class Connection
        {
            internal TcpClient Client;
            internal readonly TaskCompletionSource<bool> Completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        private readonly object sync = new object();
        private readonly WebSocketListenerOptions options;
        private readonly HashSet<Connection> connections = new HashSet<Connection>();
        private TcpListener listener;
        private CancellationTokenSource lifetime;
        private Task accepting = Task.CompletedTask, stopping;
        private bool disposed;
        public bool IsRunning { get { lock (sync) return listener != null; } }
        public IPEndPoint LocalEndPoint { get; private set; }
        public int ConnectionCount { get { lock (sync) return connections.Count; } }
        public Func<WebSocketHttpRequest, CancellationToken, Task<WebSocketHttpResponse>> HttpHandler { get; set; }
        public Func<WebSocketHttpRequest, CancellationToken, Task<bool>> UpgradeAuthorizer { get; set; }
        public Func<WebSocketHttpRequest, WebSocket, CancellationToken, Task> WebSocketAccepted { get; set; }
        public event Action<Exception> HandlerError;
        public WebSocketListener(WebSocketListenerOptions options = null) { this.options = (options ?? new WebSocketListenerOptions()).Snapshot(); }
        public void Start(IPEndPoint endpoint)
        {
            if (endpoint == null) throw new ArgumentNullException(nameof(endpoint));
            lock (sync)
            {
                if (disposed) throw new ObjectDisposedException(nameof(WebSocketListener));
                if (listener != null || (stopping != null && !stopping.IsCompleted)) throw new InvalidOperationException("Listener is running or still stopping.");
                var created = new TcpListener(endpoint); created.Start(Math.Min(options.MaxConnections, 512));
                lifetime?.Dispose(); lifetime = new CancellationTokenSource(); listener = created; stopping = null;
                LocalEndPoint = (IPEndPoint)created.LocalEndpoint;
                accepting = AcceptAsync(created, lifetime.Token);
            }
        }
        private async Task AcceptAsync(TcpListener current, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await current.AcceptTcpClientAsync().ConfigureAwait(false); }
                catch (Exception error) when (error is SocketException || error is ObjectDisposedException) { break; }
                client.NoDelay = true; var connection = new Connection { Client = client };
                lock (sync)
                {
                    if (token.IsCancellationRequested || connections.Count >= options.MaxConnections) { client.Dispose(); continue; }
                    connections.Add(connection);
                }
                _ = ProcessAsync(connection, token);
            }
        }
        private async Task ProcessAsync(Connection connection, CancellationToken token)
        {
            Stream stream = null; bool upgraded = false;
            try
            {
                stream = connection.Client.GetStream();
                using (var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    requestTimeout.CancelAfter(options.HandshakeTimeout);
                    // Closing the actual socket makes the deadline effective on Unity runtimes whose stream ignores cancellation.
                    using (requestTimeout.Token.Register(() => { try { connection.Client.Dispose(); } catch { } }))
                    {
                        if (options.Certificate != null)
                        {
                            var tls = new SslStream(stream, false); stream = tls;
                            await tls.AuthenticateAsServerAsync(options.Certificate, false, SslProtocols.Tls12, false).ConfigureAwait(false);
                        }
                        var request = await ReadRequestAsync(stream, (IPEndPoint)connection.Client.Client.RemoteEndPoint, requestTimeout.Token).ConfigureAwait(false);
                        if (!request.IsWebSocketRequest)
                        {
                            requestTimeout.CancelAfter(options.RequestTimeout);
                            var response = HttpHandler == null ? WebSocketHttpResponse.Text("Not found", statusCode: 404) : await HttpHandler(request, requestTimeout.Token).ConfigureAwait(false);
                            await WriteResponseAsync(stream, response, requestTimeout.Token).ConfigureAwait(false); return;
                        }
                        if (!OriginAllowed(request) || WebSocketAccepted == null || (UpgradeAuthorizer != null && !await UpgradeAuthorizer(request, requestTimeout.Token).ConfigureAwait(false)))
                        { await WriteResponseAsync(stream, WebSocketHttpResponse.Text("Forbidden", statusCode: 403), requestTimeout.Token).ConfigureAwait(false); return; }
                        var accepted = WebSocketAccepted;
                        string key = request.Headers["Sec-WebSocket-Key"];
                        string accept;
                        using (var sha = SHA1.Create()) accept = Convert.ToBase64String(sha.ComputeHash(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                        byte[] reply = Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n");
                        await stream.WriteAsync(reply, 0, reply.Length, requestTimeout.Token).ConfigureAwait(false);
                        requestTimeout.CancelAfter(Timeout.InfiniteTimeSpan); upgraded = true;
                        using (var socket = WebSocket.CreateFromStream(stream, true, null, options.KeepAliveInterval))
                        {
                            // This is the listener lifetime token, not the temporary HTTP deadline.
                            await accepted(request, socket, token).ConfigureAwait(false);
                        }
                    }
                }
            }
            catch (AuthenticationException error) { try { HandlerError?.Invoke(error); } catch { } }
            catch (Exception error) when (error is IOException || error is SocketException || error is ObjectDisposedException || error is OperationCanceledException || error is WebSocketException) { }
            catch (Exception error)
            {
                if (!upgraded && stream != null && !token.IsCancellationRequested)
                    try { await WriteResponseAsync(stream, WebSocketHttpResponse.Text("Bad request", statusCode: error is InvalidDataException ? 400 : 500), token).ConfigureAwait(false); } catch { }
                if (!(error is InvalidDataException)) try { HandlerError?.Invoke(error); } catch { }
            }
            finally
            {
                try { stream?.Dispose(); } catch { } connection.Client.Dispose();
                lock (sync) connections.Remove(connection); connection.Completion.TrySetResult(true);
            }
        }
        private async Task<WebSocketHttpRequest> ReadRequestAsync(Stream stream, IPEndPoint endpoint, CancellationToken token)
        {
            byte[] buffer = new byte[1024]; byte[] all; int headerEnd = -1;
            using (var collected = new MemoryStream())
            {
                while (headerEnd < 0)
                {
                    int read = await stream.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false);
                    if (read == 0) throw new InvalidDataException("Incomplete HTTP request.");
                    collected.Write(buffer, 0, read); all = collected.ToArray();
                    for (int i = Math.Max(0, all.Length - read - 3); i + 3 < all.Length; i++)
                        if (all[i] == 13 && all[i + 1] == 10 && all[i + 2] == 13 && all[i + 3] == 10) { headerEnd = i + 4; break; }
                    if ((headerEnd < 0 && all.Length >= options.MaxHeaderBytes) || headerEnd > options.MaxHeaderBytes) throw new InvalidDataException("HTTP headers exceed their limit.");
                }
                all = collected.ToArray();
            }
            for (int i = 0; i < headerEnd; i++)
            {
                byte value = all[i];
                if (value == 13) { if (i + 1 >= headerEnd || all[++i] != 10) throw new InvalidDataException("HTTP requires CRLF."); }
                else if (value < 32 || value > 126) throw new InvalidDataException("Invalid HTTP header character.");
            }
            var lines = Encoding.ASCII.GetString(all, 0, headerEnd - 4).Split(new[] { "\r\n" }, StringSplitOptions.None);
            var first = lines[0].Split(' ');
            if (first.Length != 3 || (first[0] != "GET" && first[0] != "POST" && first[0] != "OPTIONS") || first[2] != "HTTP/1.1" || !ValidTarget(first[1])) throw new InvalidDataException("Unsupported HTTP request line.");
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon <= 0 || !Token(lines[i].Substring(0, colon))) throw new InvalidDataException("Invalid HTTP header name.");
                string name = lines[i].Substring(0, colon); string value = lines[i].Substring(colon + 1).Trim(' ');
                if (headers.ContainsKey(name)) throw new InvalidDataException("Duplicate HTTP headers are not accepted.");
                headers.Add(name, value);
            }
            if (!headers.TryGetValue("Host", out string host) || !ValidHost(host) || headers.ContainsKey("Transfer-Encoding") || headers.ContainsKey("Expect")) throw new InvalidDataException("Invalid HTTP host, transfer encoding or expectation.");
            int bodyLength = 0;
            if (headers.TryGetValue("Content-Length", out string contentLength) &&
                (!int.TryParse(contentLength, NumberStyles.None, CultureInfo.InvariantCulture, out bodyLength) || bodyLength < 0 || bodyLength > options.MaxBodyBytes)) throw new InvalidDataException("Invalid HTTP body length.");
            bool anyUpgrade = headers.ContainsKey("Upgrade") || headers.ContainsKey("Sec-WebSocket-Key") || headers.ContainsKey("Sec-WebSocket-Version");
            if (anyUpgrade)
            {
                if (first[0] != "GET" || bodyLength != 0 || !headers.TryGetValue("Upgrade", out string upgrade) || !upgrade.Equals("websocket", StringComparison.OrdinalIgnoreCase) ||
                    !headers.TryGetValue("Connection", out string connection) || !connection.Split(',').Any(x => x.Trim().Equals("Upgrade", StringComparison.OrdinalIgnoreCase)) ||
                    !headers.TryGetValue("Sec-WebSocket-Version", out string version) || version != "13" || !headers.TryGetValue("Sec-WebSocket-Key", out string key)) throw new InvalidDataException("Invalid WebSocket upgrade.");
                try { if (Convert.FromBase64String(key).Length != 16) throw new InvalidDataException("Invalid WebSocket key."); }
                catch (FormatException error) { throw new InvalidDataException("Invalid WebSocket key.", error); }
            }
            int prefix = all.Length - headerEnd;
            if (prefix > bodyLength) throw new InvalidDataException("HTTP pipelining or unexpected body data is not accepted.");
            byte[] body = new byte[bodyLength]; if (prefix > 0) Buffer.BlockCopy(all, headerEnd, body, 0, prefix);
            for (int offset = prefix; offset < body.Length;)
            { int read = await stream.ReadAsync(body, offset, body.Length - offset, token).ConfigureAwait(false); if (read == 0) throw new InvalidDataException("Truncated HTTP body."); offset += read; }
            return new WebSocketHttpRequest(first[0], first[1], headers, body, endpoint, anyUpgrade);
        }
        private bool OriginAllowed(WebSocketHttpRequest request)
        {
            if (!request.Headers.TryGetValue("Origin", out string origin)) return true;
            string canonical; try { canonical = WebSocketListenerOptions.Origin(origin); } catch (ArgumentException) { return false; }
            if (options.AllowedOrigins.Length > 0) return options.AllowedOrigins.Contains(canonical, StringComparer.OrdinalIgnoreCase);
            return string.Equals(canonical, WebSocketListenerOptions.Origin((options.Certificate == null ? "http://" : "https://") + request.Headers["Host"]), StringComparison.OrdinalIgnoreCase);
        }
        private async Task WriteResponseAsync(Stream stream, WebSocketHttpResponse response, CancellationToken token)
        {
            if (response == null || response.StatusCode < 200 || response.StatusCode > 599 || response.Body == null || response.Body.Length > options.MaxResponseBytes ||
                !HeaderValue(response.ContentType) || response.Headers == null || response.Headers.Count > 64) throw new InvalidDataException("Invalid bounded HTTP response.");
            var text = new StringBuilder("HTTP/1.1 ").Append(response.StatusCode).Append(" ").Append(Reason(response.StatusCode)).Append("\r\nContent-Type: ").Append(response.ContentType)
                .Append("\r\nContent-Length: ").Append(response.Body.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\nConnection: close\r\n");
            foreach (var header in response.Headers)
            {
                if (!Token(header.Key) || !HeaderValue(header.Value) || header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) || header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) || header.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Invalid HTTP response header.");
                text.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
            }
            text.Append("\r\n"); if (text.Length > options.MaxHeaderBytes) throw new InvalidDataException("Response headers exceed their limit.");
            byte[] head = Encoding.ASCII.GetBytes(text.ToString()); await stream.WriteAsync(head, 0, head.Length, token).ConfigureAwait(false);
            if (response.Body.Length > 0) await stream.WriteAsync(response.Body, 0, response.Body.Length, token).ConfigureAwait(false);
        }
        private static bool ValidTarget(string target)
        {
            if (target.Length == 0 || target.Length > 4096 || target[0] != '/' || target.IndexOf('#') >= 0 || target.IndexOf('\\') >= 0 ||
                target.IndexOf("%0d", StringComparison.OrdinalIgnoreCase) >= 0 || target.IndexOf("%0a", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            for (int i = 0; i < target.Length; i++)
            {
                if (target[i] <= 32 || target[i] >= 127) return false;
                if (target[i] == '%' && (i + 2 >= target.Length || !Uri.IsHexDigit(target[++i]) || !Uri.IsHexDigit(target[++i]))) return false;
            }
            return Uri.TryCreate("http://localhost" + target, UriKind.Absolute, out var uri) && uri.Host == "localhost";
        }
        private static bool ValidHost(string host) => !string.IsNullOrEmpty(host) && host.IndexOfAny(new[] { '/', '\\', '@', '?', '#', ',', ' ' }) < 0 && Uri.TryCreate("http://" + host + "/", UriKind.Absolute, out var uri) && uri.Host.Length > 0 && uri.UserInfo.Length == 0;
        private static bool HeaderValue(string value) => value != null && value.Length <= 8192 && value.All(c => c >= 32 && c <= 126);
        private static bool Token(string value) => !string.IsNullOrEmpty(value) && value.All(c => c >= '0' && c <= '9' || c >= 'A' && c <= 'Z' || c >= 'a' && c <= 'z' || "!#$%&'*+-.^_`|~".IndexOf(c) >= 0);
        private static string Reason(int status) => status == 200 ? "OK" : status == 204 ? "No Content" : status == 400 ? "Bad Request" : status == 401 ? "Unauthorized" : status == 403 ? "Forbidden" : status == 404 ? "Not Found" : status == 429 ? "Too Many Requests" : status == 503 ? "Service Unavailable" : "Response";
        public Task StopAsync()
        {
            lock (sync)
            {
                if (stopping != null) return stopping;
                if (listener == null) return Task.CompletedTask;
                lifetime.Cancel(); listener.Stop(); listener = null;
                var active = connections.ToArray(); foreach (var item in active) item.Client.Dispose();
                return stopping = Task.WhenAll(new[] { accepting }.Concat(active.Select(x => x.Completion.Task)));
            }
        }
        public void Dispose() { lock (sync) { if (disposed) return; disposed = true; } _ = StopAsync(); }
        public async ValueTask DisposeAsync() { Dispose(); await StopAsync().ConfigureAwait(false); }
    }
}
