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
    /// Multithreaded UDP Client.
    /// Modeled after TcpClient but uses connectionless UDP datagrams.
    /// </summary>
    public partial class UdpClient : NetworkBase {

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
        private string _Name = "UdpClient";

        public override Socket Socket { get { return _Socket; } set { } }
        private Socket _Socket = null;

        public override Stream Stream { get { return null; } set { } }

        public override IPEndPoint EndPoint { get { return _RemoteEndPoint; } set { } }
        private IPEndPoint _RemoteEndPoint;

        public override bool Connected {
            get {
                return _Connected && (Options?.UdpMode != UdpMode.UDP_Reliable || ReliablePeer?.IsReady == true);
            }
        }
        private bool _Connected = false;

        public bool Connecting {
            get {
                return _Connecting;
            }
        }
        private bool _Connecting = false;

        public string CurrentHost {
            get {
                return _CurrentHost;
            }
        }
        private string _CurrentHost;

        /// <summary>
        /// Connected clients on the Server. (Includes your connection)
        /// </summary>
        public new PeerList Peers {
            get {
                lock (base.Peers)
                    return base.Peers;
            }
        }

        private bool ResetAutoReconnect = false;

        #endregion

        #region Private/Internal

        private string LastHost;
        private int LastPort;
        private CancellationTokenSource _receiveCts;
        private CancellationTokenSource _sendCts;
        private CancellationTokenSource _heartbeatCts;
        protected internal bool isPeerUpdateSubscribed = false;
        protected internal UdpConnection UdpConn;

        private IPAddress[] ResolveAllIPv4FromHostEntry(string Host) {
            if (IPAddress.TryParse(Host, out var literal) && literal.AddressFamily == AddressFamily.InterNetwork) return new[] { literal };
            if (string.Equals(Host, "localhost", StringComparison.OrdinalIgnoreCase)) return new[] { IPAddress.Loopback };
            IPHostEntry hostEntry = null;
            MethodExtensions.TryInvoke(() => hostEntry = Dns.GetHostEntry(Host));
            if (hostEntry == null) {
                var address = IPAddress.Parse(Host);
                return new[] { address };
            } else {
                var IPv4Addresses = new List<IPAddress>();
                foreach (IPAddress IP in hostEntry.AddressList) {
                    if (!IP.ToString().Contains(":"))
                        IPv4Addresses.Add(IP);
                }
                if (IPv4Addresses.Count == 0) {
                    var address = IPAddress.Parse(Host);
                    return new[] { address };
                } else {
                    return IPv4Addresses.ToArray();
                }
            }
        }

        #endregion

        #region Events

        /// <summary>
        /// Fired when connected (bound and remote endpoint set) to the remote server.
        /// </summary>
        public event OnConnectedEventHandler OnConnected;
        public delegate void OnConnectedEventHandler(ConnectedEventArgs e);

        /// <summary>
        /// Fired when disconnected from the remote server.
        /// </summary>
        public event OnDisconnectedEventHandler OnDisconnected;
        public delegate void OnDisconnectedEventHandler(DisconnectedEventArgs e);

        /// <summary>
        /// Fired when connecting takes longer than ConnectionTimeout timespan.
        /// </summary>
        public event ConnectionFailedEventHandler ConnectionFailed;
        public delegate void ConnectionFailedEventHandler(ISocket sender, string Host, int Port);

        /// <summary>
        /// Fired when a user's metadata changes.
        /// </summary>
        public event PeerConnectedEventHandler PeerMetaDataChanged;
        public delegate void PeerMetaDataChangedEventHandler(ISocket sender, Identifier Peer);

        /// <summary>
        /// Fired when the local client is identified.
        /// </summary>
        public event OnIdentifiedEventHandler OnIdentified;
        public delegate void OnIdentifiedEventHandler(ISocket sender, Identifier LocalIdentity);

        public override void InvokeOnDisconnected(ISocket sender, NetworkConnection Connection) {
#if UNITY
            MainThread.Run(() => {
                OnDisconnected?.Invoke(new DisconnectedEventArgs(sender, Connection, DisconnectionReason.Unknown));
            });
#endif
#if WINDOWS
            Application.Current.Dispatcher.Invoke(() => {
                OnDisconnected?.Invoke(new DisconnectedEventArgs(sender, Connection, DisconnectionReason.Unknown));
            });
#endif
#if !UNITY && !WINDOWS
            OnDisconnected?.Invoke(new DisconnectedEventArgs(sender, Connection, DisconnectionReason.Unknown));
#endif
        }

        public override void InvokeOnConnected(ISocket sender, NetworkConnection Connection) {
#if UNITY
            MainThread.Run(() => {
                OnConnected?.Invoke(new ConnectedEventArgs(sender, Connection));
            });
#endif
#if WINDOWS
            Application.Current.Dispatcher.Invoke(() => {
                OnConnected?.Invoke(new ConnectedEventArgs(sender, Connection));
            });
#endif
#if !UNITY && !WINDOWS
            OnConnected?.Invoke(new ConnectedEventArgs(sender, Connection));
#endif
        }

        protected internal void InvokeOnIdentified(ISocket sender, Identifier Identity) {
#if UNITY
            MainThread.Run(() => {
                OnIdentified?.Invoke(sender, Identity);
            });
#endif
#if WINDOWS
            Application.Current.Dispatcher.Invoke(() => {
                OnIdentified?.Invoke(sender, Identity);
            });
#endif
#if !UNITY && !WINDOWS
            OnIdentified?.Invoke(sender, Identity);
#endif
        }

        protected internal void InvokeOnDisconnected(DisconnectedEventArgs e) {
#if UNITY
            MainThread.Run(() => {
                OnDisconnected?.Invoke(e);
            });
#endif
#if WINDOWS
            Application.Current.Dispatcher.Invoke(() => {
                OnDisconnected?.Invoke(e);
            });
#endif
#if !UNITY && !WINDOWS
            OnDisconnected?.Invoke(e);
#endif
        }

        private void UdpClient_PeerUpdate(ISocket sender, Identifier Peer) {
            switch (Peer.Action) {
                case PeerAction.MetadataUpdate: {
                        Peers.UpdateMetaData(Peer);
                        PeerMetaDataChanged?.Invoke(this, Peer);
                        break;
                    }
                case PeerAction.RemoteIdentity: {
                        if (Options.Logging) {
                            LogFormat(@"[{0}\{1}] Connected To Server.", new[] { Name, Peer.ID.ToUpper() });
                        }
                        if (!Peers.Contains(Peer)) {
                            var RefPeer = Peer.WithParent(this);
                            Peers.AddOrUpdate(RefPeer);
                            ((ISocket)this).InvokePeerConnected(this, RefPeer);
                        }
                        break;
                    }
                case PeerAction.Dispose: {
                        if (Options.Logging) {
                            LogFormat(@"[{0}\{1}] Disconnected From Server.", new[] { Name, Peer.ID.ToUpper() });
                        }
                        if (Peers.Contains(Peer)) {
                            ((ISocket)this).InvokePeerDisconnected(this, Peer);
                            Peers.Remove(Peer);
                        }
                        break;
                    }
            }
        }

        private void SetPeerID(ISocket sender, Identifier RemotePeer) {
            switch (RemotePeer.Action) {
                case PeerAction.LocalIdentity: {
                        if (Connection != null) {
                            Connection._Identity = RemotePeer;
                        }
                        LogFormat("[{0}] Local Identity = {1}", new[] { Name, RemotePeer.ID.ToUpper() });
                        InvokeOnIdentified(sender, RemotePeer);
                        break;
                    }
            }
        }

        private void UdpClient_InternalPeerRedirect(string Recipient, string Sender, object Obj, int ReceivedBytes) {
            Type RedirectType = Obj.GetType();
            var genericType = typeof(ReceivedEventArgs<>).MakeGenericType(RedirectType);
            var receivedEventArgs = ((IReceivedEventArgs)Activator.CreateInstance(genericType));
            receivedEventArgs.Initialize(this, Connection, Obj, ReceivedBytes, Sender);
            InvokeAllCallbacks(RedirectType, receivedEventArgs);
            InvokeOnReceive(new ReceivedEventArgs<object>(this, Connection, Obj, ReceivedBytes, Sender));
        }

        private async void UdpClient_OnDisconnected(DisconnectedEventArgs args) {
            base.Peers?.Clear();
            LogFormat("[{0}] Disconnected -> {1}:{2}", new[] { string.Format(@"{0}{1}", new[] { Name, RemoteIdentity == null ? "" : "\\" + RemoteIdentity.ID.ToUpper() }), LastHost, LastPort.ToString() });
            if (!isDisposed && Options?.AutoReconnect == true)
                await Reconnect();
        }

        private void UdpClient_InternalReceiveEvent(NetworkConnection Connection, Type objType, object obj, int BytesReceived) {
            if (Options.LogReceiveEvents) {
                if (ReferenceEquals(objType, typeof(PeerRedirect))) {
                    PeerRedirect Redirect = (PeerRedirect)obj;
                    LogFormat("[{0}] Received {1} - {2}", new[] { Name + @"\" + (Connection.Identity == null ? "Null" : Connection.Identity.ID.ToUpper()), string.Format("PeerRedirect<{0}>", Redirect.CleanTypeName), BytesReceived.ByteToString() });
                } else {
                    LogFormat("[{0}] Received {1} - {2}", new[] { Name + @"\" + (Connection.Identity == null ? "Null" : Connection.Identity.ID.ToUpper()), objType.Name, BytesReceived.ByteToString() });
                }
            }
        }

        private void UdpClient_InternalReceivedByteCounter(NetworkConnection Connection, int BytesReceived) {
            if (UdpConn != null)
                Interlocked.Add(ref UdpConn.ReceivedBytesCounter, BytesReceived);
        }

        private void UdpClient_InternalSendEvent(NetworkConnection Connection, Type objType, object obj, int BytesSent) {
            if (Options.LogSendEvents) {
                if (ReferenceEquals(objType, typeof(PeerRedirect))) {
                    PeerRedirect Redirect = (PeerRedirect)obj;
                    LogFormat("[{0}] Sent {1} - {2}", new[] { Name + @"\" + (Connection.Identity == null ? "Null" : Connection.Identity.ID.ToUpper()), string.Format("PeerRedirect<{0}>", Redirect.CleanTypeName), BytesSent.ByteToString() });
                } else {
                    LogFormat("[{0}] Sent {1} - {2}", new[] { Name + @"\" + (Connection.Identity == null ? "Null" : Connection.Identity.ID.ToUpper()), objType.Name, BytesSent.ByteToString() });
                }
            }
        }

        private void UdpClient_InternalSentByteCounter(NetworkConnection Connection, int BytesSent) {
            if (UdpConn != null)
                Interlocked.Add(ref UdpConn.SentBytesCounter, BytesSent);
        }

        private void UdpClient_BytesPerSecondUpdate(int ReceivedPerSecond, int SentPerSecond) {
            if (Environment.UserInteractive && Options.UpdateConsoleTitle) {
                try {
                    string sent = UdpConn != null ? UdpConn.BytesPerSecondSent.ByteToString(2) : "0 B";
                    string received = UdpConn != null ? UdpConn.BytesPerSecondReceived.ByteToString(2) : "0 B";
                    Console.Title = string.Format("{0} - Sent {1}/s Received {2}/s", new[] { Name, sent, received });
                } catch (Exception) {
                }
            }
        }

        #endregion

        #region IDisposable Implementation

        protected override void Dispose(bool disposing) {
            Options.AutoReconnect = false;
            if (_Connected)
                Disconnect();
            var argClient = (ISocket)this;
            Globals.UnregisterClient(ref argClient);
            // Clean up anything Disconnect may not have reached
            _receiveCts?.Cancel();
            _receiveCts?.Dispose();
            _sendCts?.Cancel();
            _sendCts?.Dispose();
            _heartbeatCts?.Cancel();
            _heartbeatCts?.Dispose();
            _receiveCts = null;
            _sendCts = null;
            _heartbeatCts = null;
            if (UdpConn != null) {
                UdpConn.Dispose();
                UdpConn = null;
            }
            if (_Socket != null) {
                MethodExtensions.TryInvoke(_Socket.Close);
                _Socket = null;
            }
            Connection = null;
            base.Dispose(disposing);
        }

        #endregion

        /// <summary>
        /// Initializes a new instance of UdpClient.
        /// <para>Uses System.Text.Json as the default serializer.</para>
        /// </summary>
        /// <param name="Name">Name used for logging.</param>
        public UdpClient(string Name = "UdpClient") : base() {
            this.Name = Name;
            var argClient = (ISocket)this;
            Globals.RegisterClient(ref argClient);
            PeerUpdate += UdpClient_PeerUpdate;
            PeerUpdate += SetPeerID;
            InternalPeerRedirect += UdpClient_InternalPeerRedirect;
            OnDisconnected += UdpClient_OnDisconnected;
            InternalReceiveEvent += UdpClient_InternalReceiveEvent;
            InternalReceivedByteCounter += UdpClient_InternalReceivedByteCounter;
            InternalSendEvent += UdpClient_InternalSendEvent;
            InternalSentByteCounter += UdpClient_InternalSentByteCounter;
            BytesPerSecondUpdate += UdpClient_BytesPerSecondUpdate;
        }

        /// <summary>
        /// Initializes a new instance of UdpClient.
        /// </summary>
        /// <param name="Options">NetworkOptions for configuration.</param>
        /// <param name="Name">Name used for logging.</param>
        public UdpClient(NetworkOptions Options, string Name = "UdpClient") : base() {
            this.Options = Options;
            this.Name = Name;
            var argClient = (ISocket)this;
            Globals.RegisterClient(ref argClient);
            PeerUpdate += UdpClient_PeerUpdate;
            PeerUpdate += SetPeerID;
            InternalPeerRedirect += UdpClient_InternalPeerRedirect;
            OnDisconnected += UdpClient_OnDisconnected;
            InternalReceiveEvent += UdpClient_InternalReceiveEvent;
            InternalReceivedByteCounter += UdpClient_InternalReceivedByteCounter;
            InternalSendEvent += UdpClient_InternalSendEvent;
            InternalSentByteCounter += UdpClient_InternalSentByteCounter;
            BytesPerSecondUpdate += UdpClient_BytesPerSecondUpdate;
        }

        /// <summary>
        /// Connect to a remote UDP server.
        /// <para>Since UDP is connectionless, this binds the local socket and sets the remote endpoint.</para>
        /// </summary>
        /// <param name="Host">The host to send datagrams to.</param>
        /// <param name="Port">The port the host uses.</param>
        public async Task<bool> Connect(string Host, int Port) {
            if (isDisposed)
                throw new ObjectDisposedException(Name + " is disposed, cannot connect.");
            if (_Connecting)
                return false;
            _Connecting = true;
            _CurrentHost = Host;
            LastHost = Host;
            LastPort = Port;

            try {
                LogFormat("[{0}] Connecting -> {1}:{2}...", new[] { Name, Host, Port.ToString() });

                IPAddress[] addresses = ResolveAllIPv4FromHostEntry(Host);
                if (addresses.Length == 0) {
                    LogFormat("[{0}] Connection -> {1}:{2} failed! No IPv4 addresses found.", new[] { Name, Host, Port.ToString() });
                    ConnectionFailed?.Invoke(this, Host, Port);
                    _Connecting = false;
                    return false;
                }

                safeModeReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _RemoteEndPoint = new IPEndPoint(addresses[0], Port);

                _Socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                _Socket.ReceiveBufferSize = int.MaxValue;
                _Socket.SendBufferSize = int.MaxValue;
                if (Options.EnableBroadcast)
                    _Socket.EnableBroadcast = true;

                _Socket.Bind(new IPEndPoint(IPAddress.Any, _Port));

                UdpConn = new UdpConnection(this, _Socket, _RemoteEndPoint);
                UdpConn.ID = Guid.NewGuid();
                Connection = new NetworkConnection(this, null);
                Connection.EndPoint = _RemoteEndPoint;

                _Connected = true;

                if (Options.UdpMode == UdpMode.UDP_Reliable) {
                    ReliableTransport = new UdpReliableTransport(_Socket, false, Options, p => { }, ReliableReceive, ReliableFailed);
                    ReliableTransport.Start();
                    using (var connectTimeout = new CancellationTokenSource(Options.ConnectionTimeout)) {
                        ReliablePeer = await ReliableTransport.Connect(_RemoteEndPoint, connectTimeout.Token);
                        UdpConn.ReliablePeer = ReliablePeer;
                        ReliablePeer.Transfers = new UdpReliableTransfers(ReliablePeer, request => TransferRequested?.Invoke(request));
                    }
                } else {
                    StartUdpReceiving();
                    StartUdpSending();
                }
                if (Options.SafeMode) {
                    byte[] hello = SafeModeHandshake.Create(Options);
                    var deadline = DateTime.UtcNow.AddSeconds(5);
                    while (!Connection.SafeModeVerified && DateTime.UtcNow < deadline) {
                        if (Options.UdpMode == UdpMode.UDP_Reliable)
                            await ReliablePeer.Send(hello, 0, false, false, CancellationToken.None);
                        else _Socket.SendTo(hello, _RemoteEndPoint);
                        await Task.WhenAny(safeModeReady.Task, Task.Delay(300));
                        if (safeModeReady.Task.IsFaulted) await safeModeReady.Task;
                    }
                    if (!Connection.SafeModeVerified) throw SafeModeHandshake.Rejected();
                }
                StartUdpHeartbeat();

                LogFormat("[{0}] Connected  -> {1}:{2}", new[] { Name, Host, Port.ToString() });

                if (ResetAutoReconnect) {
                    ResetAutoReconnect = false;
                    Options.AutoReconnect = true;
                }

                StartPingLoop();
                OnConnected?.Invoke(new ConnectedEventArgs(this, Connection));
                _Connecting = false;
                return true;
            } catch (Exception ex) {
                _Connecting = false;
                Disconnect();
                InvokeOnError(null, ex);
                return false;
            }
        }

        /// <summary>
        /// Attempt to connect to the last host and port.
        /// </summary>
        public async Task<bool> Reconnect() {
            return await Connect(LastHost, LastPort);
        }

        /// <summary>
        /// Disconnect from the server.
        /// </summary>
        public void Disconnect() {
            if (Options?.AutoReconnect == true && !isDisposed) {
                Options.AutoReconnect = false;
                ResetAutoReconnect = true;
            }
            if (_Connected) {
                var reliableTransport = ReliableTransport;
                ReliableTransport = null;
                ReliablePeer = null;
                _CurrentHost = default;
                _Connected = false;
                reliableTransport?.Dispose();
                StopPingLoop();
                _receiveCts?.Cancel();
                _sendCts?.Cancel();
                _heartbeatCts?.Cancel();
                if (UdpConn != null) {
                    UdpConn.Dispose();
                    UdpConn = null;
                }
                if (_Socket != null) {
                    MethodExtensions.TryInvoke(_Socket.Close);
                    _Socket = null;
                }
                var conn = Connection;
                Connection = null;
                InvokeOnDisconnected(new DisconnectedEventArgs(this, conn, DisconnectionReason.LocalSocketClosed));
                Peers.Clear();
                _receiveCts?.Dispose();
                _sendCts?.Dispose();
                _heartbeatCts?.Dispose();
                _receiveCts = null;
                _sendCts = null;
                _heartbeatCts = null;
            }
        }

        /// <summary>
        /// Send an object to the server as a UDP datagram.
        /// </summary>
        /// <param name="Obj">An object.</param>
        public void Send(object Obj) {
            if (Options.UdpMode == UdpMode.UDP_Reliable) { ObserveReliable(SendReliable(Obj, false, CancellationToken.None)); return; }
            if (!_Connected || _Socket == null || _RemoteEndPoint == null) return;

            var wrapped = new Wrapper(Obj, this);
            byte[] bytes = Options.Serializer.Serialize(wrapped);
            if (UdpConn != null) {
                bytes = UdpConn.PatternCache.PrepareSend(bytes, Options, frame => {
                    byte[] candidate = Options.UseCompression ? Options.CompressionAlgorithm.Compress(frame) : frame;
                    return candidate.Length <= Options.MaxDatagramSize;
                });
            }
            byte[] processedBytes = Options.UseCompression ? Options.CompressionAlgorithm.Compress(bytes) : bytes;

            if (processedBytes.Length > Options.MaxDatagramSize) {
                InvokeOnError(Connection, new InvalidOperationException(
                    string.Format("Datagram too large ({0} bytes). UDP max is {1} bytes.", processedBytes.Length, Options.MaxDatagramSize)));
                return;
            }

            if (UdpConn != null) {
                UdpConn.SendQueue.Enqueue(new UdpSendItem(processedBytes, _RemoteEndPoint));
                try { UdpConn._SendSignal.Release(); } catch (ObjectDisposedException) { }
            }
        }

        /// <inheritdoc />
        protected override void SendPingInternal(object obj) {
            Send(obj);
        }

        /// <summary>
        /// Send an object to a Remote Client on the server via peer redirect.
        /// <para>This will <see langword="NOT"/> expose your remote IP.</para>
        /// </summary>
        public override void Send(Identifier Recipient, object Obj) {
            if (RemoteIdentity is null) {
                InvokeOnError(Connection, new P2PException("P2P Client not yet initialized." + Environment.NewLine +
                                                           "    ConnectedSocket.Identifier property cannot equal null." + Environment.NewLine +
                                                           "    Invoke via UdpClient.OnIdentified Event instead of UdpClient.OnConnected."));
            } else {
                Send(new PeerRedirect(RemoteIdentity.ID, Recipient.ID, Obj));
            }
        }

        /// <summary>
        /// Send an object to all Remote Clients on the server via peer broadcast.
        /// </summary>
        public void SendPeerBroadcast(object Obj) {
            if (RemoteIdentity is null) {
                InvokeOnError(Connection, new P2PException("P2P Client not yet initialized." + Environment.NewLine +
                                                           "    ConnectedSocket.Identifier property cannot equal null." + Environment.NewLine +
                                                           "    Invoke via UdpClient.OnIdentified Event instead of UdpClient.OnConnected."));
            } else {
                Send(new PeerRedirect(RemoteIdentity.ID, "#ALL#", Obj));
            }
        }

        #region UDP Receive/Send Loops

        private void StartUdpReceiving() {
            _receiveCts = new CancellationTokenSource();
            var token = _receiveCts.Token;
            Task.Factory.StartNew(async () => {
                byte[] buffer = new byte[Options.UdpReceiveBufferSize];
                EndPoint senderEP = new IPEndPoint(IPAddress.Any, 0);
                while (!token.IsCancellationRequested && !isDisposed && _Connected) {
                    try {
                        if (_Socket == null || _Socket.Available == 0) {
                            await Task.Delay(1);
                            continue;
                        }
                        int bytesRead = _Socket.ReceiveFrom(buffer, ref senderEP);
                        if (bytesRead > 0) {
                            byte[] data = new byte[bytesRead];
                            Buffer.BlockCopy(buffer, 0, data, 0, bytesRead);

                            if (UdpConn != null) {
                                Interlocked.Add(ref UdpConn._TotalBytesReceived, bytesRead);
                                ((ISocket)this).InvokeInternalReceivedByteCounter(Connection, bytesRead);
                            }

                            if (!_RemoteEndPoint.Equals(senderEP)) continue;
                            if (ReceiveSafeModeReply(data)) continue;
                            Task.Run(() => DeserializeAndDispatch(data, bytesRead, (IPEndPoint)senderEP));
                        }
                    } catch (SocketException) when (token.IsCancellationRequested || isDisposed || !_Connected) {
                        break;
                    } catch (ObjectDisposedException) {
                        break;
                    } catch (SocketException ex) {
                        // WSAECONNRESET (10054) occurs on Windows when the remote endpoint
                        // is unreachable (e.g. server stopped).  Treat it as a disconnection
                        // rather than a fatal error.
                        if (ex.SocketErrorCode == SocketError.ConnectionReset) {
                            if (_Connected) {
                                _Connected = false;
                                StopPingLoop();
                                _sendCts?.Cancel();
                                _heartbeatCts?.Cancel();
                                if (UdpConn != null) {
                                    UdpConn.Dispose();
                                    UdpConn = null;
                                }
                                if (_Socket != null) {
                                    MethodExtensions.TryInvoke(_Socket.Close);
                                    _Socket = null;
                                }
                                var conn = Connection;
                                Connection = null;
                                InvokeOnDisconnected(new DisconnectedEventArgs(this, conn, DisconnectionReason.RemoteSocketClosed));
                                Peers.Clear();
                            }
                            break;
                        }
                        if (!isDisposed && _Connected)
                            InvokeOnError(Connection, ex);
                    } catch (Exception ex) {
                        if (!isDisposed && _Connected)
                            InvokeOnError(Connection, ex);
                    }
                }
            }, TaskCreationOptions.LongRunning);
        }

        private void StartUdpSending() {
            _sendCts = new CancellationTokenSource();
            var token = _sendCts.Token;
            Task.Factory.StartNew(async () => {
                while (!token.IsCancellationRequested && !isDisposed && _Connected) {
                    if (UdpConn == null) {
                        await Task.Delay(1);
                        continue;
                    }

                    UdpSendItem item;
                    if (UdpConn.SendQueue.TryDequeue(out item)) {
                        try {
                            _Socket.SendTo(item.Data, item.EndPoint);
                            Interlocked.Add(ref UdpConn._TotalBytesSent, item.Data.Length);
                            ((ISocket)this).InvokeInternalSentByteCounter(Connection, item.Data.Length);
                            ((ISocket)this).InvokeInternalSendEvent(Connection, typeof(byte[]), "[DATAGRAM]", item.Data.Length);
                            ((ISocket)this).InvokeOnSent(new SentEventArgs(this, Connection, typeof(byte[]), item.Data.Length));
                        } catch (SocketException) when (token.IsCancellationRequested || isDisposed) {
                            break;
                        } catch (ObjectDisposedException) {
                            break;
                        } catch (Exception ex) {
                            if (!isDisposed && _Connected)
                                InvokeOnError(Connection, ex);
                        }
                    } else {
                        try {
                            await UdpConn._SendSignal.WaitAsync(100);
                        } catch (ObjectDisposedException) {
                            break;
                        }
                    }
                }
            }, TaskCreationOptions.LongRunning);
        }

        private void StartUdpHeartbeat() {
            _heartbeatCts = new CancellationTokenSource();
            var token = _heartbeatCts.Token;
            Task.Factory.StartNew(async () => {
                while (!token.IsCancellationRequested && !isDisposed && _Connected) {
                    await Task.Delay(1000);
                    if (UdpConn != null) {
                        UdpConn._SentBytesPerSecond = UdpConn.SentBytesCounter;
                        UdpConn._ReceivedBytesPerSecond = UdpConn.ReceivedBytesCounter;
                        Interlocked.Exchange(ref UdpConn.SentBytesCounter, 0);
                        Interlocked.Exchange(ref UdpConn.ReceivedBytesCounter, 0);

                        // Mirror onto the NetworkConnection so InvokeBytesPerSecondUpdate
                        // reads the correct values (it reads from Connection, not UdpConn).
                        if (Connection != null) {
                            Connection._SentBytesPerSecond = UdpConn._SentBytesPerSecond;
                            Connection._ReceivedBytesPerSecond = UdpConn._ReceivedBytesPerSecond;
                        }

                        InvokeBytesPerSecondUpdate(Connection);
                    }
                }
            }, TaskCreationOptions.LongRunning);
        }

        private TaskCompletionSource<bool> safeModeReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool ReceiveSafeModeReply(byte[] data) {
            if (!Options.SafeMode) return false;
            if (data.SequenceEqual(SafeModeHandshake.Accepted)) {
                SafeModeHandshake.MarkVerified(Connection, Options);
                safeModeReady.TrySetResult(true);
                return true;
            }
            if (!Connection.SafeModeVerified) {
                safeModeReady.TrySetException(SafeModeHandshake.Rejected());
                Disconnect();
                return true;
            }
            return false;
        }

        private void DeserializeAndDispatch(byte[] bytes, int byteLength, IPEndPoint sender) {
            try {
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

                if (Connection != null && Connection.PatternCache != null) {
                    if (!Connection.PatternCache.TryResolveReceived(data, Options, out data, out string cacheError)) {
                        InvokeOnError(Connection, new Exception(cacheError));
                        return;
                    }
                }
                object message = InboundMessageDecoder.Read(this, Connection, data);
                if (message != null) HandleReceive(Connection, message, message.GetType(), byteLength);
            } catch (Exception ex) {
                InvokeOnError(Connection, ex);
            }
        }

        #endregion
    }
}
