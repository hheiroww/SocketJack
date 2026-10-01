using SocketJack.Extensions;
using SocketJack.Net.P2P;
using SocketJack.Serialization;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace SocketJack.Net {

    /// <summary>
    /// Multithreaded UDP Server.
    /// Modeled after TcpServer but uses connectionless UDP datagrams.
    /// </summary>
    public partial class UdpServer : NetworkBase {

        #region Properties

        /// <summary>
        /// (Optional) Name used for logging purposes.
        /// </summary>
        public override string Name {
            get {
                return _Name;
            }
            set {
                _Name = value;
            }
        }
        private string _Name;

        public bool IsListening { get; set; } = false;

        /// <summary>
        /// Connected clients tracked by their endpoint.
        /// </summary>
        public ConcurrentDictionary<string, UdpConnection> Clients {
            get {
                return _Clients;
            }
        }
        protected internal ConcurrentDictionary<string, UdpConnection> _Clients = new ConcurrentDictionary<string, UdpConnection>();

        public override int Port {
            get {
                return _Port;
            }
            set {
                _Port = value;
            }
        }

        /// <summary>
        /// Stop the client from updating metadata keys that are restricted, such as `Username`.
        /// <para>Not case sensitive.</para>
        /// </summary>
        public List<string> RestrictedMetadataKeys = new List<string>();

        #endregion

        #region Events

        public event ClientConnectedEventHandler ClientConnected;
        public delegate void ClientConnectedEventHandler(ConnectedEventArgs e);

        public event OnClientDisconnectedEventHandler ClientDisconnected;
        public delegate void OnClientDisconnectedEventHandler(DisconnectedEventArgs e);

        public event PortUnavailableEventHandler PortUnavailable;
        public delegate void PortUnavailableEventHandler(ref UdpServer Server, int Port);

        public event StoppedListeningEventHandler StoppedListening;
        public delegate void StoppedListeningEventHandler(UdpServer sender);

        private void UdpServer_InternalReceiveEvent(NetworkConnection Connection, Type objType, object obj, int BytesReceived) {
            if (Options.LogReceiveEvents) {
                string identity = Connection != null && Connection.Identity != null ? Connection.Identity.ID.ToUpper() : "Unknown";
                if (ReferenceEquals(objType, typeof(PeerRedirect))) {
                    PeerRedirect Redirect = (PeerRedirect)obj;
                    LogFormat("[{0}] Received {1} - {2}", new[] { Name + @"\" + identity, string.Format("PeerRedirect<{0}>", Redirect.CleanTypeName), BytesReceived.ByteToString() });
                } else {
                    LogFormat("[{0}] Received {1} - {2}", new[] { Name + @"\" + identity, objType.Name, BytesReceived.ByteToString() });
                }
            }
        }

        private void UdpServer_InternalReceiveByteCounter(NetworkConnection Connection, int BytesReceived) {
            if (Connection != null)
                Interlocked.Add(ref Connection.ReceivedBytesCounter, BytesReceived);
        }

        private void UdpServer_InternalSendEvent(NetworkConnection Connection, Type objType, object obj, int BytesSent) {
            if (Options.LogSendEvents) {
                string identity = Connection != null && Connection.Identity != null ? Connection.Identity.ID.ToUpper() : "Unknown";
                if (ReferenceEquals(objType, typeof(PeerRedirect))) {
                    PeerRedirect Redirect = (PeerRedirect)obj;
                    LogFormat("[{0}] Sent {1} - {2}", new[] { Name + @"\" + identity, string.Format("PeerRedirect<{0}>", Redirect.CleanTypeName), BytesSent.ByteToString() });
                } else {
                    LogFormat("[{0}] Sent {1} - {2}", new[] { Name + @"\" + identity, objType.Name, BytesSent.ByteToString() });
                }
            }
        }

        private void UdpServer_InternalSentByteCounter(NetworkConnection Connection, int BytesSent) {
            if (Connection != null)
                Interlocked.Add(ref Connection.SentBytesCounter, BytesSent);
        }

        private void UdpServer_BytesPerSecondUpdate(int ReceivedPerSecond, int SentPerSecond) {
            if (Environment.UserInteractive && Options.UpdateConsoleTitle) {
                try {
                    Console.Title = string.Format("{0} - Sent {1}/s Received {2}/s", new[] { Name, SentPerSecond.ByteToString(2), ReceivedPerSecond.ByteToString(2) });
                } catch (Exception) {
                }
            }
        }

        private void UdpServer_InternalSendToClient(string Recipient, string Sender, object Obj, int BytesReceived) {
            if (Recipient == "#ALL#") {
                var SenderGuid = Guid.TryParse(Sender, out Guid senderGuid) ? senderGuid : default;
                UdpConnection except = null;
                if (SenderGuid != default) {
                    foreach (var kvp in Clients) {
                        if (kvp.Value.ID == SenderGuid) {
                            except = kvp.Value;
                            break;
                        }
                    }
                }
                SendBroadcastUdp(new PeerRedirect(Sender, Recipient, Obj), except);
            } else {
                foreach (var kvp in Clients) {
                    if (kvp.Value.Identity != null && kvp.Value.Identity.ID == Recipient) {
                        SendTo(kvp.Value, new PeerRedirect(Sender, Recipient, Obj));
                        break;
                    }
                }
            }
        }

        private void OnReceived_MetadataKeyValue(ReceivedEventArgs<MetadataKeyValue> Args) {
            if (string.IsNullOrEmpty(Args.Object.Key)) return;
            if (!RestrictedMetadataKeys.Contains(Args.Object.Key.ToLower())) {
                if (Args.Connection != null && Args.Connection.Identity != null && Peers.ContainsKey(Args.Connection.Identity.ID)) {
                    Peers[Args.Connection.Identity.ID].SetMetaData(this, Args.Object.Key, Args.Object.Value, broadcastUpdate: false);
                    try {
                        var update = Peers[Args.Connection.Identity.ID].Clone<Identifier>();
                        update.Action = PeerAction.MetadataUpdate;
                        update.IP = string.Empty;
                        SendBroadcastUdp(update);
                    } catch {
                    }
                }
            }
        }

        private void OnPingReceived(ReceivedEventArgs<P2P.Ping> e) {
            if (e.Object == null || e.Connection == null) return;
            var pong = new P2P.Pong { TimestampMs = e.Object.TimestampMs };
            var id = e.Connection.ID;
            foreach (var kvp in Clients) {
                if (kvp.Value.ID == id) {
                    SendTo(kvp.Value, pong);
                    break;
                }
            }
        }

        #endregion

        #region Private/Internal

        private CancellationTokenSource _receiveCts;
        private CancellationTokenSource _sendCts;
        private CancellationTokenSource _heartbeatCts;
        private CancellationTokenSource _timeoutCts;
        private ConcurrentQueue<UdpSendItem> _sendQueue = new ConcurrentQueue<UdpSendItem>();
        private SemaphoreSlim _sendSignal = new SemaphoreSlim(0);

        /// <summary>
        /// Track last activity time per client endpoint.
        /// </summary>
        private ConcurrentDictionary<string, DateTime> _clientLastActivity = new ConcurrentDictionary<string, DateTime>();

        /// <summary>
        /// Map from client endpoint key to their NetworkConnection (for HandleReceive compatibility).
        /// </summary>
        private ConcurrentDictionary<string, NetworkConnection> _clientNetworkConnections = new ConcurrentDictionary<string, NetworkConnection>();

        private async void Init(int Port, string Name = "UdpServer") {
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

        private UdpConnection GetOrCreateClient(IPEndPoint remoteEP, UdpReliablePeer reliablePeer = null) {
            string key = remoteEP.ToString();
            _clientLastActivity.AddOrUpdate(key, DateTime.UtcNow);

            if (!Clients.ContainsKey(key)) {
                var conn = new UdpConnection(this, Socket, remoteEP);
                conn.ID = Guid.NewGuid();
                conn.IsServer = true;
                conn.ReliablePeer = reliablePeer;
                conn._Identity = Identifier.Create(conn.ID, false, remoteEP.Address.ToString());
                Clients.TryAdd(key, conn);

                var tcpConn = new NetworkConnection(this, null);
                tcpConn.ID = conn.ID;
                tcpConn._Identity = conn._Identity;
                tcpConn.EndPoint = remoteEP;
                if (Options.SafeMode) SafeModeHandshake.MarkVerified(tcpConn, Options);
                _clientNetworkConnections.TryAdd(key, tcpConn);

                LogFormat("[{0}] Client Connected.", new[] { Name + @"\" + conn.Identity.ID.ToUpper(), Port.ToString() });

                if (Options.UsePeerToPeer)
                    InitializePeer(conn);

                ClientConnected?.Invoke(new ConnectedEventArgs(this, tcpConn));

                conn.SendLocalIdentity();
            }
            return Clients[key];
        }

        private void RemoveClient(string key) {
            if (Clients.ContainsKey(key)) {
                var udpConn = Clients[key];
                string id = udpConn.Identity != null ? udpConn.Identity.ID.ToUpper() : "null";
                LogFormat("[{0}] Client Disconnected.", new[] { Name + @"\" + id, Port.ToString() });

                if (udpConn.Identity != null && Peers.Contains(udpConn.Identity)) {
                    Peers.Remove(udpConn.Identity);
                    SendBroadcastUdp(new Identifier(udpConn.ID.ToString(), PeerAction.Dispose));
                }

                NetworkConnection tcpConn = null;
                _clientNetworkConnections.TryRemove(key, out tcpConn);
                Clients.TryRemove(key, out _);
                _clientLastActivity.TryRemove(key, out _);

                ClientDisconnected?.Invoke(new DisconnectedEventArgs(this, tcpConn, DisconnectionReason.RemoteSocketClosed));
                udpConn.Dispose();
            }
        }

        private void InitializePeer(UdpConnection conn) {
            Task.Run(() => {
                Peers.AddOrUpdate(conn.Identity);
                SyncPeer(conn);
            });
        }

        private void SyncPeer(UdpConnection conn) {
            string key = conn.EndPoint.ToString();
            if (_clientNetworkConnections.ContainsKey(key)) {
                var tcpConn = _clientNetworkConnections[key];
                SendTo(conn, Peers.ToArrayWithLocal(tcpConn));
                foreach (var kvp in Clients) {
                    if (kvp.Key != key) {
                        SendTo(kvp.Value, conn.Identity);
                    }
                }
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
        /// Initializes a new instance of UdpServer.
        /// <para>Uses System.Text.Json as the default serializer.</para>
        /// </summary>
        /// <param name="Port">Socket Listen Port.</param>
        /// <param name="Name">Name used for Logging.</param>
        public UdpServer(int Port, string Name = "UdpServer") : base() {
            Init(Port, Name);
            InternalPeerRedirect += UdpServer_InternalSendToClient;
            InternalReceiveEvent += UdpServer_InternalReceiveEvent;
            InternalReceivedByteCounter += UdpServer_InternalReceiveByteCounter;
            InternalSendEvent += UdpServer_InternalSendEvent;
            InternalSentByteCounter += UdpServer_InternalSentByteCounter;
            BytesPerSecondUpdate += UdpServer_BytesPerSecondUpdate;
            RegisterCallback(new Action<ReceivedEventArgs<MetadataKeyValue>>(OnReceived_MetadataKeyValue));
            RegisterCallback<P2P.Ping>(OnPingReceived);
        }

        /// <summary>
        /// Initializes a new instance of UdpServer.
        /// </summary>
        /// <param name="Options">NetworkOptions for configuration.</param>
        /// <param name="Port">Socket Listen Port.</param>
        /// <param name="Name">Name used for Logging.</param>
        public UdpServer(NetworkOptions Options, int Port, string Name = "UdpServer") : base() {
            this.Options = Options;
            Init(Port, Name);
            InternalPeerRedirect += UdpServer_InternalSendToClient;
            InternalReceiveEvent += UdpServer_InternalReceiveEvent;
            InternalReceivedByteCounter += UdpServer_InternalReceiveByteCounter;
            InternalSendEvent += UdpServer_InternalSendEvent;
            InternalSentByteCounter += UdpServer_InternalSentByteCounter;
            BytesPerSecondUpdate += UdpServer_BytesPerSecondUpdate;
            RegisterCallback(new Action<ReceivedEventArgs<MetadataKeyValue>>(OnReceived_MetadataKeyValue));
            RegisterCallback<P2P.Ping>(OnPingReceived);
        }

        /// <summary>
        /// Starts listening on the specified Port for UDP datagrams.
        /// </summary>
        public bool Listen() {
            if (!IsListening) {
                Socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                Socket.ReceiveBufferSize = int.MaxValue;
                Socket.SendBufferSize = int.MaxValue;
                if (Options.EnableBroadcast)
                    Socket.EnableBroadcast = true;

                Connection = new NetworkConnection(this, null);

                try {
                    Socket.Bind(new IPEndPoint(IPAddress.Any, Port));
                    LogFormat("[{0}] Listening on port {1}.", new[] { Name, Port.ToString() });
                    IsListening = true;
                    if (Options.UdpMode == UdpMode.UDP_Reliable) {
                        ReliableTransport = new UdpReliableTransport(Socket, true, Options, ReliableConnected, ReliableReceive, ReliableFailed);
                        ReliableTransport.Start();
                    } else {
                        StartUdpReceiving();
                        StartUdpSending();
                    }
                    StartHeartbeat();
                    StartClientTimeout();
                    return true;
                } catch (Exception ex) {
                    IsListening = false;
                    ReliableTransport?.Dispose();
                    ReliableTransport = null;
                    Socket?.Close();
                    Socket = null;
                    InvokeOnError(Connection, ex);
                    return false;
                }
            } else {
                InvokeOnError(null, new Exception("Already listening."));
                return false;
            }
        }

        /// <summary>
        /// Stops listening.
        /// </summary>
        public void StopListening() {
            if (IsListening) {
                IsListening = false;
                ReliableTransport?.Dispose();
                ReliableTransport = null;
                _receiveCts?.Cancel();
                _sendCts?.Cancel();
                _heartbeatCts?.Cancel();
                _timeoutCts?.Cancel();

                foreach (var kvp in Clients.ToArray()) {
                    RemoveClient(kvp.Key);
                }

                if (Socket != null) {
                    MethodExtensions.TryInvoke(Socket.Close);
                    Socket = null;
                }

                // Drain the send queue
                while (_sendQueue.TryDequeue(out _)) { }

                // Clear internal tracking dictionaries
                _clientLastActivity.Clear();
                _clientNetworkConnections.Clear();

                // Dispose cancellation token sources
                _receiveCts?.Dispose();
                _sendCts?.Dispose();
                _heartbeatCts?.Dispose();
                _timeoutCts?.Dispose();
                _receiveCts = null;
                _sendCts = null;
                _heartbeatCts = null;
                _timeoutCts = null;

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
                Connection = null;
            } else {
                InvokeOnError(null, new Exception("Not listening."));
            }
        }

        /// <summary>
        /// Sends an object to a specific UDP client.
        /// </summary>
        /// <param name="Client">The UdpConnection representing the client.</param>
        /// <param name="Obj">Object to send.</param>
        public void SendTo(UdpConnection Client, object Obj) {
            if (Options.UdpMode == UdpMode.UDP_Reliable) { ObserveReliable(SendReliable(Client, Obj, false, CancellationToken.None)); return; }
            if (Client == null || Client.Closed || Client.EndPoint == null) return;

            var wrapped = new Wrapper(Obj, this);
            byte[] bytes = Options.Serializer.Serialize(wrapped);
            bytes = Client.PatternCache.PrepareSend(bytes, Options, frame => {
                byte[] candidate = Options.UseCompression ? Options.CompressionAlgorithm.Compress(frame) : frame;
                return candidate.Length <= Options.MaxDatagramSize;
            });
            byte[] processedBytes = Options.UseCompression ? Options.CompressionAlgorithm.Compress(bytes) : bytes;

            if (processedBytes.Length > Options.MaxDatagramSize) {
                InvokeOnError(Connection, new InvalidOperationException(
                    string.Format("Datagram too large ({0} bytes). UDP max is {1} bytes.", processedBytes.Length, Options.MaxDatagramSize)));
                return;
            }

            _sendQueue.Enqueue(new UdpSendItem(processedBytes, Client.EndPoint));
            try { _sendSignal.Release(); } catch (ObjectDisposedException) { }
        }

        /// <summary>
        /// Sends an object to a peer by Identifier.
        /// </summary>
        public override void Send(Identifier Recipient, object Obj) {
            if (Recipient != null) {
                foreach (var kvp in Clients) {
                    if (kvp.Value.Identity != null && kvp.Value.Identity.ID == Recipient.ID) {
                        SendTo(kvp.Value, Obj);
                        return;
                    }
                }
            }
        }

        /// <summary>
        /// Sends an object to a client by string ID.
        /// </summary>
        public void Send(string ID, object Obj) {
            foreach (var kvp in Clients) {
                if (kvp.Value.Identity != null && kvp.Value.Identity.ID == ID) {
                    SendTo(kvp.Value, Obj);
                    return;
                }
            }
        }

        /// <summary>
        /// Send a serializable object to all connected UDP clients.
        /// </summary>
        public override void SendBroadcast(object Obj) {
            SendBroadcastUdp(Obj);
        }

        /// <summary>
        /// Send a serializable object to all connected UDP clients except one.
        /// </summary>
        public override void SendBroadcast(object Obj, NetworkConnection Except) {
            UdpConnection exceptUdp = null;
            if (Except != null) {
                foreach (var kvp in Clients) {
                    if (kvp.Value.ID == Except.ID) {
                        exceptUdp = kvp.Value;
                        break;
                    }
                }
            }
            SendBroadcastUdp(Obj, exceptUdp);
        }

        /// <summary>
        /// Send a serializable object to all connected UDP clients.
        /// </summary>
        private void SendBroadcastUdp(object Obj, UdpConnection Except = null) {
            if (Options.UdpMode == UdpMode.UDP_Reliable) {
                foreach (var client in Clients.Values) if (client != Except) SendTo(client, Obj);
                return;
            }
            var wrapped = new Wrapper(Obj, this);
            byte[] bytes = Options.Serializer.Serialize(wrapped);

            foreach (var kvp in Clients.ToArray()) {
                if (Except != null && ReferenceEquals(kvp.Value, Except)) continue;
                if (kvp.Value != null && !kvp.Value.Closed && kvp.Value.EndPoint != null) {
                    byte[] clientBytes = kvp.Value.PatternCache.PrepareSend(bytes, Options, frame => {
                        byte[] candidate = Options.UseCompression ? Options.CompressionAlgorithm.Compress(frame) : frame;
                        return candidate.Length <= Options.MaxDatagramSize;
                    });
                    byte[] processedBytes = Options.UseCompression ? Options.CompressionAlgorithm.Compress(clientBytes) : clientBytes;
                    if (processedBytes.Length > Options.MaxDatagramSize) {
                        InvokeOnError(Connection, new InvalidOperationException(
                            string.Format("Datagram too large ({0} bytes). UDP max is {1} bytes.", processedBytes.Length, Options.MaxDatagramSize)));
                        continue;
                    }
                    _sendQueue.Enqueue(new UdpSendItem(processedBytes, kvp.Value.EndPoint));
                    try { _sendSignal.Release(); } catch (ObjectDisposedException) { }
                }
            }
        }

        #region UDP Receive/Send Loops

        private void StartUdpReceiving() {
            _receiveCts = new CancellationTokenSource();
            var token = _receiveCts.Token;
            Task.Factory.StartNew(async () => {
                byte[] buffer = new byte[Options.UdpReceiveBufferSize];
                EndPoint senderEP = new IPEndPoint(IPAddress.Any, 0);
                while (!token.IsCancellationRequested && IsListening) {
                    try {
                        if (Socket == null) {
                            await Task.Delay(1);
                            continue;
                        }
                        int bytesRead = Socket.ReceiveFrom(buffer, ref senderEP);
                        if (bytesRead > 0) {
                            byte[] data = new byte[bytesRead];
                            Buffer.BlockCopy(buffer, 0, data, 0, bytesRead);

                            IPEndPoint remoteEP = (IPEndPoint)senderEP;
                            if (Options.SafeMode && (SafeModeHandshake.HasMagic(data) || !Clients.ContainsKey(remoteEP.ToString()))) {
                                try {
                                    SafeModeHandshake.Validate(data, Options, null);
                                    Socket.SendTo(SafeModeHandshake.Accepted, remoteEP);
                                    GetOrCreateClient(remoteEP);
                                } catch {
                                    RemoveClient(remoteEP.ToString());
                                    Socket.SendTo(Encoding.ASCII.GetBytes("SJNO1"), remoteEP);
                                }
                                continue;
                            }
                            var udpConn = GetOrCreateClient(remoteEP);

                            Interlocked.Add(ref udpConn._TotalBytesReceived, bytesRead);

                            string key = remoteEP.ToString();
                            NetworkConnection tcpConn = null;
                            _clientNetworkConnections.TryGetValue(key, out tcpConn);

                            if (tcpConn != null) {
                                ((ISocket)this).InvokeInternalReceivedByteCounter(tcpConn, bytesRead);
                            }

                            Task.Run(() => DeserializeAndDispatch(data, bytesRead, remoteEP));
                        }
                    } catch (SocketException) when (token.IsCancellationRequested || !IsListening) {
                        break;
                    } catch (ObjectDisposedException) {
                        break;
                    } catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset) {
                        // WSAECONNRESET (10054) – a client became unreachable.
                        // Harmless for a UDP server; just continue receiving.
                        continue;
                    } catch (Exception ex) {
                        if (IsListening)
                            InvokeOnError(Connection, ex);
                    }
                }
            }, TaskCreationOptions.LongRunning);
        }

        private void StartUdpSending() {
            _sendCts = new CancellationTokenSource();
            var token = _sendCts.Token;
            Task.Factory.StartNew(async () => {
                while (!token.IsCancellationRequested && IsListening) {
                    UdpSendItem item;
                    if (_sendQueue.TryDequeue(out item)) {
                        try {
                            Socket.SendTo(item.Data, item.EndPoint);

                            string key = item.EndPoint.ToString();
                            if (Clients.ContainsKey(key)) {
                                Interlocked.Add(ref Clients[key]._TotalBytesSent, item.Data.Length);
                            }

                            NetworkConnection tcpConn = null;
                            _clientNetworkConnections.TryGetValue(key, out tcpConn);
                            if (tcpConn != null) {
                                ((ISocket)this).InvokeInternalSentByteCounter(tcpConn, item.Data.Length);
                                ((ISocket)this).InvokeInternalSendEvent(tcpConn, typeof(byte[]), "[DATAGRAM]", item.Data.Length);
                                ((ISocket)this).InvokeOnSent(new SentEventArgs(this, tcpConn, typeof(byte[]), item.Data.Length));
                            }
                        } catch (SocketException) when (token.IsCancellationRequested) {
                            break;
                        } catch (ObjectDisposedException) {
                            break;
                        } catch (Exception ex) {
                            if (IsListening)
                                InvokeOnError(Connection, ex);
                        }
                    } else {
                        try {
                            await _sendSignal.WaitAsync(100);
                        } catch (ObjectDisposedException) {
                            break;
                        }
                    }
                }
            }, TaskCreationOptions.LongRunning);
        }

        private void StartHeartbeat() {
            _heartbeatCts = new CancellationTokenSource();
            var token = _heartbeatCts.Token;
            Task.Factory.StartNew(async () => {
                while (!token.IsCancellationRequested && IsListening) {
                    await Task.Delay(1000);
                    int totalSent = 0;
                    int totalReceived = 0;
                    foreach (var kvp in Clients.ToArray()) {
                        var udpConn = kvp.Value;
                        udpConn._SentBytesPerSecond = udpConn.SentBytesCounter;
                        udpConn._ReceivedBytesPerSecond = udpConn.ReceivedBytesCounter;
                        Interlocked.Exchange(ref udpConn.SentBytesCounter, 0);
                        Interlocked.Exchange(ref udpConn.ReceivedBytesCounter, 0);
                        totalSent += udpConn._SentBytesPerSecond;
                        totalReceived += udpConn._ReceivedBytesPerSecond;
                    }

                    // Mirror aggregated totals onto the NetworkConnection so
                    // InvokeBytesPerSecondUpdate reads the correct values.
                    if (Connection != null) {
                        Connection._SentBytesPerSecond = totalSent;
                        Connection._ReceivedBytesPerSecond = totalReceived;
                        InvokeBytesPerSecondUpdate(Connection);
                    }
                }
            }, TaskCreationOptions.LongRunning);
        }

        private void StartClientTimeout() {
            _timeoutCts = new CancellationTokenSource();
            var token = _timeoutCts.Token;
            Task.Factory.StartNew(async () => {
                while (!token.IsCancellationRequested && IsListening) {
                    await Task.Delay(Options.ClientTimeoutCheckIntervalMs);
                    var now = DateTime.UtcNow;
                    foreach (var kvp in _clientLastActivity.ToArray()) {
                        if (Options.UdpMode == UdpMode.UDP_Reliable) continue;
                        if ((now - kvp.Value).TotalSeconds > Options.ClientTimeoutSeconds) {
                            RemoveClient(kvp.Key);
                        }
                    }
                }
            }, TaskCreationOptions.LongRunning);
        }

        private void DeserializeAndDispatch(byte[] bytes, int byteLength, IPEndPoint senderEP) {
            try {
                string key = senderEP.ToString();
                _clientLastActivity.AddOrUpdate(key, DateTime.UtcNow);

                byte[] data = bytes;
                if (Options.UseCompression && Options.UdpMode == UdpMode.UDP_Reliable) {
                    data = UdpReliableObjects.Decompress(data, Options);
                } else if (Options.UseCompression) {
                    var result = MethodExtensions.TryInvoke(b => UdpReliableObjects.Decompress(b, Options), ref data);
                    if (result.Success) {
                        data = result.Result;
                    } else {
                        InvokeOnError(Connection, result.Exception);
                        return;
                    }
                }

                NetworkConnection cacheConnection = null;
                _clientNetworkConnections.TryGetValue(key, out cacheConnection);
                if (cacheConnection != null && cacheConnection.PatternCache != null) {
                    if (!cacheConnection.PatternCache.TryResolveReceived(data, Options, out data, out string cacheError)) {
                        InvokeOnError(cacheConnection, new Exception(cacheError));
                        return;
                    }
                }

                object message = InboundMessageDecoder.Read(this, cacheConnection, data);
                if (message != null) HandleReceive(cacheConnection, message, message.GetType(), byteLength);
            } catch (Exception ex) {
                {
                    if (Clients.TryGetValue(senderEP.ToString(), out var invalid)) invalid.ReliablePeer?.Fail(SafeModeHandshake.Rejected());
                    RemoveClient(senderEP.ToString());
                }
                InvokeOnError(Connection, ex);
            }
        }

        #endregion
    }
}
