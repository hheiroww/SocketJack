using SocketJack.Extensions;
using SocketJack.Net.Database;
using SocketJack.Net.P2P;
using SocketJack.Serialization;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace SocketJack.Net {

    /// <summary>
    /// Well-known protocol names used by <see cref="MutableTcpServer.EnabledProtocols"/>.
    /// Custom protocol handlers use their <see cref="IProtocolHandler.Name"/> value.
    /// </summary>
    public static class MutableTcpProtocols {
        public const string Http = "HTTP";
        public const string SocketJack = "SocketJack";
        public const string WebSocket = "WebSocket";
        public const string Rtmp = "RTMP";
        public const string Tds = "TDS";
    }

    /// <summary>
    /// Controls which remote clients may use an enabled SQL/TDS protocol handler.
    /// </summary>
    public enum SqlRemoteAccessMode {
        /// <summary>Only loopback clients, including localhost, 127.0.0.1, and ::1.</summary>
        LocalOnly,
        /// <summary>Loopback and private/link-local network clients.</summary>
        LocalNetwork,
        /// <summary>Clients from any address. SQL authentication and IP allowlists still apply.</summary>
        Any
    }

    /// <summary>
    /// Access controls for SQL/TDS connections routed by a <see cref="MutableTcpServer"/>.
    /// </summary>
    public sealed class MutableTcpServerSqlOptions {
        /// <summary>
        /// Gets or sets the remote access level. The default is
        /// <see cref="SqlRemoteAccessMode.LocalOnly"/>.
        /// </summary>
        public SqlRemoteAccessMode RemoteAccess { get; set; } = SqlRemoteAccessMode.LocalOnly;

        /// <summary>
        /// Gets the case-insensitive whitelist of individual remote IP addresses that
        /// may connect in addition to the addresses allowed by <see cref="RemoteAccess"/>.
        /// </summary>
        public ISet<string> AllowedRemoteIpAddresses { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        internal bool IsClientAllowed(System.Net.IPAddress address) {
            if (address == null)
                return false;
            if (address.IsIPv4MappedToIPv6)
                address = address.MapToIPv4();
            if (System.Net.IPAddress.IsLoopback(address))
                return true;

            if (AllowedRemoteIpAddresses.Any(value => System.Net.IPAddress.TryParse(value, out var allowed)
                && (allowed.IsIPv4MappedToIPv6 ? allowed.MapToIPv4() : allowed).Equals(address)))
                return true;
            if (RemoteAccess == SqlRemoteAccessMode.Any)
                return true;
            if (RemoteAccess != SqlRemoteAccessMode.LocalNetwork)
                return false;

            byte[] bytes = address.IsIPv4MappedToIPv6 ? address.MapToIPv4().GetAddressBytes() : address.GetAddressBytes();
            if (bytes.Length == 4) {
                return bytes[0] == 10
                    || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                    || (bytes[0] == 192 && bytes[1] == 168)
                    || (bytes[0] == 169 && bytes[1] == 254);
            }

            return bytes.Length == 16 && ((bytes[0] & 0xfe) == 0xfc || (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80));
        }
    }

    /// <summary>
    /// Defines a protocol handler that can detect and process incoming data
    /// on a <see cref="MutableTcpServer"/>.
    /// </summary>
    public interface IProtocolHandler {

        /// <summary>
        /// Display name used for logging.
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Inspects the first bytes received on a connection to determine
        /// whether this handler can process the data.
        /// </summary>
        bool CanHandle(byte[] data);

        /// <summary>
        /// Processes incoming data for a connection assigned to this handler.
        /// </summary>
        void ProcessReceive(MutableTcpServer server, NetworkConnection connection, ref IReceivedEventArgs e);

        /// <summary>
        /// Called when a connection assigned to this handler disconnects.
        /// </summary>
        void OnDisconnected(MutableTcpServer server, NetworkConnection connection);
    }

    /// <summary>
    /// A TCP server that detects the incoming byte format (e.g. HTTP request vs
    /// SocketJack-framed JSON) and routes each connection to the appropriate
    /// <see cref="IProtocolHandler"/>. This allows a single listening port to serve
    /// multiple protocols simultaneously.
    /// <para>
    /// <b>All protocols are disabled by default.</b> Add each required built-in or
    /// custom handler name to <see cref="EnabledProtocols"/> before calling
    /// <see cref="TcpServer.Listen"/>. This property is an explicit whitelist.
    /// Registered custom handlers are evaluated in registration order, with enabled
    /// built-in handlers checked last.
    /// </para>
    /// <para>
    /// SQL/TDS access is local-machine only by default, even after TDS is added to
    /// <see cref="EnabledProtocols"/>. Configure <see cref="SqlOptions"/> to allow
    /// private-network, public, or individually whitelisted remote IP addresses.
    /// </para>
    /// </summary>
    public class MutableTcpServer : HttpServer {

        #region Fields

        private readonly List<IProtocolHandler> _handlers = new List<IProtocolHandler>();
        private readonly ConcurrentDictionary<Guid, IProtocolHandler> _connectionHandlers = new ConcurrentDictionary<Guid, IProtocolHandler>();

        private readonly HttpProtocolHandler _httpHandler;
        private readonly SocketJackProtocolHandler _socketJackHandler;
        private readonly WebSocketProtocolHandler _webSocketHandler;
        private readonly RtmpDelegateHandler _rtmpDelegateHandler = new RtmpDelegateHandler();
        private SocketJack.Net.Database.SqlAdminPanel _sqlAdminPanel;
        private SocketJack.Net.Database.AdminSuite _adminSuite;

        #endregion

        #region Constructors

        /// <summary>
        /// Initializes a new <see cref="MutableTcpServer"/> that listens on the given port.
        /// </summary>
        public MutableTcpServer(int Port, string Name = "MutableTcpServer") : base(Port, Name) {
            Options.UseTerminatedStreams = false;

            // Defer peer initialization until a connection is identified as SocketJack.
            // Without this, NewConnection sends SocketJack-framed peer data to every
            // connection (including HTTP clients), corrupting their response stream.
            DeferPeerInitialization = true;

            // Remove the base HttpServer's receive handler — MutableTcpServer routes
            // HTTP through HttpProtocolHandler via RouteReceive instead.
            this.OnReceive -= GetRequestAsync;

            _httpHandler = new HttpProtocolHandler(this);
            _socketJackHandler = new SocketJackProtocolHandler();
            _webSocketHandler = new WebSocketProtocolHandler();

            this.OnReceive += RouteReceive;
            this.ClientDisconnected += OnClientDisconnected_Cleanup;
        }

        /// <summary>
        /// Initializes a new <see cref="MutableTcpServer"/> with explicit options.
        /// </summary>
        public MutableTcpServer(NetworkOptions Options, int Port, string Name = "MutableTcpServer") : base(Options, Port, Name) {
            Options.UseTerminatedStreams = false;

            // Defer peer initialization until a connection is identified as SocketJack.
            DeferPeerInitialization = true;

            // Remove the base HttpServer's receive handler — MutableTcpServer routes
            // HTTP through HttpProtocolHandler via RouteReceive instead.
            this.OnReceive -= GetRequestAsync;

            _httpHandler = new HttpProtocolHandler(this);
            _socketJackHandler = new SocketJackProtocolHandler();
            _webSocketHandler = new WebSocketProtocolHandler();

            this.OnReceive += RouteReceive;
            this.ClientDisconnected += OnClientDisconnected_Cleanup;
        }

        #endregion

        #region Protocol Registration

        /// <summary>
        /// Gets the case-insensitive whitelist of protocol names this server may route.
        /// <b>The whitelist is empty and every protocol is disabled by default.</b>
        /// Use the constants in <see cref="MutableTcpProtocols"/> for built-in protocols,
        /// or an <see cref="IProtocolHandler.Name"/> for a custom handler.
        /// </summary>
        /// <remarks>
        /// Registering or configuring a handler does not enable it. Add its name here
        /// explicitly, for example <c>server.EnabledProtocols.Add(MutableTcpProtocols.Http)</c>.
        /// Configure before listening; stop the listener and disconnect clients before
        /// changing this collection or SQL options. SQL handlers can take ownership of
        /// an established connection's stream, so changes are not live revocation.
        /// </remarks>
        public ISet<string> EnabledProtocols { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Gets SQL/TDS-specific remote access options. TDS remains disabled until
        /// <see cref="MutableTcpProtocols.Tds"/> is added to <see cref="EnabledProtocols"/>.
        /// </summary>
        public MutableTcpServerSqlOptions SqlOptions { get; } = new MutableTcpServerSqlOptions();

        /// <summary>
        /// Registers a custom protocol handler. Custom handlers are evaluated before
        /// the built-in HTTP and SocketJack handlers.
        /// </summary>
        public void RegisterProtocol(IProtocolHandler handler) {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            _handlers.Add(handler);
        }

        /// <summary>
        /// Removes a previously registered custom protocol handler.
        /// </summary>
        public bool RemoveProtocol(IProtocolHandler handler) {
            return _handlers.Remove(handler);
        }

        /// <summary>
        /// Returns the built-in <see cref="HttpProtocolHandler"/> for configuring HTTP
        /// routes, directory mappings, and other HTTP-specific settings.
        /// </summary>
        public HttpProtocolHandler Http => _httpHandler;

        /// <summary>
        /// Returns the built-in <see cref="SocketJackProtocolHandler"/> for reference.
        /// SocketJack-protocol connections are also handled through the normal
        /// <see cref="NetworkBase.OnReceive"/> event and registered callbacks.
        /// </summary>
        public SocketJackProtocolHandler SocketJack => _socketJackHandler;

        /// <summary>
        /// Gets or sets whether the unified SocketJack Admin Suite is enabled.
        /// The suite is served at <c>/Admin</c> and keeps the legacy SQL Admin
        /// surface available at <c>/sql</c> for compatibility and authentication.
        /// </summary>
        public bool AdminSuiteEnabled {
            get => _adminSuite != null;
            set {
                if (value && _adminSuite == null) {
                    GetOrCreateDataServer();
                    if (!SqlAdminPanelEnabled)
                        SqlAdminPanelEnabled = true;
                    _adminSuite = new SocketJack.Net.Database.AdminSuite(this);
                    _adminSuite.Register();
                } else if (!value && _adminSuite != null) {
                    _adminSuite.Unregister();
                    _adminSuite = null;
                }
            }
        }

        /// <summary>
        /// Returns the <see cref="Database.AdminSuite"/> instance, or
        /// <see langword="null"/> when <see cref="AdminSuiteEnabled"/> is false.
        /// </summary>
        internal SocketJack.Net.Database.AdminSuite AdminSuite => _adminSuite;

        /// <summary>
        /// Returns the built-in <see cref="WebSocketProtocolHandler"/> for configuring
        /// WebSocket-specific settings. WebSocket connections share the same serialization
        /// and callback pipeline as SocketJack connections but use RFC 6455 framing.
        /// </summary>
        public WebSocketProtocolHandler WebSocket => _webSocketHandler;

        /// <summary>
        /// Raised when a connection's first data is identified as SocketJack protocol.
        /// Use this instead of <see cref="TcpServer.ClientConnected"/> when you need
        /// to send SocketJack-framed objects only to SocketJack clients (not HTTP).
        /// </summary>
        public event Action<NetworkConnection> SocketJackClientConnected;

        /// <summary>
        /// Raised when a WebSocket upgrade handshake completes successfully.
        /// Use this to perform initialization for browser-based WebSocket clients.
        /// </summary>
        public event Action<NetworkConnection> WebSocketClientConnected;

        /// <summary>
        /// Gets or sets whether the SQL Admin Panel is enabled.
        /// When <see langword="true"/>, an HTTP-based administration interface
        /// (similar to SQL Server Management Studio) is served at <c>/sql</c>.
        /// The panel authenticates against the <see cref="Database.DataServer.Users"/>
        /// collection of the registered <see cref="Database.TdsProtocolHandler"/>,
        /// automatically creating one if needed.
        /// <para>
        /// Features include an Object Explorer tree, a SQL query editor,
        /// and a results grid.
        /// </para>
        /// </summary>
        public bool SqlAdminPanelEnabled {
            get => _sqlAdminPanel != null;
            set {
                if (value && _sqlAdminPanel == null) {
                    GetOrCreateDataServer(); // Ensure a TdsProtocolHandler is registered
                    _sqlAdminPanel = new SocketJack.Net.Database.SqlAdminPanel(this);
                    _sqlAdminPanel.Register();
                } else if (!value && _sqlAdminPanel != null) {
                    _sqlAdminPanel.Unregister();
                    _sqlAdminPanel = null;
                }
            }
        }

        /// <summary>
        /// Returns the <see cref="Database.SqlAdminPanel"/> instance, or
        /// <see langword="null"/> when <see cref="SqlAdminPanelEnabled"/> is
        /// <see langword="false"/>.
        /// </summary>
        internal SocketJack.Net.Database.SqlAdminPanel SqlAdminPanel => _sqlAdminPanel;

        #endregion

        #region Core Routing

        private void RouteReceive(ref IReceivedEventArgs e) {
            if (e == null || e.Obj == null || e.Connection == null)
                return;

            var connId = e.Connection.ID;

            // Fast path: connection already assigned to a handler.
            if (_connectionHandlers.TryGetValue(connId, out var assigned)) {
                if (!IsProtocolEnabled(assigned)) {
                    e.Connection.CloseConnection();
                    return;
                }
                // SocketJack and WebSocket connections: only intercept raw byte data
                // from the network. Deserialized objects dispatched through HandleReceive
                // flow through the normal OnReceive/callback pipeline, acting like a
                // standard TcpServer.
                if ((assigned == _socketJackHandler || assigned == _webSocketHandler) && !(e.Obj is List<byte>))
                    return;
                if (assigned != _socketJackHandler && assigned != _webSocketHandler && assigned != _httpHandler && assigned != _rtmpDelegateHandler) {
                    byte[] assignedBytes = TryGetRawBytes(e.Obj);
                    var assignedDecision = RecordEndpointSecurityProtocolEvent(e.Connection, assigned.Name, assignedBytes?.Length ?? 0, IsAdministrativeProtocol(assigned), 0.25, "protocol receive");
                    if (!assignedDecision.Allowed) {
                        CloseEndpointSecurityBlockedConnection(e.Connection, assignedDecision);
                        return;
                    }
                    ApplyEndpointSecurityDelay(assignedDecision);
                }
                assigned.ProcessReceive(this, e.Connection, ref e);
                return;
            }

            // First data on this connection — detect the protocol.
            byte[] probe = TryGetRawBytes(e.Obj);
            if (probe == null || probe.Length == 0)
                return;

            // Check custom handlers first (in registration order).
            for (int i = 0; i < _handlers.Count; i++) {
                if (_handlers[i].CanHandle(probe)) {
                    if (!IsProtocolEnabled(_handlers[i])) {
                        e.Connection.CloseConnection();
                        return;
                    }
                    if (_handlers[i] is TdsProtocolHandler && !SqlOptions.IsClientAllowed(e.Connection.EndPoint?.Address)) {
                        e.Connection.CloseConnection();
                        return;
                    }
                    var customDecision = RecordEndpointSecurityProtocolEvent(e.Connection, _handlers[i].Name, probe.Length, IsAdministrativeProtocol(_handlers[i]), 0.5, "protocol probe");
                    if (!customDecision.Allowed) {
                        CloseEndpointSecurityBlockedConnection(e.Connection, customDecision);
                        return;
                    }
                    ApplyEndpointSecurityDelay(customDecision);
                    _connectionHandlers.TryAdd(connId, _handlers[i]);
                    _handlers[i].ProcessReceive(this, e.Connection, ref e);
                    return;
                }
            }

            // Check built-in WebSocket handler before HTTP — a WebSocket upgrade
            // starts as an HTTP GET request, so the HTTP handler would also match.
            if (_webSocketHandler.CanHandle(probe)) {
                if (!IsProtocolEnabled(_webSocketHandler)) {
                    e.Connection.CloseConnection();
                    return;
                }
                _connectionHandlers.TryAdd(connId, _webSocketHandler);
                e.Connection._Protocol = TcpProtocol.WebSocket;
                _webSocketHandler.ProcessReceive(this, e.Connection, ref e);
                return;
            }

            // Check built-in HTTP handler — HTTP requests are short-lived
            // and must be answered promptly.  SocketJack is checked after so that
            // an HTTP client connecting to the same port is never misrouted.
            if (IsProtocolEnabled(_httpHandler) && _httpHandler.CanHandle(probe)) {
                _connectionHandlers.TryAdd(connId, _httpHandler);
                e.Connection._Protocol = TcpProtocol.Http;
                _httpHandler.ProcessReceive(this, e.Connection, ref e);
                return;
            }

            // Check built-in SocketJack handler.
            if (IsProtocolEnabled(_socketJackHandler) && _socketJackHandler.CanHandle(probe)) {
                _connectionHandlers.TryAdd(connId, _socketJackHandler);
                e.Connection._Protocol = TcpProtocol.SocketJack;
                _socketJackHandler.ProcessReceive(this, e.Connection, ref e);
                if (!Options.SafeMode) InvokeSocketJackClientConnected(e.Connection);
                return;
            }

            // Check for RTMP connections by the C0 version byte. Modern FFmpeg/OBS
            // use the complex handshake and put non-zero version data in C1 bytes
            // 4-7, so requiring those bytes to be zero rejects valid publishers.
            // Delegate to the base HttpServer's GetRequestAsync which has full RTMP
            // handshake, session management, and publish route support built in.
            if (IsProtocolEnabled(_rtmpDelegateHandler) && probe.Length >= 9 && probe[0] == 0x03) {
                _connectionHandlers.TryAdd(connId, _rtmpDelegateHandler);
                e.Connection._Protocol = TcpProtocol.Rtmp;
                e.Connection.SuppressConnectionTest = true;
                GetRequestAsync(ref e);
                return;
            }

            var unknownDecision = RecordEndpointSecurityProtocolEvent(e.Connection, "unknown-protocol", probe.Length, false, 12.0, "unrecognized protocol probe");
            if (!unknownDecision.Allowed) {
                CloseEndpointSecurityBlockedConnection(e.Connection, unknownDecision);
                return;
            }
            ApplyEndpointSecurityDelay(unknownDecision);

            // No handler matched — data flows through to other OnReceive subscribers.
        }

        private void OnClientDisconnected_Cleanup(DisconnectedEventArgs args) {
            if (args.Connection == null) return;
            if (_connectionHandlers.TryRemove(args.Connection.ID, out var handler)) {
                handler.OnDisconnected(this, args.Connection);
            }
        }

        private static bool IsAdministrativeProtocol(IProtocolHandler handler) {
            string name = handler?.Name ?? "";
            return name.Equals("TDS", StringComparison.OrdinalIgnoreCase)
                || name.IndexOf("admin", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("sql", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("database", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private bool IsProtocolEnabled(IProtocolHandler handler) {
            return handler != null
                && !string.IsNullOrWhiteSpace(handler.Name)
                && EnabledProtocols.Contains(handler.Name);
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Overrides peer synchronization so that only connections confirmed as
        /// SocketJack receive broadcast peer updates.  Without this filter,
        /// <c>SendBroadcast</c> pushes
        /// SocketJack-framed bytes into every connection's send queue —
        /// including HTTP connections — corrupting their response stream.
        /// </summary>
        protected internal override void SyncPeer(NetworkConnection Client) {
            Task.Run(() => {
                // Send the peer array to the new client using the correct framing.
                var peerArray = Peers.ToArrayWithLocal(Client);
                if (_connectionHandlers.TryGetValue(Client.ID, out var clientHandler) && clientHandler == _webSocketHandler) {
                    _webSocketHandler.SendObject(this, Client, peerArray);
                } else {
                    Client.Send(peerArray);
                }
                foreach (var kvp in Clients) {
                    var conn = kvp.Value;
                    if (conn == null || conn.ID == Client.ID || conn.Closed)
                        continue;
                    if (conn.Socket == null || !conn.Socket.Connected)
                        continue;
                    // Only send to connections that have been identified as SocketJack or WebSocket.
                    if (_connectionHandlers.TryGetValue(conn.ID, out var handler)) {
                        if (handler == _socketJackHandler) {
                            Send(conn, Client.Identity);
                        } else if (handler == _webSocketHandler) {
                            _webSocketHandler.SendObject(this, conn, Client.Identity);
                        }
                    }
                }
            });
        }

        /// <inheritdoc />
        public override void SendBroadcast(object Obj) {
            SendBroadcastToSocketJackOnly(Obj, null);
        }

        /// <inheritdoc />
        public override void SendBroadcast(object Obj, NetworkConnection Except) {
            SendBroadcastToSocketJackOnly(Obj, Except);
        }

        /// <inheritdoc />
        public override void SendBroadcast(NetworkConnection[] ClientArray, object Obj, NetworkConnection Except = null) {
            if (ClientArray == null) return;

            byte[] wsBytes = null;

            foreach (var conn in ClientArray) {
                if (conn == null || conn.Closed)
                    continue;
                if (conn.Socket == null || !conn.Socket.Connected)
                    continue;
                if (Except != null && conn.ID == Except.ID)
                    continue;
                if (_connectionHandlers.TryGetValue(conn.ID, out var handler)) {
                    if (handler == _socketJackHandler) {
                        conn.TryEnqueueSendBytes(BuildSocketJackPatternFrame(conn, Obj));
                    } else if (handler == _webSocketHandler) {
                        if (wsBytes == null) {
                            byte[] payload = Options.Serializer.Serialize(new Wrapper(Obj, this));
                            bool useBinary = Options.UseCompression;
                            if (useBinary) {
                                payload = Options.CompressionAlgorithm.Compress(payload);
                            }
                            wsBytes = WebSocketFrameHelper.BuildFrame(payload, useBinary);
                        }
                        var stream = conn.Stream;
                        if (stream != null) {
                            try {
                                lock (conn.SendQueueRaw) {
                                    stream.Write(wsBytes, 0, wsBytes.Length);
                                    stream.Flush();
                                }
                            } catch (Exception ex) {
                                InvokeOnError(conn, ex);
                            }
                        }
                    }
                }
            }
        }

        private void SendBroadcastToSocketJackOnly(object Obj, NetworkConnection Except) {
            // Pre-serialize once per protocol type to avoid redundant work per client.
            byte[] wsBytes = null;   // WebSocket framed bytes (serialize + frame header)

            foreach (var kvp in Clients) {
                var conn = kvp.Value;
                if (conn == null || conn.Closed)
                    continue;
                if (conn.Socket == null || !conn.Socket.Connected)
                    continue;
                if (Except != null && conn.ID == Except.ID)
                    continue;
                if (_connectionHandlers.TryGetValue(conn.ID, out var handler)) {
                    if (handler == _socketJackHandler) {
                        conn.TryEnqueueSendBytes(BuildSocketJackPatternFrame(conn, Obj));
                    } else if (handler == _webSocketHandler) {
                        if (wsBytes == null) {
                            byte[] payload = Options.Serializer.Serialize(new Wrapper(Obj, this));
                            bool useBinary = Options.UseCompression;
                            if (useBinary) {
                                payload = Options.CompressionAlgorithm.Compress(payload);
                            }
                            wsBytes = WebSocketFrameHelper.BuildFrame(payload, useBinary);
                        }
                        var stream = conn.Stream;
                        if (stream != null) {
                            try {
                                lock (conn.SendQueueRaw) {
                                    stream.Write(wsBytes, 0, wsBytes.Length);
                                    stream.Flush();
                                }
                            } catch (Exception ex) {
                                InvokeOnError(conn, ex);
                            }
                        }
                    }
                }
            }
        }

        private byte[] BuildSocketJackPatternFrame(NetworkConnection connection, object obj) {
            var wrapped = new Wrapper(obj, this);
            var raw = Options.Serializer.Serialize(wrapped);
            raw = connection.PatternCache.PrepareSend(raw, Options);
            byte[] processed = Options.UseCompression ? Options.CompressionAlgorithm.Compress(raw) : raw;
            return processed.Terminate();
        }

        /// <summary>
        /// Routes identifier-based sends to the correct protocol handler.
        /// On a standard <see cref="TcpServer"/>, this wraps the object in a
        /// <see cref="PeerRedirect"/> and sends to the server connection. Here we
        /// find the actual client connection and use the appropriate framing.
        /// </summary>
        public override void Send(Identifier Recipient, object Obj) {
            if (Recipient == null) return;
            NetworkConnection conn = null;
            foreach (var kvp in Clients) {
                if (kvp.Value != null && kvp.Value.Identity != null && kvp.Value.Identity.ID == Recipient.ID) {
                    conn = kvp.Value;
                    break;
                }
            }
            if (conn == null) return;
            if (_connectionHandlers.TryGetValue(conn.ID, out var handler) && handler == _webSocketHandler) {
                _webSocketHandler.SendObject(this, conn, Obj);
            } else {
                Send(conn, Obj);
            }
        }

        internal void InvokeSocketJackClientConnected(NetworkConnection connection) {
            try { SocketJackClientConnected?.Invoke(connection); } catch { }
        }
        /// <summary>Raises the typed WebSocket connected event after validation.</summary>
        internal void InvokeWebSocketClientConnected(NetworkConnection connection) {
            try { WebSocketClientConnected?.Invoke(connection); } catch { }
        }

        public static byte[] TryGetRawBytes(object obj) {
            if (obj == null)
                return null;

            if (obj is byte[] b)
                return b;

            if (obj is List<byte> lb)
                return lb.ToArray();

            if (obj is IEnumerable<byte> eb)
                return System.Linq.Enumerable.ToArray(eb);

            if (obj is ArraySegment<byte> seg) {
                if (seg.Array == null) return null;
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

        #endregion

        #region DataServer Support

        /// <summary>
        /// Finds the first registered <see cref="TdsProtocolHandler"/> and returns
        /// its backing <see cref="DataServer"/>, or <see langword="null"/> if no
        /// TDS handler has been registered.
        /// </summary>
        internal DataServer FindDataServer() {
            for (int i = 0; i < _handlers.Count; i++) {
                if (_handlers[i] is TdsProtocolHandler tds)
                    return tds.Server;
            }
            return null;
        }

        /// <summary>
        /// Returns the <see cref="DataServer"/> backing the registered
        /// <see cref="TdsProtocolHandler"/>, automatically registering a
        /// hosted-mode handler if one has not yet been added.
        /// </summary>
        internal DataServer GetOrCreateDataServer() {
            var ds = FindDataServer();
            if (ds == null) {
                var tds = new TdsProtocolHandler();
                RegisterProtocol(tds);
                ds = tds.Server;
            }
            return ds;
        }

        /// <summary>
        /// Imports schema and data from an existing MSSQL database into the
        /// <see cref="DataServer"/>'s in-memory store. A <see cref="TdsProtocolHandler"/>
        /// is automatically registered if one has not already been added.
        /// <example>
        /// <code>
        /// var mutable = new MutableTcpServer(1433, "MultiServer");
        /// mutable.EnabledProtocols.Add(MutableTcpProtocols.Tds);
        /// mutable.Listen();
        ///
        /// var conn = new SqlConnection("Server=.;Database=MyDb;Trusted_Connection=True;TrustServerCertificate=True;");
        /// mutable.ImportFromMssql(conn);
        /// </code>
        /// </example>
        /// </summary>
        /// <param name="connection">An open (or openable) <see cref="DbConnection"/> to the source MSSQL server.</param>
        /// <param name="databaseName">
        /// Override the target database name in the DataServer. When <see langword="null"/>,
        /// the source connection's <see cref="DbConnection.Database"/> name is used.
        /// </param>
        /// <param name="tableFilter">
        /// Optional list of table names to import. When <see langword="null"/> or empty,
        /// all user tables are imported.
        /// </param>
        /// <param name="importData">
        /// When <see langword="true"/> (default), all rows are imported.
        /// Set to <see langword="false"/> to import only schema (columns).
        /// </param>
        /// <param name="maxRowsPerTable">
        /// Maximum number of rows to import per table. 0 = unlimited.
        /// </param>
        public void ImportFromMssql(
            DbConnection connection,
            string databaseName = null,
            string[] tableFilter = null,
            bool importData = true,
            int maxRowsPerTable = 0) {

            GetOrCreateDataServer().ImportFromMssql(connection, databaseName, tableFilter, importData, maxRowsPerTable);
        }

        /// <summary>
        /// Saves the <see cref="DataServer"/>'s current state (users, databases,
        /// tables, rows) to its <see cref="DataServer.DataPath"/>.
        /// A <see cref="TdsProtocolHandler"/> is automatically registered if one
        /// has not already been added.
        /// </summary>
        public void Save() {
            GetOrCreateDataServer().Save();
        }

        #endregion

        #region Dispose

        protected override void Dispose(bool disposing) {
            this.OnReceive -= RouteReceive;
            this.ClientDisconnected -= OnClientDisconnected_Cleanup;
            _adminSuite?.Unregister();
            _adminSuite = null;
            _sqlAdminPanel?.Unregister();
            _sqlAdminPanel = null;
            _connectionHandlers.Clear();
            base.Dispose(disposing);
        }

        #endregion

        /// <summary>
        /// Lightweight handler that delegates RTMP connections to the base
        /// <see cref="HttpServer.GetRequestAsync"/> method, which contains
        /// the full RTMP handshake, session, and publish-route pipeline.
        /// </summary>
        private class RtmpDelegateHandler : IProtocolHandler {
            public string Name => "RTMP";
            public bool CanHandle(byte[] data) {
                return data != null && data.Length >= 9
                    && data[0] == 0x03
                    && data[5] == 0 && data[6] == 0 && data[7] == 0 && data[8] == 0;
            }
            public void ProcessReceive(MutableTcpServer server, NetworkConnection connection, ref IReceivedEventArgs e) {
                connection._Protocol = TcpProtocol.Rtmp;
                connection.SuppressConnectionTest = true;
                server.GetRequestAsync(ref e);
            }
            public void OnDisconnected(MutableTcpServer server, NetworkConnection connection) {
                if (connection != null)
                    connection.SuppressConnectionTest = false;
            }
        }
    }

    #region Built-in Protocol Handlers

    /// <summary>
    /// Detects and processes SocketJack length-prefixed framed messages.
    /// When <see cref="MutableTcpServer"/> receives raw bytes that begin with a
    /// 15-byte padded length header, this handler extracts the payload, deserializes
    /// it through the SocketJack serializer, and dispatches it through the normal
    /// <see cref="NetworkBase.HandleReceive"/> pipeline.
    /// </summary>
    public class SocketJackProtocolHandler : IProtocolHandler {

        public string Name => "SocketJack";

        /// <summary>
        /// Per-connection buffer for accumulating partial SocketJack frames.
        /// </summary>
        private readonly ConcurrentDictionary<Guid, List<byte>> _buffers = new ConcurrentDictionary<Guid, List<byte>>();

        /// <summary>
        /// Returns <see langword="true"/> if <paramref name="data"/> starts with a
        /// 15-byte NUL-padded numeric length header (the SocketJack framing format).
        /// </summary>
        public bool CanHandle(byte[] data) {
            if (data == null || data.Length < 15)
                return false;

            // SocketJack frames start with a 15-char length field padded with NUL bytes.
            // Verify the first 15 bytes form a valid NUL-padded decimal number.
            bool foundDigit = false;
            bool pastPadding = false;
            for (int i = 0; i < 15; i++) {
                byte c = data[i];
                if (c == 0) {
                    // NUL padding is only valid before the digits start.
                    if (pastPadding)
                        return false;
                    continue;
                }
                if (c >= (byte)'0' && c <= (byte)'9') {
                    pastPadding = true;
                    foundDigit = true;
                } else {
                    return false;
                }
            }
            return foundDigit;
        }

        public void ProcessReceive(MutableTcpServer server, NetworkConnection connection, ref IReceivedEventArgs e) {
            byte[] incoming = MutableTcpServer.TryGetRawBytes(e.Obj);
            if (incoming == null || incoming.Length == 0)
                return;

            // Detect first data on a SocketJack connection — initialize peer if enabled.
            // This runs before ProcessBuffer so the peer is registered in Peers before
            // any deserialized message handlers (e.g. MetadataKeyValue) execute.
            bool isNewConnection = !_buffers.ContainsKey(connection.ID);
            if (isNewConnection && server.Options.SafeMode) _ = Task.Run(async () => {
                await Task.Delay(5000);
                if (!connection.SafeModeVerified) connection.Close(server, DisconnectionReason.Unknown);
            });
            if (isNewConnection && server.Options.UsePeerToPeer && !server.Options.SafeMode) {
                server.InitializePeer(connection);
            }

            var buffer = _buffers.GetOrAdd(connection.ID, _ => new List<byte>());
            lock (buffer) {
                buffer.AddRange(incoming);
                ProcessBuffer(buffer, connection, server);
            }
        }

        public void OnDisconnected(MutableTcpServer server, NetworkConnection connection) {
            _buffers.TryRemove(connection.ID, out _);
        }

        private static readonly char[] LengthTrimChars = { '\0', ' ', '\r', '\n' };

        private static void ProcessBuffer(List<byte> buffer, NetworkConnection sender, MutableTcpServer target) {
            // Each SocketJack frame: [15-byte length header][payload bytes]
            while (buffer.Count >= 15) {
                // Extract 15-byte header without intermediate List/Array allocation
                var hdrBytes = new byte[15];
                buffer.CopyTo(0, hdrBytes, 0, 15);
                string lengthStr = Encoding.UTF8.GetString(hdrBytes).Trim(LengthTrimChars);
                if (!int.TryParse(lengthStr, out int payloadLength) || payloadLength <= 0)
                    break;

                if (payloadLength > (target.Options.SafeMode && !sender.SafeModeVerified ? SafeModeHandshake.MaximumBytes : target.Options.MaximumBufferSize)) {
                    sender.Close(target, DisconnectionReason.Unknown); buffer.Clear(); return;
                }
                int totalFrameSize = checked(15 + payloadLength);
                if (buffer.Count < totalFrameSize)
                    break; // Wait for more data.

                byte[] payload = new byte[payloadLength];
                buffer.CopyTo(15, payload, 0, payloadLength);
                buffer.RemoveRange(0, totalFrameSize);

                if (target.Options.SafeMode && !sender.SafeModeVerified) {
                    try {
                        SafeModeHandshake.Validate(payload, target.Options, sender);
                        sender.Stream.Write(SafeModeHandshake.Accepted, 0, SafeModeHandshake.Accepted.Length);
                        if (target.Options.UsePeerToPeer) target.InitializePeer(sender);
                        target.InvokeSocketJackClientConnected(sender);
                    } catch { sender.Close(target, DisconnectionReason.Unknown); return; }
                    continue;
                }
                var securityDecision = target.RecordEndpointSecuritySocketJackFrame(sender, payloadLength);
                if (!securityDecision.Allowed) {
                    target.CloseEndpointSecurityBlockedConnection(sender, securityDecision);
                    return;
                }
                target.ApplyEndpointSecurityDelay(securityDecision);


                // Preserve receive order. Segment reassembly is stateful, so dispatching every
                // frame through Task.Run lets later segments race earlier ones and corrupts
                // large screen/audio messages under sustained load.
                try {
                    byte[] bytes = payload;
                    if (target.Options.UseCompression) {
                        bytes = UdpReliableObjects.Decompress(bytes, target.Options);
                    }
                    if (sender != null && sender.PatternCache != null) {
                        if (!sender.PatternCache.TryResolveReceived(bytes, target.Options, out bytes, out string cacheError)) {
                            target.InvokeOnError(sender, new Exception(cacheError));
                            return;
                        }
                    }
                    object unwrapped = InboundMessageDecoder.Read(target, sender, bytes);
                    if (unwrapped != null) {
                        target.HandleReceive(sender, unwrapped, unwrapped.GetType(), payloadLength);
                        var genericType = typeof(ReceivedEventArgs<>).MakeGenericType(unwrapped.GetType());
                        var receivedEventArgs = (IReceivedEventArgs)Activator.CreateInstance(genericType);
                        receivedEventArgs.Initialize(target, sender, unwrapped, payloadLength);
                        target.InvokeCallbacks(receivedEventArgs);
                    }
                } catch (Exception ex) {
                    sender.Close(target, DisconnectionReason.Unknown);
                    target.InvokeOnError(sender, ex);
                }
            }
        }
    }

    public class WebChatApiWebSocketProtocolHandler : IProtocolHandler {
        public string Name => "WebChatApiWebSocket";

        private const string WebSocketMagicGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
        private const int OpcodeText = 0x1;
        private const int OpcodeBinary = 0x2;
        private const int OpcodeClose = 0x8;
        private const int OpcodePing = 0x9;
        private const int OpcodePong = 0xA;

        private readonly string _path;
        private readonly ConcurrentDictionary<Guid, State> _states = new ConcurrentDictionary<Guid, State>();

        public WebChatApiWebSocketProtocolHandler(string path = "/api/web-chat/ws") {
            _path = NormalizeWebSocketPath(path);
        }

        public bool CanHandle(byte[] data) {
            if (data == null || data.Length < 20)
                return false;
            if (data[0] != (byte)'G' || data[1] != (byte)'E' || data[2] != (byte)'T' || data[3] != (byte)' ')
                return false;

            string text = Encoding.UTF8.GetString(data, 0, Math.Min(data.Length, 4096));
            if (text.IndexOf("Upgrade:", StringComparison.OrdinalIgnoreCase) < 0 ||
                text.IndexOf("websocket", StringComparison.OrdinalIgnoreCase) < 0)
                return false;

            string path = GetRequestPath(text);
            return string.Equals(path, _path, StringComparison.OrdinalIgnoreCase);
        }

        public void ProcessReceive(MutableTcpServer server, NetworkConnection connection, ref IReceivedEventArgs e) {
            byte[] incoming = MutableTcpServer.TryGetRawBytes(e.Obj);
            if (incoming == null || incoming.Length == 0)
                return;

            var state = _states.GetOrAdd(connection.ID, _ => new State());
            lock (state) {
                state.Buffer.AddRange(incoming);
                if (!state.HandshakeComplete)
                    TryCompleteHandshake(server, connection, state);
                else
                    ProcessFrames(server, connection, state);
            }
        }

        public void OnDisconnected(MutableTcpServer server, NetworkConnection connection) {
            if (connection == null)
                return;
            if (_states.TryRemove(connection.ID, out State state)) {
                foreach (CancellationTokenSource cts in state.Requests.Values) {
                    try { cts.Cancel(); } catch { }
                    try { cts.Dispose(); } catch { }
                }
            }
        }

        private static string NormalizeWebSocketPath(string path) {
            if (string.IsNullOrWhiteSpace(path))
                return "/";
            path = path.Trim();
            return path.StartsWith("/", StringComparison.Ordinal) ? path : "/" + path;
        }

        private static string GetRequestPath(string requestText) {
            string firstLine = requestText.Split(new[] { "\r\n" }, StringSplitOptions.None).FirstOrDefault() ?? "";
            string[] parts = firstLine.Split(' ');
            if (parts.Length < 2)
                return "";
            string target = parts[1];
            int query = target.IndexOf('?');
            return query >= 0 ? target.Substring(0, query) : target;
        }

        private static void TryCompleteHandshake(MutableTcpServer server, NetworkConnection connection, State state) {
            int hdrEnd = FindHeaderEnd(state.Buffer);
            if (hdrEnd < 0)
                return;

            string request = Encoding.UTF8.GetString(state.Buffer.ToArray(), 0, hdrEnd);
            try {
                var parsedRequest = HttpServer.ParseHttpRequest(Encoding.UTF8.GetBytes(request));
                var securityDecision = server.EvaluateEndpointSecurityHttpRequest(connection, parsedRequest);
                if (!securityDecision.Allowed) {
                    SendHttpResponse(connection, HttpServer.EndpointSecurityStatusText(securityDecision), securityDecision.Message);
                    try { connection.Close(server, DisconnectionReason.LocalSocketClosed); } catch { }
                    return;
                }
                server.ApplyEndpointSecurityDelay(securityDecision);
                if (!server.IsHostAllowed(parsedRequest)) {
                    SendHttpResponse(connection, "421 Misdirected Request", "421 Misdirected Request");
                    try { connection.Close(server, DisconnectionReason.LocalSocketClosed); } catch { }
                    return;
                }
            } catch {
                try { connection.Close(server, DisconnectionReason.LocalSocketClosed); } catch { }
                return;
            }

            string secKey = "";
            foreach (string line in request.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries)) {
                if (line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase)) {
                    secKey = line.Substring(line.IndexOf(':') + 1).Trim();
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(secKey)) {
                try { connection.Close(server, DisconnectionReason.LocalSocketClosed); } catch { }
                return;
            }

            string acceptKey;
            using (SHA1 sha1 = SHA1.Create()) {
                byte[] hash = sha1.ComputeHash(Encoding.UTF8.GetBytes(secKey + WebSocketMagicGuid));
                acceptKey = Convert.ToBase64String(hash);
            }

            byte[] responseBytes = Encoding.UTF8.GetBytes(
                "HTTP/1.1 101 Switching Protocols\r\n" +
                "Upgrade: websocket\r\n" +
                "Connection: Upgrade\r\n" +
                "Sec-WebSocket-Accept: " + acceptKey + "\r\n" +
                "\r\n");
            try {
                Stream stream = connection.Stream;
                stream.Write(responseBytes, 0, responseBytes.Length);
                stream.Flush();
            } catch (Exception ex) {
                server.InvokeOnError(connection, ex);
                return;
            }

            state.HandshakeComplete = true;
            state.Buffer.RemoveRange(0, hdrEnd);
            SendJson(connection, state, new JsonObject {
                ["type"] = "ready",
                ["ok"] = true,
                ["transport"] = "websocket",
                ["secure"] = connection.SSL,
                ["sentUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
            });

            if (state.Buffer.Count > 0)
                ProcessFrames(server, connection, state);
        }

        private static int FindHeaderEnd(List<byte> buffer) {
            for (int i = 0; i <= buffer.Count - 4; i++) {
                if (buffer[i] == (byte)'\r' && buffer[i + 1] == (byte)'\n' &&
                    buffer[i + 2] == (byte)'\r' && buffer[i + 3] == (byte)'\n')
                    return i + 4;
            }
            return -1;
        }

        private static void ProcessFrames(MutableTcpServer server, NetworkConnection connection, State state) {
            while (state.Buffer.Count >= 2) {
                int offset = 0;
                byte b0 = state.Buffer[offset++];
                byte b1 = state.Buffer[offset++];
                int opcode = b0 & 0x0F;
                bool masked = (b1 & 0x80) != 0;
                long payloadLen = b1 & 0x7F;

                if (payloadLen == 126) {
                    if (state.Buffer.Count < offset + 2) return;
                    payloadLen = (state.Buffer[offset] << 8) | state.Buffer[offset + 1];
                    offset += 2;
                } else if (payloadLen == 127) {
                    if (state.Buffer.Count < offset + 8) return;
                    payloadLen = 0;
                    for (int i = 0; i < 8; i++)
                        payloadLen = (payloadLen << 8) | state.Buffer[offset + i];
                    offset += 8;
                }

                if (payloadLen > int.MaxValue) {
                    SendCloseFrame(connection);
                    try { connection.Close(server, DisconnectionReason.LocalSocketClosed); } catch { }
                    return;
                }

                byte[] maskKey = null;
                if (masked) {
                    if (state.Buffer.Count < offset + 4) return;
                    maskKey = new byte[4];
                    for (int i = 0; i < 4; i++)
                        maskKey[i] = state.Buffer[offset + i];
                    offset += 4;
                }

                if (state.Buffer.Count < offset + payloadLen)
                    return;

                int count = (int)payloadLen;
                byte[] payload = new byte[count];
                state.Buffer.CopyTo(offset, payload, 0, count);
                state.Buffer.RemoveRange(0, offset + count);
                if (masked && maskKey != null) {
                    for (int i = 0; i < count; i++)
                        payload[i] ^= maskKey[i % 4];
                }

                var securityDecision = server.RecordEndpointSecurityWebSocketFrame(connection, count);
                if (!securityDecision.Allowed) {
                    SendCloseFrame(connection);
                    try { connection.Close(server, DisconnectionReason.LocalSocketClosed); } catch { }
                    return;
                }
                server.ApplyEndpointSecurityDelay(securityDecision);

                switch (opcode) {
                    case OpcodeText:
                        HandleText(server, connection, state, Encoding.UTF8.GetString(payload));
                        break;
                    case OpcodeBinary:
                        break;
                    case OpcodeClose:
                        SendCloseFrame(connection);
                        try { connection.Close(server, DisconnectionReason.RemoteSocketClosed); } catch { }
                        return;
                    case OpcodePing:
                        SendFrame(connection, state, payload, useBinary: false, opcode: OpcodePong);
                        break;
                    case OpcodePong:
                        break;
                }
            }
        }

        private static void HandleText(MutableTcpServer server, NetworkConnection connection, State state, string text) {
            JsonObject message;
            try {
                message = JsonNode.Parse(text)?.AsObject();
            } catch {
                return;
            }
            if (message == null)
                return;

            string type = JsonText(message, "type", "");
            if (type.Equals("ping", StringComparison.OrdinalIgnoreCase)) {
                SendJson(connection, state, new JsonObject {
                    ["type"] = "pong",
                    ["sentUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
                });
                return;
            }

            string id = JsonText(message, "id", JsonText(message, "requestId", ""));
            if (type.Equals("cancel", StringComparison.OrdinalIgnoreCase)) {
                if (!string.IsNullOrWhiteSpace(id) && state.Requests.TryRemove(id, out CancellationTokenSource cts)) {
                    try { cts.Cancel(); } catch { }
                    try { cts.Dispose(); } catch { }
                }
                return;
            }

            if (!type.Equals("request", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(id))
                return;

            var requestCts = new CancellationTokenSource();
            state.Requests[id] = requestCts;
            Task.Run(async () => {
                try {
                    await HandleRequestAsync(server, connection, state, id, message, requestCts.Token).ConfigureAwait(false);
                } catch (OperationCanceledException) {
                    SendJson(connection, state, new JsonObject {
                        ["type"] = "error",
                        ["id"] = id,
                        ["error"] = "Request canceled."
                    });
                } catch (Exception ex) {
                    SendJson(connection, state, new JsonObject {
                        ["type"] = "error",
                        ["id"] = id,
                        ["error"] = ex.Message
                    });
                } finally {
                    state.Requests.TryRemove(id, out _);
                    requestCts.Dispose();
                }
            });
        }

        private static async Task HandleRequestAsync(MutableTcpServer server, NetworkConnection connection, State state, string id, JsonObject message, CancellationToken cancellationToken) {
            bool responseStarted = false;
            var request = BuildHttpRequest(message);
            var callbacks = new HttpStreamDispatchCallbacks {
                OnStart = response => {
                    responseStarted = true;
                    SendResponseStart(connection, state, id, response);
                },
                OnChunk = (buffer, offset, count) => {
                    if (!responseStarted) {
                        responseStarted = true;
                        SendResponseStart(connection, state, id, request.Context.Response);
                    }
                    SendBinaryChunk(connection, state, id, buffer, offset, count);
                },
                OnEnd = () => SendResponseEnd(connection, state, id, request.Context)
            };

            Stopwatch stopwatch = Stopwatch.StartNew();
            HttpDispatchResult result = await server.DispatchMappedHttpRequestAsync(connection, request, callbacks, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            if (result.Streamed)
                return;

            SendResponseStart(connection, state, id, result.Response);
            byte[] body = result.BodyBytes ?? Array.Empty<byte>();
            if (body.Length > 0)
                SendBinaryChunk(connection, state, id, body, 0, body.Length);
            SendResponseEnd(connection, state, id, result.Context, (int)Math.Min(int.MaxValue, stopwatch.ElapsedMilliseconds));
        }

        private static HttpRequest BuildHttpRequest(JsonObject message) {
            string method = JsonText(message, "method", "GET").ToUpperInvariant();
            string path = NormalizeRequestPath(JsonText(message, "path", "/"));
            string queryString = JsonText(message, "queryString", "");
            int queryIndex = path.IndexOf('?');
            if (queryIndex >= 0) {
                if (string.IsNullOrWhiteSpace(queryString))
                    queryString = path.Substring(queryIndex + 1);
                path = path.Substring(0, queryIndex);
            }

            byte[] body = DecodeBody(message);
            JsonObject headers = JsonObjectProperty(message, "headers");
            var headerBuilder = new StringBuilder();
            headerBuilder.Append(method).Append(' ').Append(path);
            if (!string.IsNullOrEmpty(queryString))
                headerBuilder.Append('?').Append(queryString);
            headerBuilder.Append(" HTTP/1.1\r\n");
            bool hasHost = false;
            bool hasContentLength = false;
            if (headers != null) {
                foreach (KeyValuePair<string, JsonNode> kv in headers) {
                    string name = kv.Key ?? "";
                    if (string.IsNullOrWhiteSpace(name) || ShouldSkipEnvelopeHeader(name))
                        continue;
                    string value = kv.Value?.ToString() ?? "";
                    if (name.Equals("Host", StringComparison.OrdinalIgnoreCase))
                        hasHost = true;
                    if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                        hasContentLength = true;
                    headerBuilder.Append(name).Append(": ").Append(value.Replace("\r", "").Replace("\n", "")).Append("\r\n");
                }
            }
            if (!hasHost)
                headerBuilder.Append("Host: localhost\r\n");
            if (!hasContentLength)
                headerBuilder.Append("Content-Length: ").Append(body.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            headerBuilder.Append("\r\n");

            byte[] headerBytes = Encoding.UTF8.GetBytes(headerBuilder.ToString());
            byte[] raw = new byte[headerBytes.Length + body.Length];
            Buffer.BlockCopy(headerBytes, 0, raw, 0, headerBytes.Length);
            if (body.Length > 0)
                Buffer.BlockCopy(body, 0, raw, headerBytes.Length, body.Length);
            return HttpServer.ParseHttpRequest(raw);
        }

        private static string NormalizeRequestPath(string path) {
            if (string.IsNullOrWhiteSpace(path))
                return "/";
            path = path.Trim();
            return path.StartsWith("/", StringComparison.Ordinal) ? path : "/" + path;
        }

        private static bool ShouldSkipEnvelopeHeader(string name) {
            return name.Equals("Connection", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Proxy-Connection", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase);
        }

        private static byte[] DecodeBody(JsonObject message) {
            string bodyBase64 = JsonText(message, "bodyBase64", "");
            if (!string.IsNullOrWhiteSpace(bodyBase64)) {
                try { return Convert.FromBase64String(bodyBase64); } catch { }
            }
            string bodyText = JsonText(message, "bodyText", "");
            return string.IsNullOrEmpty(bodyText) ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(bodyText);
        }

        private static void SendResponseStart(NetworkConnection connection, State state, string id, HttpResponse response) {
            var headers = new JsonObject();
            if (response?.Headers != null) {
                foreach (KeyValuePair<string, string> header in response.Headers)
                    headers[header.Key] = header.Value;
            }
            SendJson(connection, state, new JsonObject {
                ["type"] = "response-start",
                ["id"] = id,
                ["statusCode"] = response?.StatusCodeNumber ?? 200,
                ["reasonPhrase"] = response?.ReasonPhrase ?? "OK",
                ["contentType"] = response?.ContentType ?? "application/octet-stream",
                ["headers"] = headers
            });
        }

        private static void SendResponseEnd(NetworkConnection connection, State state, string id, HttpContext context, int durationMs = 0) {
            SendJson(connection, state, new JsonObject {
                ["type"] = "response-end",
                ["id"] = id,
                ["statusCode"] = context?.StatusCodeNumber ?? 200,
                ["durationMs"] = durationMs
            });
        }

        private static void SendBinaryChunk(NetworkConnection connection, State state, string id, byte[] buffer, int offset, int count) {
            if (buffer == null || count <= 0)
                return;
            byte[] idBytes = Encoding.ASCII.GetBytes(FixedRequestId(id));
            byte[] payload = new byte[32 + count];
            Buffer.BlockCopy(idBytes, 0, payload, 0, 32);
            Buffer.BlockCopy(buffer, offset, payload, 32, count);
            SendFrame(connection, state, payload, useBinary: true, opcode: OpcodeBinary);
        }

        private static string FixedRequestId(string id) {
            id = (id ?? "").Trim();
            if (id.Length > 32)
                return id.Substring(0, 32);
            return id.PadRight(32, ' ');
        }

        private static void SendJson(NetworkConnection connection, State state, JsonObject payload) {
            byte[] bytes = Encoding.UTF8.GetBytes(payload.ToJsonString());
            SendFrame(connection, state, bytes, useBinary: false, opcode: OpcodeText);
        }

        private static void SendFrame(NetworkConnection connection, State state, byte[] payload, bool useBinary, int opcode) {
            try {
                byte[] frame = opcode == OpcodeText || opcode == OpcodeBinary
                    ? WebSocketFrameHelper.BuildFrame(payload ?? Array.Empty<byte>(), useBinary)
                    : BuildControlFrame(opcode, payload ?? Array.Empty<byte>());
                Stream stream = connection.Stream;
                if (stream == null)
                    return;
                lock (state.SendLock) {
                    stream.Write(frame, 0, frame.Length);
                    stream.Flush();
                }
            } catch { }
        }

        private static byte[] BuildControlFrame(int opcode, byte[] payload) {
            payload ??= Array.Empty<byte>();
            if (payload.Length > 125)
                payload = payload.Take(125).ToArray();
            byte[] frame = new byte[2 + payload.Length];
            frame[0] = (byte)(0x80 | opcode);
            frame[1] = (byte)payload.Length;
            if (payload.Length > 0)
                Buffer.BlockCopy(payload, 0, frame, 2, payload.Length);
            return frame;
        }

        private static void SendCloseFrame(NetworkConnection connection) {
            try {
                Stream stream = connection.Stream;
                if (stream == null)
                    return;
                byte[] close = new byte[] { 0x88, 0x00 };
                stream.Write(close, 0, close.Length);
                stream.Flush();
            } catch { }
        }

        private static void SendHttpResponse(NetworkConnection connection, string status, string body) {
            try {
                byte[] bodyBytes = Encoding.UTF8.GetBytes(body ?? "");
                byte[] headerBytes = Encoding.UTF8.GetBytes(
                    "HTTP/1.1 " + status + "\r\n" +
                    "Content-Type: text/plain\r\n" +
                    "Content-Length: " + bodyBytes.Length.ToString(CultureInfo.InvariantCulture) + "\r\n" +
                    "Connection: close\r\n\r\n");
                Stream stream = connection.Stream;
                if (stream == null)
                    return;
                stream.Write(headerBytes, 0, headerBytes.Length);
                if (bodyBytes.Length > 0)
                    stream.Write(bodyBytes, 0, bodyBytes.Length);
                stream.Flush();
            } catch { }
        }

        private static string JsonText(JsonObject obj, string name, string fallback) {
            if (obj == null || string.IsNullOrWhiteSpace(name) || !obj.TryGetPropertyValue(name, out JsonNode node) || node == null)
                return fallback;
            return node.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : node.ToString();
        }

        private static JsonObject JsonObjectProperty(JsonObject obj, string name) {
            if (obj == null || !obj.TryGetPropertyValue(name, out JsonNode node) || node == null)
                return null;
            return node as JsonObject;
        }

        private sealed class State {
            public bool HandshakeComplete;
            public List<byte> Buffer = new List<byte>();
            public object SendLock = new object();
            public ConcurrentDictionary<string, CancellationTokenSource> Requests = new ConcurrentDictionary<string, CancellationTokenSource>(StringComparer.OrdinalIgnoreCase);
        }
    }
    /// <summary>
    /// Detects WebSocket upgrade requests and manages WebSocket (RFC 6455)
    /// connections inside a <see cref="MutableTcpServer"/>. After completing
    /// the HTTP upgrade handshake, incoming WebSocket frames are decoded,
    /// deserialized through the SocketJack serializer, and dispatched via
    /// <see cref="NetworkBase.HandleReceive"/>. This allows browser-based
    /// WebSocket clients to share the same port as SocketJack and HTTP clients.
    /// </summary>
    public class WebSocketProtocolHandler : IProtocolHandler {

        public string Name => "WebSocket";

        private const string WebSocketMagicGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
        private const int OpcodeText = 0x1;
        private const int OpcodeBinary = 0x2;
        private const int OpcodeClose = 0x8;
        private const int OpcodePing = 0x9;
        private const int OpcodePong = 0xA;
        private const int OpcodeBrowserClient = 0xB;

        /// <summary>
        /// Per-connection state for the WebSocket protocol.
        /// </summary>
        private readonly ConcurrentDictionary<Guid, WebSocketState> _states = new ConcurrentDictionary<Guid, WebSocketState>();

        /// <summary>
        /// Returns <see langword="true"/> when <paramref name="data"/> looks like
        /// an HTTP GET request that contains the <c>Upgrade: websocket</c> header.
        /// </summary>
        public bool CanHandle(byte[] data) {
            if (data == null || data.Length < 20)
                return false;

            // Must start with "GET "
            if (data[0] != (byte)'G' || data[1] != (byte)'E' ||
                data[2] != (byte)'T' || data[3] != (byte)' ')
                return false;

            // Look for "Upgrade:" and "websocket" (case-insensitive) in the header area.
            int scanLen = Math.Min(data.Length, 4096);
            string text = Encoding.UTF8.GetString(data, 0, scanLen);
            return text.IndexOf("Upgrade:", StringComparison.OrdinalIgnoreCase) >= 0 &&
                   text.IndexOf("websocket", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public void ProcessReceive(MutableTcpServer server, NetworkConnection connection, ref IReceivedEventArgs e) {
            byte[] incoming = MutableTcpServer.TryGetRawBytes(e.Obj);
            if (incoming == null || incoming.Length == 0)
                return;

            var state = _states.GetOrAdd(connection.ID, _ => new WebSocketState());
            lock (state) {
                state.Buffer.AddRange(incoming);

                if (!state.HandshakeComplete) {
                    TryCompleteHandshake(server, connection, state);
                } else {
                    ProcessFrames(server, connection, state);
                }
            }
        }

        public void OnDisconnected(MutableTcpServer server, NetworkConnection connection) {
            _states.TryRemove(connection.ID, out _);
        }

        #region Handshake

        private static void TryCompleteHandshake(MutableTcpServer server, NetworkConnection connection, WebSocketState state) {
            // Wait until we have complete HTTP headers (\r\n\r\n).
            int hdrEnd = -1;
            for (int i = 0; i <= state.Buffer.Count - 4; i++) {
                if (state.Buffer[i] == (byte)'\r' && state.Buffer[i + 1] == (byte)'\n' &&
                    state.Buffer[i + 2] == (byte)'\r' && state.Buffer[i + 3] == (byte)'\n') {
                    hdrEnd = i + 4;
                    break;
                }
            }
            if (hdrEnd == -1)
                return;

            string request = Encoding.UTF8.GetString(state.Buffer.ToArray(), 0, hdrEnd);
            try {
                var parsedRequest = HttpServer.ParseHttpRequest(Encoding.UTF8.GetBytes(request));
                var securityDecision = server.EvaluateEndpointSecurityHttpRequest(connection, parsedRequest);
                if (!securityDecision.Allowed) {
                    SendHttpResponse(connection, HttpServer.EndpointSecurityStatusText(securityDecision), securityDecision.Message);
                    try { connection.Close(server, DisconnectionReason.LocalSocketClosed); } catch { }
                    return;
                }
                server.ApplyEndpointSecurityDelay(securityDecision);
                if (!server.IsHostAllowed(parsedRequest)) {
                    SendHttpResponse(connection, "421 Misdirected Request", "421 Misdirected Request");
                    try { connection.Close(server, DisconnectionReason.LocalSocketClosed); } catch { }
                    return;
                }
            } catch {
                try { connection.Close(server, DisconnectionReason.LocalSocketClosed); } catch { }
                return;
            }

            string secKey = null;
            foreach (var line in request.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries)) {
                if (line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase)) {
                    secKey = line.Substring(line.IndexOf(':') + 1).Trim();
                    break;
                }
            }

            if (string.IsNullOrEmpty(secKey)) {
                try { connection.Close(server, DisconnectionReason.LocalSocketClosed); } catch { }
                return;
            }

            try { SafeModeHandshake.ValidateHttp(request, server.Options, connection); }
            catch { connection.Close(server, DisconnectionReason.Unknown); return; }
            string acceptKey;
            using (var sha1 = SHA1.Create()) {
                byte[] hash = sha1.ComputeHash(Encoding.UTF8.GetBytes(secKey + WebSocketMagicGuid));
                acceptKey = Convert.ToBase64String(hash);
            }

            string response =
                "HTTP/1.1 101 Switching Protocols\r\n" +
                "Upgrade: websocket\r\n" +
                "Connection: Upgrade\r\n" +
                "Sec-WebSocket-Accept: " + acceptKey + "\r\n" +
                "\r\n";
            byte[] responseBytes = Encoding.UTF8.GetBytes(response);
            try {
                var stream = connection.Stream;
                if (stream != null) {
                    stream.Write(responseBytes, 0, responseBytes.Length);
                    stream.Flush();
                }
            } catch (Exception ex) {
                server.InvokeOnError(connection, ex);
                return;
            }

            state.HandshakeComplete = true;
            state.Buffer.RemoveRange(0, hdrEnd);

            if (server.Options.UsePeerToPeer) {
                server.InitializePeer(connection);
            }

            server.InvokeWebSocketClientConnected(connection);

            if (state.Buffer.Count > 0) {
                ProcessFrames(server, connection, state);
            }
        }

        private static void SendHttpResponse(NetworkConnection connection, string status, string body) {
            try {
                var bodyBytes = Encoding.UTF8.GetBytes(body ?? "");
                var headerBytes = Encoding.UTF8.GetBytes(
                    "HTTP/1.1 " + status + "\r\n" +
                    "Content-Type: text/plain\r\n" +
                    "Content-Length: " + bodyBytes.Length + "\r\n" +
                    "Connection: close\r\n\r\n");
                var stream = connection.Stream;
                if (stream != null) {
                    stream.Write(headerBytes, 0, headerBytes.Length);
                    if (bodyBytes.Length > 0)
                        stream.Write(bodyBytes, 0, bodyBytes.Length);
                    stream.Flush();
                }
            } catch { }
        }
        #endregion

        #region Frame Processing

        private static void ProcessFrames(MutableTcpServer server, NetworkConnection connection, WebSocketState state) {
            while (state.Buffer.Count >= 2) {
                int offset = 0;
                byte b0 = state.Buffer[offset++];
                byte b1 = state.Buffer[offset++];

                int opcode = b0 & 0x0F;
                bool masked = (b1 & 0x80) != 0;
                long payloadLen = b1 & 0x7F;

                if (payloadLen == 126) {
                    if (state.Buffer.Count < offset + 2) return;
                    payloadLen = (state.Buffer[offset] << 8) | state.Buffer[offset + 1];
                    offset += 2;
                } else if (payloadLen == 127) {
                    if (state.Buffer.Count < offset + 8) return;
                    payloadLen = 0;
                    for (int i = 0; i < 8; i++)
                        payloadLen = (payloadLen << 8) | state.Buffer[offset + i];
                    offset += 8;
                }

                if (payloadLen > int.MaxValue) {
                    SendCloseFrame(connection);
                    try { connection.Close(server, DisconnectionReason.LocalSocketClosed); } catch { }
                    return;
                }

                if (masked) {
                    if (state.Buffer.Count < offset + 4) return;
                    offset += 4;
                }

                if (state.Buffer.Count < offset + payloadLen)
                    return;

                int intPayloadLen = (int)payloadLen;

                byte[] maskKey = null;
                if (masked) {
                    int maskOffset = offset - 4;
                    maskKey = new byte[4];
                    for (int i = 0; i < 4; i++)
                        maskKey[i] = state.Buffer[maskOffset + i];
                }

                byte[] payload = new byte[intPayloadLen];
                state.Buffer.CopyTo(offset, payload, 0, intPayloadLen);
                state.Buffer.RemoveRange(0, offset + intPayloadLen);

                if (masked && maskKey != null) {
                    for (int i = 0; i < intPayloadLen; i++)
                        payload[i] ^= maskKey[i % 4];
                }

                var securityDecision = server.RecordEndpointSecurityWebSocketFrame(connection, intPayloadLen);
                if (!securityDecision.Allowed) {
                    SendCloseFrame(connection);
                    try { connection.Close(server, DisconnectionReason.LocalSocketClosed); } catch { }
                    return;
                }
                server.ApplyEndpointSecurityDelay(securityDecision);

                switch (opcode) {
                    case OpcodeText:
                    case OpcodeBinary:
                        DispatchPayload(server, connection, payload, intPayloadLen, opcode == OpcodeBinary);
                        break;
                    case OpcodeClose:
                        SendCloseFrame(connection);
                        try { connection.Close(server, DisconnectionReason.RemoteSocketClosed); } catch { }
                        return;
                    case OpcodePing:
                        SendPongFrame(connection, payload);
                        break;
                    case OpcodePong:
                        break;
                    case OpcodeBrowserClient:
                        SendConstructors(server, connection);
                        break;
                }
            }
        }

        private static void DispatchPayload(MutableTcpServer server, NetworkConnection connection, byte[] payload, int payloadLen, bool isBinary) {
            // Process frames in the order they were parsed from this WebSocket. Segment
            // reassembly depends on that order and is fast; application handlers can still
            // schedule expensive work after the object has been reconstructed.
            try {
                byte[] data = payload;
                if (server.Options.UseCompression && isBinary) {
                    data = UdpReliableObjects.Decompress(data, server.Options);
                }
                object unwrapped = InboundMessageDecoder.Read(server, connection, data);
                if (unwrapped != null) {
                    server.HandleReceive(connection, unwrapped, unwrapped.GetType(), payloadLen);
                    var genericType = typeof(ReceivedEventArgs<>).MakeGenericType(unwrapped.GetType());
                    var receivedEventArgs = (IReceivedEventArgs)Activator.CreateInstance(genericType);
                    receivedEventArgs.Initialize(server, connection, unwrapped, payloadLen);
                    server.InvokeCallbacks(receivedEventArgs);
                }
            } catch (Exception ex) {
                connection.Close(server, DisconnectionReason.Unknown);
                server.InvokeOnError(connection, ex);
            }
        }

        #endregion

        #region Sending

        internal void SendObject(MutableTcpServer server, NetworkConnection connection, object obj) {
            if (connection == null || connection.Closed || connection.Socket == null || !connection.Socket.Connected)
                return;
            try {
                Type objType = obj.GetType();
                byte[] payload = objType == typeof(PeerRedirect)
                    ? server.Options.Serializer.Serialize(obj)
                    : server.Options.Serializer.Serialize(new Wrapper(obj, server));

                bool useBinary = server.Options.UseCompression;
                if (useBinary) {
                    payload = server.Options.CompressionAlgorithm.Compress(payload);
                }

                byte[] frame = WebSocketFrameHelper.BuildFrame(payload, useBinary);
                var stream = connection.Stream;
                if (stream == null) return;

                lock (connection.SendQueueRaw) {
                    stream.Write(frame, 0, frame.Length);
                    stream.Flush();
                }
            } catch (Exception ex) {
                server.InvokeOnError(connection, ex);
            }
        }

        private static void SendCloseFrame(NetworkConnection connection) {
            try {
                var stream = connection.Stream;
                if (stream != null) {
                    byte[] close = new byte[] { 0x88, 0x00 };
                    stream.Write(close, 0, close.Length);
                    stream.Flush();
                }
            } catch { }
        }

        private static void SendPongFrame(NetworkConnection connection, byte[] payload) {
            try {
                var stream = connection.Stream;
                if (stream != null) {
                    int headerLen;
                    byte[] frame;
                    if (payload.Length <= 125) {
                        headerLen = 2;
                        frame = new byte[headerLen + payload.Length];
                        frame[0] = 0x8A;
                        frame[1] = (byte)payload.Length;
                    } else {
                        headerLen = 4;
                        frame = new byte[headerLen + payload.Length];
                        frame[0] = 0x8A;
                        frame[1] = 126;
                        frame[2] = (byte)((payload.Length >> 8) & 0xFF);
                        frame[3] = (byte)(payload.Length & 0xFF);
                    }
                    Buffer.BlockCopy(payload, 0, frame, headerLen, payload.Length);
                    stream.Write(frame, 0, frame.Length);
                    stream.Flush();
                }
            } catch { }
        }

        private static void SendConstructors(MutableTcpServer server, NetworkConnection connection) {
            var script = new SocketJack.Net.WebSockets.WebSocketServer.JSContructors(WebSocketFrameHelper.GenerateJSConstructors(server.Options.Whitelist));
            try {
                byte[] payload = server.Options.Serializer.Serialize(new Wrapper(script, server));
                byte[] frame = WebSocketFrameHelper.BuildFrame(payload, false);
                var stream = connection.Stream;
                if (stream != null) {
                    lock (connection.SendQueueRaw) {
                        stream.Write(frame, 0, frame.Length);
                        stream.Flush();
                    }
                }
            } catch { }
        }

        #endregion
        private class WebSocketState {
            public bool HandshakeComplete;
            public List<byte> Buffer = new List<byte>();
        }
    }

    /// <summary>
    /// Detects and processes HTTP requests. Routes are stored in the parent
    /// <see cref="HttpServer"/> infrastructure so that a single set of mappings
    /// is shared regardless of whether requests arrive through the
    /// <see cref="MutableTcpServer"/> protocol router or the base class directly.
    /// </summary>
    public class HttpProtocolHandler : IProtocolHandler {

        public string Name => "HTTP";

        private readonly MutableTcpServer _server;

        #region Delegates

        /// <inheritdoc cref="HttpServer.RouteHandler"/>
        public delegate object RouteHandler(NetworkConnection connection, HttpRequest request, CancellationToken cancellationToken);

        /// <inheritdoc cref="HttpServer.RouteHandler{T}"/>
        public delegate object RouteHandler<T>(NetworkConnection connection, T body, HttpRequest request, CancellationToken cancellationToken);

        /// <inheritdoc cref="HttpServer.StreamRouteHandler"/>
        public delegate Task StreamRouteHandler(NetworkConnection connection, HttpRequest request, ChunkedStream chunkedStream, CancellationToken cancellationToken);

        /// <inheritdoc cref="HttpServer.UploadStreamRouteHandler"/>
        public delegate void UploadStreamRouteHandler(NetworkConnection connection, HttpRequest request, UploadStream uploadStream, CancellationToken cancellationToken);

        #endregion

        #region Constructors

        /// <summary>
        /// Creates a standalone handler for protocol detection only (e.g., <see cref="CanHandle"/>).
        /// Route mapping and request processing require the server-backed constructor.
        /// </summary>
        public HttpProtocolHandler() { }

        /// <summary>
        /// Creates an <see cref="HttpProtocolHandler"/> backed by the given
        /// <see cref="MutableTcpServer"/>. Route registrations and HTTP request
        /// processing are delegated to the server's <see cref="HttpServer"/> base.
        /// </summary>
        internal HttpProtocolHandler(MutableTcpServer server) {
            _server = server ?? throw new ArgumentNullException(nameof(server));
        }

        #endregion

        #region Properties

        /// <summary>
        /// HTML served at the root path (<c>/</c>) for GET requests.
        /// Delegates to the underlying <see cref="HttpServer.IndexPageHtml"/>.
        /// </summary>
        public string IndexPageHtml {
            get => _server?.IndexPageHtml ?? "<html><body><h1>SocketJack HttpServer</h1></body></html>";
            set { if (_server != null) _server.IndexPageHtml = value; }
        }

        /// <summary>
        /// Content served at <c>/robots.txt</c>. Set to <see langword="null"/> to disable.
        /// Delegates to the underlying <see cref="HttpServer.Robots"/>.
        /// </summary>
        public string Robots {
            get => _server?.Robots;
            set { if (_server != null) _server.Robots = value; }
        }

        /// <summary>
        /// Minimum body size in bytes before the server switches to chunked transfer encoding.
        /// Delegates to the underlying <see cref="HttpServer.ChunkedThreshold"/>.
        /// </summary>
        public int ChunkedThreshold {
            get => _server?.ChunkedThreshold ?? 8192;
            set { if (_server != null) _server.ChunkedThreshold = value; }
        }

        /// <summary>
        /// Size of each chunk when using chunked transfer encoding.
        /// Delegates to the underlying <see cref="HttpServer.ChunkedSize"/>.
        /// </summary>
        public int ChunkedSize {
            get => _server?.ChunkedSize ?? 4096;
            set { if (_server != null) _server.ChunkedSize = value; }
        }

        /// <summary>
        /// Gets a snapshot of routes registered through this HTTP handler.
        /// </summary>
        public IReadOnlyList<HttpMappedRoute> MappedRoutes {
            get {
                EnsureServer();
                return _server.MappedRoutes;
            }
        }

        /// <inheritdoc cref="HttpServer.GetMappedRoutes"/>
        public IReadOnlyList<HttpMappedRoute> GetMappedRoutes() {
            EnsureServer();
            return _server.GetMappedRoutes();
        }

        /// <summary>
        /// When <see langword="true"/>, mapped directories that contain no
        /// <c>index.html</c> / <c>index.htm</c> will return an auto-generated
        /// HTML listing of their contents. Defaults to <see langword="false"/>.
        /// Delegates to the underlying <see cref="HttpServer.AllowDirectoryListing"/>.
        /// </summary>
        public bool AllowDirectoryListing {
            get => _server?.AllowDirectoryListing ?? false;
            set { if (_server != null) _server.AllowDirectoryListing = value; }
        }

        #endregion

        #region Events

        /// <summary>
        /// Raised for unmatched HTTP requests. Delegates to the server's
        /// <see cref="HttpServer.OnHttpRequest"/> event.
        /// </summary>
        public event HttpServer.RequestHandler OnHttpRequest {
            add { if (_server != null) _server.OnHttpRequest += value; }
            remove { if (_server != null) _server.OnHttpRequest -= value; }
        }

        #endregion

        #region Route Mapping

        private void EnsureServer() {
            if (_server == null)
                throw new InvalidOperationException("HttpProtocolHandler was not initialized with a server reference.");
        }

        /// <inheritdoc cref="HttpServer.Map(string, string, HttpServer.RouteHandler)"/>
        public void Map(string method, string path, RouteHandler handler) {
            EnsureServer();
            _server.Map(method, path, (HttpServer.RouteHandler)((conn, req, ct) => handler(conn, req, ct)));
            _server.SetMappedRouteMetadata(method, path, handler, null);
        }

        /// <inheritdoc cref="HttpServer.Map{T}(string, string, HttpServer.RouteHandler{T})"/>
        public void Map<T>(string method, string path, RouteHandler<T> handler) {
            EnsureServer();
            _server.Map<T>(method, path, (HttpServer.RouteHandler<T>)((conn, body, req, ct) => handler(conn, body, req, ct)));
            _server.SetMappedRouteMetadata(method, path, handler, typeof(T));
        }

        /// <inheritdoc cref="HttpServer.RemoveRoute"/>
        public bool RemoveRoute(string method, string path) {
            EnsureServer();
            return _server.RemoveRoute(method, path);
        }

        /// <inheritdoc cref="HttpServer.MapHost(string, string, string, HttpServer.RouteHandler)"/>
        public void MapHost(string hostName, string method, string path, RouteHandler handler) {
            EnsureServer();
            _server.MapHost(hostName, method, path, (HttpServer.RouteHandler)((conn, req, ct) => handler(conn, req, ct)));
        }

        /// <inheritdoc cref="HttpServer.MapHost{T}(string, string, string, HttpServer.RouteHandler{T})"/>
        public void MapHost<T>(string hostName, string method, string path, RouteHandler<T> handler) {
            EnsureServer();
            _server.MapHost<T>(hostName, method, path, (HttpServer.RouteHandler<T>)((conn, body, req, ct) => handler(conn, body, req, ct)));
        }

        /// <inheritdoc cref="HttpServer.RemoveHostRoute"/>
        public bool RemoveHostRoute(string hostName, string method, string path) {
            EnsureServer();
            return _server.RemoveHostRoute(hostName, method, path);
        }

        /// <inheritdoc cref="HttpServer.MapStream"/>
        public void MapStream(string method, string path, StreamRouteHandler handler) {
            EnsureServer();
            _server.MapStream(method, path, (HttpServer.StreamRouteHandler)((conn, req, chunked, ct) => handler(conn, req, chunked, ct)));
        }

        /// <inheritdoc cref="HttpServer.MapUploadStream"/>
        public void MapUploadStream(string method, string path, UploadStreamRouteHandler handler) {
            EnsureServer();
            _server.MapUploadStream(method, path, (HttpServer.UploadStreamRouteHandler)((conn, req, upload, ct) => handler(conn, req, upload, ct)));
        }

        /// <inheritdoc cref="HttpServer.RemoveUploadStreamRoute"/>
        public bool RemoveUploadStreamRoute(string method, string path) {
            EnsureServer();
            return _server.RemoveUploadStreamRoute(method, path);
        }

        /// <summary>
        /// Maps a local directory to a URL prefix.
        /// </summary>
        public void MapDirectory(string urlPrefix, string localDirectory) {
            EnsureServer();
            _server.MapDirectory(urlPrefix, localDirectory);
        }

        /// <summary>
        /// Maps a local directory to a URL prefix and configures a <c>.htaccess</c> file
        /// using the fluent <see cref="HtAccessBuilder"/> API for directory-level security.
        /// </summary>
        public void MapDirectory(string urlPrefix, string localDirectory, Action<HtAccessBuilder> configure) {
            EnsureServer();
            _server.MapDirectory(urlPrefix, localDirectory, configure);
        }

        /// <inheritdoc cref="HttpServer.RemoveDirectoryMapping"/>
        public bool RemoveDirectoryMapping(string urlPrefix) {
            EnsureServer();
            return _server.RemoveDirectoryMapping(urlPrefix);
        }

        /// <inheritdoc cref="HttpServer.MapFile"/>
        public void MapFile(string urlPath, string localFilePath) {
            EnsureServer();
            _server.MapFile(urlPath, localFilePath);
        }

        /// <inheritdoc cref="HttpServer.RemoveFileMapping"/>
        public bool RemoveFileMapping(string urlPath) {
            EnsureServer();
            return _server.RemoveFileMapping(urlPath);
        }

        /// <inheritdoc cref="HttpServer.MapDocumentRoot(string, HttpDocumentRootResolver)"/>
        public void MapDocumentRoot(string urlPrefix, HttpDocumentRootResolver resolver) {
            EnsureServer();
            _server.MapDocumentRoot(urlPrefix, resolver);
        }

        /// <inheritdoc cref="HttpServer.MapDocumentRoot(string, string)"/>
        public void MapDocumentRoot(string urlPrefix, string localDirectory) {
            EnsureServer();
            _server.MapDocumentRoot(urlPrefix, localDirectory);
        }

        /// <inheritdoc cref="HttpServer.RemoveDocumentRootMapping"/>
        public bool RemoveDocumentRootMapping(string urlPrefix) {
            EnsureServer();
            return _server.RemoveDocumentRootMapping(urlPrefix);
        }
        /// <inheritdoc cref="HttpServer.MapHostDirectory(string, string, string)"/>
        public void MapHostDirectory(string hostName, string urlPrefix, string localDirectory) {
            EnsureServer();
            _server.MapHostDirectory(hostName, urlPrefix, localDirectory);
        }

        /// <inheritdoc cref="HttpServer.MapHostDirectory(string, string, string, Action{HtAccessBuilder})"/>
        public void MapHostDirectory(string hostName, string urlPrefix, string localDirectory, Action<HtAccessBuilder> configure) {
            EnsureServer();
            _server.MapHostDirectory(hostName, urlPrefix, localDirectory, configure);
        }

        /// <inheritdoc cref="HttpServer.RemoveHostDirectoryMapping"/>
        public bool RemoveHostDirectoryMapping(string hostName, string urlPrefix) {
            EnsureServer();
            return _server.RemoveHostDirectoryMapping(hostName, urlPrefix);
        }

        /// <inheritdoc cref="HttpServer.MapHostFile"/>
        public void MapHostFile(string hostName, string urlPath, string localFilePath) {
            EnsureServer();
            _server.MapHostFile(hostName, urlPath, localFilePath);
        }

        /// <inheritdoc cref="HttpServer.RemoveHostFileMapping"/>
        public bool RemoveHostFileMapping(string hostName, string urlPath) {
            EnsureServer();
            return _server.RemoveHostFileMapping(hostName, urlPath);
        }

        /// <inheritdoc cref="HttpServer.MapSubdomain"/>
        public void MapSubdomain(string hostName, string targetBaseUrl) {
            EnsureServer();
            _server.MapSubdomain(hostName, targetBaseUrl);
        }

        /// <inheritdoc cref="HttpServer.ProxyHost(string, string)"/>
        public void ProxyHost(string hostName, string targetBaseUrl) {
            EnsureServer();
            _server.ProxyHost(hostName, targetBaseUrl);
        }

        /// <inheritdoc cref="HttpServer.ProxyHost(string, string, bool)"/>
        public void ProxyHost(string hostName, string targetBaseUrl, bool preserveHostHeader) {
            EnsureServer();
            _server.ProxyHost(hostName, targetBaseUrl, preserveHostHeader);
        }

        /// <inheritdoc cref="HttpServer.ProxyHost(string, string, int, bool)"/>
        public void ProxyHost(string hostName, string targetHost, int targetPort, bool useHttps = false) {
            EnsureServer();
            _server.ProxyHost(hostName, targetHost, targetPort, useHttps);
        }

        /// <inheritdoc cref="HttpServer.RemoveHostProxy"/>
        public bool RemoveHostProxy(string hostName) {
            EnsureServer();
            return _server.RemoveHostProxy(hostName);
        }

        #endregion

        #region IProtocolHandler

        /// <summary>
        /// Returns <see langword="true"/> when the first bytes look like an HTTP request
        /// (start with a known HTTP method keyword).
        /// </summary>
        public bool CanHandle(byte[] data) {
            if (data == null || data.Length < 3)
                return false;

            // Check for common HTTP method first bytes.
            byte first = data[0];
            if (first != (byte)'G' && first != (byte)'P' && first != (byte)'H' &&
                first != (byte)'D' && first != (byte)'O' && first != (byte)'T' &&
                first != (byte)'C')
                return false;

            // Verify the beginning spells out a known HTTP method.
            string prefix;
            int prefixLen = data.Length > 10 ? 10 : data.Length;
            prefix = Encoding.UTF8.GetString(data, 0, prefixLen);

            return prefix.StartsWith("GET ") || prefix.StartsWith("POST ") ||
                   prefix.StartsWith("PUT ") || prefix.StartsWith("DELETE ") ||
                   prefix.StartsWith("HEAD ") || prefix.StartsWith("OPTIONS ") ||
                   prefix.StartsWith("PATCH ") || prefix.StartsWith("TRACE ") ||
                   prefix.StartsWith("CONNECT ");
        }

        public void ProcessReceive(MutableTcpServer server, NetworkConnection connection, ref IReceivedEventArgs e) {
            server.GetRequestAsync(ref e);
        }

        public void OnDisconnected(MutableTcpServer server, NetworkConnection connection) {
            server.CleanupHttpConnection(connection.ID);
        }

        #endregion
    }

    #endregion
}
