using SocketJack.Extensions;
using SocketJack.Net.P2P;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace SocketJack.Net {

    /// <summary>
    /// Stores SNI certificate bindings for servers that host multiple TLS domains on one listener.
    /// </summary>
    public sealed class SslCertificateBindingCollection {
        private readonly Dictionary<string, X509Certificate> _bindings = new Dictionary<string, X509Certificate>(StringComparer.OrdinalIgnoreCase);
        private readonly object _sync = new object();

        public int Count {
            get {
                lock (_sync)
                    return _bindings.Count;
            }
        }

        public void Add(string hostName, X509Certificate certificate) {
            hostName = TcpServer.NormalizeSslHostName(hostName);
            if (string.IsNullOrWhiteSpace(hostName))
                throw new ArgumentException("Host name is required.", nameof(hostName));
            if (certificate == null)
                throw new ArgumentNullException(nameof(certificate));

            lock (_sync)
                _bindings[hostName] = certificate;
        }

        public bool Remove(string hostName) {
            hostName = TcpServer.NormalizeSslHostName(hostName);
            if (string.IsNullOrWhiteSpace(hostName))
                return false;
            lock (_sync)
                return _bindings.Remove(hostName);
        }

        public void Clear() {
            lock (_sync)
                _bindings.Clear();
        }

        public X509Certificate Resolve(string hostName) {
            hostName = TcpServer.NormalizeSslHostName(hostName);
            if (string.IsNullOrWhiteSpace(hostName))
                return null;

            lock (_sync) {
                if (_bindings.TryGetValue(hostName, out X509Certificate exact))
                    return exact;

                int dot = hostName.IndexOf('.');
                while (dot > 0 && dot < hostName.Length - 1) {
                    string wildcard = "*" + hostName.Substring(dot);
                    if (_bindings.TryGetValue(wildcard, out X509Certificate wildcardCertificate))
                        return wildcardCertificate;
                    dot = hostName.IndexOf('.', dot + 1);
                }

                return _bindings.TryGetValue("*", out X509Certificate fallback) ? fallback : null;
            }
        }
    }

    /// <summary>
    /// Multithreaded TCP Server.
    /// </summary>
    /// <remarks></remarks>
    public class TcpServer : NetworkBase {

        #region Properties

        /// <summary>
        /// (Optional) Name used for logging purposes.
        /// </summary>
        /// <returns></returns>
        public override string Name {
            get {
                return PeerToPeerInstance ? _Name + @"\P2P" : _Name;
            }
            set {
                _Name = value;
            }
        }
        private string _Name;

        /// <summary>
        /// Server certificate for SSL connections.
        /// </summary>
        public X509Certificate SslCertificate { get; set; }

        /// <summary>
        /// Optional server-side certificate selector used for SNI-based HTTPS hosting.
        /// </summary>
        public Func<string, X509Certificate> SslCertificateSelector { get; set; }

        /// <summary>
        /// Host-name to certificate mappings used for SNI-based HTTPS hosting.
        /// Exact host names and wildcard names such as <c>*.example.com</c> are supported.
        /// </summary>
        public SslCertificateBindingCollection SslCertificateBindings { get; } = new SslCertificateBindingCollection();

        public bool IsListening { get; set; } = false;

        /// <summary>
        /// Connected Clients.
        /// </summary>
        /// <returns></returns>
        public ConcurrentDictionary<Guid, NetworkConnection> Clients {
            get {
                return _Clients;
            }
        }
        protected internal ConcurrentDictionary<Guid, NetworkConnection> _Clients = new ConcurrentDictionary<Guid, NetworkConnection>();

        public override int Port {
            get {
                return _Port;
            }
            set {
                _Port = value;
            }
        }

        public bool RawTcpMode { get; set; }
        public bool SuppressConnectionTest { get; set; }

        #endregion

        #region Events

        public event ClientConnectedEventHandler ClientConnected;
        public delegate void ClientConnectedEventHandler(ConnectedEventArgs e);

        public event OnClientDisconnectedEventHandler ClientDisconnected;
        public delegate void OnClientDisconnectedEventHandler(DisconnectedEventArgs e);

        public event PortUnavailableEventHandler PortUnavailable;
        public delegate void PortUnavailableEventHandler(ref TcpServer Server, int Port);

        public event StoppedListeningEventHandler StoppedListening;
        public delegate void StoppedListeningEventHandler(TcpServer sender);

        protected internal void InvokeOnDisconnected(DisconnectedEventArgs e) {
            if (e.Connection != Connection) {
#if UNITY
            MainThread.Run(() => {
		        ClientDisconnected?.Invoke(e);
            });
#endif
#if WINDOWS
                Application.Current.Dispatcher.Invoke(() => {
                    ClientDisconnected?.Invoke(e);
                });
#endif
#if !UNITY && !WINDOWS
                ClientDisconnected?.Invoke(e);
#endif
            }
        }

        private void TcpServer_InternalReceiveEvent(NetworkConnection Connection, Type objType, object obj, int BytesReceived) {
            if (Options.LogReceiveEvents) {
                if (ReferenceEquals(objType, typeof(PeerRedirect))) {
                    PeerRedirect Redirect = (PeerRedirect)obj;
                    LogFormat("[{0}] Received {1} - {2}", new[] { Name + @"\" + Connection.Identity.ID.ToUpper(), string.Format("PeerRedirect<{0}>", Redirect.CleanTypeName), BytesReceived.ByteToString() });
                } else {
                    LogFormat("[{0}] Received {1} - {2}", new[] { Name + @"\" + Connection.Identity.ID.ToUpper(), objType.Name, BytesReceived.ByteToString() });
                }
            }
        }
        private void TcpServer_InternalReceiveByteCounter(NetworkConnection Connection, int BytesReceived) {
            Interlocked.Add(ref Connection.ReceivedBytesCounter, BytesReceived);
        }
        private void TcpServer_InternalSendEvent(NetworkConnection Connection, Type objType, object obj, int BytesSent) {
            if (Options.LogSendEvents) {
                if (ReferenceEquals(objType, typeof(PeerRedirect))) {
                    PeerRedirect Redirect = (PeerRedirect)obj;
                    LogFormat("[{0}] Sent {1} - {2}", new[] { Name + @"\" + Connection.Identity.ID.ToUpper(), string.Format("PeerRedirect<{0}>", Redirect.CleanTypeName), BytesSent.ByteToString() });
                } else {
                    LogFormat("[{0}] Sent {1} - {2}", new[] { Name + @"\" + Connection.Identity.ID.ToUpper(), objType.Name, BytesSent.ByteToString() });
                }
            }
        }
        private void TcpServer_InternalSentByteCounter(NetworkConnection Connection, int BytesSent) {
            Interlocked.Add(ref Connection.SentBytesCounter, BytesSent);
        }
        private void TcpServer_BytesPerSecondUpdate(int ReceivedPerSecond, int SentPerSecond) {
            if (Environment.UserInteractive && Options.UpdateConsoleTitle) {
                try {
                    Console.Title = string.Format("{0} - Sent {1}/s Received {2}/s", new[] { Name, Connection.BytesPerSecondSent.ByteToString(2),
                                                                                                   Connection.BytesPerSecondReceived.ByteToString(2) });
                } catch (Exception) {
                }
            }
        }
        private void TcpServer_OnClientDisconnected(DisconnectedEventArgs args) {
            string id = args.Connection.Identity == null ? "null" : args.Connection.Identity.ID.ToUpper();
            LogFormat("[{0}] Client Disconnected.", new[] { Name + @"\" + id, Port.ToString() });
            _Clients.Remove(args.Connection.ID);
#if WINDOWS
            if (args.Connection.Identity != null) {
                string disconnectedId = args.Connection.ID.ToString();
                foreach (var key in _ControlShareSessions.Keys) {
                    if (key.Sharer == disconnectedId || key.Viewer == disconnectedId)
                        _ControlShareSessions.TryRemove(key, out _);
                }
            }
#endif
            if (args.Connection.Identity != null && Peers.Contains(args.Connection.Identity)) {
                Peers.Remove(args.Connection.Identity);
                SendBroadcast(new Identifier(args.Connection.ID.ToString(), PeerAction.Dispose));
            }
            if (args.Connection.Identity != null && PeerServers.ContainsKey(args.Connection.Identity.ID)) {
                List<PeerServer> ServerList;
                lock (PeerServers)
                    ServerList = PeerServers[args.Connection.Identity.ID];
                for (int i = 0, loopTo = ServerList.Count - 1; i <= loopTo; i++) {
                    var Server = ServerList[i];
                    Send(Server.RemotePeer.ID, Server.ShutdownSignal());
                }
                lock (PeerServers)
                    PeerServers[args.Connection.Identity.ID] = ServerList;
            }
        }
        #endregion

        #region Peer To Peer

        /// <summary>
        /// Stop the client from updating metadata keys that are restricted, such as `Username`.
        /// <para>Not case sensitive.</para>
        /// </summary>
        public List<string> RestrictedMetadataKeys = new List<string>();

        private void TcpServer_PeerServerShutdown(object sender, PeerServer Server) {
            Guid ClientID = default;

            Guid.TryParse(Server.RemotePeer.ID, out ClientID);
            if (ClientID != default) {
                if (Clients.ContainsKey(ClientID)) {
                    if (Server.Shutdown) {
                        if (PeerServers.ContainsKey(Server.LocalClient.ID)) {
                            List<PeerServer> ServerList;
                            lock (PeerServers)
                                ServerList = PeerServers[Server.LocalClient.ID];
                            for (int i = ServerList.Count - 1; i >= 0; i -= 1) {
                                if ((ServerList[i].LocalClient.ID ?? "") == (Server.LocalClient.ID ?? "") && (ServerList[i].RemotePeer.ID ?? "") == (Server.RemotePeer.ID ?? "")) {
                                    ServerList.RemoveAt(i);
                                }
                                lock (PeerServers)
                                    PeerServers[Server.LocalClient.ID] = ServerList;
                            }
                        }
                    }
                    var Client = Clients[ClientID];
                    Client.Send(Server);
                }
            }

        }
        private void TcpServer_PeerRefusedConnection(object sender, ConnectionRefusedArgs e) {
            Send(e.Reference.Host, e);
        }
        private void TcpServer_PeerConnectionRequest(object sender, PeerServer Server) {
            Guid ClientID = default;

            Guid.TryParse(Server.RemotePeer.ID, out ClientID);
            if (ClientID != default) {
                if (Clients.ContainsKey(ClientID)) {
                    if (Server.Shutdown) {
                        if (PeerServers.ContainsKey(Server.LocalClient.ID)) {
                            List<PeerServer> ServerList;
                            lock (PeerServers)
                                ServerList = PeerServers[Server.LocalClient.ID];
                            for (int i = ServerList.Count - 1; i >= 0; i -= 1) {
                                if ((ServerList[i].LocalClient.ID ?? "") == (Server.LocalClient.ID ?? "") && (ServerList[i].RemotePeer.ID ?? "") == (Server.RemotePeer.ID ?? "")) {
                                    ServerList.RemoveAt(i);
                                }
                                lock (PeerServers)
                                    PeerServers[Server.LocalClient.ID] = ServerList;
                            }
                        }
                    } else {
                        lock (PeerServers) {
                            if (!PeerServers.ContainsKey(Server.LocalClient.ID)) {
                                PeerServers.Add(Server.LocalClient.ID, new List<PeerServer>() { Server });
                            } else if (!PeerServers[Server.LocalClient.ID].Contains(Server)) {
                                PeerServers[Server.LocalClient.ID].Add(Server);
                            }
                        }
                    }
                    var Client = Clients[ClientID];
                    Client.Send(Server);
                }
            }

        }
        private void TcpServer_InternalSendToClient(string Recipient, string Sender, object Obj, int BytesReceived) {
            if (Recipient == "#ALL#") {
                var SenderGuid = Guid.TryParse(Sender, out Guid senderGuid) ? senderGuid : default;
                if (SenderGuid != default && Clients.ContainsKey(SenderGuid)) {
                    var SenderClient = Clients[SenderGuid];
                    SendBroadcast(new PeerRedirect(Sender, Recipient, Obj), SenderClient);
                } else {
                    SendBroadcast(new PeerRedirect(Sender, Recipient, Obj));
                }
            } else {
                Guid ClientID = default;

                Guid.TryParse(Recipient, out ClientID);
                if (ClientID != default) {
                    if (Clients.ContainsKey(ClientID)) {
                        var Client = Clients[ClientID];
                        Client.Send(new PeerRedirect(Sender, Recipient, Obj));
                    }
                }
            }
        }
        /// <summary>
        /// Sends the current peer list to the given client and broadcasts the
        /// new peer to all other clients.
        /// </summary>
        protected internal virtual void SyncPeer(NetworkConnection Client) {
            Task.Run(() => {
                Client.Send(Peers.ToArrayWithLocal(Client));
                Clients.SendBroadcast(Client.Identity, Client);
            });
        }

        /// <summary>
        /// Registers a connection as a peer and synchronizes peer lists.
        /// <c>Peers.AddOrUpdate</c> runs synchronously so the peer is
        /// immediately visible to message handlers; the network sync runs
        /// asynchronously.
        /// </summary>
        protected internal void InitializePeer(NetworkConnection Client) {
            if (Client?.Identity == null) return;
            Peers.AddOrUpdate(Client.Identity);
            SyncPeer(Client);
        }
        private void OnReceived_MetadataKeyValue(ReceivedEventArgs<MetadataKeyValue> Args) {
            if (string.IsNullOrEmpty(Args.Object.Key)) return;
            if (!RestrictedMetadataKeys.Contains(Args.Object.Key.ToLower())) {
                // Update metadata locally without triggering an immediate broadcast per change.
                Peers[Args.Connection.Identity.ID].SetMetaData(this, Args.Object.Key, Args.Object.Value, broadcastUpdate: false);

                // Single broadcast of the updated metadata to all clients.
                try {
                    var update = Peers[Args.Connection.Identity.ID].Clone<Identifier>();
                    update.Action = PeerAction.MetadataUpdate;
                    update.IP = string.Empty;
                    SendBroadcast(update);
                } catch {
                }
            }
        }

        private void OnPingReceived(ReceivedEventArgs<P2P.Ping> e) {
            if (e.Object == null || e.Connection == null) return;
            Send(e.Connection, new P2P.Pong { TimestampMs = e.Object.TimestampMs });
        }
        #endregion

        #region Private/Internal

        private Dictionary<string, List<PeerServer>> PeerServers = new();
        private bool DelayListen = false;
        protected internal int ActiveClients = 0;
        protected internal int PendingConnections = 0;

        /// <summary>
        /// When <see langword="true"/>, <see cref="NewConnection"/> will not call
        /// <see cref="InitializePeer"/> automatically.  Derived classes (e.g.
        /// <see cref="MutableTcpServer"/>) set this so that peer initialization
        /// is deferred until the connection's protocol has been identified.
        /// </summary>
        protected internal bool DeferPeerInitialization = false;

        private void DelayListenDelegate(int MTU, IPAddress LocalIP) => InvokeDelayedListen();
        private async void Init(int Port, string Name = "TcpServer") {
            try {
                this.Name = Name;
                this.Port = Port;
                if (!await NIC.PortAvailable(Port)) {
                    var argServer1 = this;
                    PortUnavailable?.Invoke(ref argServer1, Port);
                    InvokeOnError(null, new Exception("Port already in use."));
                }
                var argServer2 = (ISocket)this;
                Globals.RegisterServer(ref argServer2);
            } catch (Exception ex) {
                InvokeOnError(Connection, ex);
            }
        }
        protected internal void StartServerLoop() {
            Task.Factory.StartNew(async () => {
                while (IsListening) {
                    int AcceptNext = Options.Backlog - PendingConnections;
                    if (AcceptNext > 0) {
                        Parallel.For(1, AcceptNext, (i) => {
                            if (PendingConnections + 1 <= Options.Backlog) {
                                Interlocked.Increment(ref PendingConnections);
                                Socket.BeginAccept(new AsyncCallback(AcceptCallback), null);
                            }
                            });
                            }
                            await Task.Delay(10);
                        }
                        LogFormat("[{0}] Shutdown Started *:{1}", new[] { Name, Port.ToString() });
                Clients.ToList().ForEach((KeyValuePair<Guid, NetworkConnection> kvp) => {
                    if (kvp.Value != null && !kvp.Value.Closed) {
                        ClientDisconnected?.Invoke(new DisconnectedEventArgs(this, kvp.Value, DisconnectionReason.LocalSocketClosed));
                        CloseConnection(kvp.Value);
                    }
                });
                CloseConnection(Connection);
                while (Clients.Count != 0)
                    await Task.Delay(1);
                LogFormat("[{0}] Shutdown Complete *:{1}", new[] { Name, Port.ToString() });
            }, TaskCreationOptions.LongRunning);
        }
        protected internal async void AcceptCallback(IAsyncResult ar) {
            if (!IsListening)
                return;
            var tryResult = MethodExtensions.TryInvoke(Socket.EndAccept, ref ar);
            if (PendingConnections >= 1) Interlocked.Decrement(ref PendingConnections);
            if (tryResult.Success) {
                var newSocket = tryResult.Result;
                if (newSocket.Connected) {
                    var newConnection = await NewConnection(newSocket);
                    if (newConnection == null)
                        return;
                    LogFormat("[{0}] Client Connected.", new[] { Name + @"\" + newConnection.Identity.ID.ToUpper(), Port.ToString() });
                    ClientConnected?.Invoke(new ConnectedEventArgs(this, newConnection));
                }
            }
        }

private async Task<NetworkConnection> NewConnection(Socket handler) {
    handler.NoDelay = true;
    var newConnection = new NetworkConnection(this, handler);
    newConnection._Stream = new NetworkStream(newConnection.Socket);
            try {
                if (Options.UseSsl)
                    newConnection.InitializeSslStream(SslCertificate, SslTargetHost, GetSslCertificateSelector());
                if (!RawTcpMode && Options.UseTerminatedStreams)
                    await SafeModeHandshake.Exchange(newConnection, Options, true);
            } catch (Exception ex) {
                InvokeOnError(newConnection, ex);
                CloseConnection(newConnection, DisconnectionReason.Unknown);
                return null;
            }
            // GUID collisions are practically impossible; assign directly
            // without sleeping to avoid unnecessary latency per connection.
            newConnection.ID = Guid.NewGuid();
            while (!Clients.TryAdd(newConnection.ID, newConnection)) {
                newConnection.ID = Guid.NewGuid();
            }
            newConnection._Identity = Identifier.Create(newConnection);
            if (RawTcpMode) { newConnection.RawTcpMode = true; }
            if (SuppressConnectionTest) { newConnection.SuppressConnectionTest = true; }
            newConnection.StartReceiving();
            newConnection.StartSending();
            newConnection.StartConnectionTester();

            if (Options.UsePeerToPeer && !DeferPeerInitialization)
                InitializePeer(newConnection);
            return newConnection;
        }

        private Func<string, X509Certificate> GetSslCertificateSelector() {
            if (SslCertificateSelector == null && SslCertificateBindings.Count == 0)
                return null;

            return hostName => {
                string normalized = NormalizeSslHostName(hostName);
                X509Certificate selected = null;
                if (SslCertificateSelector != null)
                    selected = SslCertificateSelector(normalized);
                return selected ?? SslCertificateBindings.Resolve(normalized) ?? SslCertificate;
            };
        }

        public void BindSslCertificate(string hostName, X509Certificate certificate) {
            SslCertificateBindings.Add(hostName, certificate);
        }

        public void BindSslCertificate(IEnumerable<string> hostNames, X509Certificate certificate) {
            if (hostNames == null)
                throw new ArgumentNullException(nameof(hostNames));
            foreach (string hostName in hostNames)
                SslCertificateBindings.Add(hostName, certificate);
        }

        public bool RemoveSslCertificateBinding(string hostName) {
            return SslCertificateBindings.Remove(hostName);
        }

        public void ClearSslCertificateBindings() {
            SslCertificateBindings.Clear();
        }

        internal static string NormalizeSslHostName(string hostName) {
            if (string.IsNullOrWhiteSpace(hostName))
                return null;

            hostName = hostName.Trim().Replace("\r", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal);
            if (hostName.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                hostName.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) {
                if (Uri.TryCreate(hostName, UriKind.Absolute, out Uri uri))
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
        private void InvokeDelayedListen() {
            if (DelayListen) {
                DelayListen = false;
                NIC.OnInterfaceDiscovered -= this.DelayListenDelegate;
                LogAsync("Network Interface Card Discovered.");
                Listen();
            }
        }

        #endregion

        #region IDisposable Implementation

        protected override void Dispose(bool disposing) {
            StopListening();
            var argServer = (ISocket)this;
            Globals.UnregisterServer(ref argServer);
            IsListening = false;
            base.Dispose(disposing);
        }

        #endregion

        /// <summary>
        /// Initializes a new instance of TcpServer.
        /// <para>Uses System.Text.Json as the default serializer.</para>
        /// </summary>
        /// <param name="Port">Socket Listen Port.</param>
        /// <param name="Name">Name used for Logging.</param>
        public TcpServer(int Port, string Name = "TcpServer") : base() {
            Init(Port, Name);
            PeerServerShutdown += TcpServer_PeerServerShutdown;
            PeerRefusedConnection += TcpServer_PeerRefusedConnection;
            PeerConnectionRequest += TcpServer_PeerConnectionRequest;
            InternalPeerRedirect += TcpServer_InternalSendToClient;
            InternalReceiveEvent += TcpServer_InternalReceiveEvent;
            InternalReceivedByteCounter += TcpServer_InternalReceiveByteCounter;
            InternalSendEvent += TcpServer_InternalSendEvent;
            InternalSentByteCounter += TcpServer_InternalSentByteCounter;
            BytesPerSecondUpdate += TcpServer_BytesPerSecondUpdate;
            ClientDisconnected += TcpServer_OnClientDisconnected;
            RegisterCallback(new Action<ReceivedEventArgs<MetadataKeyValue>>(OnReceived_MetadataKeyValue));
            RegisterCallback<P2P.Ping>(OnPingReceived);
        }

        /// <summary>
        /// Initializes a new instance of TcpServer.
        /// </summary>
        /// <param name="Port">Socket Listen Port.</param>
        /// <param name="Name">Name used for Logging.</param>
        public TcpServer(NetworkOptions Options, int Port, string Name = "TcpServer") : base() {
            this.Options = Options;
            Init(Port, Name);
            PeerServerShutdown += TcpServer_PeerServerShutdown;
            PeerRefusedConnection += TcpServer_PeerRefusedConnection;
            PeerConnectionRequest += TcpServer_PeerConnectionRequest;
            InternalPeerRedirect += TcpServer_InternalSendToClient;
            InternalReceiveEvent += TcpServer_InternalReceiveEvent;
            InternalReceivedByteCounter += TcpServer_InternalReceiveByteCounter;
            InternalSendEvent += TcpServer_InternalSendEvent;
            InternalSentByteCounter += TcpServer_InternalSentByteCounter;
            BytesPerSecondUpdate += TcpServer_BytesPerSecondUpdate;
            ClientDisconnected += TcpServer_OnClientDisconnected;
            RegisterCallback(new Action<ReceivedEventArgs<MetadataKeyValue>>(OnReceived_MetadataKeyValue));
            RegisterCallback<P2P.Ping>(OnPingReceived);
        }

        /// <summary>
        /// Starts listening on the specified Port.
        /// </summary>
        public bool Listen() {
            if (!IsListening) {
                PendingConnections = 0;
                try {
                    ValidateSslServerConfiguration();
                    Socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    {
                        var withBlock = Socket;
                        withBlock.NoDelay = true;
                        withBlock.ReceiveBufferSize = int.MaxValue;
                        withBlock.SendBufferSize = int.MaxValue;
                        withBlock.ReceiveTimeout = -1;
                        withBlock.SendTimeout = -1;
                    }
                    Connection = new NetworkConnection(this, Socket);
                    Bind(Port);
                    //if (!NIC.InterfaceDiscovered) {
                    //    Log("Waiting for Network Interface Card..");
                    //    DelayListen = true;
                    //    NIC.OnInterfaceDiscovered += this.DelayListenDelegate;
                    //    return false;
                    //} else {
                    //    Socket.Listen(Options.Backlog);
                    //    LogFormat("[{0}] Listening on port {1}.", new[] { Name, Port.ToString() });
                    //    IsListening = true;
                    //    StartServerLoop();
                    //    return true;
                    //}
                    Socket.Listen(Options.Backlog);
                    LogFormat("[{0}] Listening on port {1}.", new[] { Name, Port.ToString() });
                    IsListening = true;
                    StartServerLoop();
                    return true;
                } catch (Exception ex) {
                    InvokeOnError(Connection, ex);
                    if (Socket != null) {
                        MethodExtensions.TryInvoke(Socket.Close);
                        Socket = null;
                    }
                    return false;
                }
            } else {
                InvokeOnError(null, new Exception("Already listening."));
                return false;
            }
        }

        private void ValidateSslServerConfiguration() {
            if (Options?.UseSsl != true)
                return;

            if (SslCertificate == null)
                throw new InvalidOperationException("SSL is enabled for " + Name + " on port " + Port.ToString() + ", but no server certificate is configured.");

            if (SslCertificate is not X509Certificate2 certificate)
                return;

            if (!certificate.HasPrivateKey)
                throw new InvalidOperationException("SSL certificate for " + Name + " on port " + Port.ToString() + " does not include an accessible private key.");

            try {
                using RSA rsa = certificate.GetRSAPrivateKey();
                using ECDsa ecdsa = certificate.GetECDsaPrivateKey();
                if (rsa == null && ecdsa == null)
                    throw new InvalidOperationException("SSL certificate private key is not RSA or ECDSA.");
            } catch (Exception ex) when (ex is not InvalidOperationException) {
                throw new InvalidOperationException("SSL certificate private key for " + Name + " on port " + Port.ToString() + " could not be opened by this process.", ex);
            }
        }

		/// <summary>
		/// Stops listening.
		/// </summary>
		public void StopListening() {
			if (IsListening) {
				IsListening = false;

				// Close all connected clients
				foreach (var kvp in Clients.ToArray()) {
					var conn = kvp.Value;
					if (conn != null && !conn.Closed) {
						conn.Close(this, DisconnectionReason.LocalSocketClosed);
					}
				}
				Clients.Clear();

				Connection.Close(this);

				// Explicitly close the listener socket (it is not "Connected" so
				// NetworkConnection.Close will skip it).
				if (Socket != null) {
					MethodExtensions.TryInvoke(Socket.Close);
					Socket = null;
				}

#if UNITY
			MainThread.Run(() => {
				StoppedListening?.Invoke(this);
			});
#endif
#if WINDOWS
			Application.Current.Dispatcher.Invoke(() => {
				StoppedListening?.Invoke(this);
			});
#endif
#if !UNITY && !WINDOWS
				StoppedListening?.Invoke(this);
#endif
				Peers.Clear();
			} else {
				InvokeOnError(null, new Exception("Not listening."));
			}
		}

        /// <summary>
        /// Sends an object to a client.
        /// </summary>
        /// <param name="Client">The Client's NetworkConnection.</param>
        /// <param name="Obj">Object to send to the client.</param>
        /// <remarks>Can also be accessed directly via NetworkConnection.Send()</remarks>
        public new void Send(NetworkConnection Client, object Obj) {
            if (Client != null && !Client.Closed && Client.Socket.Connected) {
                base.Send(Client, Obj);
            }
        }

        /// <summary>
        /// Sends an object to a peer.
        /// </summary>
        /// <param name="Client">The Client's NetworkConnection.</param>
        /// <param name="Obj">Object to send to the client.</param>
        /// <remarks>Can also be accessed directly via NetworkConnection.Send()</remarks>
        public override void Send(Identifier Recipient, object Obj) {
            if (Recipient != null) {
                var Client = Clients.Where(Clients => Clients.Value.Identity != null && Clients.Value.Identity.ID == Recipient.ID).Select(Clients => Clients.Value).FirstOrDefault();
                base.Send(Client, Obj);
            }
        }

        /// <summary>
        /// Sends an object to a client.
        /// </summary>
        /// <param name="ID">The clients's ID.</param>
        /// <param name="Obj">An object.</param>
        /// <remarks>Can also be accessed directly via NetworkConnection.Send()</remarks>
        public void Send(string ID, object Obj) {
            Guid ClientGuid = default;
            if (Guid.TryParse(ID, out ClientGuid)) {
                Send(ClientGuid, Obj);
            }
        }

        /// <summary>
        /// Sends an object to a client.
        /// </summary>
        /// <param name="ID">The clients's ID.</param>
        /// <param name="Obj">An Object.</param>
        /// <remarks>Can also be accessed directly from NetworkConnection.Send()</remarks>
        public void Send(Guid ID, object Obj) {
            if (Clients.ContainsKey(ID)) {
                var Client = Clients[ID];
                base.Send(Client, Obj);
            }
        }

        /// <summary>
        /// Send an object to an array of NetworkConnection.
        /// </summary>
        /// <param name="Clients">An array of ConnectedSocket</param>
        /// <param name="Obj">Object to send to the client.</param>
        /// <param name="Except">The client to exclude.</param>
        /// <remarks>Can be accessed directly from TcpServer.Clients.SendBroadcast()</remarks>
        public override void SendBroadcast(NetworkConnection[] Clients, object Obj, NetworkConnection Except = null) {
            this.Clients.SendBroadcast(Obj, Except);
        }

        /// <summary>
        /// Send a serializable object all ConnectedSockets.
        /// </summary>
        /// <param name="Obj">Serializable Object to send to the client.</param>
        /// <remarks>Can be accessed directly from TcpServer.Clients.SendBroadcast()</remarks>
        public override void SendBroadcast(object Obj) {
            Clients.SendBroadcast(Obj);
        }

        /// <summary>
        /// Send a serializable object to all ConnectedSockets (Except)
        /// </summary>
        /// <param name="Obj">Serializable Object to send to the client.</param>
        /// <param name="Except">The socket to exclude.</param>
        /// <remarks></remarks>
        public override void SendBroadcast(object Obj, NetworkConnection Except) {
            Clients.SendBroadcast(Obj, Except);
        }
    }
}
