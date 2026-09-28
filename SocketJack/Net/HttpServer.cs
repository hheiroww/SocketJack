
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Globalization;
using System.Net.Mime;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SocketJack.Serialization;

namespace SocketJack.Net {

    /// <summary>
    /// Read-only description of a route registered through <see cref="HttpServer.Map"/>.
    /// </summary>
    public sealed class HttpMappedRoute {
        /// <summary>The HTTP method registered for the route.</summary>
        public string Method { get; private set; }

        /// <summary>The URL path registered for the route.</summary>
        public string Path { get; private set; }

        /// <summary>The declaring type for the runtime handler delegate, when available.</summary>
        public string HandlerType { get; private set; }

        /// <summary>The runtime handler method name, when available.</summary>
        public string HandlerName { get; private set; }

        /// <summary>A best-effort runtime signature for the handler delegate.</summary>
        public string HandlerSignature { get; private set; }

        /// <summary>Comma-separated API input variables inferred from route and typed body metadata.</summary>
        public string InputVariables { get; private set; }

        /// <summary>JSON metadata describing route, query, body, and handler parameters.</summary>
        public string ParametersSchema { get; private set; }

        /// <summary>JSON schema/example inferred for typed request bodies.</summary>
        public string RequestBodySchema { get; private set; }

        /// <summary>JSON schema/example inferred for the handler return type.</summary>
        public string ResponseSchema { get; private set; }

        /// <summary>Best-effort request body mode inferred from the route registration.</summary>
        public string RequestBodyKind { get; private set; }

        public HttpMappedRoute(string method, string path) {
            Method = method;
            Path = path;
        }

        internal HttpMappedRoute(string method, string path, string handlerType, string handlerName, string handlerSignature, string inputVariables, string parametersSchema, string requestBodySchema, string responseSchema, string requestBodyKind) {
            Method = method;
            Path = path;
            HandlerType = handlerType;
            HandlerName = handlerName;
            HandlerSignature = handlerSignature;
            InputVariables = inputVariables;
            ParametersSchema = parametersSchema;
            RequestBodySchema = requestBodySchema;
            ResponseSchema = responseSchema;
            RequestBodyKind = requestBodyKind;
        }
    }

    public sealed class HttpDispatchResult {
        public HttpContext Context { get; internal set; }
        public HttpRequest Request => Context?.Request;
        public HttpResponse Response => Context?.Response;
        public int StatusCode => Context?.StatusCodeNumber ?? 500;
        public string ReasonPhrase => Context?.ReasonPhrase ?? "Internal Server Error";
        public bool Streamed { get; internal set; }
        public byte[] BodyBytes { get; internal set; } = Array.Empty<byte>();
    }

    public sealed class HttpDocumentRootResponse {
        public byte[] Data { get; set; }
        public string Text { get; set; }
        public string FileName { get; set; }
        public string ContentType { get; set; }
        public DateTime? LastModifiedUtc { get; set; }
        public Dictionary<string, string> Headers { get; set; }
    }

    public delegate bool HttpDocumentRootResolver(HttpRequest request, string relativePath, out HttpDocumentRootResponse response);
    public sealed class HttpStreamDispatchCallbacks {
        public Action<HttpResponse> OnStart { get; set; }
        public Action<byte[], int, int> OnChunk { get; set; }
        public Action OnEnd { get; set; }
    }
    public partial class HttpServer : TcpServer {

        // Internal cache for processed HTML/string with variable replacement
        private readonly ConcurrentDictionary<string, (string html, DateTime lastWrite)> _routeFileCache = new();
        private readonly ConcurrentDictionary<string, string> _routeStringCache = new();
        private readonly object _httpRoutePatternCacheLock = new object();
        private readonly Dictionary<string, HttpRoutePatternCacheEntry> _httpRoutePatternCache = new Dictionary<string, HttpRoutePatternCacheEntry>(StringComparer.OrdinalIgnoreCase);
        private long _httpRoutePatternCacheBytes;
        private long _httpRoutePatternCacheSequence;

        private sealed class HttpRoutePatternCacheEntry {
            public string Key;
            public string Hash;
            public byte[] Body;
            public int Size;
            public int StableCount;
            public long LastUsed;
        }
        /// <summary>
        /// Maps a route to an HTML file with variable replacement (e.g., $Username) and caching.
        /// </summary>
        public void RouteFile(string method, string path, string filePath, string[][] vars = null, int cacheSeconds = 60) {
            if (string.IsNullOrWhiteSpace(method)) throw new ArgumentException("Method is required.", nameof(method));
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Path is required.", nameof(path));
            if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("File path is required.", nameof(filePath));

            Map(method, path, (conn, req, ct) =>
            {
                string cacheKey = filePath + "|" + (vars != null ? GetVarsCacheKey(vars) : "");
                string html;
                DateTime lastWrite = DateTime.MinValue;
                if (cacheSeconds > 0) {
                    if (_routeFileCache.TryGetValue(cacheKey, out var cached)) {
                        try { lastWrite = File.GetLastWriteTimeUtc(filePath); } catch { }
                        if (cached.lastWrite == lastWrite)
                            return cached.html;
                    }
                }
                try {
                    html = File.ReadAllText(filePath);
                    lastWrite = File.GetLastWriteTimeUtc(filePath);
                } catch (Exception ex) {
                    return $"<html><body><h2>File not found</h2><pre>{System.Net.WebUtility.HtmlEncode(ex.Message)}</pre></body></html>";
                }
                html = ReplaceVars(html, vars);
                if (cacheSeconds > 0)
                    _routeFileCache[cacheKey] = (html, lastWrite);
                return html;
            });
        }

        /// <summary>
        /// Maps a route to a string (e.g., HTML template) with variable replacement and caching.
        /// </summary>
        public void RouteString(string method, string path, string template, string[][] vars = null, string cacheKey = null, int cacheSeconds = 60) {
            if (string.IsNullOrWhiteSpace(method)) throw new ArgumentException("Method is required.", nameof(method));
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Path is required.", nameof(path));
            if (template == null) throw new ArgumentNullException(nameof(template));

            Map(method, path, (conn, req, ct) =>
            {
                string key = cacheKey ?? (template.GetHashCode() + "|" + (vars != null ? GetVarsCacheKey(vars) : ""));
                if (cacheSeconds > 0 && _routeStringCache.TryGetValue(key, out var cached))
                    return cached;
                var result = ReplaceVars(template, vars);
                if (cacheSeconds > 0)
                    _routeStringCache[key] = result;
                return result;
            });
        }

        /// <summary>
        /// Replaces variables in the form $VarName in the input string with values from vars.
        /// Accepts string[][] where each element is a [key, value] pair.
        /// </summary>
        private static string ReplaceVars(string input, string[][] vars) {
            if (input == null || vars == null) return input;
            foreach (var pair in vars) {
                if (pair == null || pair.Length < 2) continue;
                string key = "$" + pair[0];
                string val = pair[1] ?? string.Empty;
                input = input.Replace(key, val);
            }
            return input;
        }

        /// <summary>
        /// Generates a cache key for a variable array (string[][]).
        /// </summary>
        private static string GetVarsCacheKey(string[][] vars) {
            if (vars == null) return string.Empty;
            // Sort by key for stable cache key
            return string.Join("|", vars
                .Where(pair => pair != null && pair.Length >= 2)
                .OrderBy(pair => pair[0])
                .Select(pair => pair[0] + "=" + (pair[1] ?? "")));
        }

        //new private event PeerConnectionRequestEventHandler PeerConnectionRequest;

        public event RequestHandler OnHttpRequest;
        public delegate void RequestHandler(NetworkConnection Connection, ref HttpContext context, CancellationToken cancellationToken);

        public delegate object RouteHandler(NetworkConnection connection, HttpRequest request, CancellationToken cancellationToken);

        /// <summary>
        /// Typed route handler delegate. The request body is automatically deserialized to <typeparamref name="T"/>
        /// and passed as the <paramref name="body"/> parameter.
        /// </summary>
        public delegate object RouteHandler<T>(NetworkConnection connection, T body, HttpRequest request, CancellationToken cancellationToken);

        /// <summary>
        /// Delegate for streaming routes. The handler receives a <see cref="ChunkedStream"/>
        /// that sends each <see cref="ChunkedStream.WriteLine"/> as an HTTP chunk, keeping
        /// the connection open until the handler returns or calls <see cref="ChunkedStream.Finish"/>.
        /// </summary>
        public delegate Task StreamRouteHandler(NetworkConnection connection, HttpRequest request, ChunkedStream chunkedStream, CancellationToken cancellationToken);

        /// <summary>
        /// Delegate for upload-streaming routes. The handler receives an <see cref="UploadStream"/>
        /// from which it can read incoming data chunks (e.g., OBS video) as they arrive on the
        /// connection. The handler runs on a background task and should return when it is done
        /// consuming data or when <see cref="UploadStream.ReadAsync"/> returns <see langword="null"/>.
        /// </summary>
        public delegate void UploadStreamRouteHandler(NetworkConnection connection, HttpRequest request, UploadStream uploadStream, CancellationToken cancellationToken);

        /// <summary>
        /// Delegate for RTMP publish routes. The handler receives an <see cref="UploadStream"/>
        /// that yields media chunks as OBS (or another RTMP encoder) publishes. Each chunk is
        /// prefixed with a single byte indicating the RTMP message type (8 = audio, 9 = video,
        /// 18 = metadata) followed by the raw payload.
        /// </summary>
        public delegate Task RtmpPublishHandler(NetworkConnection connection, string app, string streamKey, UploadStream uploadStream, CancellationToken cancellationToken);

        private readonly object _routesLock = new object();
        private readonly Dictionary<string, Dictionary<string, RouteHandler>> _routes = new Dictionary<string, Dictionary<string, RouteHandler>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Dictionary<string, HttpMappedRoute>> _routeMetadata = new Dictionary<string, Dictionary<string, HttpMappedRoute>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Dictionary<string, StreamRouteHandler>> _streamRoutes = new Dictionary<string, Dictionary<string, StreamRouteHandler>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Dictionary<string, UploadStreamRouteHandler>> _uploadStreamRoutes = new Dictionary<string, Dictionary<string, UploadStreamRouteHandler>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Tracks connections that currently have an active <see cref="MapStream"/> handler running.
        /// Subsequent data on these connections is raw payload (e.g., OBS video) and must not be
        /// parsed as HTTP. The data still flows through <see cref="NetworkBase.OnReceive"/> for
        /// other subscribers to capture.
        /// </summary>
        private readonly ConcurrentDictionary<Guid, byte> _activeStreamConnections = new ConcurrentDictionary<Guid, byte>();

        /// <summary>
        /// Tracks connections that have an active <see cref="MapUploadStream"/> handler running.
        /// Subsequent raw data on these connections is forwarded to the corresponding
        /// <see cref="UploadStream"/> instead of being parsed as HTTP.
        /// </summary>
        private readonly ConcurrentDictionary<Guid, UploadStream> _activeUploadConnections = new ConcurrentDictionary<Guid, UploadStream>();

        private readonly Dictionary<string, string> _directoryMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _fileMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, HttpDocumentRootResolver> _documentRootMappings = new Dictionary<string, HttpDocumentRootResolver>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Dictionary<string, Dictionary<string, RouteHandler>>> _hostRoutes = new Dictionary<string, Dictionary<string, Dictionary<string, RouteHandler>>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Dictionary<string, string>> _hostDirectoryMappings = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Dictionary<string, string>> _hostFileMappings = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, HostProxyTarget> _hostProxyMappings = new Dictionary<string, HostProxyTarget>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _allowedHostNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private enum RouteMatchMode {
            ExactAndWildcard,
            ExactOnly,
            WildcardOnly
        }

        private sealed class HostProxyTarget {
            public Uri BaseUri { get; }
            public string Host { get; }
            public int Port { get; }
            public bool UseHttps { get; }
            public string BasePath { get; }
            public bool PreserveHostHeader { get; }

            public HostProxyTarget(Uri baseUri, bool preserveHostHeader = false) {
                BaseUri = baseUri ?? throw new ArgumentNullException(nameof(baseUri));
                Host = baseUri.Host;
                Port = baseUri.Port > 0 ? baseUri.Port : (baseUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? 443 : 80);
                UseHttps = baseUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase);
                BasePath = string.IsNullOrWhiteSpace(baseUri.AbsolutePath) || baseUri.AbsolutePath == "/" ? "" : baseUri.AbsolutePath.TrimEnd('/');
                PreserveHostHeader = preserveHostHeader;
            }
        }
        /// <summary>
        /// Per-connection buffer for accumulating partial HTTP requests
        /// that span multiple TCP segments.
        /// </summary>
        private readonly ConcurrentDictionary<Guid, List<byte>> _requestBuffers = new ConcurrentDictionary<Guid, List<byte>>();
        private readonly object _httpAccessLogLock = new object();

        private readonly Dictionary<string, RtmpPublishHandler> _rtmpPublishRoutes = new Dictionary<string, RtmpPublishHandler>(StringComparer.OrdinalIgnoreCase);
        /// <summary>
        /// Restricts HTTP routing to the supplied host name. Leave empty to allow all hosts.
        /// </summary>
        public void AllowHost(string hostName) {
            hostName = NormalizeHostName(hostName);
            if (string.IsNullOrWhiteSpace(hostName))
                throw new ArgumentException("Host name is required.", nameof(hostName));

            lock (_routesLock) {
                _allowedHostNames.Add(hostName);
            }
        }

        /// <summary>
        /// Clears HTTP host restrictions and restores the default allow-all behavior.
        /// </summary>
        public void ClearAllowedHosts() {
            lock (_routesLock) {
                _allowedHostNames.Clear();
            }
        }

        public bool IsHostAllowed(HttpRequest request) {
            return IsHostAllowed(GetRequestHostName(request));
        }

        public bool IsHostAllowed(string hostName) {
            hostName = NormalizeHostName(hostName);
            lock (_routesLock) {
                return _allowedHostNames.Count == 0 || (!string.IsNullOrWhiteSpace(hostName) && _allowedHostNames.Contains(hostName));
            }
        }

        /// <summary>
        /// Tracks connections running the RTMP protocol (e.g., OBS publishing via <c>rtmp://</c>).
        /// Subsequent data on these connections is fed into the <see cref="RtmpSession"/> state
        /// machine instead of being parsed as HTTP.
        /// </summary>
        private readonly ConcurrentDictionary<Guid, RtmpSession> _activeRtmpConnections = new ConcurrentDictionary<Guid, RtmpSession>();

        /// <summary>
        /// Running total of bytes sent across all HTTP responses (including headers).
        /// This counter persists across connection lifetimes, unlike per-connection
        /// counters which are lost when short-lived HTTP connections close.
        /// </summary>
        public long HttpTotalBytesSent => Interlocked.Read(ref _httpTotalBytesSent);
        private long _httpTotalBytesSent;

        /// <summary>
        /// Running total of bytes received across all HTTP requests.
        /// </summary>
        public long HttpTotalBytesReceived => Interlocked.Read(ref _httpTotalBytesReceived);
        private long _httpTotalBytesReceived;

        /// <summary>
        /// Bytes sent via HTTP responses since the last call to <see cref="SnapshotHttpBytesSent"/>.
        /// Use this to compute an HTTP bytes-per-second rate from the WPF metrics timer.
        /// </summary>
        private long _httpBytesSentSnapshot;

        /// <summary>
        /// Bytes received via HTTP requests since the last call to <see cref="SnapshotHttpBytesReceived"/>.
        /// </summary>
        private long _httpBytesReceivedSnapshot;

        /// <summary>
        /// Returns the number of HTTP bytes sent since the last snapshot and resets the delta.
        /// Call once per second to get a bytes-per-second rate.
        /// </summary>
        public long SnapshotHttpBytesSent() {
            var current = Interlocked.Read(ref _httpTotalBytesSent);
            var delta = current - _httpBytesSentSnapshot;
            _httpBytesSentSnapshot = current;
            return delta;
        }

        /// <summary>
        /// Returns the number of HTTP bytes received since the last snapshot and resets the delta.
        /// Call once per second to get a bytes-per-second rate.
        /// </summary>
        public long SnapshotHttpBytesReceived() {
            var current = Interlocked.Read(ref _httpTotalBytesReceived);
            var delta = current - _httpBytesReceivedSnapshot;
            _httpBytesReceivedSnapshot = current;
            return delta;
        }

        public string IndexPageHtml { get; set; } = LoadDefaultIndexPageHtml();

        public string DownloadPageHtml { get; set; } = null;

        private static string LoadDefaultIndexPageHtml() {
            const string fallback = "<html><body><h1>SocketJack HttpServer</h1></body></html>";
            try {
                if (SocketJack.HtmlPageResources.TryGetHtml("SocketJackHttpServerDefaultIndex.html", out string embedded))
                    return embedded;

                var baseDirectory = AppContext.BaseDirectory;
                var candidates = new[] {
                    Path.Combine(baseDirectory, "html", "SocketJackHttpServerDefaultIndex.html"),
                    Path.Combine(baseDirectory, "SocketJackHttpServerDefaultIndex.html"),
                    Path.Combine(Path.GetDirectoryName(typeof(HttpServer).Assembly.Location) ?? baseDirectory, "html", "SocketJackHttpServerDefaultIndex.html"),
                    Path.Combine(Path.GetDirectoryName(typeof(HttpServer).Assembly.Location) ?? baseDirectory, "SocketJackHttpServerDefaultIndex.html"),
                    Path.Combine(Directory.GetCurrentDirectory(), "html", "SocketJackHttpServerDefaultIndex.html"),
                    Path.Combine(Directory.GetCurrentDirectory(), "SocketJackHttpServerDefaultIndex.html")
                };

                foreach (var candidate in candidates) {
                    if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                        return File.ReadAllText(candidate, Encoding.UTF8);
                }
            } catch {
            }

            return fallback;
        }

        /// <summary>
        /// Gets or sets the content served at <c>/robots.txt</c>. Set to <see langword="null"/> to
        /// disable the built-in robots.txt route. Defaults to a permissive policy.
        /// </summary>
        public string Robots { get; set; } = "User-agent: *\nAllow: /\n";

        /// <summary>
        /// When <see langword="true"/>, mapped directories that contain no
        /// <c>index.html</c> / <c>index.htm</c> will return an auto-generated
        /// HTML listing of their contents. Defaults to <see langword="false"/>.
        /// A <c>.htaccess</c> file inside the directory can override this
        /// per-directory with <c>Options +Indexes</c> or <c>Options -Indexes</c>.
        /// </summary>
        public bool AllowDirectoryListing { get; set; } = false;

        /// <summary>
        /// Optional request gate. Return a non-empty message to reject the request before
        /// routes, static files, streaming handlers, or upload handlers run.
        /// </summary>
        public Func<NetworkConnection, HttpRequest, string> RequestGate { get; set; }

        /// <summary>
        /// Shared per-IP bot filter and endpoint abuse monitor for this server.
        /// </summary>
        public EndpointSecurityMonitor EndpointSecurity { get; } = new EndpointSecurityMonitor();
        /// <summary>
        /// Gets or sets the <c>Cache-Control</c> header value added to every HTTP response.
        /// When <see langword="null"/> (the default), no <c>Cache-Control</c> header is emitted.
        /// Example values: <c>"public, max-age=3600"</c>, <c>"no-cache"</c>,
        /// <c>"private, max-age=86400"</c>.
        /// For static files served via <c>MapDirectory</c>, an <c>ETag</c> and
        /// <c>Last-Modified</c> header are also sent, and <c>If-None-Match</c> /
        /// <c>If-Modified-Since</c> conditional requests are honoured with <c>304 Not Modified</c>.
        /// </summary>
        public string CacheControl { get; set; } = null;

        /// <summary>
        /// Gets or sets whether a non-SSL server bound to port 80 should redirect requests to HTTPS.
        /// </summary>
        public bool EnablePort80HttpsRedirect { get; set; } = false;

        public void Map(string method, string path, RouteHandler handler) {
            if (string.IsNullOrWhiteSpace(method))
                throw new ArgumentException("Method is required.", nameof(method));
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Path is required.", nameof(path));
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            lock (_routesLock) {
                if (!_routes.TryGetValue(method, out var byPath)) {
                    byPath = new Dictionary<string, RouteHandler>(StringComparer.OrdinalIgnoreCase);
                    _routes[method] = byPath;
                }
                byPath[path] = handler;
                SetMappedRouteMetadataLocked(method, path, CreateMappedRouteMetadata(method, path, handler, null));
            }
        }

        /// <summary>
        /// Maps a route whose handler receives a deserialized body of type <typeparamref name="T"/>.
        /// The request body is deserialized using the configured serializer before the handler is invoked.
        /// </summary>
        public void Map<T>(string method, string path, RouteHandler<T> handler) {
            if (string.IsNullOrWhiteSpace(method))
                throw new ArgumentException("Method is required.", nameof(method));
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Path is required.", nameof(path));
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            Map(method, path, (connection, request, cancellationToken) => {
                T body = default;
                if (request.BodyBytes != null && request.BodyBytes.Length > 0) {
                    var wrapped = Options.Serializer.Deserialize(request.BodyBytes);
                    if (wrapped != null) {
                        var obj = wrapped.Unwrap(this as ISocket);
                        if (obj is T typed)
                            body = typed;
                    }
                }
                return handler(connection, body, request, cancellationToken);
            });
            SetMappedRouteMetadata(method, path, handler, typeof(T));
        }

        /// <summary>
        /// Maps a route that only matches requests whose Host header matches <paramref name="hostName"/>.
        /// Exact host names are supported, and wildcard entries such as <c>*.example.com</c> match subdomains.
        /// </summary>
        public void MapHost(string hostName, string method, string path, RouteHandler handler) {
            hostName = NormalizeHostName(hostName);
            if (string.IsNullOrWhiteSpace(hostName))
                throw new ArgumentException("Host name is required.", nameof(hostName));
            if (string.IsNullOrWhiteSpace(method))
                throw new ArgumentException("Method is required.", nameof(method));
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Path is required.", nameof(path));
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            lock (_routesLock) {
                if (!_hostRoutes.TryGetValue(hostName, out var byMethod)) {
                    byMethod = new Dictionary<string, Dictionary<string, RouteHandler>>(StringComparer.OrdinalIgnoreCase);
                    _hostRoutes[hostName] = byMethod;
                }
                if (!byMethod.TryGetValue(method, out var byPath)) {
                    byPath = new Dictionary<string, RouteHandler>(StringComparer.OrdinalIgnoreCase);
                    byMethod[method] = byPath;
                }
                byPath[path] = handler;
            }
        }

        /// <summary>
        /// Maps a host-specific route whose handler receives a deserialized request body.
        /// </summary>
        public void MapHost<T>(string hostName, string method, string path, RouteHandler<T> handler) {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            MapHost(hostName, method, path, (connection, request, cancellationToken) => {
                T body = default;
                if (request.BodyBytes != null && request.BodyBytes.Length > 0) {
                    var wrapped = Options.Serializer.Deserialize(request.BodyBytes);
                    if (wrapped != null) {
                        var obj = wrapped.Unwrap(this as ISocket);
                        if (obj is T typed)
                            body = typed;
                    }
                }
                return handler(connection, body, request, cancellationToken);
            });
        }

        /// <summary>
        /// Removes a previously mapped host-specific route.
        /// </summary>
        public bool RemoveHostRoute(string hostName, string method, string path) {
            hostName = NormalizeHostName(hostName);
            lock (_routesLock) {
                if (!_hostRoutes.TryGetValue(hostName, out var byMethod))
                    return false;
                if (!byMethod.TryGetValue(method, out var byPath))
                    return false;
                var removed = byPath.Remove(path);
                if (byPath.Count == 0)
                    byMethod.Remove(method);
                if (byMethod.Count == 0)
                    _hostRoutes.Remove(hostName);
                return removed;
            }
        }
        public bool RemoveRoute(string method, string path) {
            lock (_routesLock) {
                if (!_routes.TryGetValue(method, out var byPath))
                    return false;
                var removed = byPath.Remove(path);
                if (removed && _routeMetadata.TryGetValue(method, out var metaByPath))
                    metaByPath.Remove(path);
                return removed;
            }
        }

        internal void SetMappedRouteMetadata(string method, string path, Delegate handler, Type requestBodyType) {
            if (string.IsNullOrWhiteSpace(method) || string.IsNullOrWhiteSpace(path))
                return;

            lock (_routesLock) {
                SetMappedRouteMetadataLocked(method, path, CreateMappedRouteMetadata(method, path, handler, requestBodyType));
            }
        }

        private void SetMappedRouteMetadataLocked(string method, string path, HttpMappedRoute metadata) {
            if (!_routeMetadata.TryGetValue(method, out var byPath)) {
                byPath = new Dictionary<string, HttpMappedRoute>(StringComparer.OrdinalIgnoreCase);
                _routeMetadata[method] = byPath;
            }
            byPath[path] = metadata;
        }

        /// <summary>
        /// Gets a snapshot of routes registered through <see cref="Map(string, string, RouteHandler)"/>.
        /// </summary>
        public IReadOnlyList<HttpMappedRoute> MappedRoutes => GetMappedRoutes();

        /// <summary>
        /// Returns a point-in-time snapshot of routes registered through <see cref="Map(string, string, RouteHandler)"/>.
        /// </summary>
        public IReadOnlyList<HttpMappedRoute> GetMappedRoutes() {
            var mappedRoutes = new List<HttpMappedRoute>();
            lock (_routesLock) {
                foreach (var byMethod in _routes) {
                    foreach (var byPath in byMethod.Value) {
                        if (_routeMetadata.TryGetValue(byMethod.Key, out var metaByPath)
                            && metaByPath.TryGetValue(byPath.Key, out var metadata)) {
                            mappedRoutes.Add(metadata);
                        } else {
                            mappedRoutes.Add(new HttpMappedRoute((byMethod.Key ?? string.Empty).ToUpperInvariant(), byPath.Key));
                        }
                    }
                }
            }
            return mappedRoutes
                .OrderBy(route => route.Path, StringComparer.OrdinalIgnoreCase)
                .ThenBy(route => route.Method, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static HttpMappedRoute CreateMappedRouteMetadata(string method, string path, Delegate handler, Type requestBodyType) {
            var handlerMethod = handler?.Method;
            var handlerType = handlerMethod?.DeclaringType != null ? TypeDisplayName(handlerMethod.DeclaringType) : "";
            var handlerName = handlerMethod?.Name ?? "";
            var handlerSignature = BuildHandlerSignature(handlerMethod);
            var bodyKind = requestBodyType == null ? InferBodyKindFromMethod(method) : InferBodyKindFromType(requestBodyType);
            var bodySchema = requestBodyType == null || bodyKind == "none" ? "" : SerializeSchema(DescribeTypeSchema(requestBodyType));
            var responseSchema = handlerMethod == null ? "" : SerializeSchema(DescribeTypeSchema(UnwrapTaskType(handlerMethod.ReturnType)));
            var inputVariables = InferInputVariables(path, handlerMethod, requestBodyType);
            var parametersSchema = SerializeSchema(BuildParametersSchema(path, handlerMethod, requestBodyType));

            return new HttpMappedRoute(
                (method ?? string.Empty).ToUpperInvariant(),
                path,
                handlerType,
                handlerName,
                handlerSignature,
                inputVariables,
                parametersSchema,
                bodySchema,
                responseSchema,
                bodyKind);
        }

        private static string BuildHandlerSignature(MethodInfo method) {
            if (method == null)
                return "";

            var parameters = method.GetParameters()
                .Select(p => TypeDisplayName(p.ParameterType) + " " + p.Name);
            return TypeDisplayName(UnwrapTaskType(method.ReturnType)) + " "
                + TypeDisplayName(method.DeclaringType) + "." + method.Name
                + "(" + string.Join(", ", parameters) + ")";
        }

        private static object BuildParametersSchema(string path, MethodInfo method, Type requestBodyType) {
            var pathParams = InferPathParameters(path)
                .ToDictionary(name => name, name => (object)"string", StringComparer.OrdinalIgnoreCase);

            var handlerParams = new List<object>();
            if (method != null) {
                foreach (var p in method.GetParameters()) {
                    handlerParams.Add(new {
                        name = p.Name,
                        type = TypeDisplayName(p.ParameterType),
                        source = IsInfrastructureParameter(p.ParameterType) ? "runtime" : "handler"
                    });
                }
            }

            return new {
                path = pathParams,
                query = new Dictionary<string, object> { { "*", "string" } },
                headers = new Dictionary<string, object> {
                    { "Content-Type", "string" },
                    { "Authorization", "string" }
                },
                body = requestBodyType == null ? null : DescribeTypeSchema(requestBodyType),
                handler = new {
                    signature = BuildHandlerSignature(method),
                    parameters = handlerParams
                }
            };
        }

        private static IEnumerable<string> InferPathParameters(string path) {
            if (string.IsNullOrWhiteSpace(path))
                yield break;

            var parts = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            int wildcardIndex = 1;
            foreach (var part in parts) {
                if (part == "*") {
                    yield return "path" + wildcardIndex++;
                } else if (part.StartsWith("{", StringComparison.Ordinal) && part.EndsWith("}", StringComparison.Ordinal) && part.Length > 2) {
                    yield return CleanVariableName(part.Substring(1, part.Length - 2));
                } else if (part.StartsWith(":", StringComparison.Ordinal) && part.Length > 1) {
                    yield return CleanVariableName(part.Substring(1));
                } else if (part.StartsWith("<", StringComparison.Ordinal) && part.EndsWith(">", StringComparison.Ordinal) && part.Length > 2) {
                    yield return CleanVariableName(part.Substring(1, part.Length - 2));
                } else if (part.EndsWith("/*", StringComparison.Ordinal)) {
                    yield return "path" + wildcardIndex++;
                }
            }
        }

        private static string InferInputVariables(string path, MethodInfo method, Type requestBodyType) {
            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in InferPathParameters(path))
                if (!string.IsNullOrWhiteSpace(p)) names.Add("$" + p);

            if (requestBodyType != null && !IsSimpleType(requestBodyType)) {
                foreach (var prop in requestBodyType.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetIndexParameters().Length == 0)) {
                    if (!string.IsNullOrWhiteSpace(prop.Name))
                        names.Add("$" + prop.Name);
                }
            } else if (requestBodyType != null) {
                names.Add("$body");
            }

            if (method != null) {
                foreach (var p in method.GetParameters()) {
                    if (!IsInfrastructureParameter(p.ParameterType) && !string.IsNullOrWhiteSpace(p.Name))
                        names.Add("$" + CleanVariableName(p.Name));
                }
            }

            return string.Join(",", names);
        }

        private static string InferBodyKindFromMethod(string method) {
            if (string.IsNullOrWhiteSpace(method))
                return "none";
            switch (method.ToUpperInvariant()) {
                case "POST":
                case "PUT":
                case "PATCH":
                    return "json";
                default:
                    return "none";
            }
        }

        private static string InferBodyKindFromType(Type type) {
            if (type == null || type == typeof(void))
                return "none";
            if (type == typeof(byte[]) || type == typeof(Stream))
                return "binary";
            if (type == typeof(string))
                return "string";
            return "json";
        }

        private static object DescribeTypeSchema(Type type, int depth = 0) {
            type = UnwrapTaskType(type);
            if (type == null || type == typeof(void))
                return "void";

            var nullable = Nullable.GetUnderlyingType(type);
            if (nullable != null)
                type = nullable;

            if (type == typeof(string) || type == typeof(char) || type == typeof(Guid) || type == typeof(Uri))
                return "string";
            if (type == typeof(bool))
                return "boolean";
            if (type == typeof(DateTime) || type == typeof(DateTimeOffset))
                return "datetime";
            if (IsNumericType(type))
                return "number";
            if (type == typeof(byte[]) || type == typeof(Stream))
                return "binary";
            if (type.IsEnum)
                return Enum.GetNames(type);
            if (depth >= 2)
                return TypeDisplayName(type);

            var elementType = GetEnumerableElementType(type);
            if (elementType != null && type != typeof(string))
                return new[] { DescribeTypeSchema(elementType, depth + 1) };

            var schema = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) {
                { "$type", TypeDisplayName(type) }
            };

            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanRead && p.GetIndexParameters().Length == 0)) {
                schema[prop.Name] = DescribeTypeSchema(prop.PropertyType, depth + 1);
            }
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance)) {
                if (!schema.ContainsKey(field.Name))
                    schema[field.Name] = DescribeTypeSchema(field.FieldType, depth + 1);
            }

            return schema.Count == 1 ? (object)TypeDisplayName(type) : schema;
        }

        private static Type UnwrapTaskType(Type type) {
            if (type == null)
                return null;
            if (type == typeof(Task))
                return typeof(void);
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
                return type.GetGenericArguments()[0];
            return type;
        }

        private static Type GetEnumerableElementType(Type type) {
            if (type == null || type == typeof(string))
                return null;
            if (type.IsArray)
                return type.GetElementType();
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                return type.GetGenericArguments()[0];
            var enumerable = type.GetInterfaces()
                .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
            return enumerable?.GetGenericArguments()[0];
        }

        private static bool IsInfrastructureParameter(Type type) {
            if (type == null)
                return false;
            return type == typeof(NetworkConnection)
                || type == typeof(HttpRequest)
                || type == typeof(CancellationToken)
                || type == typeof(ChunkedStream)
                || type == typeof(UploadStream);
        }

        private static bool IsSimpleType(Type type) {
            type = Nullable.GetUnderlyingType(type) ?? type;
            return type.IsPrimitive
                || type.IsEnum
                || type == typeof(string)
                || type == typeof(decimal)
                || type == typeof(DateTime)
                || type == typeof(DateTimeOffset)
                || type == typeof(Guid)
                || type == typeof(Uri);
        }

        private static bool IsNumericType(Type type) {
            type = Nullable.GetUnderlyingType(type) ?? type;
            return type == typeof(byte)
                || type == typeof(sbyte)
                || type == typeof(short)
                || type == typeof(ushort)
                || type == typeof(int)
                || type == typeof(uint)
                || type == typeof(long)
                || type == typeof(ulong)
                || type == typeof(float)
                || type == typeof(double)
                || type == typeof(decimal);
        }

        private static string TypeDisplayName(Type type) {
            if (type == null)
                return "";
            type = UnwrapTaskType(type);
            if (type == typeof(void))
                return "void";
            if (!type.IsGenericType)
                return type.FullName ?? type.Name;

            var name = type.FullName ?? type.Name;
            var tick = name.IndexOf('`');
            if (tick >= 0)
                name = name.Substring(0, tick);
            return name + "<" + string.Join(", ", type.GetGenericArguments().Select(TypeDisplayName)) + ">";
        }

        private static string CleanVariableName(string name) {
            if (string.IsNullOrWhiteSpace(name))
                return "";
            var sb = new StringBuilder(name.Length);
            foreach (var c in name) {
                if (char.IsLetterOrDigit(c) || c == '_')
                    sb.Append(c);
            }
            return sb.ToString();
        }

        private static string SerializeSchema(object schema) {
            if (schema == null)
                return "";
            return JsonSerializer.Serialize(schema, new JsonSerializerOptions { WriteIndented = true });
        }
        /// <summary>
        /// Maps a streaming route. The handler receives a <see cref="ChunkedStream"/> it can
        /// write to line-by-line. The connection stays open until the handler returns.
        /// </summary>
        public void MapStream(string method, string path, StreamRouteHandler handler) {
            if (string.IsNullOrWhiteSpace(method))
                throw new ArgumentException("Method is required.", nameof(method));
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Path is required.", nameof(path));
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            if (!_streamRoutes.TryGetValue(method, out var byPath)) {
                byPath = new Dictionary<string, StreamRouteHandler>(StringComparer.OrdinalIgnoreCase);
                _streamRoutes[method] = byPath;
            }
            byPath[path] = handler;
        }

        /// <summary>
        /// Maps an upload-streaming route. When an incoming HTTP request matches, the handler
        /// runs on a background task and receives an <see cref="UploadStream"/> that yields
        /// data chunks as they arrive (e.g., continuous OBS MPEG-TS video over HTTP POST).
        /// </summary>
        public void MapUploadStream(string method, string path, UploadStreamRouteHandler handler) {
            if (string.IsNullOrWhiteSpace(method))
                throw new ArgumentException("Method is required.", nameof(method));
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Path is required.", nameof(path));
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            if (!_uploadStreamRoutes.TryGetValue(method, out var byPath)) {
                byPath = new Dictionary<string, UploadStreamRouteHandler>(StringComparer.OrdinalIgnoreCase);
                _uploadStreamRoutes[method] = byPath;
            }
            byPath[path] = handler;
        }

        public bool RemoveUploadStreamRoute(string method, string path) {
            if (!_uploadStreamRoutes.TryGetValue(method, out var byPath))
                return false;
            return byPath.Remove(path);
        }

        /// <summary>
        /// Maps an RTMP publish route. When an OBS (or compatible) encoder connects via RTMP
        /// and publishes to the given <paramref name="app"/> name, the <paramref name="handler"/>
        /// runs on a background task and receives an <see cref="UploadStream"/> with media data.
        /// Use <c>"*"</c> as a wildcard to match any app name.
        /// </summary>
        public void MapRtmpPublish(string app, RtmpPublishHandler handler) {
            if (string.IsNullOrWhiteSpace(app))
                throw new ArgumentException("App name is required.", nameof(app));
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));
            _rtmpPublishRoutes[app] = handler;
        }

        public bool RemoveRtmpPublishRoute(string app) {
            return _rtmpPublishRoutes.Remove(app);
        }

        /// <summary>
        /// Maps a local directory to a URL prefix so that files inside the directory are
        /// served when a GET (or HEAD) request matches. For example,
        /// <c>MapDirectory("/static", @"C:\wwwroot")</c> serves <c>C:\wwwroot\style.css</c>
        /// when the client requests <c>/static/style.css</c>.
        /// If <paramref name="urlPrefix"/> is <c>"/"</c>, the directory is served at the root.
        /// </summary>
        /// <param name="urlPrefix">The URL path prefix (e.g., <c>"/static"</c>).</param>
        /// <param name="localDirectory">The absolute or relative path to the local directory.</param>
        public void MapDirectory(string urlPrefix, string localDirectory) {
            if (string.IsNullOrWhiteSpace(urlPrefix))
                throw new ArgumentException("URL prefix is required.", nameof(urlPrefix));
            if (string.IsNullOrWhiteSpace(localDirectory))
                throw new ArgumentException("Local directory is required.", nameof(localDirectory));
            if (!Directory.Exists(localDirectory))
                throw new DirectoryNotFoundException("Directory not found: " + localDirectory);

            var normalized = urlPrefix.TrimEnd('/');
            if (string.IsNullOrEmpty(normalized))
                normalized = "/";
            _directoryMappings[normalized] = Path.GetFullPath(localDirectory);
        }

        /// <summary>
        /// Maps a local directory to a URL prefix and configures a <c>.htaccess</c> file
        /// using the fluent <see cref="HtAccessBuilder"/> API for directory-level security.
        /// </summary>
        /// <param name="urlPrefix">The URL path prefix (e.g., <c>"/secure"</c>).</param>
        /// <param name="localDirectory">The absolute or relative path to the local directory.</param>
        /// <param name="configure">A delegate that configures the <see cref="HtAccessBuilder"/>.</param>
        public void MapDirectory(string urlPrefix, string localDirectory, Action<HtAccessBuilder> configure) {
            MapDirectory(urlPrefix, localDirectory);
            if (configure != null) {
                var builder = new HtAccessBuilder();
                configure(builder);
                builder.WriteTo(Path.GetFullPath(localDirectory));
            }
        }

        /// <summary>
        /// Removes a previously mapped directory route.
        /// </summary>
        public bool RemoveDirectoryMapping(string urlPrefix) {
            var normalized = urlPrefix.TrimEnd('/');
            if (string.IsNullOrEmpty(normalized))
                normalized = "/";
            return _directoryMappings.Remove(normalized);
        }

        /// <summary>
        /// Maps a URL path to a specific local file. When a GET (or HEAD) request
        /// matches the <paramref name="urlPath"/>, the file at <paramref name="localFilePath"/>
        /// is served with the appropriate MIME type. For example,
        /// <c>MapFile("/js/widget.js", @"C:\Pages\widget.js")</c> serves the file
        /// when the client requests <c>/js/widget.js</c>.
        /// </summary>
        /// <param name="urlPath">The URL path to match (e.g., <c>"/js/widget.js"</c>).</param>
        /// <param name="localFilePath">The absolute or relative path to the local file.</param>
        public void MapFile(string urlPath, string localFilePath) {
            if (string.IsNullOrWhiteSpace(urlPath))
                throw new ArgumentException("URL path is required.", nameof(urlPath));
            if (string.IsNullOrWhiteSpace(localFilePath))
                throw new ArgumentException("Local file path is required.", nameof(localFilePath));
            _fileMappings[NormalizePath(urlPath)] = Path.GetFullPath(localFilePath);
        }

        /// <summary>
        /// Removes a previously mapped file route.
        /// </summary>
        public bool RemoveFileMapping(string urlPath) {
            return _fileMappings.Remove(NormalizePath(urlPath));
        }

        /// <summary>
        /// Maps a URL prefix to a virtual document root resolved by application code.
        /// Resolvers can serve SQL-backed, sandboxed, or other non-disk content while
        /// reusing SocketJack's HTTP MIME, header, and route ordering behavior.
        /// </summary>
        public void MapDocumentRoot(string urlPrefix, HttpDocumentRootResolver resolver) {
            if (string.IsNullOrWhiteSpace(urlPrefix))
                throw new ArgumentException("URL prefix is required.", nameof(urlPrefix));
            if (resolver == null)
                throw new ArgumentNullException(nameof(resolver));

            var normalized = NormalizePath(urlPrefix);
            if (normalized.Length > 1 && normalized.EndsWith("/", StringComparison.Ordinal))
                normalized = normalized.TrimEnd('/');
            _documentRootMappings[normalized] = resolver;
        }

        /// <summary>
        /// Maps a disk directory as an explicit document root. Disk roots delegate to
        /// <see cref="MapDirectory(string, string)"/> so the existing path containment and .htaccess
        /// protections remain authoritative.
        /// </summary>
        public void MapDocumentRoot(string urlPrefix, string localDirectory) {
            MapDirectory(urlPrefix, localDirectory);
        }

        /// <summary>
        /// Removes a virtual document root mapping.
        /// </summary>
        public bool RemoveDocumentRootMapping(string urlPrefix) {
            var normalized = NormalizePath(urlPrefix);
            if (normalized.Length > 1 && normalized.EndsWith("/", StringComparison.Ordinal))
                normalized = normalized.TrimEnd('/');
            return _documentRootMappings.Remove(normalized);
        }
        /// <summary>
        /// Maps a local directory to a URL prefix for a specific Host header.
        /// </summary>
        public void MapHostDirectory(string hostName, string urlPrefix, string localDirectory) {
            hostName = NormalizeHostName(hostName);
            if (string.IsNullOrWhiteSpace(hostName))
                throw new ArgumentException("Host name is required.", nameof(hostName));
            if (string.IsNullOrWhiteSpace(urlPrefix))
                throw new ArgumentException("URL prefix is required.", nameof(urlPrefix));
            if (string.IsNullOrWhiteSpace(localDirectory))
                throw new ArgumentException("Local directory is required.", nameof(localDirectory));
            if (!Directory.Exists(localDirectory))
                throw new DirectoryNotFoundException("Directory not found: " + localDirectory);

            var normalized = urlPrefix.TrimEnd('/');
            if (string.IsNullOrEmpty(normalized))
                normalized = "/";

            lock (_routesLock) {
                if (!_hostDirectoryMappings.TryGetValue(hostName, out var mappings)) {
                    mappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    _hostDirectoryMappings[hostName] = mappings;
                }
                mappings[normalized] = Path.GetFullPath(localDirectory);
            }
        }

        /// <summary>
        /// Maps a local directory to a URL prefix for a specific Host header and configures a <c>.htaccess</c> file.
        /// </summary>
        public void MapHostDirectory(string hostName, string urlPrefix, string localDirectory, Action<HtAccessBuilder> configure) {
            MapHostDirectory(hostName, urlPrefix, localDirectory);
            if (configure != null) {
                var builder = new HtAccessBuilder();
                configure(builder);
                builder.WriteTo(Path.GetFullPath(localDirectory));
            }
        }

        /// <summary>
        /// Removes a previously mapped host-specific directory route.
        /// </summary>
        public bool RemoveHostDirectoryMapping(string hostName, string urlPrefix) {
            hostName = NormalizeHostName(hostName);
            var normalized = urlPrefix.TrimEnd('/');
            if (string.IsNullOrEmpty(normalized))
                normalized = "/";
            lock (_routesLock) {
                if (!_hostDirectoryMappings.TryGetValue(hostName, out var mappings))
                    return false;
                var removed = mappings.Remove(normalized);
                if (mappings.Count == 0)
                    _hostDirectoryMappings.Remove(hostName);
                return removed;
            }
        }

        /// <summary>
        /// Maps a URL path to a local file for a specific Host header.
        /// </summary>
        public void MapHostFile(string hostName, string urlPath, string localFilePath) {
            hostName = NormalizeHostName(hostName);
            if (string.IsNullOrWhiteSpace(hostName))
                throw new ArgumentException("Host name is required.", nameof(hostName));
            if (string.IsNullOrWhiteSpace(urlPath))
                throw new ArgumentException("URL path is required.", nameof(urlPath));
            if (string.IsNullOrWhiteSpace(localFilePath))
                throw new ArgumentException("Local file path is required.", nameof(localFilePath));

            lock (_routesLock) {
                if (!_hostFileMappings.TryGetValue(hostName, out var mappings)) {
                    mappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    _hostFileMappings[hostName] = mappings;
                }
                mappings[NormalizePath(urlPath)] = Path.GetFullPath(localFilePath);
            }
        }

        /// <summary>
        /// Removes a previously mapped host-specific file route.
        /// </summary>
        public bool RemoveHostFileMapping(string hostName, string urlPath) {
            hostName = NormalizeHostName(hostName);
            lock (_routesLock) {
                if (!_hostFileMappings.TryGetValue(hostName, out var mappings))
                    return false;
                var removed = mappings.Remove(NormalizePath(urlPath));
                if (mappings.Count == 0)
                    _hostFileMappings.Remove(hostName);
                return removed;
            }
        }

        /// <summary>
        /// Proxies every request for <paramref name="hostName"/> to an existing HTTP or HTTPS server.
        /// Use this to route a public subdomain to a local site, for example
        /// <c>MapSubdomain("OtherWebsite.MyDomain.com", "http://127.0.0.1:7567")</c>.
        /// </summary>
        public void MapSubdomain(string hostName, string targetBaseUrl) {
            ProxyHost(hostName, targetBaseUrl);
        }

        /// <summary>
        /// Proxies every request for <paramref name="hostName"/> to an existing HTTP or HTTPS server.
        /// </summary>
        public void ProxyHost(string hostName, string targetBaseUrl) {
            ProxyHost(hostName, targetBaseUrl, false);
        }

        /// <summary>
        /// Proxies every request for <paramref name="hostName"/> and optionally preserves that Host header upstream.
        /// </summary>
        public void ProxyHost(string hostName, string targetBaseUrl, bool preserveHostHeader) {
            hostName = NormalizeHostName(hostName);
            if (string.IsNullOrWhiteSpace(hostName))
                throw new ArgumentException("Host name is required.", nameof(hostName));
            if (string.IsNullOrWhiteSpace(targetBaseUrl))
                throw new ArgumentException("Target URL is required.", nameof(targetBaseUrl));

            if (!Uri.TryCreate(targetBaseUrl, UriKind.Absolute, out var targetUri))
                throw new ArgumentException("Target URL must be an absolute http:// or https:// URL.", nameof(targetBaseUrl));
            if (!targetUri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) && !targetUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Target URL must use http:// or https://.", nameof(targetBaseUrl));

            lock (_routesLock) {
                _hostProxyMappings[hostName] = new HostProxyTarget(targetUri, preserveHostHeader);
            }
        }

        /// <summary>
        /// Proxies every request for <paramref name="hostName"/> to an existing server.
        /// </summary>
        public void ProxyHost(string hostName, string targetHost, int targetPort, bool useHttps = false) {
            if (string.IsNullOrWhiteSpace(targetHost))
                throw new ArgumentException("Target host is required.", nameof(targetHost));
            if (targetPort <= 0 || targetPort > 65535)
                throw new ArgumentOutOfRangeException(nameof(targetPort));
            var scheme = useHttps ? "https" : "http";
            ProxyHost(hostName, scheme + "://" + targetHost + ":" + targetPort);
        }

        /// <summary>
        /// Removes a host/subdomain proxy mapping.
        /// </summary>
        public bool RemoveHostProxy(string hostName) {
            hostName = NormalizeHostName(hostName);
            lock (_routesLock) {
                return _hostProxyMappings.Remove(hostName);
            }
        }
        private bool TryResolveDirectoryFile(string method, string path, bool downloadDirectoryZip, string clientIp, string authHeader, out byte[] fileBytes, out string sourceFilePath, out string contentType, out string fileName, out HtAccessResult accessResult, out string authRealm, out Dictionary<string, string> extraHeaders, out DateTime? lastModifiedUtc) {
            return TryResolveDirectoryFile(_directoryMappings, method, path, downloadDirectoryZip, clientIp, authHeader, out fileBytes, out sourceFilePath, out contentType, out fileName, out accessResult, out authRealm, out extraHeaders, out lastModifiedUtc);
        }

        private bool TryResolveDirectoryFile(Dictionary<string, string> directoryMappings, string method, string path, bool downloadDirectoryZip, string clientIp, string authHeader, out byte[] fileBytes, out string sourceFilePath, out string contentType, out string fileName, out HtAccessResult accessResult, out string authRealm, out Dictionary<string, string> extraHeaders, out DateTime? lastModifiedUtc) {
            fileBytes = null;
            sourceFilePath = null;
            contentType = null;
            fileName = null;
            accessResult = HtAccessResult.Allowed;
            authRealm = null;
            extraHeaders = null;
            lastModifiedUtc = null;

            if (directoryMappings == null || directoryMappings.Count == 0)
                return false;

            if (method == null || !(method.Equals("GET", StringComparison.OrdinalIgnoreCase) || method.Equals("HEAD", StringComparison.OrdinalIgnoreCase)))
                return false;

            if (string.IsNullOrEmpty(path))
                return false;

            // Block .htaccess from ever being served to clients.
            var requestedFileName = path.Substring(path.LastIndexOf('/') + 1);
            if (requestedFileName.Equals(".htaccess", StringComparison.OrdinalIgnoreCase)) {
                RecordEndpointSecurityFilesystemEvent(clientIp, path, "static-file", ".htaccess probe");
                return false;
            }

            foreach (var kv in directoryMappings) {
                var prefix = kv.Key;
                var localDir = Path.GetFullPath(kv.Value);

                string relativePath = null;
                if (prefix == "/") {
                    relativePath = path;
                } else if (path.Equals(prefix, StringComparison.OrdinalIgnoreCase)) {
                    // Exact match on prefix with no trailing path ï¿½ try index files
                    relativePath = "/";
                } else if (path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)) {
                    relativePath = path.Substring(prefix.Length);
                }

                if (relativePath == null)
                    continue;

                // Convert URL slashes to OS path separators and strip leading slash
                var localRelative = relativePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
                var fullPath = string.IsNullOrEmpty(localRelative)
                    ? localDir
                    : Path.Combine(localDir, localRelative);
                fullPath = Path.GetFullPath(fullPath);

                // Security: ensure the resolved path is still within the mapped directory.
                if (!IsPathInsideRoot(fullPath, localDir)) {
                    RecordEndpointSecurityFilesystemEvent(clientIp, path, "static-file", "path escaped mapped directory");
                    return false;
                }

                // Evaluate .htaccess access rules before serving any content.
                string resolvedFileName = Path.GetFileName(fullPath);
                string htDir = Directory.Exists(fullPath) ? fullPath : Path.GetDirectoryName(fullPath);
                accessResult = HtAccessEvaluator.Evaluate(htDir, resolvedFileName, clientIp, authHeader, out authRealm, out extraHeaders);
                if (accessResult != HtAccessResult.Allowed)
                    return true;

                // If path points to a directory, try common index files
                if (Directory.Exists(fullPath)) {
                    bool directoryListingAllowed = IsDirectoryListingAllowed(fullPath);
                    if (downloadDirectoryZip && directoryListingAllowed) {
                        fileBytes = CreateDirectoryZipBytes(fullPath);
                        contentType = "application/zip";
                        fileName = MakeSafeDownloadFileName((new DirectoryInfo(fullPath)).Name, "directory") + ".zip";
                        try { lastModifiedUtc = Directory.GetLastWriteTimeUtc(fullPath); } catch { }
                        return true;
                    }

                    var indexHtml = Path.Combine(fullPath, "index.html");
                    var indexHtm = Path.Combine(fullPath, "index.htm");
                    if (File.Exists(indexHtml))
                        fullPath = indexHtml;
                    else if (File.Exists(indexHtm))
                        fullPath = indexHtm;
                    else {
                        // Check if directory listing is allowed.
                        if (directoryListingAllowed) {
                            fileBytes = Encoding.UTF8.GetBytes(GenerateDirectoryListing(fullPath, path));
                            contentType = "text/html";
                            fileName = null;
                            return true;
                        }
                        continue;
                    }
                }

                if (!File.Exists(fullPath))
                    continue;

                // Block .htaccess at the file level as well.
                if (Path.GetFileName(fullPath).Equals(".htaccess", StringComparison.OrdinalIgnoreCase)) {
                    RecordEndpointSecurityFilesystemEvent(clientIp, path, "static-file", ".htaccess probe");
                    return false;
                }

                sourceFilePath = fullPath;
                fileName = Path.GetFileName(fullPath);
                contentType = GetMimeType(fullPath);
                try { lastModifiedUtc = File.GetLastWriteTimeUtc(fullPath); } catch { }
                return true;
            }

            return false;
        }

        /// <summary>
        /// Determines whether directory listing is allowed for the given local
        /// directory. Checks for a <c>.htaccess</c> file first; if it contains
        /// <c>Options +Indexes</c> listing is allowed, <c>Options -Indexes</c>
        /// denies it. Falls back to <see cref="AllowDirectoryListing"/>.
        /// </summary>
        private bool IsDirectoryListingAllowed(string localDirectory) {
            try {
                var htaccess = Path.Combine(localDirectory, ".htaccess");
                if (File.Exists(htaccess)) {
                    var lines = File.ReadAllLines(htaccess);
                    for (int i = lines.Length - 1; i >= 0; i--) {
                        var line = lines[i].Trim();
                        if (line.StartsWith("#") || line.Length == 0) continue;
                        if (line.StartsWith("Options", StringComparison.OrdinalIgnoreCase)) {
                            if (line.IndexOf("+Indexes", StringComparison.OrdinalIgnoreCase) >= 0)
                                return true;
                            if (line.IndexOf("-Indexes", StringComparison.OrdinalIgnoreCase) >= 0)
                                return false;
                        }
                    }
                }
            } catch { }
            return AllowDirectoryListing;
        }

        private static bool IsDirectoryZipDownloadRequest(HttpRequest request) {
            if (request == null || request.QueryParameters == null)
                return false;

            return (request.QueryParameters.TryGetValue("download", out string download) &&
                    download.Equals("zip", StringComparison.OrdinalIgnoreCase)) ||
                   (request.QueryParameters.TryGetValue("zip", out string zip) &&
                    (zip.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                     zip.Equals("true", StringComparison.OrdinalIgnoreCase)));
        }

        private static byte[] CreateDirectoryZipBytes(string localDirectory) {
            using (var memory = new MemoryStream()) {
                using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true)) {
                    string root = Path.GetFullPath(localDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    foreach (string filePath in Directory.EnumerateFiles(localDirectory, "*", SearchOption.AllDirectories)) {
                        string name = Path.GetFileName(filePath);
                        if (name.Equals(".htaccess", StringComparison.OrdinalIgnoreCase))
                            continue;

                        string fullPath = Path.GetFullPath(filePath);
                        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                            continue;

                        string relative = fullPath.Substring(root.Length).Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
                        relative = NormalizeZipEntryName(relative);
                        ZipArchiveEntry entry = archive.CreateEntry(relative, CompressionLevel.Fastest);
                        using (Stream input = OpenSharedReadStream(fullPath))
                        using (Stream output = entry.Open())
                            input.CopyTo(output);
                    }
                }

                return memory.ToArray();
            }
        }

        private static string MakeSafeDownloadFileName(string value, string fallback) {
            string name = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return string.IsNullOrWhiteSpace(name) ? fallback : name;
        }

        private static string NormalizeZipEntryName(string value) {
            string[] parts = (value ?? "").Replace('\\', '/').Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            string normalized = string.Join("/", parts.Select(part => MakeSafeDownloadFileName(part, "file")));
            return string.IsNullOrWhiteSpace(normalized) ? "file" : normalized;
        }

        /// <summary>
        /// Generates an HTML page listing the contents of a directory.
        /// </summary>
        private static string GenerateDirectoryListing(string localDirectory, string urlPath) {
            var sb = new StringBuilder();
            sb.Append("<!DOCTYPE html><html><head><meta charset=\"UTF-8\">");
            sb.Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
            sb.Append("<title>Index of " + System.Net.WebUtility.HtmlEncode(urlPath) + "</title>");
            sb.Append("<style>");
            sb.Append("*{margin:0;padding:0;box-sizing:border-box}");
            sb.Append("body{font-family:'Segoe UI',system-ui,sans-serif;background:#111;color:#e8e8e8;padding:2rem}");
            sb.Append("h1{font-weight:300;margin-bottom:1rem;color:#0078d4}");
            sb.Append(".tools{display:flex;align-items:center;gap:.75rem;margin:0 0 1rem;flex-wrap:wrap}");
            sb.Append("button{border:1px solid rgba(0,120,212,.55);background:rgba(0,120,212,.16);color:#e8f4ff;border-radius:6px;padding:.52rem .8rem;cursor:pointer}");
            sb.Append("button:disabled{opacity:.62;cursor:wait}");
            sb.Append(".progress{color:#888;font-size:.9rem}");
            sb.Append("table{width:100%;border-collapse:collapse}");
            sb.Append("th,td{text-align:left;padding:.5rem .75rem;border-bottom:1px solid rgba(255,255,255,.08)}");
            sb.Append("th{color:#888;font-size:.8rem;text-transform:uppercase;letter-spacing:.05em}");
            sb.Append("a{color:#0078d4;text-decoration:none}a:hover{text-decoration:underline}");
            sb.Append("tr:hover{background:rgba(0,120,212,.06)}");
            sb.Append(".size{color:#888;font-variant-numeric:tabular-nums}");
            sb.Append(".date{color:#888}");
            sb.Append("</style></head><body>");
            sb.Append("<h1>Index of " + System.Net.WebUtility.HtmlEncode(urlPath) + "</h1>");
            sb.Append("<div class=\"tools\"><button id=\"zipDownload\" type=\"button\">Download Directory (Zip)</button><span id=\"zipProgress\" class=\"progress\"></span></div>");
            sb.Append("<table><thead><tr><th>Name</th><th>Size</th><th>Modified</th></tr></thead><tbody>");

            // Parent directory link
            if (urlPath != "/" && urlPath.Length > 1) {
                var parent = urlPath.TrimEnd('/');
                var lastSlash = parent.LastIndexOf('/');
                parent = lastSlash > 0 ? parent.Substring(0, lastSlash) : "/";
                sb.Append("<tr><td><a href=\"" + parent + "\">..</a></td><td></td><td></td></tr>");
            }

            try {
                // Directories first
                foreach (var dir in Directory.GetDirectories(localDirectory)) {
                    var name = Path.GetFileName(dir);
                    var info = new DirectoryInfo(dir);
                    var href = urlPath.TrimEnd('/') + "/" + Uri.EscapeDataString(name) + "/";
                    sb.Append("<tr><td><a href=\"" + href + "\">[DIR] " + System.Net.WebUtility.HtmlEncode(name) + "/</a></td>");
                    sb.Append("<td class=\"size\">-</td>");
                    sb.Append("<td class=\"date\">" + info.LastWriteTime.ToString("yyyy-MM-dd HH:mm") + "</td></tr>");
                }
                // Files (skip .htaccess)
                foreach (var file in Directory.GetFiles(localDirectory)) {
                    var name = Path.GetFileName(file);
                    if (name.Equals(".htaccess", StringComparison.OrdinalIgnoreCase)) continue;
                    var info = new FileInfo(file);
                    var href = urlPath.TrimEnd('/') + "/" + Uri.EscapeDataString(name);
                    sb.Append("<tr><td><a href=\"" + href + "\">" + System.Net.WebUtility.HtmlEncode(name) + "</a></td>");
                    sb.Append("<td class=\"size\">" + FormatFileSize(info.Length) + "</td>");
                    sb.Append("<td class=\"date\">" + info.LastWriteTime.ToString("yyyy-MM-dd HH:mm") + "</td></tr>");
                }
            } catch { }

            sb.Append("</tbody></table>");
            sb.Append("<script>");
            sb.Append("(function(){const b=document.getElementById('zipDownload'),p=document.getElementById('zipProgress');");
            sb.Append("function fmt(n){if(!n)return'0 B';const u=['B','KB','MB','GB','TB'];let i=0,v=n;while(v>=1024&&i<u.length-1){v/=1024;i++;}return(i?v.toFixed(v>=10?1:2):Math.round(v))+' '+u[i];}");
            sb.Append("function name(r){const d=r.headers.get('Content-Disposition')||'';let m=d.match(/filename\\*=UTF-8''([^;]+)/i);if(m){try{return decodeURIComponent(m[1].replace(/^\\\"|\\\"$/g,''));}catch(e){}}m=d.match(/filename=\\\"([^\\\"]+)\\\"/i)||d.match(/filename=([^;]+)/i);return m?m[1].trim().replace(/^\\\"|\\\"$/g,''):'directory.zip';}");
            sb.Append("b.addEventListener('click',async()=>{if(b.disabled)return;b.disabled=true;b.textContent='Preparing zip...';p.textContent='';try{const u=new URL(location.href);u.searchParams.set('download','zip');const r=await fetch(u.href);if(!r.ok)throw new Error('HTTP '+r.status);const total=Number(r.headers.get('Content-Length')||0);const reader=r.body&&r.body.getReader?r.body.getReader():null;let chunks=[],got=0;if(reader){while(true){const x=await reader.read();if(x.done)break;chunks.push(x.value);got+=x.value.byteLength||x.value.length||0;b.textContent=total?'Downloading '+Math.round(got/total*100)+'%':'Downloading '+fmt(got);}}else{chunks=[new Uint8Array(await r.arrayBuffer())];got=chunks[0].byteLength;}const blob=new Blob(chunks,{type:r.headers.get('Content-Type')||'application/zip'});const a=document.createElement('a');const href=URL.createObjectURL(blob);a.href=href;a.download=name(r);document.body.appendChild(a);a.click();a.remove();setTimeout(()=>URL.revokeObjectURL(href),1000);b.textContent='Download complete';p.textContent=fmt(got);}catch(e){b.textContent='Download failed';p.textContent=e&&e.message?e.message:String(e);}finally{setTimeout(()=>{b.disabled=false;b.textContent='Download Directory (Zip)';},900);}});");
            sb.Append("})();</script></body></html>");
            return sb.ToString();
        }

        private static string FormatFileSize(long bytes) {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("N1") + " KB";
            if (bytes < 1024 * 1024 * 1024L) return (bytes / (1024.0 * 1024)).ToString("N1") + " MB";
            return (bytes / (1024.0 * 1024 * 1024)).ToString("N2") + " GB";
        }

        private static bool IsPathInsideRoot(string path, string root) {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root))
                return false;

            string fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return fullPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)
                || fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || fullPath.StartsWith(fullRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private bool TryResolveFileMapping(string method, string path, out byte[] fileBytes, out string sourceFilePath, out string contentType, out string fileName, out DateTime? lastModifiedUtc) {
            return TryResolveFileMapping(_fileMappings, method, path, out fileBytes, out sourceFilePath, out contentType, out fileName, out lastModifiedUtc);
        }

        private bool TryResolveFileMapping(Dictionary<string, string> fileMappings, string method, string path, out byte[] fileBytes, out string sourceFilePath, out string contentType, out string fileName, out DateTime? lastModifiedUtc) {
            fileBytes = null;
            sourceFilePath = null;
            contentType = null;
            fileName = null;
            lastModifiedUtc = null;

            if (fileMappings == null || fileMappings.Count == 0)
                return false;

            if (method == null || !(method.Equals("GET", StringComparison.OrdinalIgnoreCase) || method.Equals("HEAD", StringComparison.OrdinalIgnoreCase)))
                return false;

            if (string.IsNullOrEmpty(path))
                return false;

            var normalized = NormalizePath(path);
            if (fileMappings.TryGetValue(normalized, out var filePath)) {
                if (File.Exists(filePath)) {
                    sourceFilePath = Path.GetFullPath(filePath);
                    fileName = Path.GetFileName(filePath);
                    contentType = GetMimeType(filePath);
                    try { lastModifiedUtc = File.GetLastWriteTimeUtc(filePath); } catch { }
                    return true;
                }
            }
            return false;
        }

        private bool TryResolveDocumentRootFile(HttpRequest request, out HttpDocumentRootResponse response) {
            response = null;
            if (_documentRootMappings.Count == 0)
                return false;
            if (request == null || request.Method == null || !(request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) || request.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase)))
                return false;
            if (string.IsNullOrEmpty(request.Path))
                return false;

            string normalizedPath = NormalizePath(request.Path);
            foreach (var kv in _documentRootMappings.OrderByDescending(item => item.Key.Length)) {
                string prefix = kv.Key;
                string relative = null;
                if (prefix == "/") {
                    relative = normalizedPath.TrimStart('/');
                } else if (normalizedPath.Equals(prefix, StringComparison.OrdinalIgnoreCase)) {
                    relative = "";
                } else if (normalizedPath.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)) {
                    relative = normalizedPath.Substring(prefix.Length + 1);
                }

                if (relative == null)
                    continue;
                if (relative.Contains("..") || relative.IndexOf('\\') >= 0) {
                    RecordEndpointSecurityFilesystemEvent(null, normalizedPath, "document-root", "path escaped mapped document root");
                    return false;
                }

                if (kv.Value(request, relative, out response) && response != null)
                    return true;
            }

            return false;
        }

        private bool TryResolveAndApplyDocumentRoot(HttpContext context, HttpRequest request) {
            if (context == null || request == null)
                return false;
            if (!TryResolveDocumentRootFile(request, out var docResponse) || docResponse == null)
                return false;

            string fileName = docResponse.FileName;
            string contentType = !string.IsNullOrWhiteSpace(docResponse.ContentType)
                ? docResponse.ContentType
                : GetMimeType(fileName);
            ApplyServedFileHeaders(context.Response, contentType, fileName);
            if (docResponse.LastModifiedUtc.HasValue)
                context.Response.Headers["Last-Modified"] = docResponse.LastModifiedUtc.Value.ToString("R", CultureInfo.InvariantCulture);
            if (docResponse.Headers != null) {
                foreach (var kv in docResponse.Headers)
                    context.Response.Headers[kv.Key] = kv.Value;
            }

            if (docResponse.Data != null) {
                context.Response.BodyBytes = docResponse.Data;
            } else {
                context.Response.Body = docResponse.Text ?? "";
            }
            context.Response.ContentType = contentType;
            return true;
        }
        public static string GetMimeType(string fileNameOrExtension) {
            string extension = fileNameOrExtension;
            if (!string.IsNullOrWhiteSpace(extension) && (!extension.StartsWith(".", StringComparison.Ordinal) || extension.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }) >= 0))
                extension = Path.GetExtension(extension);
            if (string.IsNullOrEmpty(extension))
                return "application/octet-stream";
            switch (extension.ToLowerInvariant()) {
                case ".html":
                case ".htm":  return "text/html";
                case ".css":  return "text/css";
                case ".js":
                case ".mjs":  return "application/javascript";
                case ".ts":
                case ".mts":
                case ".cts":
                case ".tsx":  return "text/typescript";
                case ".map":  return "application/json";
                case ".json": return "application/json";
                case ".xml":  return "application/xml";
                case ".txt":  return "text/plain";
                case ".md":   return "text/markdown";
                case ".csv":  return "text/csv";
                case ".svg":  return "image/svg+xml";
                case ".png":  return "image/png";
                case ".jpg":
                case ".jpeg": return "image/jpeg";
                case ".gif":  return "image/gif";
                case ".ico":  return "image/x-icon";
                case ".webp": return "image/webp";
                case ".avif": return "image/avif";
                case ".bmp":  return "image/bmp";
                case ".woff": return "font/woff";
                case ".woff2":return "font/woff2";
                case ".ttf":  return "font/ttf";
                case ".otf":  return "font/otf";
                case ".eot":  return "application/vnd.ms-fontobject";
                case ".pdf":  return "application/pdf";
                case ".zip":  return "application/zip";
                case ".gz":   return "application/gzip";
                case ".tar":  return "application/x-tar";
                case ".7z":   return "application/x-7z-compressed";
                case ".rar":  return "application/vnd.rar";
                case ".doc":  return "application/msword";
                case ".docx": return "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
                case ".xls":  return "application/vnd.ms-excel";
                case ".xlsx": return "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
                case ".ppt":  return "application/vnd.ms-powerpoint";
                case ".pptx": return "application/vnd.openxmlformats-officedocument.presentationml.presentation";
                case ".mp3":  return "audio/mpeg";
                case ".m4a":  return "audio/mp4";
                case ".mp4":  return "video/mp4";
                case ".mov":  return "video/quicktime";
                case ".mkv":  return "video/x-matroska";
                case ".webm": return "video/webm";
                case ".ogg":  return "audio/ogg";
                case ".wav":  return "audio/wav";
                case ".wasm": return "application/wasm";
                default:       return "application/octet-stream";
            }
        }

        private static void ApplyServedFileHeaders(HttpResponse response, string contentType, string fileName) {
            if (response == null)
                return;
            if (response.Headers == null)
                response.Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(contentType)) {
                response.ContentType = contentType;
                if (!response.Headers.ContainsKey("Content-Type"))
                    response.Headers["Content-Type"] = contentType;
            }

            if (!string.IsNullOrWhiteSpace(fileName) && !response.Headers.ContainsKey("Content-Disposition"))
                response.Headers["Content-Disposition"] = BuildContentDispositionHeader(fileName);

            response.DisableChunkedTransfer = true;
        }

        private static string BuildContentDispositionHeader(string fileName) {
            string safeFileName = Path.GetFileName(fileName);
            if (string.IsNullOrWhiteSpace(safeFileName))
                safeFileName = "file";
            return "inline; filename=\"" + EscapeHttpQuotedString(safeFileName) + "\"; filename*=UTF-8''" + Uri.EscapeDataString(safeFileName);
        }

        private static string EscapeHttpQuotedString(string value) {
            return (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static string GetRequestHostName(HttpRequest request) {
            if (request == null)
                return null;
            if (!string.IsNullOrWhiteSpace(request.HostName))
                return request.HostName;
            if (request.Headers != null && request.Headers.TryGetValue("Host", out var host))
                return NormalizeHostName(host);
            return null;
        }

        private static string NormalizeHostName(string hostName) {
            if (string.IsNullOrWhiteSpace(hostName))
                return null;

            hostName = SanitizeRedirectHost(hostName).Trim();
            if (hostName.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || hostName.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) {
                if (Uri.TryCreate(hostName, UriKind.Absolute, out var uri))
                    hostName = uri.Host;
            }

            if (hostName.StartsWith("[", StringComparison.Ordinal)) {
                int close = hostName.IndexOf(']');
                if (close > 0)
                    hostName = hostName.Substring(1, close - 1);
            } else {
                int colon = hostName.LastIndexOf(':');
                if (colon > 0 && hostName.IndexOf(':') == colon)
                    hostName = hostName.Substring(0, colon);
            }

            hostName = hostName.Trim().TrimEnd('.');
            return string.IsNullOrWhiteSpace(hostName) ? null : hostName.ToLowerInvariant();
        }

        private static T TryGetHostMapping<T>(Dictionary<string, T> mappings, string hostName) where T : class {
            if (mappings == null || mappings.Count == 0)
                return null;

            hostName = NormalizeHostName(hostName);
            if (!string.IsNullOrWhiteSpace(hostName)) {
                if (mappings.TryGetValue(hostName, out var exact))
                    return exact;

                int dot = hostName.IndexOf('.');
                while (dot > 0 && dot < hostName.Length - 1) {
                    var wildcard = "*" + hostName.Substring(dot);
                    if (mappings.TryGetValue(wildcard, out var wildcardMatch))
                        return wildcardMatch;
                    dot = hostName.IndexOf('.', dot + 1);
                }
            }

            return mappings.TryGetValue("*", out var any) ? any : null;
        }

        private Dictionary<string, string> GetHostFileMappings(HttpRequest request) {
            return TryGetHostMapping(_hostFileMappings, GetRequestHostName(request));
        }

        private Dictionary<string, string> GetHostDirectoryMappings(HttpRequest request) {
            return TryGetHostMapping(_hostDirectoryMappings, GetRequestHostName(request));
        }
        private object ResolveRouteObject(NetworkConnection connection, HttpRequest request, CancellationToken cancellationToken, RouteMatchMode matchMode = RouteMatchMode.ExactAndWildcard) {

            if (request == null)
                return null;

            string method = request.Method;
            string path = NormalizePath(request.Path);
            string hostName = GetRequestHostName(request);
            bool hasHostStaticMappings = GetHostFileMappings(request) != null || GetHostDirectoryMappings(request) != null;

            var hostRouteMap = TryGetHostMapping(_hostRoutes, hostName);
            var hostHandler = FindRouteHandler(hostRouteMap, method, path, request, matchMode);
            if (hostHandler != null)
                return hostHandler(connection, request, cancellationToken);

            var handler = FindRouteHandler(_routes, method, path, request, matchMode);
            if (handler != null)
                return handler(connection, request, cancellationToken);

            if (matchMode != RouteMatchMode.WildcardOnly && !hasHostStaticMappings && method != null && (method.Equals("GET", StringComparison.OrdinalIgnoreCase) || method.Equals("HEAD", StringComparison.OrdinalIgnoreCase))) {
                if (path == "/" || string.IsNullOrEmpty(path))
                    return IndexPageHtml;
                if (path.Equals("/download", StringComparison.OrdinalIgnoreCase))
                    return DownloadPageHtml ?? IndexPageHtml;
                if (path == "/robots.txt" && Robots != null)
                    return Robots;
            }

            return null;
        }

        private bool TryResolveAndApplyRoute(NetworkConnection connection, HttpRequest request, CancellationToken cancellationToken, HttpContext context, RouteMatchMode matchMode) {
            if (_hostRoutes.Count == 0 && _routes.Count == 0 && IndexPageHtml == null && Robots == null)
                return false;

            var routeObj = ResolveRouteObject(connection, request, cancellationToken, matchMode);
            return TryApplyRouteResponse(context, request, routeObj);
        }

        private bool TryApplyRouteResponse(HttpContext context, HttpRequest request, object routeObj) {
            if (routeObj == null)
                return false;

            // FileResponse allows route handlers to return binary data with an explicit content type.
            if (routeObj is FileResponse fileResp) {
                string responseFileName = !string.IsNullOrWhiteSpace(fileResp.FileName)
                    ? fileResp.FileName
                    : !string.IsNullOrWhiteSpace(fileResp.FilePath)
                        ? Path.GetFileName(fileResp.FilePath)
                        : null;
                string contentType = !string.IsNullOrWhiteSpace(fileResp.ContentType)
                    ? fileResp.ContentType
                    : GetMimeType(!string.IsNullOrWhiteSpace(responseFileName) ? responseFileName : fileResp.FilePath);
                ApplyServedFileHeaders(context.Response, contentType, responseFileName);
                if (!string.IsNullOrWhiteSpace(fileResp.FilePath)) {
                    context.Response.StreamFile = fileResp;
                    try {
                        var info = new FileInfo(fileResp.FilePath);
                        context.Response.Headers["Content-Length"] = info.Length.ToString(CultureInfo.InvariantCulture);
                        context.Response.Headers["Last-Modified"] = info.LastWriteTimeUtc.ToString("R", CultureInfo.InvariantCulture);
                    } catch {
                    }
                } else {
                    context.Response.BodyBytes = fileResp.Data ?? Array.Empty<byte>();
                }
                return true;
            }

            context.Response.Body = routeObj;

            var isIndex = request.Method != null && (request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) || request.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase)) && (request.Path == "/" || string.IsNullOrEmpty(request.Path)) && IndexPageHtml != null;

            if (routeObj is string strBody) {
                var trimmed = strBody.TrimStart();
                if (isIndex || trimmed.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase)) {
                    context.Response.ContentType = "text/html";
                } else if (trimmed.Length > 0 && (trimmed[0] == '{' || trimmed[0] == '[')) {
                    context.Response.ContentType = "application/json";
                } else {
                    context.Response.ContentType = "text/plain";
                }
            } else {
                // SocketJack returns wrapped object payloads as binary;
                // mark them as json so our HttpClient will attempt to deserialize.
                context.Response.ContentType = "application/json";
            }

            return true;
        }

        public async Task<HttpDispatchResult> DispatchMappedHttpRequestAsync(NetworkConnection connection, HttpRequest request, HttpStreamDispatchCallbacks streamCallbacks = null, CancellationToken cancellationToken = default) {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            var context = new HttpContext {
                Request = request,
                Connection = connection,
                cancellationToken = cancellationToken
            };
            request.Context = context;

            if (!IsHostAllowed(request)) {
                context.StatusCodeNumber = 421;
                context.ReasonPhrase = "Misdirected Request";
                context.Response.Body = "421 Misdirected Request";
                context.Response.ContentType = "text/plain";
                return FinalizeDispatchedHttpResponse(context, streamed: false);
            }

            string requestGateMessage = RequestGate?.Invoke(connection, request);
            if (!string.IsNullOrWhiteSpace(requestGateMessage)) {
                context.StatusCodeNumber = 403;
                context.ReasonPhrase = "Forbidden";
                context.Response.Body = requestGateMessage;
                context.Response.ContentType = "text/plain";
                return FinalizeDispatchedHttpResponse(context, streamed: false);
            }

            StreamRouteHandler streamHandler = ResolveStreamRoute(request);
            if (streamHandler != null) {
                var chunked = new ChunkedStream(
                    context.Response,
                    connection,
                    streamCallbacks?.OnStart,
                    streamCallbacks?.OnChunk,
                    streamCallbacks?.OnEnd);
                try {
                    await streamHandler(connection, request, chunked, cancellationToken).ConfigureAwait(false);
                } finally {
                    chunked.Finish();
                }
                return FinalizeDispatchedHttpResponse(context, streamed: true);
            }

            bool handledByRoute = TryResolveAndApplyRoute(connection, request, cancellationToken, context, RouteMatchMode.ExactOnly);
            if (!handledByRoute)
                handledByRoute = TryResolveAndApplyDocumentRoot(context, request);
            if (!handledByRoute)
                handledByRoute = TryResolveAndApplyRoute(connection, request, cancellationToken, context, RouteMatchMode.WildcardOnly);

            if (!handledByRoute)
                OnHttpRequest?.Invoke(connection, ref context, cancellationToken);

            if (!handledByRoute && IsDefaultEmptyHttpResponse(context)) {
                context.StatusCodeNumber = 404;
                context.ReasonPhrase = "Not Found";
                context.Response.Body = "404 Not Found: " + (request.Method ?? "GET") + " " + (request.Path ?? "/");
                context.Response.ContentType = "text/plain";
            }

            EnsureErrorResponseBody(context);
            if (context.Response.StreamFile != null && !string.IsNullOrWhiteSpace(context.Response.StreamFile.FilePath) && streamCallbacks != null) {
                ApplyDefaultDispatchedResponseHeaders(context.Response);
                bool isHead = string.Equals(context.Request?.Method, "HEAD", StringComparison.OrdinalIgnoreCase);
                StreamFileToDispatchCallbacks(context.Request, context.Response, context.Response.StreamFile, streamCallbacks, isHead, cancellationToken);
                return new HttpDispatchResult {
                    Context = context,
                    Streamed = true,
                    BodyBytes = Array.Empty<byte>()
                };
            }

            return FinalizeDispatchedHttpResponse(context, streamed: false);
        }

        private void ApplyDefaultDispatchedResponseHeaders(HttpResponse response) {
            if (response == null || response.Headers == null)
                return;
            if (!response.Headers.ContainsKey("Server"))
                response.Headers["Server"] = "SocketJack";
            if (!response.Headers.ContainsKey("Access-Control-Allow-Origin") && !string.IsNullOrWhiteSpace(Options?.HttpDefaultCorsOrigin))
                response.Headers["Access-Control-Allow-Origin"] = Options.HttpDefaultCorsOrigin;
            if (!response.Headers.ContainsKey("Date"))
                response.Headers["Date"] = DateTime.UtcNow.ToString("R");
            if (!response.Headers.ContainsKey("Cache-Control") && CacheControl != null)
                response.Headers["Cache-Control"] = CacheControl;
        }


        private HttpDispatchResult FinalizeDispatchedHttpResponse(HttpContext context, bool streamed) {
            ApplyDefaultDispatchedResponseHeaders(context.Response);

            if (!streamed) {
                if (string.Equals(context.Request?.Method, "HEAD", StringComparison.OrdinalIgnoreCase)) {
                    context.Response.BodyBytes = Array.Empty<byte>();
                } else if (context.Response.StreamFile != null && !string.IsNullOrWhiteSpace(context.Response.StreamFile.FilePath)) {
                    context.Response.BodyBytes = File.Exists(context.Response.StreamFile.FilePath)
                        ? ReadFileBytesShared(context.Response.StreamFile.FilePath)
                        : Array.Empty<byte>();
                    context.Response.StreamFile = null;
                }
                context.Response.EnsureBodyBytes();
                ApplyHttpRoutePatternCache(context.Request, context);
            }
            return new HttpDispatchResult {
                Context = context,
                Streamed = streamed,
                BodyBytes = streamed ? Array.Empty<byte>() : (context.Response.BodyBytes ?? Array.Empty<byte>())
            };
        }
        private RouteHandler FindRouteHandler(Dictionary<string, Dictionary<string, RouteHandler>> routes, string method, string path, HttpRequest request, RouteMatchMode matchMode = RouteMatchMode.ExactAndWildcard) {
            if (routes == null || routes.Count == 0 || method == null || path == null)
                return null;

            var handler = FindRouteHandlerForMethod(routes, method, path, request, matchMode);
            if (handler != null)
                return handler;

            if (method.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
                return FindRouteHandlerForMethod(routes, "GET", path, request, matchMode);

            return null;
        }

        private RouteHandler FindRouteHandlerForMethod(Dictionary<string, Dictionary<string, RouteHandler>> routes, string method, string path, HttpRequest request, RouteMatchMode matchMode) {
            if (!routes.TryGetValue(method, out var byPath))
                return null;

            if (matchMode != RouteMatchMode.WildcardOnly && byPath.TryGetValue(path, out var handler))
                return handler;

            if (matchMode == RouteMatchMode.ExactOnly)
                return null;

            foreach (var kv in byPath) {
                var routePath = kv.Key;
                if (!routePath.EndsWith("/*", StringComparison.Ordinal))
                    continue;

                var basePath = routePath.Substring(0, routePath.Length - 2);
                if (!path.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (path.Length <= basePath.Length || path[basePath.Length] != '/')
                    continue;

                var variable = path.Substring(basePath.Length + 1);
                if (!string.IsNullOrEmpty(variable)) {
                    request.PathVariables = new List<string> { variable };
                    return kv.Value;
                }
            }

            return null;
        }

        private StreamRouteHandler ResolveStreamRoute(HttpRequest request) {
            if (request == null || _streamRoutes.Count == 0)
                return null;

            string method = request.Method;
            string path = NormalizePath(request.Path);

            if (method != null && path != null) {
                if (_streamRoutes.TryGetValue(method, out var byPath)) {
                    if (byPath.TryGetValue(path, out var handler))
                        return handler;
                }
            }
            return null;
        }

        private UploadStreamRouteHandler ResolveUploadStreamRoute(HttpRequest request) {
            if (request == null || _uploadStreamRoutes.Count == 0)
                return null;

            string method = request.Method;
            string path = NormalizePath(request.Path);

            if (method != null && path != null) {
                if (_uploadStreamRoutes.TryGetValue(method, out var byPath)) {
                    if (byPath.TryGetValue(path, out var handler))
                        return handler;
                }
            }
            return null;
        }

        private RtmpSession CreateRtmpSession(NetworkConnection conn) {
            var session = new RtmpSession(conn);
            _activeRtmpConnections.TryAdd(conn.ID, session);

            session.OnPublishStart = (app, streamKey) => {
                RtmpPublishHandler handler = null;
                if (!_rtmpPublishRoutes.TryGetValue(app, out handler))
                    _rtmpPublishRoutes.TryGetValue("*", out handler);

                if (handler != null) {
                    var upload = new UploadStream(conn);
                    session.AttachUploadStream(upload);

                    var connId = conn.ID;
                    Task.Run(async () => {
                        try {
                            handler(conn, app, streamKey, upload, default).GetAwaiter().GetResult();
                        } catch {
                        } finally {
                            upload.Complete();
                        }
                    });
                }
            };

            session.OnPublishStop = () => {
                _activeRtmpConnections.TryRemove(conn.ID, out _);
            };

            return session;
        }

        internal EndpointSecurityDecision EvaluateEndpointSecurityHttpRequest(NetworkConnection connection, HttpRequest request) {
            if (!IsEndpointSecurityEnabled())
                return EndpointSecurityDecision.Allow();
            return EndpointSecurity.EvaluateHttpRequest(connection, request, Options.EndpointSecurity);
        }

        internal EndpointSecurityDecision RecordEndpointSecurityHttpResult(NetworkConnection connection, HttpRequest request, int statusCode) {
            if (!IsEndpointSecurityEnabled())
                return EndpointSecurityDecision.Allow();
            return EndpointSecurity.RecordHttpResult(connection, request, statusCode, Options.EndpointSecurity);
        }

        internal EndpointSecurityDecision RecordEndpointSecurityProtocolEvent(NetworkConnection connection, string protocol, int bytes, bool administrative, double baseSeverity, string reason) {
            if (!IsEndpointSecurityEnabled())
                return EndpointSecurityDecision.Allow();
            return EndpointSecurity.RecordProtocolEvent(connection, protocol, bytes, administrative, baseSeverity, reason, Options.EndpointSecurity);
        }

        internal EndpointSecurityDecision RecordEndpointSecuritySocketJackFrame(NetworkConnection connection, int payloadLength) {
            if (!IsEndpointSecurityEnabled())
                return EndpointSecurityDecision.Allow();
            return EndpointSecurity.RecordSocketJackFrame(connection, "SocketJack", payloadLength, Options.EndpointSecurity);
        }

        internal EndpointSecurityDecision RecordEndpointSecurityWebSocketFrame(NetworkConnection connection, int payloadLength) {
            if (!IsEndpointSecurityEnabled())
                return EndpointSecurityDecision.Allow();
            return EndpointSecurity.RecordSocketJackFrame(connection, "WebSocket", payloadLength, Options.EndpointSecurity);
        }

        internal EndpointSecurityDecision RecordEndpointSecurityFilesystemEvent(string clientIp, string path, string operation, string reason) {
            if (!IsEndpointSecurityEnabled())
                return EndpointSecurityDecision.Allow(clientIp);
            return EndpointSecurity.RecordFilesystemEvent(clientIp, path, operation, reason, Options.EndpointSecurity);
        }

        internal void ApplyEndpointSecurityDelay(EndpointSecurityDecision decision) {
            if (decision == null || !decision.Allowed || decision.DelayMilliseconds <= 0)
                return;
            Thread.Sleep(Math.Min(Math.Max(0, decision.DelayMilliseconds), Math.Max(1, Options.EndpointSecurity.MaxThrottleMs)));
        }

        internal void CloseEndpointSecurityBlockedConnection(NetworkConnection connection, EndpointSecurityDecision decision) {
            try { CloseConnection(connection, DisconnectionReason.LocalSocketClosed); } catch { }
        }

        internal static string EndpointSecurityStatusText(EndpointSecurityDecision decision) {
            if (decision == null)
                return "429 Too Many Requests";
            int statusCode = decision.StatusCode > 0 ? decision.StatusCode : 429;
            string reasonPhrase = string.IsNullOrWhiteSpace(decision.ReasonPhrase)
                ? (statusCode == 403 ? "Forbidden" : "Too Many Requests")
                : decision.ReasonPhrase;
            return statusCode.ToString(CultureInfo.InvariantCulture) + " " + reasonPhrase;
        }

        private bool IsEndpointSecurityEnabled() {
            return EndpointSecurity != null && Options != null && Options.EndpointSecurity != null && Options.EndpointSecurity.Enabled;
        }

        private void ApplyEndpointSecurityHttpDecision(HttpContext context, EndpointSecurityDecision decision) {
            if (context == null || decision == null)
                return;
            int statusCode = decision.StatusCode > 0 ? decision.StatusCode : 429;
            context.StatusCodeNumber = statusCode;
            context.ReasonPhrase = string.IsNullOrWhiteSpace(decision.ReasonPhrase)
                ? (statusCode == 403 ? "Forbidden" : "Too Many Requests")
                : decision.ReasonPhrase;
            context.Response.Body = string.IsNullOrWhiteSpace(decision.Message)
                ? "Request blocked by SocketJack endpoint security."
                : decision.Message;
            context.Response.ContentType = "text/plain";
            context.Response.Headers["X-SocketJack-Endpoint-Security"] = decision.IsDisabled ? "disabled" : "throttled";
            if (decision.RetryAfterSeconds > 0)
                context.Response.Headers["Retry-After"] = decision.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        }
        private void WriteHttpAccessLog(NetworkConnection connection, HttpRequest request, HttpContext context, EndpointSecurityDecision securityDecision, long responseBytes, bool streamed) {
            if (Options == null || !Options.HttpAccessLogging)
                return;

            try {
                string directory = string.IsNullOrWhiteSpace(Options.HttpAccessLogDirectory)
                    ? Path.Combine(Path.GetTempPath(), "SocketJack", "Logs")
                    : Options.HttpAccessLogDirectory;
                Directory.CreateDirectory(directory);

                string dateFormat = string.IsNullOrWhiteSpace(Options.HttpAccessLogDateFormat)
                    ? "yyyy-MM-dd"
                    : Options.HttpAccessLogDateFormat;
                string datePart;
                try {
                    datePart = DateTime.Now.ToString(dateFormat, CultureInfo.InvariantCulture);
                } catch {
                    datePart = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                }

                string fileName = SanitizeHttpAccessLogFileName(datePart) + ".log";
                string filePath = Path.Combine(directory, fileName);
                string pathAndQuery = request?.Path ?? "";
                if (!string.IsNullOrEmpty(request?.QueryString))
                    pathAndQuery += "?" + request.QueryString;

                var entry = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
                    ["timestamp"] = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
                    ["remoteIp"] = GetRemoteIpForAccessLog(connection),
                    ["remoteEndpoint"] = GetRemoteEndpointForAccessLog(connection),
                    ["method"] = request?.Method ?? "",
                    ["path"] = pathAndQuery,
                    ["host"] = GetRequestHeaderForAccessLog(request, "Host"),
                    ["status"] = (context?.StatusCodeNumber ?? 0).ToString(CultureInfo.InvariantCulture),
                    ["reason"] = context?.ReasonPhrase ?? "",
                    ["responseBytes"] = Math.Max(0L, responseBytes).ToString(CultureInfo.InvariantCulture),
                    ["streamed"] = streamed ? "true" : "false",
                    ["userAgent"] = GetRequestHeaderForAccessLog(request, "User-Agent"),
                    ["referer"] = GetRequestHeaderForAccessLog(request, "Referer"),
                    ["securityAllowed"] = securityDecision == null || securityDecision.Allowed ? "true" : "false",
                    ["securityDisabled"] = securityDecision != null && securityDecision.IsDisabled ? "true" : "false",
                    ["securityCategory"] = securityDecision?.Category ?? "",
                    ["securityReason"] = securityDecision?.Reason ?? "",
                    ["securityScore"] = (securityDecision?.Score ?? 0.0).ToString("0.###", CultureInfo.InvariantCulture),
                    ["securityDelayMs"] = (securityDecision?.DelayMilliseconds ?? 0).ToString(CultureInfo.InvariantCulture)
                };

                string line = JsonSerializer.Serialize(entry);
                lock (_httpAccessLogLock) {
                    File.AppendAllText(filePath, line + Environment.NewLine, Encoding.UTF8);
                }
            } catch {
            }
        }

        private static string SanitizeHttpAccessLogFileName(string fileName) {
            if (string.IsNullOrWhiteSpace(fileName))
                return DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            var invalid = Path.GetInvalidFileNameChars();
            var chars = fileName.Select(ch => invalid.Contains(ch) ? '-' : ch).ToArray();
            string sanitized = new string(chars).Trim();
            return string.IsNullOrWhiteSpace(sanitized) ? DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : sanitized;
        }

        private static string GetRequestHeaderForAccessLog(HttpRequest request, string name) {
            try {
                if (request?.Headers != null && request.Headers.TryGetValue(name, out string value))
                    return value ?? "";
            } catch {
            }
            return "";
        }

        private static string GetRemoteIpForAccessLog(NetworkConnection connection) {
            try {
                if (connection?.EndPoint?.Address != null)
                    return connection.EndPoint.Address.ToString();
            } catch {
            }
            try {
                if (connection?.Socket?.RemoteEndPoint is System.Net.IPEndPoint ep && ep.Address != null)
                    return ep.Address.ToString();
            } catch {
            }
            return "";
        }

        private static string GetRemoteEndpointForAccessLog(NetworkConnection connection) {
            try {
                if (connection?.Socket?.RemoteEndPoint != null)
                    return connection.Socket.RemoteEndPoint.ToString();
            } catch {
            }
            try {
                return connection?.EndPoint?.ToString() ?? "";
            } catch {
            }
            return "";
        }
        private static bool IsDefaultEmptyHttpResponse(HttpContext context) {
            if (context == null || context.StatusCodeNumber != 200)
                return false;
            return !HasHttpResponseBody(context.Response);
        }

        private static void EnsureErrorResponseBody(HttpContext context) {
            if (context == null || context.StatusCodeNumber < 400)
                return;
            if (context.StatusCodeNumber == 204 || context.StatusCodeNumber == 304)
                return;
            HttpResponse response = context.Response;
            if (response.RawResponseWritten || response.StreamFile != null || HasHttpResponseBody(response))
                return;
            response.Body = context.StatusCodeNumber.ToString(CultureInfo.InvariantCulture) + " " + (context.ReasonPhrase ?? "Error");
            response.ContentType = "text/plain";
        }

        private static bool HasHttpResponseBody(HttpResponse response) {
            if (response == null)
                return false;
            if (response.RawResponseWritten || response.StreamFile != null)
                return true;
            if (response.Body is string text && text.Length > 0)
                return true;
            byte[] body = response.BodyBytes;
            return body != null && body.Length > 0;
        }

        private static string NormalizePath(string path) {
            if (string.IsNullOrEmpty(path))
                return path;
            var q = path.IndexOf('?');
            if (q >= 0)
                path = path.Substring(0, q);
            try {
                if (Uri.TryCreate(path, UriKind.Absolute, out Uri absoluteUri))
                    path = absoluteUri.AbsolutePath;
            } catch { }
            // URL-decode the path portion so percent-encoded segments
            // (e.g. %20, %2B) match the plain-text route registration.
            try { path = Uri.UnescapeDataString(path); } catch { }
            if (path.Length > 1 && path[path.Length - 1] == '/')
                path = path.Substring(0, path.Length - 1);
            return path;
        }

        private static int FindHeaderEnd(List<byte> buffer) {
            for (int i = 0; i <= buffer.Count - 4; i++) {
                if (buffer[i] == (byte)'\r' && buffer[i + 1] == (byte)'\n' &&
                    buffer[i + 2] == (byte)'\r' && buffer[i + 3] == (byte)'\n')
                    return i + 4;
            }
            return -1;
        }

        private static long ExtractContentLength(List<byte> buffer, int hdrEnd) {
            var headerText = Encoding.UTF8.GetString(buffer.GetRange(0, hdrEnd).ToArray());
            foreach (var line in headerText.Split(new[] { "\r\n" }, StringSplitOptions.None)) {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) {
                    if (long.TryParse(line.Substring(15).Trim(), out long cl))
                        return cl;
                }
            }
            return -1;
        }

        private static bool IsChunkedTransfer(List<byte> buffer, int hdrEnd) {
            var headerText = Encoding.UTF8.GetString(buffer.GetRange(0, hdrEnd).ToArray());
            foreach (var line in headerText.Split(new[] { "\r\n" }, StringSplitOptions.None)) {
                if (line.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase)
                    && line.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        private static bool IsChunkedBodyComplete(List<byte> buffer, int bodyStart) {
            int pos = bodyStart;
            while (true) {
                int lineEnd = FindCrlf(buffer, pos);
                if (lineEnd < 0)
                    return false;

                string sizeLine = Encoding.ASCII.GetString(buffer.GetRange(pos, lineEnd - pos).ToArray());
                int extensionIndex = sizeLine.IndexOf(';');
                if (extensionIndex >= 0)
                    sizeLine = sizeLine.Substring(0, extensionIndex);
                if (!int.TryParse(sizeLine.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int chunkSize))
                    return false;

                pos = lineEnd + 2;
                if (chunkSize == 0) {
                    while (true) {
                        int trailerEnd = FindCrlf(buffer, pos);
                        if (trailerEnd < 0)
                            return false;
                        if (trailerEnd == pos)
                            return true;
                        pos = trailerEnd + 2;
                    }
                }

                if (buffer.Count < pos + chunkSize + 2)
                    return false;
                pos += chunkSize;
                if (buffer[pos] != (byte)'\r' || buffer[pos + 1] != (byte)'\n')
                    return false;
                pos += 2;
            }
        }

        private static int FindCrlf(List<byte> buffer, int start) {
            for (int i = Math.Max(0, start); i < buffer.Count - 1; i++) {
                if (buffer[i] == (byte)'\r' && buffer[i + 1] == (byte)'\n')
                    return i;
            }
            return -1;
        }
        private bool IsUploadStreamRoute(List<byte> buffer, int hdrEnd) {
            if (_uploadStreamRoutes.Count == 0) return false;
            int lineEnd = -1;
            for (int i = 0; i < Math.Min(hdrEnd, buffer.Count) - 1; i++) {
                if (buffer[i] == (byte)'\r' && buffer[i + 1] == (byte)'\n') {
                    lineEnd = i;
                    break;
                }
            }
            if (lineEnd <= 0) return false;
            var requestLine = Encoding.UTF8.GetString(buffer.GetRange(0, lineEnd).ToArray());
            var parts = requestLine.Split(' ');
            if (parts.Length < 2) return false;
            var method = parts[0];
            var path = NormalizePath(parts[1]);
            return method != null && path != null &&
                   _uploadStreamRoutes.TryGetValue(method, out var byPath) &&
                   byPath.ContainsKey(path);
        }

        /// <summary>
        /// Returns <see langword="true"/> when the request body appears to be fully
        /// available in <see cref="HttpRequest.BodyBytes"/> (as opposed to a streaming
        /// upload that arrives over multiple reads).
        /// </summary>
        private static bool IsBodyComplete(HttpRequest request) {
            if (request.Headers != null
                && request.Headers.TryGetValue("Content-Length", out var clStr)
                && long.TryParse(clStr, out var cl)
                && cl > 0) {
                return request.BodyBytes != null && request.BodyBytes.Length >= cl;
            }
            // Chunked transfer encoding = streaming, not complete.
            if (request.Headers != null
                && request.Headers.TryGetValue("Transfer-Encoding", out var te)
                && te != null
                && te.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0) {
                return false;
            }
            // No Content-Length and not chunked ï¿½ body is whatever was received.
            return true;
        }

        /// <summary>
        /// Minimum body size in bytes before fixed-length HTTP responses are written in buffered chunks.
        /// Bodies smaller than this are sent with Content-Length in a single body write.
        /// Default is 8 KB.
        /// </summary>
        public int ChunkedThreshold { get; set; } = 8192;

        /// <summary>
        /// Preferred body write size for large fixed-length responses. Default is 4 KB.
        /// </summary>
        public int ChunkedSize { get; set; } = 4096;

        private void ApplyHttpRoutePatternCache(HttpRequest request, HttpContext context) {
            if (request == null || context == null || context.Response == null || Options == null || !Options.EnablePatternCache)
                return;

            string method = request.Method ?? "GET";
            if (!method.Equals("GET", StringComparison.OrdinalIgnoreCase) && !method.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
                return;

            var response = context.Response;
            if (response.RawResponseWritten || response.StreamFile != null)
                return;
            if (context.StatusCodeNumber < 200 || context.StatusCodeNumber >= 300 || context.StatusCodeNumber == 204 || context.StatusCodeNumber == 206 || context.StatusCodeNumber == 304)
                return;
            if (request.Headers != null && request.Headers.ContainsKey("Range"))
                return;
            if (response.Headers != null && response.Headers.ContainsKey("ETag"))
                return;
            if (HttpResponseCacheControlHasNoStore(response))
                return;

            response.EnsureBodyBytes();
            byte[] body = response.BodyBytes ?? Array.Empty<byte>();
            if (body.Length == 0)
                return;

            string hash = SocketJackPatternCache.ComputeSha256Hex(body);
            if (string.IsNullOrWhiteSpace(hash))
                return;

            string etag = "W/\"sjpc-" + hash.Substring(0, Math.Min(16, hash.Length)) + "\"";
            string cacheKey = BuildHttpRoutePatternCacheKey(request);
            bool stable;

            lock (_httpRoutePatternCacheLock) {
                if (!_httpRoutePatternCache.TryGetValue(cacheKey, out HttpRoutePatternCacheEntry entry) || !string.Equals(entry.Hash, hash, StringComparison.Ordinal)) {
                    if (entry != null)
                        RemoveHttpRoutePatternCacheBodyLocked(entry);
                    entry = new HttpRoutePatternCacheEntry {
                        Key = cacheKey,
                        Hash = hash,
                        StableCount = 1,
                        LastUsed = ++_httpRoutePatternCacheSequence
                    };
                    _httpRoutePatternCache[cacheKey] = entry;
                } else {
                    entry.StableCount++;
                    entry.LastUsed = ++_httpRoutePatternCacheSequence;
                }

                StoreHttpRoutePatternBodyLocked(entry, body);
                stable = entry.StableCount >= Math.Max(1, Options.PatternCachePromotionThreshold);
            }

            if (response.Headers == null)
                response.Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            response.Headers["ETag"] = etag;

            if (!stable)
                return;

            if (!response.Headers.ContainsKey("Cache-Control"))
                response.Headers["Cache-Control"] = "private, max-age=0, must-revalidate";

            if (request.Headers != null && request.Headers.TryGetValue("If-None-Match", out string ifNoneMatch) && HttpEtagMatches(ifNoneMatch, etag)) {
                context.StatusCodeNumber = 304;
                context.ReasonPhrase = "Not Modified";
                response.BodyBytes = Array.Empty<byte>();
                response.Headers.Remove("Content-Length");
                response.Headers.Remove("Content-Encoding");
            }
        }

        private void StoreHttpRoutePatternBodyLocked(HttpRoutePatternCacheEntry entry, byte[] body) {
            if (entry == null)
                return;

            int size = body?.Length ?? 0;
            int minBytes = Math.Max(1, Options.PatternCacheMinimumBytes);
            int maxBytes = Math.Max(1, Options.PatternCacheMaximumBytes);
            if (size < minBytes || size > maxBytes) {
                RemoveHttpRoutePatternCacheBodyLocked(entry);
                return;
            }

            RemoveHttpRoutePatternCacheBodyLocked(entry);
            entry.Body = new byte[size];
            Buffer.BlockCopy(body, 0, entry.Body, 0, size);
            entry.Size = size;
            _httpRoutePatternCacheBytes += size;
            EvictHttpRoutePatternBodiesLocked(maxBytes);
        }

        private void RemoveHttpRoutePatternCacheBodyLocked(HttpRoutePatternCacheEntry entry) {
            if (entry == null || entry.Body == null)
                return;
            _httpRoutePatternCacheBytes -= entry.Size;
            if (_httpRoutePatternCacheBytes < 0)
                _httpRoutePatternCacheBytes = 0;
            entry.Body = null;
            entry.Size = 0;
        }

        private void EvictHttpRoutePatternBodiesLocked(int maxBytes) {
            while (_httpRoutePatternCacheBytes > maxBytes) {
                var victim = _httpRoutePatternCache.Values
                    .Where(entry => entry.Body != null)
                    .OrderBy(entry => entry.LastUsed)
                    .FirstOrDefault();
                if (victim == null)
                    break;
                RemoveHttpRoutePatternCacheBodyLocked(victim);
            }
        }

        private static bool HttpResponseCacheControlHasNoStore(HttpResponse response) {
            if (response?.Headers == null || !response.Headers.TryGetValue("Cache-Control", out string cacheControl) || string.IsNullOrWhiteSpace(cacheControl))
                return false;
            return cacheControl.IndexOf("no-store", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool HttpEtagMatches(string headerValue, string etag) {
            if (string.IsNullOrWhiteSpace(headerValue) || string.IsNullOrWhiteSpace(etag))
                return false;
            foreach (string rawPart in headerValue.Split(',')) {
                string part = rawPart.Trim();
                if (part == "*")
                    return true;
                if (string.Equals(part, etag, StringComparison.Ordinal))
                    return true;
                if (string.Equals(NormalizeWeakEtag(part), NormalizeWeakEtag(etag), StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        private static string NormalizeWeakEtag(string etag) {
            if (string.IsNullOrWhiteSpace(etag))
                return string.Empty;
            etag = etag.Trim();
            return etag.StartsWith("W/", StringComparison.OrdinalIgnoreCase) ? etag.Substring(2).Trim() : etag;
        }

        private static string BuildHttpRoutePatternCacheKey(HttpRequest request) {
            string method = (request?.Method ?? "GET").ToUpperInvariant();
            string host = request?.HostName ?? request?.Host ?? string.Empty;
            string path = NormalizePath(request?.Path ?? "/") ?? "/";
            string query = request?.QueryString ?? string.Empty;
            return method + "|" + host + "|" + path + "?" + query;
        }
        private void ApplyHttpResponseCompression(HttpRequest request, HttpResponse response) {
            if (request == null || response == null || !Options.UseHttpCompression)
                return;

            response.EnsureBodyBytes();
            byte[] body = response.BodyBytes;
            if (!ShouldCompressHttpResponse(request, response, body))
                return;

            string encoding = SelectHttpCompressionEncoding(request);
            if (string.IsNullOrWhiteSpace(encoding))
                return;

            byte[] compressed = CompressHttpBody(body, encoding);
            if (compressed == null || compressed.Length <= 0 || compressed.Length >= body.Length)
                return;

            response.BodyBytes = compressed;
            response.Headers["Content-Encoding"] = encoding;
            response.Headers["Vary"] = AppendHeaderToken(response.Headers.TryGetValue("Vary", out string vary) ? vary : null, "Accept-Encoding");
            response.Headers.Remove("Content-MD5");
            response.Headers.Remove("Content-Length");
        }

        private bool ShouldCompressHttpResponse(HttpRequest request, HttpResponse response, byte[] body) {
            if (body == null || body.Length < Math.Max(1, Options.HttpCompressionMinimumBytes))
                return false;
            if (response.StatusCodeNumber < 200 || response.StatusCodeNumber == 204 || response.StatusCodeNumber == 304)
                return false;
            if (request.Method != null && request.Method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
                return false;
            if (response.Headers != null && response.Headers.ContainsKey("Content-Encoding"))
                return false;

            string contentType = response.ContentType ?? "";
            if (response.Headers != null && response.Headers.TryGetValue("Content-Type", out string headerContentType))
                contentType = headerContentType;
            return IsCompressibleContentType(contentType, response.Path);
        }

        private static string SelectHttpCompressionEncoding(HttpRequest request) {
            if (request?.Headers == null || !request.Headers.TryGetValue("Accept-Encoding", out string acceptEncoding))
                return null;

            if (AcceptsHttpEncoding(acceptEncoding, "gzip"))
                return "gzip";
            if (AcceptsHttpEncoding(acceptEncoding, "deflate"))
                return "deflate";
            return null;
        }

        private static bool AcceptsHttpEncoding(string acceptEncoding, string encoding) {
            if (string.IsNullOrWhiteSpace(acceptEncoding) || string.IsNullOrWhiteSpace(encoding))
                return false;

            foreach (string part in acceptEncoding.Split(',')) {
                string value = part.Trim();
                if (value.Length == 0)
                    continue;
                string[] pieces = value.Split(';');
                string name = pieces[0].Trim();
                if (!name.Equals(encoding, StringComparison.OrdinalIgnoreCase) && name != "*")
                    continue;

                double q = 1;
                for (int i = 1; i < pieces.Length; i++) {
                    string parameter = pieces[i].Trim();
                    if (parameter.StartsWith("q=", StringComparison.OrdinalIgnoreCase) &&
                        double.TryParse(parameter.Substring(2), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double parsed)) {
                        q = parsed;
                    }
                }
                return q > 0;
            }
            return false;
        }

        private static byte[] CompressHttpBody(byte[] body, string encoding) {
            try {
                using (var output = new MemoryStream()) {
                    Stream compressor = encoding.Equals("deflate", StringComparison.OrdinalIgnoreCase)
                        ? (Stream)new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true)
                        : new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true);
                    using (compressor) {
                        compressor.Write(body, 0, body.Length);
                    }
                    return output.ToArray();
                }
            } catch {
                return null;
            }
        }

        private static bool IsCompressibleContentType(string contentType, string path) {
            contentType = (contentType ?? "").ToLowerInvariant();
            int semicolon = contentType.IndexOf(';');
            if (semicolon >= 0)
                contentType = contentType.Substring(0, semicolon).Trim();

            if (contentType.StartsWith("text/", StringComparison.Ordinal))
                return true;
            if (contentType == "application/json" ||
                contentType == "application/javascript" ||
                contentType == "application/xml" ||
                contentType == "application/xhtml+xml" ||
                contentType == "application/wasm" ||
                contentType.EndsWith("+json", StringComparison.Ordinal) ||
                contentType.EndsWith("+xml", StringComparison.Ordinal))
                return true;

            string extension = Path.GetExtension(path ?? "").ToLowerInvariant();
            switch (extension) {
                case ".html":
                case ".htm":
                case ".css":
                case ".js":
                case ".json":
                case ".xml":
                case ".svg":
                case ".txt":
                case ".csv":
                case ".wasm":
                    return true;
                default:
                    return false;
            }
        }

        private static string AppendHeaderToken(string headerValue, string token) {
            if (string.IsNullOrWhiteSpace(token))
                return headerValue ?? "";
            if (string.IsNullOrWhiteSpace(headerValue))
                return token;
            foreach (string part in headerValue.Split(',')) {
                if (part.Trim().Equals(token, StringComparison.OrdinalIgnoreCase))
                    return headerValue;
            }
            return headerValue + ", " + token;
        }

        private void Disposing() {
            OnDisposing -= Disposing;
            this.OnReceive -= GetRequestAsync;
            _requestBuffers.Clear();
            foreach (var kv in _activeUploadConnections) {
                kv.Value.Complete();
            }
            _activeUploadConnections.Clear();
            foreach (var kv in _activeRtmpConnections) {
                kv.Value.Stop();
            }
            _activeRtmpConnections.Clear();
        }

        /// <summary>
        /// Cleans up per-connection HTTP state (buffers, active streams, uploads).
        /// Called on client disconnection.
        /// </summary>
        internal void CleanupHttpConnection(Guid connId) {
            _requestBuffers.TryRemove(connId, out _);
            _activeStreamConnections.TryRemove(connId, out _);
            if (_activeUploadConnections.TryRemove(connId, out var upload))
                upload.Complete();
        }

        private void OnHttpClientDisconnected(DisconnectedEventArgs e) {
            if (e.Connection != null)
                CleanupHttpConnection(e.Connection.ID);
        }

        public HttpServer(int Port, string Name = "HttpServer") : base(Port, Name) {
            Options.UseTerminatedStreams = false;
            Options.UsePeerToPeer = false;
            OnDisposing += Disposing;
            this.OnReceive += GetRequestAsync;
            this.ClientDisconnected += OnHttpClientDisconnected;
        }

        // Local callback registry mirroring TcpBase behavior
        new private Dictionary<Type, List<Action<IReceivedEventArgs>>> TypeCallbacks = new Dictionary<Type, List<Action<IReceivedEventArgs>>>();
        private Dictionary<Delegate, Action<IReceivedEventArgs>> CallbackMap = new Dictionary<Delegate, Action<IReceivedEventArgs>>();

        new public void RegisterCallback<T>(Action<ReceivedEventArgs<T>> Action) {
            var Type = typeof(T);
            Options.Whitelist.Add(Type);
            Action<IReceivedEventArgs> wrapped = e => Action((ReceivedEventArgs<T>)e);
            lock (TypeCallbacks) {
                if (TypeCallbacks.ContainsKey(Type)) {
                    TypeCallbacks[Type].Add(wrapped);
                } else {
                    TypeCallbacks[Type] = new List<Action<IReceivedEventArgs>>() { wrapped };
                }
                CallbackMap[Action] = wrapped;
            }
            if (!Options.Whitelist.Contains(Type)) Options.Whitelist.Add(Type);
        }

        new public void RemoveCallback<T>(Action<ReceivedEventArgs<T>> Action) {
            var Type = typeof(T);
            lock (TypeCallbacks) {
                if (CallbackMap.TryGetValue(Action, out var wrapped)) {
                    if (TypeCallbacks.ContainsKey(Type)) {
                        TypeCallbacks[Type].Remove(wrapped);
                        if (TypeCallbacks[Type].Count == 0) {
                            TypeCallbacks.Remove(Type);
                            if (Options.Whitelist.Contains(Type)) Options.Whitelist.Remove(Type);
                        }
                    }
                    CallbackMap.Remove(Action);
                }
            }
        }

        new public void RemoveCallback<T>() {
            var Type = typeof(T);
            lock (TypeCallbacks) {
                if (TypeCallbacks.ContainsKey(Type)) {
                    var wrappers = TypeCallbacks[Type];
                    var keysToRemove = new List<Delegate>();
                    foreach (var kv in CallbackMap) {
                        if (wrappers.Contains(kv.Value)) keysToRemove.Add(kv.Key);
                    }
                    foreach (var k in keysToRemove) CallbackMap.Remove(k);
                    TypeCallbacks.Remove(Type);
                    if (Options.Whitelist.Contains(Type)) Options.Whitelist.Remove(Type);
                }
            }
        }

        internal void InvokeCallbacks(IReceivedEventArgs e) {
            if (e == null || e.Obj == null) return;
            var t = e.Obj.GetType();
            if (TypeCallbacks.ContainsKey(t)) {
                var list = TypeCallbacks[t];
                for (int i = 0; i < list.Count; i++) {
                    try { list[i].Invoke(e); } catch { }
                }
            }
        }

        public HttpServer(NetworkOptions Options, int Port, string Name = "HttpServer") : base(Options, Port, Name) {
            Options.UseTerminatedStreams = false;
            Options.UsePeerToPeer = false;
            OnDisposing += Disposing;
            this.OnReceive += GetRequestAsync;
            this.ClientDisconnected += OnHttpClientDisconnected;
        }

        /// <summary>
        /// Tracks bytes written directly to an HTTP connection's stream so that
        /// <see cref="NetworkConnection.TotalBytesSent"/> and the per-second
        /// counters remain accurate for HTTP traffic. Also updates the server-level
        /// <see cref="HttpTotalBytesSent"/> aggregate counter.
        /// </summary>
        internal static void TrackHttpBytesSent(NetworkConnection connection, int byteCount) {
            if (byteCount <= 0) return;
            if (connection != null) {
                Interlocked.Add(ref connection._TotalBytesSent, byteCount);
                Interlocked.Add(ref connection.SentBytesCounter, byteCount);
                if (connection.Parent is HttpServer httpServer) {
                    Interlocked.Add(ref httpServer._httpTotalBytesSent, byteCount);
                }
            }
        }

        private bool TryApplyPort80HttpsRedirect(HttpContext context) {
            if (context == null || context.Request == null)
                return false;
            if (Options?.UseSsl == true)
                return false;
            if (!EnablePort80HttpsRedirect)
                return false;

            int boundPort = Port;
            try {
                if (boundPort == 0 && Socket?.LocalEndPoint is System.Net.IPEndPoint endpoint)
                    boundPort = endpoint.Port;
            } catch { }

            if (boundPort != 80)
                return false;

            string location = BuildHttpsRedirectLocation(context.Request);
            context.StatusCodeNumber = 308;
            context.ReasonPhrase = "Permanent Redirect";
            context.Response.Body = "Permanent Redirect";
            context.Response.ContentType = "text/plain";
            context.Response.Headers["Location"] = location;
            context.Response.Headers["Cache-Control"] = "no-store";
            return true;
        }

        private static string BuildHttpsRedirectLocation(HttpRequest request) {
            string host = "";
            if (request?.Headers != null)
                request.Headers.TryGetValue("Host", out host);

            host = SanitizeRedirectHost(host);
            if (string.IsNullOrWhiteSpace(host))
                host = "localhost";

            host = StripHttpDefaultPort(host);
            string path = request?.Path;
            if (string.IsNullOrWhiteSpace(path))
                path = "/";
            else if (!path.StartsWith("/", StringComparison.Ordinal))
                path = "/" + path;

            string query = string.IsNullOrEmpty(request?.QueryString) ? "" : "?" + request.QueryString;
            return "https://" + host + path + query;
        }

        private static string SanitizeRedirectHost(string host) {
            host = (host ?? "").Trim();
            int cr = host.IndexOf('\r');
            int lf = host.IndexOf('\n');
            int cut = -1;
            if (cr >= 0)
                cut = cr;
            if (lf >= 0 && (cut < 0 || lf < cut))
                cut = lf;
            return cut >= 0 ? host.Substring(0, cut).Trim() : host;
        }

        private static string StripHttpDefaultPort(string host) {
            if (string.IsNullOrWhiteSpace(host))
                return host;

            if (host.StartsWith("[", StringComparison.Ordinal)) {
                int close = host.IndexOf(']');
                if (close > 0 && host.Length == close + 4 && host.Substring(close + 1).Equals(":80", StringComparison.OrdinalIgnoreCase))
                    return host.Substring(0, close + 1);
                return host;
            }

            int colon = host.LastIndexOf(':');
            if (colon > 0 && host.IndexOf(':') == colon && host.Substring(colon).Equals(":80", StringComparison.OrdinalIgnoreCase))
                return host.Substring(0, colon);
            return host;
        }
        private bool TryProxyHostRequest(HttpContext context, NetworkConnection connection, HttpRequest request) {
            var target = TryGetHostMapping(_hostProxyMappings, GetRequestHostName(request));
            if (target == null) return false;
            if (HasMappedRouteForRequest(request)) return false;
            try {
                using (var upstream = new System.Net.Sockets.TcpClient()) {
                    upstream.NoDelay = true;
                    upstream.ReceiveTimeout = 30000;
                    upstream.SendTimeout = 30000;
                    var connectTask = upstream.ConnectAsync(target.Host, target.Port);
                    if (!connectTask.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Proxy target connection timed out.");
                    connectTask.GetAwaiter().GetResult();
                    Stream upstreamStream = upstream.GetStream();
                    if (target.UseHttps) {
                        var ssl = new System.Net.Security.SslStream(upstreamStream, false, (sender, cert, chain, errors) => true);
                        ssl.AuthenticateAsClient(target.Host);
                        upstreamStream = ssl;
                    }
                    var bodyBytes = request.BodyBytes ?? Array.Empty<byte>();
                    var sb = new StringBuilder();
                    sb.AppendFormat("{0} {1} {2}\r\n", request.Method ?? "GET", BuildProxyRequestTarget(target, request), string.IsNullOrWhiteSpace(request.Version) ? "HTTP/1.1" : request.Version);
                    var originalHost = request.Headers != null && request.Headers.TryGetValue("Host", out var hostHeader) ? hostHeader : request.Host;
                    sb.Append("Host: " + BuildProxyHostHeader(target, originalHost) + "\r\n");
                    if (request.Headers != null) {
                        foreach (var header in request.Headers) {
                            if (!ShouldSkipProxyRequestHeader(header.Key)) sb.Append(header.Key + ": " + header.Value + "\r\n");
                        }
                    }
                    if (!string.IsNullOrWhiteSpace(originalHost)) sb.Append("X-Forwarded-Host: " + SanitizeRedirectHost(originalHost) + "\r\n");
                    try { sb.Append("X-Forwarded-For: " + connection?.EndPoint?.Address?.ToString() + "\r\n"); } catch { }
                    sb.Append("X-Forwarded-Proto: " + (Options?.UseSsl == true ? "https" : "http") + "\r\n");
                    if (bodyBytes.Length > 0) sb.Append("Content-Length: " + bodyBytes.Length + "\r\n");
                    sb.Append("Connection: close\r\n\r\n");
                    var headerBytes = Encoding.UTF8.GetBytes(sb.ToString());
                    upstreamStream.Write(headerBytes, 0, headerBytes.Length);
                    if (bodyBytes.Length > 0) upstreamStream.Write(bodyBytes, 0, bodyBytes.Length);
                    upstreamStream.Flush();
                    var responseStream = connection.Stream;
                    var buffer = new byte[81920];
                    int read;
                    while ((read = upstreamStream.Read(buffer, 0, buffer.Length)) > 0) {
                        responseStream.Write(buffer, 0, read);
                        TrackHttpBytesSent(connection, read);
                    }
                    responseStream.Flush();
                }
                context.Response.RawResponseWritten = true;
                return true;
            } catch (Exception ex) {
                context.StatusCodeNumber = 502;
                context.ReasonPhrase = "Bad Gateway";
                context.Response.ContentType = "text/plain";
                context.Response.Body = "Proxy target failed: " + ex.Message;
                return true;
            }
        }

        private static string BuildProxyRequestTarget(HostProxyTarget target, HttpRequest request) {
            var path = string.IsNullOrWhiteSpace(request?.Path) ? "/" : request.Path;
            if (!path.StartsWith("/", StringComparison.Ordinal)) path = "/" + path;
            if (!string.IsNullOrWhiteSpace(target.BasePath)) path = path == "/" ? target.BasePath + "/" : target.BasePath + path;
            if (!string.IsNullOrEmpty(request?.QueryString)) path += "?" + request.QueryString;
            return path;
        }

        private static string BuildProxyHostHeader(HostProxyTarget target, string originalHost = null) {
            if (target.PreserveHostHeader && !string.IsNullOrWhiteSpace(originalHost))
                return SanitizeRedirectHost(originalHost);

            bool defaultPort = (!target.UseHttps && target.Port == 80) || (target.UseHttps && target.Port == 443);
            return defaultPort ? target.Host : target.Host + ":" + target.Port;
        }

        private bool HasMappedRouteForRequest(HttpRequest request) {
            if (request == null)
                return false;

            string hostName = GetRequestHostName(request);
            string method = request.Method;
            string path = NormalizePath(request.Path);
            if (string.IsNullOrWhiteSpace(method) || string.IsNullOrWhiteSpace(path))
                return false;

            lock (_routesLock) {
                var hostRouteMap = TryGetHostMapping(_hostRoutes, hostName);
                if (FindRouteHandler(hostRouteMap, method, path, request) != null)
                    return true;

                return FindRouteHandler(_routes, method, path, request) != null;
            }
        }
        private static bool ShouldSkipProxyRequestHeader(string headerName) {
            if (string.IsNullOrWhiteSpace(headerName)) return true;
            return headerName.Equals("Host", StringComparison.OrdinalIgnoreCase)
                || headerName.Equals("Connection", StringComparison.OrdinalIgnoreCase)
                || headerName.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase)
                || headerName.Equals("Proxy-Connection", StringComparison.OrdinalIgnoreCase)
                || headerName.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
                || headerName.Equals("Content-Length", StringComparison.OrdinalIgnoreCase);
        }

        internal void GetRequestAsync(ref IReceivedEventArgs e) {
            try {
                if (e == null || e.Obj == null || e.Connection == null)
                    return;

                // If this connection has an active streaming handler (e.g., OBS uploading video),
                // skip HTTP parsing. The raw data is still available to other OnReceive subscribers.
                if (_activeStreamConnections.ContainsKey(e.Connection.ID))
                    return;

                // If this connection has an active upload-stream handler, forward raw data to it.
                if (_activeUploadConnections.TryGetValue(e.Connection.ID, out var activeUpload)) {
                    var uploadBytes = TryGetRequestBytes(e.Obj);
                    if (uploadBytes != null && uploadBytes.Length > 0) {
                        activeUpload.Enqueue(uploadBytes);
                    }
                    return;
                }

                // If this connection has an active RTMP session, forward data to it.
                if (_activeRtmpConnections.TryGetValue(e.Connection.ID, out var rtmpSession)) {
                    var rtmpBytes = TryGetRequestBytes(e.Obj);
                    if (rtmpBytes != null && rtmpBytes.Length > 0)
                        rtmpSession.ProcessData(rtmpBytes);
                    return;
                }

                // Check if we're already buffering an HTTP request for this connection.
                // If so, skip protocol detection and HTTP validation ï¿½ just accumulate data.
                bool isBuffering = _requestBuffers.ContainsKey(e.Connection.ID);

                if (!isBuffering) {
                    // Detect RTMP by its C0 version byte. FFmpeg and OBS commonly
                    // send a complex C1 handshake whose version field is non-zero.
                    if (_rtmpPublishRoutes.Count > 0) {
                        var probeBytes = TryGetRequestBytes(e.Obj);
                        if (probeBytes != null && probeBytes.Length >= 9
                            && probeBytes[0] == 0x03) {
                            var session = CreateRtmpSession(e.Connection);
                            session.ProcessData(probeBytes);
                            return;
                        }
                    }

                    // Only handle raw HTTP requests here.
                    // SocketJack-serialized payloads (e.g., JSON-wrapped objects), ignore them.
                    if (e.Obj is string s) {
                        if (string.IsNullOrWhiteSpace(s))
                            return;
                        var prefix = s.Length > 16 ? s.Substring(0, 16) : s;
                        if (!(prefix.StartsWith("GET ") || prefix.StartsWith("POST ") || prefix.StartsWith("PUT ") || prefix.StartsWith("DELETE ") || prefix.StartsWith("HEAD ") || prefix.StartsWith("OPTIONS ") || prefix.StartsWith("PATCH ") || prefix.StartsWith("TRACE "))) {
                            return;
                        }
                    } else if (e.Obj is byte[] || e.Obj is List<byte> || e.Obj is IEnumerable<byte> || e.Obj is ArraySegment<byte>) {
                        var bytes = TryGetRequestBytes(e.Obj);
                        if (bytes == null || bytes.Length == 0)
                            return;
                        // Fast check for common methods in ASCII
                        if (!LooksLikeHttpRequestPrefix(bytes, bytes.Length))
                            return;
                    } else {
                        // Not a raw HTTP payload.
                        return;
                    }
                }

                // Accumulate incoming bytes into a per-connection buffer so that
                // HTTP requests spanning multiple TCP segments are fully received
                // before parsing. Without this, large POST uploads are parsed from
                // the first segment alone and the connection is closed prematurely.
                var incoming = TryGetRequestBytes(e.Obj);
                if (incoming == null || incoming.Length == 0)
                    return;

                var buffer = _requestBuffers.GetOrAdd(e.Connection.ID, _ => new List<byte>());
                byte[] rawRequestBytes;
                long contentLength;
                lock (buffer) {
                    buffer.AddRange(incoming);

                    // Wait until complete HTTP headers have arrived (\r\n\r\n).
                    int hdrEnd = FindHeaderEnd(buffer);
                    if (hdrEnd == -1)
                        return;

                    // If Content-Length is present, wait for the full body unless
                    // an upload-stream route matches (those start processing with
                    // partial body data and receive the rest incrementally).
                    contentLength = ExtractContentLength(buffer, hdrEnd);
                    bool isUploadStreamRoute = IsUploadStreamRoute(buffer, hdrEnd);
                    if (contentLength > 0 && buffer.Count < hdrEnd + contentLength) {
                        if (!isUploadStreamRoute)
                            return;
                    } else if (contentLength <= 0 && IsChunkedTransfer(buffer, hdrEnd) && !isUploadStreamRoute && !IsChunkedBodyComplete(buffer, hdrEnd)) {
                        return;
                    }

                    rawRequestBytes = buffer.ToArray();
                    buffer.Clear();
                }
                _requestBuffers.TryRemove(e.Connection.ID, out _);

                // Track HTTP received bytes at the server level
                Interlocked.Add(ref _httpTotalBytesReceived, rawRequestBytes.Length);

                var request = ParseHttpRequest(rawRequestBytes);
                var context = new HttpContext {
                    Request = request,
                    // Use the connection that produced the request (from event args)
                    Connection = e.Connection,
                    cancellationToken = default
                };
                request.Context = context;

                var ct = default(CancellationToken);
                var endpointSecurityDecision = EvaluateEndpointSecurityHttpRequest(e.Connection, request);
                if (!endpointSecurityDecision.Allowed) {
                    ApplyEndpointSecurityHttpDecision(context, endpointSecurityDecision);
                    goto WriteHttpResponse;
                }
                ApplyEndpointSecurityDelay(endpointSecurityDecision);

                // If any callbacks registered for deserialized types, attempt to deserialize body and invoke
                if (request.BodyBytes != null && request.BodyBytes.Length > 0) {
                    try {
                        var wrapped = Options.Serializer.Deserialize(request.BodyBytes);
                        if (wrapped != null) {
                            var obj = wrapped.Unwrap(this as ISocket);
                            if (obj != null) {
                                var objType = obj.GetType();
                                if (TypeCallbacks.ContainsKey(objType)) {
                                    var genericType = typeof(ReceivedEventArgs<>).MakeGenericType(objType);
                                    var args = (IReceivedEventArgs)Activator.CreateInstance(genericType);
                                    args.Initialize(this as ISocket, e.Connection, obj, request.BodyBytes.Length);
                                    InvokeCallbacks(args);
                                }
                            }
                        }
                    } catch {
                        // ignore deserialize errors
                    }
                }

                if (!IsHostAllowed(request)) {
                    context.StatusCodeNumber = 421;
                    context.ReasonPhrase = "Misdirected Request";
                    context.Response.Body = "421 Misdirected Request";
                    context.Response.ContentType = "text/plain";
                    goto WriteHttpResponse;
                }

                string requestGateMessage = RequestGate?.Invoke(e.Connection, request);
                if (!string.IsNullOrWhiteSpace(requestGateMessage)) {
                    context.StatusCodeNumber = 403;
                    context.ReasonPhrase = "Forbidden";
                    context.Response.Body = requestGateMessage;
                    context.Response.ContentType = "text/plain";
                    goto WriteHttpResponse;
                }

                if (TryApplyPort80HttpsRedirect(context))
                    goto WriteHttpResponse;
                if (TryProxyHostRequest(context, e.Connection, request))
                    goto WriteHttpResponse;

                // Check for streaming routes first ï¿½ these keep the connection open
                var streamHandler = ResolveStreamRoute(request);
                if (streamHandler != null) {
                    _activeStreamConnections.TryAdd(e.Connection.ID, 0);
                    var conn = e.Connection;
                    var response = context.Response;
                    Task.Factory.StartNew(async () => {
                        var chunked = new ChunkedStream(conn.Stream, response, conn);
                        try {
                            await streamHandler(conn, request, chunked, ct);
                        } catch (Exception ex) {
                            InvokeOnError(conn, ex);
                        } finally {
                            chunked.Finish();
                            WriteHttpAccessLog(conn, request, context, endpointSecurityDecision, 0, true);
                            _activeStreamConnections.TryRemove(conn.ID, out _);
                        }
                    }, TaskCreationOptions.LongRunning);
                    return;
                }

                // Check for upload-stream routes (e.g., OBS video ingest over HTTP POST)
                var uploadHandler = ResolveUploadStreamRoute(request);
                if (uploadHandler != null) {
                    // When a regular Map route also exists for the same method+path,
                    // prefer it if the request body is already fully available (i.e.,
                    // a normal form POST rather than a streaming upload).
                    bool preferRegularRoute = false;
                    if (_routes.Count > 0) {
                        var np = NormalizePath(request.Path);
                        if (request.Method != null && np != null
                            && _routes.TryGetValue(request.Method, out var routeByPath)
                            && routeByPath.ContainsKey(np)) {
                            preferRegularRoute = IsBodyComplete(request);
                        }
                    }

                    if (!preferRegularRoute) {
                        var upload = new UploadStream(e.Connection, contentLength);
                        _activeUploadConnections.TryAdd(e.Connection.ID, upload);

                        // Queue initial body data if present in the first read
                        if (request.BodyBytes != null && request.BodyBytes.Length > 0) {
                            upload.Enqueue(request.BodyBytes);
                        }

                        // If the full body was already received in the initial request,
                        // complete the upload stream so the handler's ReadAsync loop
                        // terminates instead of blocking forever waiting for data.
                        if (IsBodyComplete(request)) {
                            upload.Complete();
                            _activeUploadConnections.TryRemove(e.Connection.ID, out _);
                        }

                        var connId = e.Connection.ID;
                        var conn = e.Connection;
                        var self = this;
                        Task.Run(() => {
                            try {
                                uploadHandler(conn, request, upload, ct);
                            } catch {
                            } finally {
                                upload.Complete();
                                _activeUploadConnections.TryRemove(connId, out _);

                                // Send the response prepared by the upload handler.  Older upload
                                // handlers do not set one, so keep their historical "OK" body.
                                try {
                                    var responseStream = conn.Stream;
                                    if (responseStream != null && !conn.Closed && !conn.Closing) {
                                        var uploadResponse = context.Response;
                                        uploadResponse.EnsureBodyBytes();
                                        if (uploadResponse.BodyBytes == null || uploadResponse.BodyBytes.Length == 0) {
                                            uploadResponse.Body = "OK";
                                            uploadResponse.ContentType = "text/plain";
                                        }
                                        if (!uploadResponse.Headers.ContainsKey("Server"))
                                            uploadResponse.Headers["Server"] = "SocketJack";
                                        if (!uploadResponse.Headers.ContainsKey("Access-Control-Allow-Origin") && !string.IsNullOrWhiteSpace(Options?.HttpDefaultCorsOrigin))
                                            uploadResponse.Headers["Access-Control-Allow-Origin"] = Options.HttpDefaultCorsOrigin;
                                        if (!uploadResponse.Headers.ContainsKey("Date"))
                                            uploadResponse.Headers["Date"] = DateTime.UtcNow.ToString("R");
                                        if (!uploadResponse.Headers.ContainsKey("Connection"))
                                            uploadResponse.Headers["Connection"] = "close";

                                        var (headerBytes, bodyBytes) = uploadResponse.ToBytesWithHeader();
                                        WriteFixedLengthResponse(responseStream, headerBytes, bodyBytes, conn, bodyBytes.Length);
                                        WriteHttpAccessLog(conn, request, context, endpointSecurityDecision, bodyBytes.Length, false);
                                    }
                                } catch { }

                                // Graceful connection close
                                try {
                                    var socket = conn.Socket;
                                    if (socket != null && socket.Connected) {
                                        socket.LingerState = new LingerOption(true, 2);
                                        socket.Shutdown(SocketShutdown.Send);
                                    }
                                } catch { }
                                try { self.CloseConnection(conn, DisconnectionReason.LocalSocketClosed); } catch { }
                            }
                        });
                        return;
                    }
                }

                // Exact routes should win first, but wildcard page fallbacks should not
                // hide mapped files under a more specific static prefix.
                var handledByRoute = TryResolveAndApplyRoute(e.Connection, request, ct, context, RouteMatchMode.ExactOnly);

                if (!handledByRoute) {
                    handledByRoute = TryResolveAndApplyDocumentRoot(context, request);
                }

                // Check file mappings (URL-to-file routes)
                if (!handledByRoute) {
                    var fileNormalizedPath = NormalizePath(request.Path);
                    var hostFileMappings = GetHostFileMappings(request);
                    if ((hostFileMappings != null && TryResolveFileMapping(hostFileMappings, request.Method, fileNormalizedPath, out var fileMapBytes, out var fileMapPath, out var fileMapMime, out var fileMapName, out var fileMapLastMod))
                        || TryResolveFileMapping(request.Method, fileNormalizedPath, out fileMapBytes, out fileMapPath, out fileMapMime, out fileMapName, out fileMapLastMod)) {
                        handledByRoute = true;
                        ApplyServedFileHeaders(context.Response, fileMapMime, fileMapName);
                        if (fileMapLastMod.HasValue)
                            context.Response.Headers["Last-Modified"] = fileMapLastMod.Value.ToString("R");
                        if (!string.IsNullOrWhiteSpace(fileMapPath)) {
                            context.Response.StreamFile = new FileResponse {
                                FilePath = fileMapPath,
                                ContentType = fileMapMime,
                                FileName = fileMapName
                            };
                        } else {
                            context.Response.BodyBytes = fileMapBytes;
                        }
                    }
                }

                // Check mapped directories for static file serving
                if (!handledByRoute) {
                    var normalizedPath = NormalizePath(request.Path);
                    string clientIp = null;
                    string authHeader = null;
                    try { clientIp = e.Connection?.EndPoint?.Address?.ToString(); } catch { }
                    try { if (request?.Headers != null) request.Headers.TryGetValue("Authorization", out authHeader); } catch { }
                    var hostDirectoryMappings = GetHostDirectoryMappings(request);
                    bool downloadDirectoryZip = IsDirectoryZipDownloadRequest(request);
                    if ((hostDirectoryMappings != null && TryResolveDirectoryFile(hostDirectoryMappings, request.Method, normalizedPath, downloadDirectoryZip, clientIp, authHeader, out var fileBytes, out var servedFilePath, out var fileMime, out var servedFileName, out var htAccessResult, out var htAuthRealm, out var htExtraHeaders, out var fileLastModified))
                        || TryResolveDirectoryFile(request.Method, normalizedPath, downloadDirectoryZip, clientIp, authHeader, out fileBytes, out servedFilePath, out fileMime, out servedFileName, out htAccessResult, out htAuthRealm, out htExtraHeaders, out fileLastModified)) {
                        if (htAccessResult == HtAccessResult.Denied) {
                            context.StatusCodeNumber = 403;
                            context.ReasonPhrase = "Forbidden";
                            context.Response.Body = "403 Forbidden";
                            context.Response.ContentType = "text/plain";
                            handledByRoute = true;
                        } else if (htAccessResult == HtAccessResult.AuthRequired) {
                            context.StatusCodeNumber = 401;
                            context.ReasonPhrase = "Unauthorized";
                            context.Response.Body = "401 Unauthorized";
                            context.Response.ContentType = "text/plain";
                            if (context.Response.Headers == null)
                                context.Response.Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                            context.Response.Headers["WWW-Authenticate"] = "Basic realm=\"" + (htAuthRealm ?? "Restricted") + "\"";
                            handledByRoute = true;
                        } else {
                            handledByRoute = true;

                            // Compute ETag and Last-Modified for cache validation
                            string etag = !string.IsNullOrWhiteSpace(servedFilePath)
                                ? BuildStaticFileEtag(servedFilePath)
                                : BuildBodyEtag(fileBytes);

                            if (context.Response.Headers == null)
                                context.Response.Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                            ApplyServedFileHeaders(context.Response, fileMime, servedFileName);

                            if (etag != null)
                                context.Response.Headers["ETag"] = etag;
                            if (fileLastModified.HasValue)
                                context.Response.Headers["Last-Modified"] = fileLastModified.Value.ToString("R");

                            // Handle conditional requests (304 Not Modified)
                            bool notModified = false;
                            if (etag != null && request.Headers.TryGetValue("If-None-Match", out var ifNoneMatch)) {
                                notModified = ifNoneMatch.Trim() == etag || ifNoneMatch.Trim() == "*";
                            }
                            if (!notModified && fileLastModified.HasValue && request.Headers.TryGetValue("If-Modified-Since", out var ifModSince)) {
                                if (DateTime.TryParse(ifModSince, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal, out var sinceDate)) {
                                    // Truncate to second precision for comparison (HTTP dates have no sub-second)
                                    var fileMod = new DateTime(fileLastModified.Value.Ticks - (fileLastModified.Value.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
                                    notModified = fileMod <= sinceDate;
                                }
                            }

                            if (notModified) {
                                context.StatusCodeNumber = 304;
                                context.ReasonPhrase = "Not Modified";
                                context.Response.BodyBytes = Array.Empty<byte>();
                                context.Response.ContentType = fileMime;
                            } else if (!string.IsNullOrWhiteSpace(servedFilePath)) {
                                context.Response.StreamFile = new FileResponse {
                                    FilePath = servedFilePath,
                                    ContentType = fileMime,
                                    FileName = servedFileName
                                };
                                context.Response.ContentType = fileMime;
                            } else {
                                context.Response.BodyBytes = fileBytes;
                                context.Response.ContentType = fileMime;
                            }
                        }
                        if (htExtraHeaders != null && htExtraHeaders.Count > 0) {
                            if (context.Response.Headers == null)
                                context.Response.Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                            foreach (var kv in htExtraHeaders)
                                context.Response.Headers[kv.Key] = kv.Value;
                        }
                    }
                }

                if (!handledByRoute) {
                    handledByRoute = TryResolveAndApplyRoute(e.Connection, request, ct, context, RouteMatchMode.WildcardOnly);
                }

                if (!handledByRoute) {
                    OnHttpRequest?.Invoke(e.Connection, ref context, ct);
                }

                if (!handledByRoute && IsDefaultEmptyHttpResponse(context)) {
                    context.StatusCodeNumber = 404;
                    context.ReasonPhrase = "Not Found";
                    context.Response.Body = "404 Not Found: " + (request.Method ?? "GET") + " " + (request.Path ?? "/");
                    context.Response.ContentType = "text/plain";
                }

WriteHttpResponse:
                EnsureErrorResponseBody(context);
                RecordEndpointSecurityHttpResult(e.Connection, request, context.StatusCodeNumber);
                if (context.Response != null && context.Response.Headers != null) {
                    if (!context.Response.Headers.ContainsKey("Server"))
                        context.Response.Headers["Server"] = "SocketJack";
                    if (!context.Response.Headers.ContainsKey("Access-Control-Allow-Origin") && !string.IsNullOrWhiteSpace(Options?.HttpDefaultCorsOrigin))
                        context.Response.Headers["Access-Control-Allow-Origin"] = Options.HttpDefaultCorsOrigin;
                    if (!context.Response.Headers.ContainsKey("Date"))
                        context.Response.Headers["Date"] = DateTime.UtcNow.ToString("R");
                    if (!context.Response.Headers.ContainsKey("Connection"))
                        context.Response.Headers["Connection"] = "close";
                    if (CacheControl != null && !context.Response.Headers.ContainsKey("Cache-Control"))
                        context.Response.Headers["Cache-Control"] = CacheControl;
                }

                // Disable Nagle's algorithm so the response is pushed to the
                // network immediately rather than waiting for a full TCP segment.
                try { e.Connection.Socket.NoDelay = true; } catch { }

                // Capture the stream reference once ï¿½ a background thread
                // can dispose the socket between property accesses.
                var responseStream = e.Connection.Stream;
                if (responseStream != null && !e.Connection.Closed && !e.Connection.Closing) {
                    if (context.Response.RawResponseWritten) {
                        WriteHttpAccessLog(e.Connection, request, context, endpointSecurityDecision, 0, false);
                        goto CloseHttpConnection;
                    }

                    bool isHead = request.Method != null && request.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase);
                    if (context.Response.StreamFile != null) {
                        long streamedBodyLen = TryGetStreamFileLength(context.Response.StreamFile);
                        WriteHttpAccessLog(e.Connection, request, context, endpointSecurityDecision, streamedBodyLen, true);
                        if (Options.Logging) {
                            try {
                                var ua = request.Headers != null && request.Headers.ContainsKey("User-Agent") ? request.Headers["User-Agent"] : "";
                                var ep = e.Connection.Socket?.RemoteEndPoint?.ToString() ?? "";
                                Log(request.Method + " " + request.Path + " > " + context.StatusCodeNumber + " (" + streamedBodyLen.ToString(CultureInfo.InvariantCulture) + " bytes streamed) UA: " + ua + " - " + ep);
                            } catch { }
                        }
                        try {
                            WriteStreamedFileResponse(responseStream, context.Response, context.Response.StreamFile, e.Connection, isHead, request);
                        } catch (ObjectDisposedException) {
                        } catch (IOException) { }
                        goto CloseHttpConnection;
                    }

                    context.Response.EnsureBodyBytes();
                    ApplyHttpRoutePatternCache(request, context);
                    ApplyHttpResponseCompression(request, context.Response);
                    int bodyLen = context.Response.BodyBytes != null ? context.Response.BodyBytes.Length : 0;
                    if (context.Response.DisableChunkedTransfer && context.Response.Headers != null)
                        context.Response.Headers["Content-Length"] = bodyLen.ToString();

                    WriteHttpAccessLog(e.Connection, request, context, endpointSecurityDecision, bodyLen, false);
                    if (Options.Logging) {
                        try {
                            var ua = request.Headers != null && request.Headers.ContainsKey("User-Agent") ? request.Headers["User-Agent"] : "";
                            var ep = e.Connection.Socket?.RemoteEndPoint?.ToString() ?? "";
                            Log(request.Method + " " + request.Path + " > " + context.StatusCodeNumber + " (" + bodyLen + " bytes) UA: " + ua + " - " + ep);
                        } catch { }
                    }

                    try {
                        if (isHead) {
                            var (hdr, _) = context.Response.ToBytesWithHeader();
                            if (hdr != null && hdr.Length > 0) {
                                responseStream.Write(hdr, 0, hdr.Length);
                                TrackHttpBytesSent(e.Connection, hdr.Length);
                            }
                            responseStream.Flush();
                        } else {
                            var (hdr, body) = context.Response.ToBytesWithHeader();
                            WriteFixedLengthResponse(responseStream, hdr, body, e.Connection, bodyLen > ChunkedThreshold ? Math.Max(ChunkedSize, 64 * 1024) : bodyLen);
                        }
                    } catch (ObjectDisposedException) {
                    } catch (IOException) { }
                }

CloseHttpConnection:
                // Graceful HTTP close sequence:
                // 1. Set linger so the OS waits for the send buffer to drain on Close().
                // 2. Shut down the send direction first ï¿½ this queues a FIN *after*
                //    any remaining response bytes, preventing an RST.
                // 3. Call Close() for resource cleanup.
                try {
                    var socket = e.Connection.Socket;
                    if (socket != null && socket.Connected) {
                        socket.LingerState = new LingerOption(true, 2);
                        socket.Shutdown(SocketShutdown.Send);
                    }
                } catch { }
                try { CloseConnection(e.Connection, DisconnectionReason.LocalSocketClosed); } catch { }
            } catch (Exception ex) {
                InvokeOnError(e.Connection, ex);
                // Ensure the client always receives a valid HTTP response,
                // even when an exception occurs during request processing.
                try {
                    var errStream = e.Connection.Stream;
                    if (errStream != null && !e.Connection.Closed && !e.Connection.Closing) {
                        var errBody = Encoding.UTF8.GetBytes("500 Internal Server Error");
                        var errHeader = Encoding.UTF8.GetBytes(
                            "HTTP/1.1 500 Internal Server Error\r\n" +
                            "Content-Type: text/plain\r\n" +
                            "Content-Length: " + errBody.Length + "\r\n" +
                            "Connection: close\r\n\r\n");
                        errStream.Write(errHeader, 0, errHeader.Length);
                        errStream.Write(errBody, 0, errBody.Length);
                        errStream.Flush();
                        TrackHttpBytesSent(e.Connection, errHeader.Length + errBody.Length);
                    }
                } catch { }
                try {
                    var socket = e.Connection.Socket;
                    if (socket != null && socket.Connected) {
                        socket.LingerState = new LingerOption(true, 2);
                        socket.Shutdown(SocketShutdown.Send);
                    }
                } catch { }
                try { CloseConnection(e.Connection, DisconnectionReason.LocalSocketClosed); } catch { }
            }
        }

        private static void WriteFixedLengthResponse(Stream responseStream, byte[] header, byte[] body, NetworkConnection connection, int bodyChunkSize) {
            int written = 0;
            if (header != null && header.Length > 0) {
                responseStream.Write(header, 0, header.Length);
                written += header.Length;
            }

            if (body != null && body.Length > 0) {
                int chunkSize = bodyChunkSize > 0 ? bodyChunkSize : body.Length;
                for (int offset = 0; offset < body.Length; offset += chunkSize) {
                    int count = Math.Min(chunkSize, body.Length - offset);
                    responseStream.Write(body, offset, count);
                    written += count;
                }
            }

            responseStream.Flush();
            TrackHttpBytesSent(connection, written);
        }

        private const int StreamedFileBufferSize = 1024 * 1024;

        private static FileStream OpenSharedReadStream(string filePath) {
            return new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, StreamedFileBufferSize, FileOptions.SequentialScan);
        }

        private static byte[] ReadFileBytesShared(string filePath) {
            using (var fileStream = OpenSharedReadStream(filePath))
            using (var memory = new MemoryStream()) {
                fileStream.CopyTo(memory);
                return memory.ToArray();
            }
        }

        private sealed class StreamFileRange {
            public bool NotSatisfiable { get; set; }
            public long Start { get; set; }
            public long End { get; set; }
            public long Length { get; set; }
            public long TotalLength { get; set; }
        }

        private static void StreamFileToDispatchCallbacks(HttpRequest request, HttpResponse response, FileResponse fileResponse, HttpStreamDispatchCallbacks callbacks, bool headersOnly, CancellationToken cancellationToken) {
            if (response == null || fileResponse == null || callbacks == null || string.IsNullOrWhiteSpace(fileResponse.FilePath))
                return;

            using (var fileStream = OpenSharedReadStream(fileResponse.FilePath)) {
                StreamFileRange range = ConfigureStreamFileHeaders(request, response, fileResponse, fileStream);
                response.BodyBytes = Array.Empty<byte>();

                callbacks.OnStart?.Invoke(response);
                try {
                    if (!headersOnly && !range.NotSatisfiable && range.Length > 0) {
                        fileStream.Position = range.Start;
                        byte[] buffer = new byte[StreamedFileBufferSize];
                        long remaining = range.Length;
                        while (remaining > 0) {
                            cancellationToken.ThrowIfCancellationRequested();
                            int bytesRead = fileStream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                            if (bytesRead <= 0)
                                break;
                            callbacks.OnChunk?.Invoke(buffer, 0, bytesRead);
                            remaining -= bytesRead;
                        }
                    }
                } finally {
                    callbacks.OnEnd?.Invoke();
                }
            }
        }

        private static StreamFileRange ConfigureStreamFileHeaders(HttpRequest request, HttpResponse response, FileResponse fileResponse, FileStream fileStream) {
            if (response.Headers == null)
                response.Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            long totalLength = fileStream.Length;
            response.Headers["Accept-Ranges"] = "bytes";
            if (!response.Headers.ContainsKey("Last-Modified")) {
                try { response.Headers["Last-Modified"] = File.GetLastWriteTimeUtc(fileResponse.FilePath).ToString("R", CultureInfo.InvariantCulture); } catch { }
            }

            if (request != null && request.Headers != null && request.Headers.TryGetValue("Range", out string rangeHeader) && !string.IsNullOrWhiteSpace(rangeHeader)) {
                if (TryParseSingleByteRange(rangeHeader, totalLength, out long start, out long end)) {
                    long length = end - start + 1;
                    response.StatusCodeNumber = 206;
                    response.ReasonPhrase = "Partial Content";
                    response.Headers["Content-Range"] = "bytes " + start.ToString(CultureInfo.InvariantCulture) + "-" + end.ToString(CultureInfo.InvariantCulture) + "/" + totalLength.ToString(CultureInfo.InvariantCulture);
                    response.Headers["Content-Length"] = length.ToString(CultureInfo.InvariantCulture);
                    return new StreamFileRange { Start = start, End = end, Length = length, TotalLength = totalLength };
                }

                response.StatusCodeNumber = 416;
                response.ReasonPhrase = "Range Not Satisfiable";
                response.Headers["Content-Range"] = "bytes */" + totalLength.ToString(CultureInfo.InvariantCulture);
                response.Headers["Content-Length"] = "0";
                return new StreamFileRange { NotSatisfiable = true, Start = 0, End = -1, Length = 0, TotalLength = totalLength };
            }

            response.Headers["Content-Length"] = totalLength.ToString(CultureInfo.InvariantCulture);
            return new StreamFileRange { Start = 0, End = totalLength > 0 ? totalLength - 1 : -1, Length = totalLength, TotalLength = totalLength };
        }

        private static bool TryParseSingleByteRange(string rangeHeader, long totalLength, out long start, out long end) {
            start = 0;
            end = totalLength > 0 ? totalLength - 1 : -1;

            if (string.IsNullOrWhiteSpace(rangeHeader) || !rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
                return false;

            string spec = rangeHeader.Substring("bytes=".Length).Trim();
            if (spec.Length == 0 || spec.Contains(",", StringComparison.Ordinal))
                return false;

            int dash = spec.IndexOf('-');
            if (dash < 0)
                return false;

            string left = spec.Substring(0, dash).Trim();
            string right = spec.Substring(dash + 1).Trim();
            if (left.Length == 0) {
                if (!long.TryParse(right, NumberStyles.None, CultureInfo.InvariantCulture, out long suffixLength) || suffixLength <= 0 || totalLength <= 0)
                    return false;
                start = suffixLength >= totalLength ? 0 : totalLength - suffixLength;
                end = totalLength - 1;
                return true;
            }

            if (!long.TryParse(left, NumberStyles.None, CultureInfo.InvariantCulture, out start) || start < 0)
                return false;

            if (right.Length == 0) {
                end = totalLength - 1;
            } else if (!long.TryParse(right, NumberStyles.None, CultureInfo.InvariantCulture, out end) || end < start) {
                return false;
            }

            if (totalLength <= 0 || start >= totalLength)
                return false;
            if (end >= totalLength)
                end = totalLength - 1;
            return true;
        }
        private static string BuildBodyEtag(byte[] bytes) {
            if (bytes == null || bytes.Length == 0)
                return null;

            using (var sha = System.Security.Cryptography.SHA256.Create()) {
                var hash = sha.ComputeHash(bytes);
                return "\"" + BitConverter.ToString(hash).Replace("-", "").Substring(0, 16).ToLowerInvariant() + "\"";
            }
        }

        private static string BuildStaticFileEtag(string filePath) {
            try {
                if (string.IsNullOrWhiteSpace(filePath))
                    return null;
                var info = new FileInfo(filePath);
                if (!info.Exists)
                    return null;
                return "\"" + info.Length.ToString("x", CultureInfo.InvariantCulture) + "-" + info.LastWriteTimeUtc.Ticks.ToString("x", CultureInfo.InvariantCulture) + "\"";
            } catch {
                return null;
            }
        }
        private static long TryGetStreamFileLength(FileResponse fileResponse) {
            try {
                if (fileResponse == null || string.IsNullOrWhiteSpace(fileResponse.FilePath))
                    return 0;
                return new FileInfo(fileResponse.FilePath).Length;
            } catch {
                return 0;
            }
        }

        private static void WriteStreamedFileResponse(Stream responseStream, HttpResponse response, FileResponse fileResponse, NetworkConnection connection, bool headersOnly, HttpRequest request) {
            if (responseStream == null || response == null || fileResponse == null || string.IsNullOrWhiteSpace(fileResponse.FilePath))
                return;

            using var fileStream = OpenSharedReadStream(fileResponse.FilePath);
            StreamFileRange range = ConfigureStreamFileHeaders(request, response, fileResponse, fileStream);
            response.BodyBytes = Array.Empty<byte>();

            var (header, _) = response.ToBytesWithHeader();
            if (header != null && header.Length > 0) {
                responseStream.Write(header, 0, header.Length);
                TrackHttpBytesSent(connection, header.Length);
            }

            if (!headersOnly && !range.NotSatisfiable && range.Length > 0) {
                fileStream.Position = range.Start;
                byte[] buffer = new byte[StreamedFileBufferSize];
                long remaining = range.Length;
                while (remaining > 0) {
                    int bytesRead = fileStream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                    if (bytesRead <= 0)
                        break;
                    responseStream.Write(buffer, 0, bytesRead);
                    TrackHttpBytesSent(connection, bytesRead);
                    remaining -= bytesRead;
                }
            }

            responseStream.Flush();
        }
        private static bool LooksLikeHttpRequestPrefix(byte[] bytes, int length) {
            if (bytes == null || length <= 0)
                return false;

            string prefix = Encoding.ASCII.GetString(bytes, 0, Math.Min(length, 8));
            return LooksLikeHttpMethod(prefix, "GET ")
                || LooksLikeHttpMethod(prefix, "POST ")
                || LooksLikeHttpMethod(prefix, "PUT ")
                || LooksLikeHttpMethod(prefix, "DELETE ")
                || LooksLikeHttpMethod(prefix, "HEAD ")
                || LooksLikeHttpMethod(prefix, "OPTIONS ")
                || LooksLikeHttpMethod(prefix, "PATCH ")
                || LooksLikeHttpMethod(prefix, "TRACE ")
                || LooksLikeHttpMethod(prefix, "CONNECT ");
        }

        private static bool LooksLikeHttpMethod(string prefix, string method) {
            return prefix.StartsWith(method, StringComparison.Ordinal)
                || method.StartsWith(prefix, StringComparison.Ordinal);
        }
        private static byte[] TryGetRequestBytes(object obj) {
            if (obj == null)
                return null;

            if (obj is byte[] b)
                return b;

            if (obj is List<byte> lb)
                return lb.ToArray();

            if (obj is IEnumerable<byte> eb)
                return eb.ToArray();

            if (obj is ArraySegment<byte> seg) {
                if (seg.Array == null)
                    return null;
                var outBytes = new byte[seg.Count];
                Buffer.BlockCopy(seg.Array, seg.Offset, outBytes, 0, seg.Count);
                return outBytes;
            }

            if (obj is string s) {
                if (string.IsNullOrEmpty(s))
                    return Array.Empty<byte>();
                return Encoding.UTF8.GetBytes(s);
            }

            return null;
        }

        /// <summary>
        /// Parses a raw HTTP request string into an HttpRequest object.
        /// </summary>
        /// <param name="rawRequest">The raw HTTP request string.</param>
        /// <returns>HttpRequest object with parsed data.</returns>
        public static HttpRequest ParseHttpRequest(byte[] rawRequestBytes) {
            if (rawRequestBytes == null || rawRequestBytes.Length == 0)
                throw new ArgumentException("Request is empty", nameof(rawRequestBytes));

            // Find header/body separator (CRLFCRLF)
            var separator = new byte[] { (byte)'\r', (byte)'\n', (byte)'\r', (byte)'\n' };
            int hdrEnd = -1;
            for (int i = 0; i < rawRequestBytes.Length - 3; i++) {
                if (rawRequestBytes[i] == separator[0] && rawRequestBytes[i + 1] == separator[1] && rawRequestBytes[i + 2] == separator[2] && rawRequestBytes[i + 3] == separator[3]) {
                    hdrEnd = i + 4;
                    break;
                }
            }

            if (hdrEnd == -1)
                hdrEnd = rawRequestBytes.Length; // assume all headers if no body

            var headerBytes = new byte[hdrEnd];
            Array.Copy(rawRequestBytes, 0, headerBytes, 0, hdrEnd);
            string headerText = Encoding.UTF8.GetString(headerBytes);

            using (var reader = new StringReader(headerText)) {
                var request = new HttpRequest();

                var requestLine = reader.ReadLine();
                if (string.IsNullOrWhiteSpace(requestLine))
                    throw new InvalidDataException("Invalid HTTP request line.");

                var parts = requestLine.Split(' ');
                if (parts.Length < 2)
                    throw new InvalidDataException("Invalid HTTP request line.");

                request.Method = parts[0];
                request.Path = parts[1];
                request.Version = parts.Length > 2 ? parts[2] : "HTTP/1.1";

                // Separate query string from path so route matching works with
                // URL-encoded parameters and handlers can access them via
                // request.QueryString / request.QueryParameters.
                var qIdx = request.Path.IndexOf('?');
                if (qIdx >= 0) {
                    request.QueryString = request.Path.Substring(qIdx + 1);
                    request.Path = request.Path.Substring(0, qIdx);
                    ParseQueryParameters(request);
                }

                string line;
                while (!string.IsNullOrEmpty(line = reader.ReadLine())) {
                    var separatorIndex = line.IndexOf(':');
                    if (separatorIndex > 0) {
                        var headerName = line.Substring(0, separatorIndex).Trim();
                        var headerValue = line.Substring(separatorIndex + 1).Trim();
                        request.Headers[headerName] = headerValue;
                    }
                }

                if (request.Headers.TryGetValue("Host", out var requestHost)) {
                    request.Host = SanitizeRedirectHost(requestHost);
                    request.HostName = NormalizeHostName(requestHost);
                }

                // Determine body
                int bodyStart = hdrEnd;
                byte[] body = null;

                if (request.Headers.TryGetValue("Content-Length", out var clVal) && long.TryParse(clVal, out var cl) && cl > 0) {
                    var remaining = rawRequestBytes.Length - bodyStart;
                    var toCopy = (int)Math.Min(cl, remaining);
                    body = new byte[toCopy];
                    Array.Copy(rawRequestBytes, bodyStart, body, 0, toCopy);
                } else if (request.Headers.TryGetValue("Transfer-Encoding", out var te) && te?.ToLowerInvariant().Contains("chunked") == true) {
                    // parse chunked body
                    using (var ms = new MemoryStream()) {
                        int pos = bodyStart;
                        while (pos < rawRequestBytes.Length) {
                            // read chunk size line
                            int lineEnd = -1;
                            for (int i = pos; i < rawRequestBytes.Length - 1; i++) {
                                if (rawRequestBytes[i] == (byte)'\r' && rawRequestBytes[i + 1] == (byte)'\n') {
                                    lineEnd = i;
                                    break;
                                }
                            }
                            if (lineEnd == -1) break;
                            var sizeLine = Encoding.UTF8.GetString(rawRequestBytes, pos, lineEnd - pos);
                            pos = lineEnd + 2;
                            if (!int.TryParse(sizeLine.Split(';')[0].Trim(), System.Globalization.NumberStyles.HexNumber, null, out int chunkSize)) break;
                            if (chunkSize == 0) {
                                // consume trailing CRLF
                                break;
                            }
                            if (pos + chunkSize > rawRequestBytes.Length) chunkSize = rawRequestBytes.Length - pos;
                            ms.Write(rawRequestBytes, pos, chunkSize);
                            pos += chunkSize;
                            // consume CRLF
                            if (pos + 1 < rawRequestBytes.Length && rawRequestBytes[pos] == (byte)'\r' && rawRequestBytes[pos + 1] == (byte)'\n') pos += 2;
                        }
                        body = ms.ToArray();
                    }
                } else if (bodyStart < rawRequestBytes.Length) {
                    // No Content-Length or Transfer-Encoding; capture any remaining bytes
                    // as the body. This covers streaming uploads (e.g., OBS MPEG-TS over POST)
                    // where only partial data is available in the initial network read.
                    var remaining = rawRequestBytes.Length - bodyStart;
                    body = new byte[remaining];
                    Array.Copy(rawRequestBytes, bodyStart, body, 0, remaining);
                }

                request.SetBody(body);
                return request;
            }
        }

        private static void ParseQueryParameters(HttpRequest request) {
            if (string.IsNullOrEmpty(request.QueryString)) return;
            foreach (var pair in request.QueryString.Split('&')) {
                if (string.IsNullOrEmpty(pair)) continue;
                var eqIdx = pair.IndexOf('=');
                string key, value;
                if (eqIdx >= 0) {
                    key = Uri.UnescapeDataString(pair.Substring(0, eqIdx).Replace('+', ' '));
                    value = Uri.UnescapeDataString(pair.Substring(eqIdx + 1).Replace('+', ' '));
                } else {
                    key = Uri.UnescapeDataString(pair.Replace('+', ' '));
                    value = "";
                }
                request.QueryParameters[key] = value;
            }
        }
    }

    public class HttpContext {
        public static string DefaultCharset = "UTF-8";
        // Numeric status and reason phrase
        public int StatusCodeNumber { get; set; } = 200;
        public string ReasonPhrase { get; set; } = "OK";
        public string StatusCode {
            get { return StatusCodeNumber.ToString() + " " + ReasonPhrase; }
            set {
                if (string.IsNullOrEmpty(value)) {
                    StatusCodeNumber = 200;
                    ReasonPhrase = "OK";
                } else {
                    var parts = value.Split(' ', 2);
                    if (parts.Length > 0 && int.TryParse(parts[0], out var n)) {
                        StatusCodeNumber = n;
                        ReasonPhrase = parts.Length > 1 ? parts[1] : string.Empty;
                    } else {
                        StatusCodeNumber = 200;
                        ReasonPhrase = value;
                    }
                }
            }
        }
        public string ContentType {
            get {
                return _ContentType + "; charset=" + DefaultCharset;
            }
            set {
                if (string.IsNullOrWhiteSpace(value)) {
                    _ContentType = "text/json";
                } else {
                    var sc = value.IndexOf(';');
                    _ContentType = sc >= 0 ? value.Substring(0, sc).Trim() : value;
                }
            }
        }
        private string _ContentType = "application/json";
        // Default to standard application/json
        // Note: kept charset appended by ContentType getter
        // This makes HttpServer responses default to JSON where not overridden.
        
        public HttpRequest Request { get; set; }
        public HttpResponse Response { 
            get { 
                if(_Response == null) _Response = CreateResponse();
                return _Response;
            } 
        }
        private HttpResponse _Response;
        public NetworkConnection Connection { get; set; }
        public CancellationToken cancellationToken { get; set; }

        public HttpResponse CreateResponse() {
            return new HttpResponse() {
                Context = this,
                Path = Request.Path,
                Version = Request.Version,
                Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                ContentType = ContentType
            };
        }
    }

    public class HttpRequest {
        public HttpContext Context { get; set; }
        public string Method { get; set; }
        public string Path { get; set; }
        public string Version { get; set; }
        /// <summary>Raw Host header value, sanitized for control characters.</summary>
        public string Host { get; set; }
        /// <summary>Normalized host name without port, suitable for host/subdomain routing.</summary>
        public string HostName { get; set; }
        /// <summary>
        /// The raw query string from the request URL (everything after the <c>?</c>),
        /// or <see langword="null"/> if no query string was present.
        /// </summary>
        public string QueryString { get; set; }
        /// <summary>
        /// URL-decoded key/value pairs parsed from the query string.
        /// Both <c>%XX</c> and <c>+</c> (as space) encodings are handled.
        /// </summary>
        public Dictionary<string, string> QueryParameters { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>
        /// List of path variables extracted from wildcard routes (e.g. /api/*).
        /// </summary>
        public List<string> PathVariables { get; set; } = new List<string>();
        public Dictionary<string, string> Headers { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public long ContentLength {
            get {
                return _ContentLength;
            }
        }
        private long _ContentLength = 0L;
        public string Body { get; set; }
        public byte[] BodyBytes { get; internal set; }

        internal void SetBody(byte[] bodyBytes) {
            if (bodyBytes == null || bodyBytes.Length == 0) {
                Body = null;
                BodyBytes = null;
                _ContentLength = 0;
            } else {
                BodyBytes = bodyBytes;
                Body = IsTextBody() ? Encoding.UTF8.GetString(bodyBytes) : null;
                _ContentLength = bodyBytes.LongLength;
            }
        }

        private bool IsTextBody() {
            if (Headers == null || !Headers.TryGetValue("Content-Type", out string contentType) || string.IsNullOrWhiteSpace(contentType))
                return true;

            contentType = contentType.Split(';')[0].Trim().ToLowerInvariant();
            return contentType.StartsWith("text/", StringComparison.Ordinal)
                || contentType == "application/json"
                || contentType == "application/xml"
                || contentType == "application/x-www-form-urlencoded"
                || contentType.EndsWith("+json", StringComparison.Ordinal)
                || contentType.EndsWith("+xml", StringComparison.Ordinal);
        }
    }

    public class HttpResponse {
        public HttpContext Context { get; set; }
        public string Path { get; set; }
        public string Version { get; set; }
        public int StatusCodeNumber {
            get => Context?.StatusCodeNumber ?? 200;
            set { if (Context != null) Context.StatusCodeNumber = value; }
        }
        public string ReasonPhrase {
            get => Context?.ReasonPhrase ?? "OK";
            set { if (Context != null) Context.ReasonPhrase = value; }
        }
        public Dictionary<string, string> Headers { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public object Body {
            get {
                return _Body;
            }
            set {
                if (value == null) {
                    _Body = string.Empty;
                    _BodyBytes = Array.Empty<byte>();
                } else if (value is string str) {
                    _Body = str;
                    _BodyBytes = null;
                } else {
                    var wrapped = new Wrapper(value, Context.Connection.Parent);
                    _BodyBytes = Context.Connection.Parent.Options.Serializer.Serialize(wrapped);
                    _Body = string.Empty;
                }
            }
        }
        private string _Body = string.Empty;
        public string ContentType {
            get {
                return _ContentType + "; charset=" + HttpContext.DefaultCharset;
            }
            set {
                if (string.IsNullOrWhiteSpace(value)) {
                    _ContentType = "text/json";
                } else {
                    var sc = value.IndexOf(';');
                    _ContentType = sc >= 0 ? value.Substring(0, sc).Trim() : value;
                }
            }
        }
        private string _ContentType = "application/json";

        private byte[] _BodyBytes = null;

        // Raw body bytes for clients that need the original payload.
        public byte[] BodyBytes {
            get {
                EnsureBodyBytes();
                return _BodyBytes;
            }
            set {
                _BodyBytes = value;
                _Body = string.Empty;
            }
        }

        /// <summary>
        /// Sends large responses with a fixed Content-Length instead of chunked transfer encoding.
        /// </summary>
        public bool DisableChunkedTransfer { get; set; }

        /// <summary>
        /// Adds latency-oriented streaming headers for routes that should render chunks immediately.
        /// </summary>
        public bool LowLatencyStreaming { get; set; }

        /// <summary>
        /// Set by advanced handlers that write a complete HTTP response directly to
        /// the connection stream and want the normal response writer to stand down.
        /// </summary>
        public bool RawResponseWritten { get; set; }

        /// <summary>
        /// Optional file response streamed directly from disk without buffering the full body in memory.
        /// </summary>
        public FileResponse StreamFile { get; set; }

        /// <summary>
        /// Builds the full HTTP status line (e.g. <c>HTTP/1.1 200 OK</c>),
        /// always falling back to <c>HTTP/1.1 200 OK</c> when any component is
        /// missing or empty.
        /// </summary>
        internal string StatusLine {
            get {
                var version = string.IsNullOrWhiteSpace(Version) ? "HTTP/1.1" : Version;
                string statusCode;
                if (Context != null) {
                    statusCode = Context.StatusCode;
                    if (string.IsNullOrWhiteSpace(statusCode))
                        statusCode = "200 OK";
                } else {
                    statusCode = "200 OK";
                }
                return version + " " + statusCode;
            }
        }

        internal void EnsureBodyBytes() {
            if (_BodyBytes == null) {
                if (_Body == null)
                    _BodyBytes = Array.Empty<byte>();
                else
                    _BodyBytes = Encoding.UTF8.GetBytes(_Body);
            }
        }

        public override string ToString() {
            EnsureBodyBytes();
            var sb = new StringBuilder();
            sb.Append(StatusLine + "\r\n");
            if (!Headers.ContainsKey("Content-Type"))
                sb.Append("Content-Type: " + (ContentType ?? Context.ContentType) + "\r\n");
            foreach (var h in Headers) {
                sb.Append(h.Key + ": " + h.Value + "\r\n");
            }
            if (!Headers.ContainsKey("Content-Length"))
                sb.Append($"Content-Length: {_BodyBytes.Length}\r\n");
            sb.Append("Connection: close\r\n");
            sb.Append("\r\n");

            // Only append textual body for debugging output.
            // For binary SocketJack wrapped payloads, _Body is intentionally empty.
            if (!string.IsNullOrEmpty(_Body))
                sb.Append(_Body);

            return sb.ToString();
        }

        public byte[] ToBytes() {
            EnsureBodyBytes();
            // Build header bytes and append raw body bytes (binary-safe)
            var headerSb = new StringBuilder();
            headerSb.Append(StatusLine + "\r\n");
            if (!Headers.ContainsKey("Content-Type"))
                headerSb.Append("Content-Type: " + (ContentType ?? Context.ContentType) + "\r\n");
            foreach (var h in Headers) {
                headerSb.Append(h.Key + ": " + h.Value + "\r\n");
            }
            if (!Headers.ContainsKey("Content-Length"))
                headerSb.Append($"Content-Length: {_BodyBytes.Length}\r\n");
            headerSb.Append("Connection: close\r\n");
            headerSb.Append("\r\n");

            var headerBytes = Encoding.UTF8.GetBytes(headerSb.ToString());
            var outBytes = new byte[headerBytes.Length + _BodyBytes.Length];
            Buffer.BlockCopy(headerBytes, 0, outBytes, 0, headerBytes.Length);
            if (_BodyBytes.Length > 0)
                Buffer.BlockCopy(_BodyBytes, 0, outBytes, headerBytes.Length, _BodyBytes.Length);
            return outBytes;
        }

        // New: return header bytes only + raw body bytes separately (binary-safe)
        public (byte[] header, byte[] body) ToBytesWithHeader() {
            EnsureBodyBytes();
            var headerSb = new StringBuilder();
            headerSb.Append(StatusLine + "\r\n");
            if (!Headers.ContainsKey("Content-Type"))
                headerSb.Append("Content-Type: " + (ContentType ?? Context.ContentType) + "\r\n");
            foreach (var h in Headers) {
                headerSb.Append(h.Key + ": " + h.Value + "\r\n");
            }
            if (!Headers.ContainsKey("Content-Length"))
                headerSb.Append($"Content-Length: {_BodyBytes.Length}\r\n");
            headerSb.Append("Connection: close\r\n");
            headerSb.Append("\r\n");

            var headerBytes = Encoding.UTF8.GetBytes(headerSb.ToString());
            return (headerBytes, _BodyBytes);
        }

        /// <summary>
        /// Writes the response to <paramref name="stream"/> using HTTP/1.1 chunked transfer encoding.
        /// Each line of text is sent as its own chunk so the client can render progressively.
        /// For binary (non-text) bodies, falls back to fixed-size chunks.
        /// </summary>
        public void WriteChunkedTo(Stream stream, int chunkSize, NetworkConnection connection = null) {
            EnsureBodyBytes();
            if (chunkSize <= 0) chunkSize = 4096;
            int totalWritten = 0;

            var headerSb = new StringBuilder();
            headerSb.Append(StatusLine + "\r\n");
            if (!Headers.ContainsKey("Content-Type"))
                headerSb.Append("Content-Type: " + (ContentType ?? Context.ContentType) + "\r\n");
            foreach (var h in Headers) {
                headerSb.Append(h.Key + ": " + h.Value + "\r\n");
            }
            headerSb.Append("Transfer-Encoding: chunked\r\n");
            headerSb.Append("Connection: close\r\n");
            headerSb.Append("\r\n");

            var headerBytes = Encoding.UTF8.GetBytes(headerSb.ToString());
            stream.Write(headerBytes, 0, headerBytes.Length);
            stream.Flush();
            totalWritten += headerBytes.Length;

            var crlf = new byte[] { (byte)'\r', (byte)'\n' };

            // Determine if body is text so we can stream line-by-line
            bool isText = !string.IsNullOrEmpty(_Body);

            if (isText) {
                // Stream line-by-line for visible progressive rendering
                using (var reader = new StringReader(_Body)) {
                    string line;
                    while ((line = reader.ReadLine()) != null) {
                        // Re-append the newline that ReadLine consumed
                        var lineBytes = Encoding.UTF8.GetBytes(line + "\n");

                        var sizeHex = Encoding.UTF8.GetBytes(lineBytes.Length.ToString("X") + "\r\n");
                        stream.Write(sizeHex, 0, sizeHex.Length);
                        stream.Write(lineBytes, 0, lineBytes.Length);
                        stream.Write(crlf, 0, 2);
                        stream.Flush();
                        totalWritten += sizeHex.Length + lineBytes.Length + 2;
                    }
                }
            } else {
                // Binary body ï¿½ fall back to fixed-size chunks
                int offset = 0;
                while (offset < _BodyBytes.Length) {
                    int remaining = _BodyBytes.Length - offset;
                    int size = Math.Min(chunkSize, remaining);

                    var sizeHex = Encoding.UTF8.GetBytes(size.ToString("X") + "\r\n");
                    stream.Write(sizeHex, 0, sizeHex.Length);
                    stream.Write(_BodyBytes, offset, size);
                    stream.Write(crlf, 0, 2);
                    stream.Flush();
                    totalWritten += sizeHex.Length + size + 2;

                    offset += size;
                }
            }

            // Terminating chunk: 0\r\n\r\n
            var terminator = Encoding.UTF8.GetBytes("0\r\n\r\n");
            stream.Write(terminator, 0, terminator.Length);
            stream.Flush();
            totalWritten += terminator.Length;

            HttpServer.TrackHttpBytesSent(connection, totalWritten);
        }
    }

    /// <summary>
    /// Represents a binary file response from a route handler.
    /// Return an instance of this from a <see cref="HttpServer.RouteHandler"/> to send
    /// raw bytes (e.g., an image or PDF) with an explicit content type instead of the
    /// default JSON serialization behaviour.
    /// </summary>
    public class FileResponse {
        /// <summary>The raw response body bytes.</summary>
        public byte[] Data { get; set; }
        /// <summary>The MIME content type (e.g., <c>"image/png"</c>). If omitted, the server detects it from <see cref="FileName"/>.</summary>
        public string ContentType { get; set; }
        /// <summary>The file name advertised in the Content-Disposition response header.</summary>
        public string FileName { get; set; }
        /// <summary>Optional source file path streamed directly to the response.</summary>
        public string FilePath { get; set; }

        public FileResponse() { }
        public FileResponse(byte[] data, string contentType) {
            Data = data;
            ContentType = contentType;
        }

        public FileResponse(byte[] data, string contentType, string fileName) {
            Data = data;
            ContentType = contentType;
            FileName = fileName;
        }

        public static FileResponse FromFile(string filePath) {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("File path is required.", nameof(filePath));

            string fullPath = Path.GetFullPath(filePath);
            return new FileResponse {
                FilePath = fullPath,
                ContentType = HttpServer.GetMimeType(fullPath),
                FileName = Path.GetFileName(fullPath)
            };
        }
    }

    /// <summary>
    /// Wraps a network stream with HTTP/1.1 chunked transfer encoding.
    /// Write lines or raw data; each write is sent as an individual chunk and flushed
    /// immediately so the client can render progressively.
    /// Call <see cref="Finish"/> (or let the server call it) to send the terminating chunk.
    /// </summary>
    public class ChunkedStream {
        private readonly Stream _stream;
        private readonly NetworkConnection _connection;
        private readonly object _writeLock = new object();
        private bool _headerSent;
        private bool _finished;
        private readonly HttpResponse _response;
        private readonly Action<HttpResponse> _onStart;
        private readonly Action<byte[], int, int> _onChunk;
        private readonly Action _onEnd;
        private static readonly byte[] Crlf = new byte[] { (byte)'\r', (byte)'\n' };

        internal ChunkedStream(Stream stream, HttpResponse response, NetworkConnection connection = null) {
            _stream = stream;
            _response = response;
            _connection = connection;
        }

        internal ChunkedStream(HttpResponse response, NetworkConnection connection, Action<HttpResponse> onStart, Action<byte[], int, int> onChunk, Action onEnd) {
            _response = response;
            _connection = connection;
            _onStart = onStart;
            _onChunk = onChunk;
            _onEnd = onEnd;
        }
        /// <summary>
        /// Gets or sets the Content-Type header for the chunked response.
        /// Must be set before the first call to <see cref="Write(string)"/>,
        /// <see cref="WriteLine"/>, or <see cref="Write(byte[], int, int)"/>.
        /// </summary>
        public string ContentType {
            get {
                if (_response.Headers.TryGetValue("Content-Type", out var ct))
                    return ct;
                return _response.ContentType;
            }
            set {
                _response.Headers["Content-Type"] = value;
            }
        }

        /// <summary>
        /// Enables low-latency streaming headers for this response.
        /// </summary>
        public bool LowLatencyMode {
            get { return _response?.LowLatencyStreaming ?? false; }
            set { if (_response != null) _response.LowLatencyStreaming = value; }
        }

        /// <summary>
        /// Sets a response header before the first chunk is written.
        /// </summary>
        public void SetHeader(string name, string value) {
            if (string.IsNullOrWhiteSpace(name) || _response == null || _headerSent) return;
            if (value == null)
                _response.Headers.Remove(name);
            else
                _response.Headers[name] = value;
        }

        private void EnsureHeader() {
            if (_headerSent) return;
            _headerSent = true;

            if (_response.LowLatencyStreaming) {
                if (!_response.Headers.ContainsKey("X-Accel-Buffering"))
                    _response.Headers["X-Accel-Buffering"] = "no";
                if (!_response.Headers.ContainsKey("Pragma"))
                    _response.Headers["Pragma"] = "no-cache";
                if (!_response.Headers.ContainsKey("Cache-Control"))
                    _response.Headers["Cache-Control"] = "no-cache, no-store, no-transform";
            }

            if (_stream == null && _onStart != null) {
                _onStart(_response);
                return;
            }

            var sb = new StringBuilder();
            sb.Append(_response.StatusLine + "\r\n");
            if (!_response.Headers.ContainsKey("Content-Type"))
                sb.Append("Content-Type: " + (_response.ContentType ?? _response.Context.ContentType) + "\r\n");
            foreach (var h in _response.Headers) {
                sb.Append(h.Key + ": " + h.Value + "\r\n");
            }
            if (!_response.Headers.ContainsKey("Server"))
                sb.Append("Server: SocketJack\r\n");
            sb.Append("Transfer-Encoding: chunked\r\n");
            sb.Append("Connection: keep-alive\r\n");
            if (!_response.Headers.ContainsKey("Cache-Control"))
                sb.Append("Cache-Control: no-cache, no-store\r\n");
            if (!_response.Headers.ContainsKey("X-Content-Type-Options"))
                sb.Append("X-Content-Type-Options: nosniff\r\n");
            if (!_response.Headers.ContainsKey("Access-Control-Allow-Origin")) {
                string defaultCorsOrigin = _response?.Context?.Connection?.Parent?.Options?.HttpDefaultCorsOrigin;
                if (!string.IsNullOrWhiteSpace(defaultCorsOrigin))
                    sb.Append("Access-Control-Allow-Origin: " + defaultCorsOrigin + "\r\n");
            }
            sb.Append("\r\n");
            var headerBytes = Encoding.UTF8.GetBytes(sb.ToString());
            _stream.Write(headerBytes, 0, headerBytes.Length);
            _stream.Flush();
            HttpServer.TrackHttpBytesSent(_connection, headerBytes.Length);
        }

        /// <summary>
        /// Sends a single line of text (with trailing newline) as one HTTP chunk.
        /// </summary>
        public void WriteLine(string text) {
            lock (_writeLock) {
                if (_finished) return;
                EnsureHeader();
                var data = Encoding.UTF8.GetBytes(text + "\n");
                WriteChunk(data);
            }
        }

        /// <summary>
        /// Sends text as one HTTP chunk without appending a newline.
        /// </summary>
        public void Write(string text) {
            lock (_writeLock) {
                if (_finished) return;
                EnsureHeader();
                var data = Encoding.UTF8.GetBytes(text);
                WriteChunk(data);
            }
        }

        /// <summary>
        /// Sends arbitrary bytes as one HTTP chunk.
        /// </summary>
        public void Write(byte[] data, int offset, int count) {
            lock (_writeLock) {
                if (_finished) return;
                EnsureHeader();
                if (_onChunk != null) {
                    _onChunk(data, offset, count);
                    return;
                }
                var packet = BuildChunkPacket(data, offset, count);
                _stream.Write(packet, 0, packet.Length);
                _stream.Flush();
                HttpServer.TrackHttpBytesSent(_connection, packet.Length);
            }
        }

        private void WriteChunk(byte[] data) {
            if (_onChunk != null) {
                _onChunk(data, 0, data.Length);
                return;
            }
            var packet = BuildChunkPacket(data, 0, data.Length);
            _stream.Write(packet, 0, packet.Length);
            _stream.Flush();
            HttpServer.TrackHttpBytesSent(_connection, packet.Length);
        }

        private static byte[] BuildChunkPacket(byte[] data, int offset, int count) {
            var sizeHex = Encoding.UTF8.GetBytes(count.ToString("X") + "\r\n");
            var packet = new byte[sizeHex.Length + count + Crlf.Length];
            Buffer.BlockCopy(sizeHex, 0, packet, 0, sizeHex.Length);
            if (count > 0)
                Buffer.BlockCopy(data, offset, packet, sizeHex.Length, count);
            Buffer.BlockCopy(Crlf, 0, packet, sizeHex.Length + count, Crlf.Length);
            return packet;
        }

        /// <summary>
        /// Sends the terminating zero-length chunk. Called automatically when the
        /// stream route handler returns.
        /// </summary>
        public void Finish() {
            lock (_writeLock) {
                if (_finished) return;
                _finished = true;
                EnsureHeader();
                if (_onEnd != null) {
                    try { _onEnd(); } catch { }
                    return;
                }
                var terminator = Encoding.UTF8.GetBytes("0\r\n\r\n");
                try {
                    _stream.Write(terminator, 0, terminator.Length);
                    _stream.Flush();
                    HttpServer.TrackHttpBytesSent(_connection, terminator.Length);
                } catch {
                    // stream may already be closed
                }
            }
        }
    }

    /// <summary>
    /// Provides a stream-like interface for reading incoming data chunks on an HTTP
    /// upload-streaming connection (e.g., OBS sending continuous MPEG-TS video via POST).
    /// Data is enqueued by the server as it arrives and can be consumed asynchronously
    /// by the <see cref="HttpServer.UploadStreamRouteHandler"/>.
    /// </summary>
    public class UploadStream : IDisposable {
        private readonly ConcurrentQueue<byte[]> _queue = new ConcurrentQueue<byte[]>();
        private readonly SemaphoreSlim _signal = new SemaphoreSlim(0);
        private readonly NetworkConnection _connection;
        private readonly object _stateLock = new object();
        private readonly long _expectedLength;
        private long _receivedLength;
        private volatile bool _completed;

        internal UploadStream(NetworkConnection connection, long expectedLength = 0) {
            _connection = connection;
            _expectedLength = Math.Max(0, expectedLength);
        }

        /// <summary>
        /// Enqueues a chunk of raw data received from the network.
        /// </summary>
        internal void Enqueue(byte[] data) {
            if (data == null || data.Length == 0) return;

            byte[] queuedData = data;
            lock (_stateLock) {
                if (_completed) return;

                if (_expectedLength > 0) {
                    long remaining = _expectedLength - _receivedLength;
                    if (remaining <= 0) {
                        _completed = true;
                        return;
                    }
                    if (queuedData.LongLength > remaining) {
                        var trimmed = new byte[(int)remaining];
                        Buffer.BlockCopy(queuedData, 0, trimmed, 0, trimmed.Length);
                        queuedData = trimmed;
                    }
                }

                _queue.Enqueue(queuedData);
                _receivedLength += queuedData.LongLength;
                if (_expectedLength > 0 && _receivedLength >= _expectedLength)
                    _completed = true;
            }
            try { _signal.Release(); } catch (ObjectDisposedException) { }
        }

        /// <summary>
        /// Marks the stream as complete. No more data will arrive.
        /// </summary>
        internal void Complete() {
            lock (_stateLock) {
                _completed = true;
            }
            try { _signal.Release(); } catch (ObjectDisposedException) { }
        }

        /// <summary>
        /// <see langword="true"/> when the stream is complete and all queued data has been consumed.
        /// </summary>
        public bool IsCompleted => _completed && _queue.IsEmpty;

        /// <summary>The request body length advertised by Content-Length, or zero for an open-ended stream.</summary>
        public long ExpectedLength => _expectedLength;

        /// <summary>The number of request-body bytes accepted by this upload stream.</summary>
        public long BytesReceived {
            get { lock (_stateLock) return _receivedLength; }
        }

        /// <summary>
        /// The <see cref="NetworkConnection"/> that this upload stream is associated with.
        /// </summary>
        public NetworkConnection Connection => _connection;

        /// <summary>
        /// Asynchronously reads the next chunk of data.
        /// Returns <see langword="null"/> when the stream is complete or the connection is closed.
        /// </summary>
        public async Task<byte[]> ReadAsync(CancellationToken ct = default) {
            while (true) {
                if (_queue.TryDequeue(out var data)) return data;
                if (_completed || _connection.Closed) return null;
                try {
                    // Use timeout to periodically check connection state
                    await _signal.WaitAsync(1000, ct);
                } catch (OperationCanceledException) {
                    return null;
                }
            }
        }

        /// <summary>
        /// Tries to read the next chunk of data synchronously.
        /// Returns <see langword="false"/> when no data is available within the timeout
        /// or the stream is complete.
        /// </summary>
        public bool TryRead(out byte[] data, int timeoutMs = 100) {
            data = null;
            if (_queue.TryDequeue(out data)) return true;
            if (_completed || _connection.Closed) return false;
            if (_signal.Wait(timeoutMs)) {
                return _queue.TryDequeue(out data);
            }
            return false;
        }

        public void Dispose() {
            Complete();
            _signal.Dispose();
        }
    }
}

