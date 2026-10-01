using SocketJack.Compression;
using SocketJack.Extensions;
using SocketJack.Net.P2P;
using SocketJack.Serialization;
using SocketJack.Serialization.Json;
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace SocketJack.Net {

    /// <summary>
    /// Default options for <see langword="TcpClient"/>, <see langword="TcpServer"/>, <see langword="UdpClient"/>, and <see langword="UdpServer"/>.
    /// <para>These options are used by default unless overridden here.</para>
    /// <para>Set before creating any instances of TcpClient, TcpServer, UdpClient, or UdpServer.</para>
    /// <para>Example:</para>
    /// <code>
    /// DefaultOptions.Logging = <see langword="True"/>
    /// </code>
    /// </summary>
    public class NetworkOptions {

        public static NetworkOptions DefaultOptions = new NetworkOptions();

        public static NetworkOptions NewDefault() {
            var source = DefaultOptions;
            var copy = source.Clone<NetworkOptions>();
            copy.Whitelist = source.Whitelist.Copy();
            copy.Blacklist = source.Blacklist.Copy();
            copy.VerifiedAssemblies = source.VerifiedAssemblies.Copy();
            source.Authorization.CopyTo(copy.Authorization);
            copy.EndpointSecurity = source.EndpointSecurity?.Copy();
            copy.UdpReliable = source.UdpReliable?.Snapshot();
            return copy;
        }

        /// <summary>
        /// Serializer for both <see langword="TcpClient"/> and <see langword="TcpServer"/>.
        /// <para>Default is System.Text.Json.</para>
        /// </summary>
        /// <summary>Local receive authorization policy. Independent of SafeMode and never negotiated by a client.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public NetworkAuthorizationOptions Authorization { get; } = new NetworkAuthorizationOptions();

        public ISerializer Serializer { get; set; } = new JsonSerializer();

        /// <summary>Require a client DLL fingerprint handshake before typed network traffic. Default: true.</summary>
        /// <remarks>WARNING: setting SafeMode = false disables the DLL handshake and verified-assembly requirement.
        /// This is not a smart move on an untrusted network. Use only for an explicitly isolated legacy endpoint.
        /// WARNING: client-reported MD5/SHA-256 values are compatibility claims, not remote attestation or authentication.
        /// Use authenticated TLS and application authorization as well. Register callbacks and approved DLL pins before connecting.</remarks>
        public bool SafeMode {
            get => safeMode;
            set {
                // WARNING: do not disable SafeMode to silently work around an invalid or unknown DLL.
                if (!value && safeMode)
                    System.Diagnostics.Trace.TraceWarning("SocketJack: SafeMode=false disables DLL verification. This is unsafe for untrusted clients.");
                safeMode = value;
            }
        }
        private bool safeMode = true;

        /// <summary>Trusted local application DLL pins. Supply SHA-256 values from your trusted release process.
        /// Never populate this registry from a network client. SafeMode requires every application message DLL to be approved.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public VerifiedAssemblyRegistry VerifiedAssemblies { get; set; } = new VerifiedAssemblyRegistry();

        /// <summary>
        /// Compression algorithm for both <see langword="TcpClient"/> and <see langword="TcpServer"/>.
        /// <para>Default is GZip2.</para>
        /// </summary>
        public ICompression CompressionAlgorithm { get; set; } = new GZip2Compression();

        /// <summary>
        /// Output OnConnected, OnDisconnected, and OnConnectionFailed events to Console.
        /// Send and Receive events only logged when LogSendEvents or LogReceiveEvents are set to True.
        /// </summary>
        /// <returns></returns>
        public bool Logging { get; set; } = false;

        /// <summary>
        /// Log sent events to console.
        /// </summary>
        /// <returns></returns>
        public bool LogSendEvents { get; set; } = false;

        /// <summary>
        /// <para>Log received events to console.</para>
        /// </summary>
        /// <returns></returns>
        public bool LogReceiveEvents { get; set; } = false;

        /// <summary>
        /// Log to Debug Output Window.
        /// </summary>
        /// <returns></returns>
        public bool LogToConsole { get; set; } = false;

        /// <summary>
        /// <para>Turns on or off Peer to Peer functionality.</para>
        /// <para>Required to be set before TcpClient.Connect or TcpServer.StartListening.</para>
        /// </summary>
        /// <returns></returns>
        public bool UsePeerToPeer { get; set; } = true;

        /// <summary>
        /// Update the title of the console window with traffic statistics.
        /// </summary>
        /// <returns></returns>
        public bool UpdateConsoleTitle { get; set; } = false;

        /// <summary>
        /// Timespan to attempt to connect to a server.
        /// <para>Default is 3 seconds.</para>
        /// </summary>
        /// <returns></returns>
        public TimeSpan ConnectionTimeout { get; set; } = TimeSpan.FromSeconds(3L);

        /// <summary>
        /// When True the client will automatically retry connection to the last used Host / Port.
        /// </summary>
        /// <returns>False by default.</returns>
        public bool AutoReconnect { get; set; } = false;

        /// <summary>
        /// Maximum concurrent pending connections.
        /// </summary>
        /// <returns>100 is default. Lower to reduce processing time.</returns>
        public int Backlog { get; set; } = 100;

        /// <summary>
        /// Local address used by TCP servers when binding their listening socket.
        /// Defaults to <see cref="IPAddress.Any"/> for the existing listen-on-all behavior.
        /// </summary>
        public IPAddress BindAddress { get; set; } = IPAddress.Any;

        /// <summary>
        /// Maximum buffer size per connection.
        /// </summary>
        /// <remarks>Default is 100MB.</remarks>
        /// <value>Long</value>
        /// <remarks></remarks>
        public int MaximumBufferSize {
            get {
                return _MaximumBufferSize;
            }
            set {
                _MaximumBufferSize = value <= 0 ? 1 : value;
            }
        }
        protected internal int _MaximumBufferSize = 104857600;

        /// <summary>
        /// Maximum queued outbound bytes per connection before the connection is
        /// closed to protect the host from slow-client memory growth.
        /// </summary>
        /// <remarks>Default is 100MB. Set to 0 for unlimited legacy behavior.</remarks>
        public int MaximumSendQueueBytes { get; set; } = 104857600;

        /// <summary>
        /// Maximum receiving bandwidth.
        /// </summary>
        /// <remarks>
        /// <para>Default is 0, which means unlimited. Set a positive value to throttle downloads.</para>
        /// <para>Setting 0 also disables explicit receive chunk throttling.</para>
        /// </remarks>
        /// <value>Integer</value>
        public double MaximumDownloadMbps {
            get {
                return _MaximumDownloadMbps;
            }
            set {
                _MaximumDownloadMbps = value;
                isDownloadBuffered = MaximumDownloadMbps > 0;
                // Calculate bytes per second from Mbps
                MaximumDownloadBytesPerSecond = (int)(MaximumDownloadMbps * 1024 * 1024 / 8);
                // Set buffer to 1/16th of max bytes per second, minimum 32 bytes
                DownloadBufferSize = Math.Max(MaximumDownloadBytesPerSecond / 16, 32);
            }
        }
        internal bool isDownloadBuffered = true;
        protected internal double _MaximumDownloadMbps = 0.0;
        protected internal int MaximumDownloadBytesPerSecond = 0;

        /// <summary>
        /// Download buffer size.
        /// <remarks>Default is 65536 bytes.</remarks>
        /// <value>Integer</value>
        /// <remarks></remarks>
        /// </summary>
        public int DownloadBufferSize { get; set; } = 819200;

        /// <summary>
        /// Maximum Upload bandwidth.
        /// <remarks>
        /// <para>Default is 0, which means unlimited. Set a positive value to throttle uploads.</para>
        /// </remarks>
        /// <value>Integer</value>
        /// <remarks></remarks>
        /// </summary>
        public double MaximumUploadMbps {
            get {
                return _MaximumUploadMbps;
            }
            set {
                _MaximumUploadMbps = value;
                isUploadBuffered = MaximumUploadMbps > 0;
                // Calculate bytes per second from Mbps
                MaximumUploadBytesPerSecond = (int)(MaximumUploadMbps * 1024 * 1024 / 8);
                // Set buffer to 1/16th of max bytes per second, minimum 32 bytes
                UploadBufferSize = Math.Max(MaximumUploadBytesPerSecond / 16, 32);
            }
        }
        internal bool isUploadBuffered = true;
        protected internal double _MaximumUploadMbps = 0.0;
        protected internal int MaximumUploadBytesPerSecond = 0;

        /// <summary>
        /// Upload buffer size.
        /// <remarks>Default is 65536 bytes.</remarks>
        /// <value>Integer</value>
        /// <remarks></remarks>
        /// </summary>
        public int UploadBufferSize { get; set; } = 819200;

        /// <summary>
        /// <para>Use compression for network transfer.</para>
        /// <para>Must be set before connection started.</para>
        /// </summary>
        public bool UseCompression { get; set; } = false;

        /// <summary>
        /// Enables HTTP response compression when a client advertises support with
        /// <c>Accept-Encoding</c>. This is independent of SocketJack object-stream
        /// compression and is safe for browser/API HTTP traffic.
        /// </summary>
        public bool UseHttpCompression { get; set; } = true;

        /// <summary>
        /// Enables repeated-payload pattern caching for SocketJack TCP/UDP object
        /// frames and stable-hash caching for generated HTTP route responses.
        /// </summary>
        public bool EnablePatternCache { get; set; } = true;

        /// <summary>
        /// Minimum serialized payload size before a repeated SocketJack value is
        /// considered large enough to promote into the per-connection pattern cache.
        /// </summary>
        public int PatternCacheMinimumBytes { get; set; } = 4096;

        /// <summary>
        /// Number of times the same serialized payload must be seen on a connection
        /// before the sender stores it remotely and later sends only its key.
        /// </summary>
        public int PatternCachePromotionThreshold { get; set; } = 6;

        /// <summary>
        /// Maximum bytes held by each per-connection pattern cache. Older cached
        /// values are evicted until a new value can fit.
        /// </summary>
        public int PatternCacheMaximumBytes { get; set; } = 32 * 1024 * 1024;

        /// <summary>
        /// Bot filtering and endpoint abuse controls for HTTP, SocketJack,
        /// WebSocket, and filesystem-backed administrative surfaces.
        /// </summary>
        public EndpointSecurityOptions EndpointSecurity { get; set; } = new EndpointSecurityOptions();

        /// <summary>
        /// Writes one HTTP access log entry per completed request.
        /// </summary>
        public bool HttpAccessLogging { get; set; } = true;

        /// <summary>
        /// Directory for daily HTTP access logs. Defaults to a SocketJack folder
        /// under the current user's temporary directory.
        /// </summary>
        public string HttpAccessLogDirectory { get; set; } = Path.Combine(Path.GetTempPath(), "SocketJack", "Logs");

        /// <summary>
        /// Date format used for HTTP access log file names, followed by <c>.log</c>.
        /// </summary>
        public string HttpAccessLogDateFormat { get; set; } = "yyyy-MM-dd";

        /// <summary>
        /// Minimum uncompressed HTTP response size, in bytes, before compression is
        /// considered. Small responses are usually cheaper to send as-is.
        /// </summary>
        public int HttpCompressionMinimumBytes { get; set; } = 1024;

        /// <summary>
        /// Optional default CORS origin for HTTP responses. Null or empty means SocketJack
        /// will not add an Access-Control-Allow-Origin header automatically.
        /// </summary>
        public string HttpDefaultCorsOrigin { get; set; } = null;

        /// <summary>
        /// Send and receive objects with terminator (default is <see langword="true"/>).
        /// <para><see langword="Required"/> to parse objects.
        /// An exception would be used in the case of HttpServer where the terminator is not used.
        /// HttpServer simply reads to the end of the stream.</para>
        /// </summary>
        public bool UseTerminatedStreams { get; set; } = true;

        /// <summary>
        /// Use SSL for network transfer.
        /// </summary>
        public bool UseSsl { get; set; } = false;


        /// <summary>
        /// When enabled, outbound messages are buffered and flushed in large chunks
        /// at the interval specified by <see cref="ChunkingIntervalMs"/>.
        /// <para>This reduces the number of small writes and syscalls at the cost of added latency.</para>
        /// <para>Default is <see langword="true"/>.</para>
        /// </summary>
        public bool Chunking { get; set; } = true;

        /// <summary>
        /// The interval in milliseconds between chunked flushes when <see cref="Chunking"/> is enabled.
        /// <para>Default is 100 ms.</para>
        /// </summary>
        public int ChunkingIntervalMs { get; set; } = 100;

        /// <summary>
        /// When <see langword="true"/>, the library automatically adjusts <see cref="ChunkingIntervalMs"/>
        /// based on measured round-trip latency so chunk flushes stay proportional to network delay.
        /// <para>Requires the built-in ping loop to be active (client-side only).</para>
        /// <para>Default is <see langword="false"/>.</para>
        /// </summary>
        public bool ChunkingAutomaticLatencyScaling { get; set; } = false;

        /// <summary>
        /// Gets or sets the frame rate, in frames per second, for processing operations.
        /// </summary>
        /// <remarks>Setting this property adjusts the timing interval used for frame updates. The value
        /// should be a positive integer to ensure correct timing behavior.
        /// 0 or less = disabled.</remarks>
        public int Fps {
            get {
                return _Fps;
            }
            set {
                _Fps = value;
                if(value <= 0)
                    _timeout = 0;
                else
                    _timeout = 1000.0 / _Fps;
            }
        }
        private int _Fps = 0;
        public double Timeout {
            get { return _timeout; }
        }
        private double _timeout = 0;

        /// <summary>
        /// Types that are allowed to be deserialized.
        /// </summary>
        public TypeList Whitelist { get; internal set; } = new TypeList(new[] {
            // SocketJack types,
            typeof(MetadataKeyValue),
            typeof(PeerAction),
            //typeof(PingObject), Deprecated
            typeof(Ping),
            typeof(Pong),
            typeof(Identifier),
            typeof(Identifier[]),
            typeof(System.Collections.Concurrent.ConcurrentDictionary<string, string>),
            typeof(PeerRedirect),
            typeof(PeerServer),
            typeof(Wrapper),
            typeof(Segment),
            // System serializable types
            typeof(string),
            typeof(int),
            typeof(long),
            typeof(short),
            typeof(byte),
            typeof(bool),
            typeof(double),
            typeof(float),
            typeof(decimal),
            typeof(DateTime),
            typeof(Guid),
            typeof(TimeSpan),
            typeof(byte[]),
            typeof(char),
            typeof(char[]),
            typeof(uint),
            typeof(ulong),
            typeof(ushort),
            typeof(sbyte)
        });

        /// <summary>
        /// Types that are not allowed to be deserialized.
        /// </summary>
        public TypeList Blacklist { get; internal set; } = new TypeList(new[] { typeof(object), typeof(Socket), typeof(NetworkConnection) } );

        #region UDP Options

        public UdpMode UdpMode { get; set; } = UdpMode.UDP;
        public UdpReliableOptions UdpReliable { get; set; } = new UdpReliableOptions();

        /// <summary>
        /// Maximum datagram payload size in bytes for UDP.
        /// <para>Datagrams exceeding this size will be rejected with an error.</para>
        /// <para>Default is 65,507 bytes (maximum safe UDP payload). Lower to ~1,400 for safe MTU.</para>
        /// </summary>
        public int MaxDatagramSize { get; set; } = 65507;

        /// <summary>
        /// Timeout in seconds before a UDP server considers a client disconnected due to inactivity.
        /// <para>Default is 30 seconds.</para>
        /// </summary>
        public int ClientTimeoutSeconds { get; set; } = 30;

        /// <summary>
        /// Size of the receive buffer used by the UDP socket in bytes.
        /// <para>Default is 65,535 bytes (maximum single UDP datagram size including headers).</para>
        /// </summary>
        public int UdpReceiveBufferSize { get; set; } = 65535;

        /// <summary>
        /// When enabled, the UDP socket is configured to allow sending broadcast datagrams.
        /// <para>Default is <see langword="false"/>.</para>
        /// </summary>
        public bool EnableBroadcast { get; set; } = false;

        /// <summary>
        /// Interval in milliseconds between client timeout checks on the UDP server.
        /// <para>Default is 5000 ms (5 seconds).</para>
        /// </summary>
        public int ClientTimeoutCheckIntervalMs { get; set; } = 5000;

        #endregion
    }
}
