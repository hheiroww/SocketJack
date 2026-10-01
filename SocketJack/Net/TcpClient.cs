using Mono.Nat;
using SocketJack.Extensions;
using SocketJack.Net.P2P;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace SocketJack.Net {

    /// <summary>
    /// Multithreaded TCP Client.
    /// </summary>
    /// <remarks></remarks>
    public class TcpClient : NetworkBase {

        #region Peer To Peer

        /// <summary>
        /// Start a connection with the specified Remote Client.
        /// </summary>
        /// <param name="RemotePeer">The Remote Client.</param>
        /// <param name="Name">Name of the TcpServer (Used for logging)</param>
        /// <returns>new TcpServer; <see langword="null"/> if error occured.</returns>
        public async Task<ISocket> StartServer(Identifier RemotePeer, string Name = "TcpServer") {
            return await StartServer(RemotePeer, this.Options, Name);
        }

        /// <summary>
        /// Start a connection with the specified Remote Client.
        /// </summary>
        /// <param name="RemotePeer">The Remote Client.</param>
        /// <param name="Serializer">Serializer used for this connection.</param>
        /// <param name="Name">Name of the TcpServer (Used for logging)</param>
        /// <returns>new TcpServer; <see langword="null"/> if error occured.</returns>
        public override async Task<ISocket> StartServer(Identifier RemotePeer, NetworkOptions Options, string Name = "TcpServer") {
            return await StartServer(RemotePeer.ID, Options, Name);
        }

        /// <summary>
        /// Start a connection with the specified Remote Client.
        /// </summary>
        /// <param name="ID">The GUID as String of the Remote Client.</param>
        /// <param name="Serializer">Serializer used for this connection.</param>
        /// <param name="Name">Name of the TcpServer (Used for logging)</param>
        /// <param name="Port">Port to listen on. If 0, a random port will be chosen.</param>
        /// <returns>new TcpServer; <see langword="null"/> if error occured.</returns>
        private async Task<TcpServer> StartServer(string ID, NetworkOptions Options, string Name = "TcpServer", int Port = 0) {
            if (!Options.UsePeerToPeer) {
                InvokeOnError(Connection, new Exception("P2P is not enabled."));
                return null;
            }
            if (!NIC.InterfaceDiscovered) {
                InvokeOnError(Connection, new P2PException("Failed to start P2P Server." + Environment.NewLine + "Network interface card has not yet been discovered."));
                return null;
            }
            if (RemoteIdentity is null) {
                InvokeOnError(null, new P2PException("Client Identity Not initialized." + Environment.NewLine + "    Invoke via TcpClient.OnIdentified Event instead of TcpClient.OnConnected."));
                return null;
            }
            try {
                string RemoteIP = RemoteIdentity.IP; // Await HttpClientExtensions.DownloadStringAsync("https: //JackOfAllFates.com/IP.aspx")
                var ServerInfo = await PeerServer.NewServer(Identifier.Create(ID), Identifier.Create(RemoteIdentity.ID), Options.Serializer, Port);
                var newServer = new TcpServer(Options, (int)ServerInfo.Port, Name) { PeerToPeerInstance = true };
                ServerInfo.Host = RemoteIP;
                newServer.StoppedListening += P2pServer_StoppedListening;
                newServer.ClientConnected += P2pServer_ClientConnected;
                newServer.ClientDisconnected += P2pServer_ClientDisconnected;
                if (P2P_Servers.ContainsKey(ID))
                    P2P_Servers[ID].CloseConnection();
                P2P_ServerInformation.AddOrUpdate(ID, ServerInfo);
                if (newServer.Listen()) {
                    LogFormat(new[] { "[{0}] Started P2P Server.", "    EndPoint      = {1}:{2}" }, new[] { newServer.Name + @"\P2P}", RemoteIP, ServerInfo.Port.ToString() });
                    P2P_Servers.AddOrUpdate(ID, newServer.Connection);
                    Send(ServerInfo);
                    return newServer;
                } else {
                    InvokeOnError(Connection, new P2PException("Failed to start P2P Server." + Environment.NewLine + "Port may be in use." + Environment.NewLine + "Check if the port is already in use."));
                    return null;
                }
            } catch (Exception ex) {
                InvokeOnError(Connection, new P2PException("Failed to start P2P Server." + Environment.NewLine + ex.Message));
                return null;
            }
        }

        private void P2pServer_StoppedListening(TcpServer sender) {
            lock (P2P_Servers) {
                for (int i = 0, loopTo = P2P_Servers.Count - 1; i <= loopTo; i++) {
                    string key = P2P_Servers.Keys.ElementAtOrDefault(i);
                    var value = P2P_Servers[key];
                    if (ReferenceEquals(value, sender)) {
                        StopP2P(key);
                    }
                }
            }
        }

        private void P2pServer_ClientConnected(ConnectedEventArgs args) {
            TcpServer server = (TcpServer)args.sender;
            LogFormat("[{0}] Client Connected -> {1}", new[] { string.Format(@"{0}\P2P\{1}", new[] { server.Name, args.Connection.Identity.ID.ToUpper() }), args.Connection.EndPoint == null ? "null" : args.Connection.EndPoint.ToString() });
        }

        private void P2pServer_ClientDisconnected(DisconnectedEventArgs args) {
            TcpServer server = (TcpServer)args.sender;
            LogFormat("[{0}] Client Disconnected -> {1}", new[] { string.Format(@"{0}\P2P\{1}", new[] { server.Name, args.Connection.Identity.ID.ToUpper() }), args.Connection.EndPoint == null ? "null" : args.Connection.EndPoint.ToString() });
        }

        private void StopP2P(string ID) {
            if (P2P_ServerInformation.ContainsKey(ID)) {
                var Server = P2P_ServerInformation[ID];
                Send(Server.ShutdownSignal());
                if(NIC.NAT != null)
                    Task.Run(() => MethodExtensions.TryInvoke(() => NIC.NAT.DeletePortMap(new Mapping(Protocol.Tcp, (int)Server.Port, (int)Server.Port))));

                lock (P2P_ServerInformation)
                    P2P_ServerInformation.Remove(ID);
                lock (P2P_Servers) {
                    P2P_Servers[ID].Dispose();
                    P2P_Servers.Remove(ID);
                }
            }
        }

        private void C_PeerServerShutdown(ISocket sender, PeerServer Server) {
            if (P2P_Clients.ContainsKey(Server.LocalClient.ID)) {
                LogFormat("[{0}] Shutdown Server -> {1}:{2}", new[] { string.Format(@"{0}\P2P\{1}", new[] { Name, Server.LocalClient.ID }), Server.Host, Server.Port.ToString() });
                P2P_Clients[Server.LocalClient.ID].Dispose();
                P2P_Clients[Server.LocalClient.ID] = null;
                P2P_Clients.Remove(Server.LocalClient.ID);
            }
        }

        protected internal bool isPeerUpdateSubscribed = false;

        private void TcpClient_PeerUpdate(ISocket sender, Identifier Peer) {
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
                        if(Options.Logging) {
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

        private void TcpClient_InternalPeerConnectionRequest(ISocket sender, ref PeerServer Server) {
            Server.LocalClient = Server.LocalClient.WithParent(this);
        }

        private void TcpClient_PeerConnectionRequest(ISocket sender, PeerServer Server) {
            if (Options.Logging) {
                LogFormat(@"[{0}\{1}] Requested Connection -> {2}:{3}", new[] { Name, Server.LocalClient.ID.ToUpper(), Server.Host, Server.Port.ToString() });
            }
        }

        #endregion

        #region Properties

        /// <summary>
        /// (Optional) Name used for logging purposes.
        /// </summary>
        /// <returns></returns>
        public override string Name {
            get {
                return PeerToPeerInstance ? string.Format(@"{0}\P2P", new[] { _Name }) : _Name;
            }
            set {
                _Name = value;
            }
        }
        private string _Name = "TcpClient";

        public override Socket Socket { get { return _Socket; } set { } }
        private Socket _Socket = null;
        public override Stream Stream { get { return _Stream; } set { } }
        private Stream _Stream = null;
        public override IPEndPoint EndPoint { get { return (IPEndPoint)Socket?.RemoteEndPoint; } set { } }
        

        public override bool Connected {
            get {
                return _Connected;
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
        /// <returns></returns>
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
        private bool DelayConnect;
#pragma warning disable CS0649 // Field is never assigned to
        private string DelayHost;
        private int DelayPort;
#pragma warning restore CS0649
        private void DelayDelegate(int MTU, IPAddress LocalIP) => ConnectDelayed(DelayHost, DelayPort);
        protected internal static ConcurrentDictionary<string, IPEndPoint> CachedHosts = new();

        private async void ConnectDelayed(string Host, int Port) {
            if (DelayConnect) {
                DelayConnect = false;
                Log("Network Interface Card Discovered.");
                NIC.OnInterfaceDiscovered -= this.DelayDelegate;
                await Connect(Host, Port);
            }
        }
        protected internal async Task<bool> ConnectAsync(Socket socket, EndPoint endpoint, TimeSpan timeout) {
            return await Task.Run(() => { try { if (isDisposed) throw new ObjectDisposedException(Name + " is disposed, cannot connect."); if (_Connecting) return false; _Connecting = true; var result = socket.BeginConnect(endpoint, null, null); bool success = result.AsyncWaitHandle.WaitOne(timeout, isDisposed); if (success) { socket.EndConnect(result); } else { socket.Close(); } _Connecting = false; return success; } catch (Exception) { _Connecting = false; return false; } });
        }
        private async Task<bool> DoConnect(EndPoint EndPoint, string Host, int Port) {
            _CurrentHost = Host;
            LogFormat("[{0}] Connecting -> {1}:{2}...", new[] { Name, Host, Port.ToString() });
            _Socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            _Socket.NoDelay = true;
            _Socket.ReceiveBufferSize = int.MaxValue;
            _Socket.SendBufferSize = int.MaxValue;
            _Socket.ReceiveTimeout = -1;
            _Socket.SendTimeout = -1;

            Connection = new NetworkConnection(this, Socket);
            
            try {
                Bind(_Port);
                if (await ConnectAsync(Socket, EndPoint, Options.ConnectionTimeout)) {
                    _Connected = true;
                    Connection._Stream = new NetworkStream(Connection.Socket);
                    _Stream = Connection._Stream;
                    if (Options.UseSsl)
                        Connection.InitializeSslStream(SslTargetHost);
                    if (Options.UseTerminatedStreams)
                        await SafeModeHandshake.Exchange(Connection, Options, false);
                    StartReceiving();
                    StartSending();
                    StartConnectionTester();
                    LogFormat("[{0}] Connected  -> {1}:{2}", new[] { Name, Host, Port.ToString() });
                    LastHost = Host;
                    LastPort = Port;
                    if (ResetAutoReconnect) {
                        ResetAutoReconnect = false;
                        Options.AutoReconnect = true;
                    }
                    Connection.EndPoint = (IPEndPoint)Socket.RemoteEndPoint;
                    StartPingLoop();
                    OnConnected?.Invoke(new ConnectedEventArgs(this, Connection));
                    return true;
                }
            } catch (Exception ex) {
                Disconnect();
                InvokeOnError(null, ex);
            }
           
            return false;
        }
        protected internal async Task<bool> Reconnect(string Host, int Port) {
            return await Connect(Host, Port);
        }
        private IPAddress[] ResolveAllIPv4FromHostEntry(string Host) {
            if (IPAddress.TryParse(Host, out IPAddress literalAddress) &&
                literalAddress.AddressFamily == AddressFamily.InterNetwork) {
                return new[] { literalAddress };
            }

            IPHostEntry hostEntry = null;
            MethodExtensions.TryInvoke(() => hostEntry = Dns.GetHostEntry(Host));
            if(hostEntry == null ) {
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
        /// Fired when connected to the remote server.
        /// </summary>
        /// <param name="sender"></param>
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
        /// <param name="sender"></param>
        /// <param name="Host"></param>
        /// <param name="Port"></param>
        public event ConnectionFailedEventHandler ConnectionFailed;
        public delegate void ConnectionFailedEventHandler(ISocket sender, string Host, int Port);

        /// <summary>
        /// Fired when a user's metadata changes.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="Peer"></param>
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
        private void TcpClient_InternalPeerRedirect(string Recipient, string Sender, object Obj, int ReceivedBytes) {
            Type RedirectType = Obj.GetType();
            var genericType = typeof(ReceivedEventArgs<>).MakeGenericType(RedirectType);

            var receivedEventArgs = ((IReceivedEventArgs)Activator.CreateInstance(genericType));
            receivedEventArgs.Initialize(this, Connection, Obj, ReceivedBytes, Sender);
            //InvokeOnReceive(receivedEventArgs);


            InvokeAllCallbacks(RedirectType, receivedEventArgs);
            InvokeOnReceive(new ReceivedEventArgs<object>(this, Connection, Obj, ReceivedBytes, Sender));
        }
        private async void TcpClient_OnDisconnected(DisconnectedEventArgs args) {
            base.Peers.Clear();
            //await Task.Delay(100);
            LogFormat("[{0}] Disconnected -> {1}:{2}", new[] { string.Format(@"{0}{1}", new[] { Name, RemoteIdentity == null ? "" : "\\" + RemoteIdentity.ID.ToUpper() }), LastHost, LastPort.ToString() });
            if (Options.AutoReconnect && !isDisposed)
                await Reconnect();
        }
        private void TcpClient_InternalReceiveEvent(NetworkConnection Connection, Type objType, object obj, int BytesReceived) {
            if (Options.LogReceiveEvents) {
                if (ReferenceEquals(objType, typeof(PeerRedirect))) {
                    PeerRedirect Redirect = (PeerRedirect)obj;
                    LogFormat("[{0}] Received {1} - {2}", new[] { Name + @"\" + (Connection.Identity == null ? "Null" : Connection.Identity.ID.ToUpper()), string.Format("PeerRedirect<{0}>", Redirect.CleanTypeName), BytesReceived.ByteToString() });
                } else {
                    LogFormat("[{0}] Received {1} - {2}", new[] { Name + @"\" + (Connection.Identity == null ? "Null" : Connection.Identity.ID.ToUpper()), objType.Name, BytesReceived.ByteToString() });
                }
            }
        }
        private void TcpClient_InternalReceivedByteCounter(NetworkConnection Connection, int BytesReceived) {
            Interlocked.Add(ref base.Connection.ReceivedBytesCounter, BytesReceived);
        }
        private void TcpClient_InternalSendEvent(NetworkConnection Connection, Type objType, object obj, int BytesSent) {
            if (Options.LogSendEvents) {
                if (ReferenceEquals(objType, typeof(PeerRedirect))) {
                    PeerRedirect Redirect = (PeerRedirect)obj;
                    LogFormat("[{0}] Sent {1} - {2}", new[] { Name + @"\" + (Connection.Identity == null ? "Null" : Connection.Identity.ID.ToUpper()), string.Format("PeerRedirect<{0}>", Redirect.CleanTypeName), BytesSent.ByteToString() });
                } else {
                    LogFormat("[{0}] Sent {1} - {2}", new[] { Name + @"\" + (Connection.Identity == null ? "Null" : Connection.Identity.ID.ToUpper()), objType.Name, BytesSent.ByteToString() });
                }
            }
        }
        private void TcpClient_InternalSentByteCounter(NetworkConnection Connection, int BytesSent) {
            Interlocked.Add(ref Connection.SentBytesCounter, BytesSent);
        }
        private void TcpClient_BytesPerSecondUpdate(int ReceivedPerSecond, int SentPerSecond) {
            if (Environment.UserInteractive && Options.UpdateConsoleTitle) {
                try {
                    Console.Title = string.Format("{0} - Sent {1}/s Received {2}/s", new[] { Name, Connection.BytesPerSecondSent.ByteToString(2), Connection.BytesPerSecondReceived.ByteToString(2) });
                } catch (Exception) {
                }
            }
        }

#endregion

        #region Static Methods
        /// <summary>
        /// Check a Port on a remote Server for availability.
        /// </summary>
        /// <param name="Host">Remote Host</param>
        /// <param name="Port">Remote Port</param>
        /// <param name="Timeout">Timeout in milliseconds.</param>
        /// <returns>True if Port is Open, False if Closed.</returns>
        public static async Task<bool> CheckPort(string Host, int Port, int Timeout = 500) {
            NetworkOptions opts = NetworkOptions.NewDefault();
            opts.ConnectionTimeout = TimeSpan.FromMilliseconds(Timeout);
            var PortChecker = new TcpClient(opts, "PortChecker");
            var success = await PortChecker.Connect(Host, Port);
            PortChecker.Disconnect();
            PortChecker.Dispose();
            return success;
        }
        #endregion

        #region"IDisposable Implementation"
        protected override void Dispose(bool disposing) {
            Options.AutoReconnect = false;
            var argClient = (ISocket)this;
            Globals.UnregisterClient(ref argClient);
            base.Dispose(disposing);
        }
        #endregion

        /// <summary>
        /// Initializes a new instance of TcpClient.
        /// <para>Uses System.Text.Json as the default serializer.</para>
        /// </summary>
        /// <param name="AutoReconnect">Auto Reconnect on Disconnect/Failed Connection to last Host / Port.</param>
        /// <param name="Name">Name used for logging. </param>
        public TcpClient(string Name = "TcpClient") : base() {
            this.Name = Name;

            var argClient = (ISocket)this;
            Globals.RegisterClient(ref argClient);
            PeerServerShutdown += C_PeerServerShutdown;
            PeerUpdate += TcpClient_PeerUpdate;
            PeerConnectionRequest += TcpClient_PeerConnectionRequest;
            Internal_PeerConnectionRequest += TcpClient_InternalPeerConnectionRequest;
            InternalPeerRedirect += TcpClient_InternalPeerRedirect;
            OnDisconnected += TcpClient_OnDisconnected;
            InternalReceiveEvent += TcpClient_InternalReceiveEvent;
            InternalReceivedByteCounter += TcpClient_InternalReceivedByteCounter;
            InternalSendEvent += TcpClient_InternalSendEvent;
            InternalSentByteCounter += TcpClient_InternalSentByteCounter;
            BytesPerSecondUpdate += TcpClient_BytesPerSecondUpdate;
        }

        /// <summary>
        /// Initializes a new instance of TcpClient.
        /// </summary>
        /// <param name="Serializer">Serializer for serialization and deserialization.</param>
        /// <param name="AutoReconnect">Auto Reconnect on Disconnect/Failed Connection to last Host / Port.</param>
        /// <param name="Name">Name used for logging. </param>
        public TcpClient(NetworkOptions Options, string Name = "TcpClient") : base() {
            this.Options = Options;
            this.Name = Name;
            var argClient = (ISocket)this;
            Globals.RegisterClient(ref argClient);
            PeerServerShutdown += C_PeerServerShutdown;
            PeerUpdate += TcpClient_PeerUpdate;
            PeerConnectionRequest += TcpClient_PeerConnectionRequest;
            Internal_PeerConnectionRequest += TcpClient_InternalPeerConnectionRequest;
            InternalPeerRedirect += TcpClient_InternalPeerRedirect;
            OnDisconnected += TcpClient_OnDisconnected;
            InternalReceiveEvent += TcpClient_InternalReceiveEvent;
            InternalReceivedByteCounter += TcpClient_InternalReceivedByteCounter;
            InternalSendEvent += TcpClient_InternalSendEvent;
            InternalSentByteCounter += TcpClient_InternalSentByteCounter;
            BytesPerSecondUpdate += TcpClient_BytesPerSecondUpdate;
        }

        /// <summary>
        /// Poll the connection to see if it is still connected.
        /// </summary>
        /// <returns></returns>
        public bool Poll() {
            return Socket != null && Connection.Poll();
        }

        /// <summary>
        /// Connect to a remote server.
        /// <para>Awaitable</para>
        /// <para>Configurable from this.ConnectionTimeout or DefaultOptions.ConnectionTimeout</para>
        /// </summary>
        /// <param name="Host">The host you intend to try and connect to (e.g. localhost, 127.0.0.1 etc..)</param>
        /// <param name="Port">The port the host uses</param>
        /// <remarks></remarks>
        public async Task<bool> Connect(string Host, int Port) {
            if (isDisposed)
                throw new ObjectDisposedException(Name + " is disposed, cannot connect.");
            if (Connecting)
                return false;
            //if (!NIC.InterfaceDiscovered) {
            //    DelayConnect = true;
            //    NIC.OnInterfaceDiscovered += this.DelayDelegate;
            //    DelayHost = Host;
            //    DelayPort = Port;

            //    return false;
            //} else {
                string cachedHost = string.Format("{0}:{1}", new object[] { Host, Port });
                if (CachedHosts.ContainsKey(cachedHost)) {
                    await DoConnect(CachedHosts[cachedHost], Host, Port);
                } else {
                    IPAddress[] Addresses = ResolveAllIPv4FromHostEntry(Host);

                    foreach (var address in Addresses) {
                        var endPoint = new IPEndPoint(address, Port);
                        if (await DoConnect(endPoint, Host, Port)) {
                            CachedHosts.AddOrUpdate(cachedHost, endPoint);
                            break;
                        }
                    }
                }

                if (!Socket.Connected) {
                    LogFormat("[{0}] Connection -> {1}:{2} failed!", new[] { Name, Host, Port.ToString() });
                    ConnectionFailed?.Invoke(this, Host, Port);
                    if (Options.AutoReconnect && !isDisposed && !Connecting) {
                        return await Reconnect(Host, Port);
                    }
                }
                return Socket.Connected;
            //}

        }

        /// <summary>
        /// Attempt to connect to the last host and port that was Connected.
        /// </summary>
        /// <returns></returns>
        public async Task<bool> Reconnect() {
            return await Reconnect(LastHost, LastPort);
        }

        /// <summary>
        /// Disconnect from the server.
        /// </summary>
        public void Disconnect() {
            if (Options.AutoReconnect) {
                Options.AutoReconnect = false;
                ResetAutoReconnect = true;
            }
            if (Connected) {
                _CurrentHost = default;
                _Connected = false;
                StopPingLoop();
                CloseConnection(Connection, DisconnectionReason.LocalSocketClosed);
            }
        }

        /// <summary>
        /// Send an object to the server.
        /// </summary>
        /// <param name="Obj">An object.</param>
        public void Send(object Obj) {
            Send(Connection, Obj);
        }

        /// <summary>
        /// Send an object to a Remote Client on the server.
        /// <para>This will <see langword="NOT"/> expose your remote IP.</para>
        /// </summary>
        /// <param name="Recipient"></param>
        /// <param name="Obj"></param>
        public override void Send(Identifier Recipient, object Obj) {
            if (RemoteIdentity is null) {
                InvokeOnError(Connection, new P2PException("P2P Client not yet initialized." + Environment.NewLine + 
                                                           "    ConnectedSocket.Identifier property cannot equal null." + Environment.NewLine +
                                                           "    Invoke via TcpClient.OnIdentified Event instead of TcpClient.OnConnected."));
            } else {
                base.Send(Recipient, Obj);
                //Send(new PeerRedirect(RemoteIdentity.ID, Recipient.ID, Obj));
            }
        }

        /// <summary>
        /// Send an object to a Remote Client on the server.
        /// <para>This will <see langword="NOT"/> expose your remote IP.</para>
        /// </summary>
        /// <param name="Obj"></param>
        public void SendPeerBroadcast(object Obj) {
            if (RemoteIdentity is null) {
                InvokeOnError(Connection, new P2PException("P2P Client not yet initialized." + Environment.NewLine +
                                                           "    ConnectedSocket.Identifier property cannot equal null." + Environment.NewLine +
                                                           "    Invoke via TcpClient.OnIdentified Event instead of TcpClient.OnConnected."));
            } else {
                Send(new PeerRedirect(RemoteIdentity.ID, "#ALL#", Obj));
            }
        }

    }

}
