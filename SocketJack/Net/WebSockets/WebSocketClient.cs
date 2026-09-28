using SocketJack;
using SocketJack.Extensions;
using SocketJack.Net;
using SocketJack.Net.P2P;
using SocketJack.Net.WebSockets;
using SocketJack.Serialization;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Buffers;

namespace SocketJack.Net.WebSockets {

    public class WebSocketClient : IDisposable, ISocket {
        private bool _handshakeComplete = false;
        private readonly SemaphoreSlim _writeSemaphore = new(1, 1);

        #region Properties

        public string Name {
            get {
                return PeerToPeerInstance ? string.Format(@"{0}\P2P", new[] { _Name }) : _Name;
            }
            set {
                _Name = value;
            }
        }
        private string _Name = "WebSocketClient";

        public Socket Socket { get { return _socket; } set { } }
        private Socket _socket = null;
        public Stream Stream { get { return _stream; } set { } }
        private Stream _stream = null;
        public IPEndPoint EndPoint { get { return (IPEndPoint)Socket?.RemoteEndPoint; } set { } }
        public PeerList Peers { get; internal set; }

        PeerList ISocket.Peers { get => Peers; set => Peers = value; }
        ConcurrentDictionary<string, PeerServer> ISocket.P2P_ServerInformation { get => P2P_ServerInformation; set => P2P_ServerInformation = value; }
        ConcurrentDictionary<string, NetworkConnection> ISocket.P2P_Servers { get => P2P_Servers; set => P2P_Servers = value; }
        ConcurrentDictionary<string, NetworkConnection> ISocket.P2P_Clients { get => P2P_Clients; set => P2P_Clients = value; }

        protected internal ConcurrentDictionary<string, PeerServer> P2P_ServerInformation = new();
        protected internal ConcurrentDictionary<string, NetworkConnection> P2P_Servers = new();
        protected internal ConcurrentDictionary<string, NetworkConnection> P2P_Clients = new();
        public NetworkOptions Options { get; set; } = NetworkOptions.NewDefault();
        public NetworkConnection Connection { get; set; }
        public Identifier RemoteIdentity { get; internal set; }

        public Guid InternalID {
            get {
                return _InternalID;
            }
        }
        private Guid _InternalID = Guid.NewGuid();
        public bool isDisposed { get; internal set; } = false;
        bool ISocket.isDisposed { get => isDisposed; set => isDisposed = value; }
        public bool Connected {
            get {
                return _socket != null && _socket.Connected && _stream != null && _stream.CanRead;
            }
        }

        private long _totalBytesReceived = 0;
        public long TotalBytesReceived => _totalBytesReceived;

        #endregion

        #region Events

        public event OnIdentifiedEventHandler OnIdentified;
        public delegate void OnIdentifiedEventHandler(ISocket sender, Identifier Peer);

        public event OnDisposingEventHandler OnDisposing;
        public delegate void OnDisposingEventHandler();

        public event OnReceiveEventHandler OnReceive;
        public delegate void OnReceiveEventHandler(ref IReceivedEventArgs e);

        public event OnSentEventHandler OnSent;
        public delegate void OnSentEventHandler(SentEventArgs e);

        public event OnErrorEventHandler OnError;
        public delegate void OnErrorEventHandler(ErrorEventArgs e);

        public event PeerConnectionRequestEventHandler PeerConnectionRequest;
        public delegate void PeerConnectionRequestEventHandler(ISocket sender, PeerServer Server);

        protected internal event InternalPeerConnectionRequestEventHandler Internal_PeerConnectionRequest;
        protected internal delegate void InternalPeerConnectionRequestEventHandler(ISocket sender, ref PeerServer Server);

        public event PeerServerShutdownEventHandler PeerServerShutdown;
        public delegate void PeerServerShutdownEventHandler(ISocket sender, PeerServer Server);

        public event BytesPerSecondUpdateEventHandler BytesPerSecondUpdate;
        public delegate void BytesPerSecondUpdateEventHandler(int ReceivedPerSecond, int SentPerSecond);

        public event LogOutputEventHandler LogOutput;
        public delegate void LogOutputEventHandler(string text);

        #pragma warning disable CS0067 // Event is never used
        protected internal event PeerRefusedConnectionEventHandler PeerRefusedConnection;
#pragma warning restore CS0067
        protected internal delegate void PeerRefusedConnectionEventHandler(ISocket sender, ConnectionRefusedArgs e);

        protected internal event PeerUpdateEventHandler PeerUpdate;
        protected internal delegate void PeerUpdateEventHandler(ISocket sender, Identifier Peer);


        /// <summary>
        /// Fired when a user's metadata changes.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="Peer"></param>
        public event PeerConnectedEventHandler PeerMetaDataChanged;
        public delegate void PeerMetaDataChangedEventHandler(ISocket sender, Identifier Peer);

        /// <summary>
        /// Fired when a user connects to the server.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="Peer"></param>
        public event PeerConnectedEventHandler PeerConnected;
        public delegate void PeerConnectedEventHandler(ISocket sender, Identifier Peer);

        public event PeerDisconnectedEventHandler PeerDisconnected;
        public delegate void PeerDisconnectedEventHandler(ISocket sender, Identifier Peer);

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

        #pragma warning disable CS0067 // Event is never used
        protected internal event InternalPeerRedirectEventHandler InternalPeerRedirect;
#pragma warning restore CS0067
        protected internal delegate void InternalPeerRedirectEventHandler(string Recipient, string Sender, object Obj, int BytesReceived);

#pragma warning disable CS0067 // Event is never used
        protected internal event InternalReceiveEventEventHandler InternalReceiveEvent;
#pragma warning restore CS0067
        protected internal delegate void InternalReceiveEventEventHandler(NetworkConnection Connection, Type objType, object obj, int BytesReceived);

        protected internal event InternalReceivedByteCounterEventHandler InternalReceivedByteCounter;
        protected internal delegate void InternalReceivedByteCounterEventHandler(NetworkConnection Connection, int BytesReceived);

        protected internal event InternalSentByteCounterEventHandler InternalSentByteCounter;
        protected internal delegate void InternalSentByteCounterEventHandler(NetworkConnection Connection, int BytesSent);

        protected internal event InternalSendEventEventHandler InternalSendEvent;
        protected internal delegate void InternalSendEventEventHandler(NetworkConnection Connection, Type objType, object obj, int BytesSent);

        public void InvokeOnDisconnected(ISocket sender, NetworkConnection Connection) {
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
        public void InvokeOnConnected(ISocket sender, NetworkConnection Connection) {
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
        public void InvokePeerConnectionRequest(ISocket sender, ref PeerServer Server) {
            if (Options.Logging) {
                LogFormat(@"[{0}\{1}] Requested Connection -> {2}:{3}", new[] { Name, Server.LocalClient.ID.ToUpper(), Server.Host, Server.Port.ToString() });
            }
#if UNITY
            var s = Server;
            MainThread.Run(() => {
                Internal_PeerConnectionRequest?.Invoke(sender, ref s);
            });
#endif
#if WINDOWS
            var s = Server;
            Application.Current.Dispatcher.Invoke(() => {
                Internal_PeerConnectionRequest?.Invoke(sender, ref s);
            });
#endif
#if !UNITY && !WINDOWS
            Internal_PeerConnectionRequest?.Invoke(sender, ref Server);
#endif

        }
        public void InvokeBytesPerSecondUpdate(int ReceivedPerSecond, int SentPerSecond) {
#if UNITY
            MainThread.Run(() => {
                BytesPerSecondUpdate?.Invoke(ReceivedPerSecond, SentPerSecond);
            });
#endif
#if WINDOWS
            Application.Current.Dispatcher.Invoke(() => {
                BytesPerSecondUpdate?.Invoke(ReceivedPerSecond, SentPerSecond);
            });
#endif
#if !UNITY && !WINDOWS
            BytesPerSecondUpdate?.Invoke(ReceivedPerSecond, SentPerSecond);
#endif
        }
        public void InvokePeerServerShutdown(ISocket sender, PeerServer Server) {
#if UNITY
            MainThread.Run(() => {
		        PeerServerShutdown?.Invoke(sender, Server);
            });
#endif
#if WINDOWS
            Application.Current.Dispatcher.Invoke(() => {
                PeerServerShutdown?.Invoke(sender, Server);
            });
#endif
#if !UNITY && !WINDOWS
            PeerServerShutdown?.Invoke(sender, Server);
#endif
        }
        public void InvokePeerUpdate(ISocket sender, Identifier Peer) {
#if UNITY
            MainThread.Run(() => {
		        PeerUpdate?.Invoke(sender, Peer);
            });
#endif
#if WINDOWS
            Application.Current.Dispatcher.Invoke(() => {
                PeerUpdate?.Invoke(sender, Peer);
            });
#endif
#if !UNITY && !WINDOWS
            PeerUpdate?.Invoke(sender, Peer);
#endif
        }
        public void InvokePeerConnected(ISocket sender, Identifier Peer) {
            if (Options.Logging) {
                LogFormat(@"[{0}\{1}] Connected To Server.", new[] { Name, Peer.ID.ToUpper() });
            }
            
#if UNITY
            MainThread.Run(() => {
		        PeerConnected?.Invoke(sender, Peer);
            });
#endif
#if WINDOWS
            Application.Current.Dispatcher.Invoke(() => {
                PeerConnected?.Invoke(sender, Peer);
            });
#endif
#if !UNITY && !WINDOWS
            PeerConnected?.Invoke(sender, Peer);
#endif
        }
        public void InvokePeerDisconnected(ISocket sender, Identifier Peer) {
            if(Options.Logging) {
                LogFormat(@"[{0}\{1}] Disconnected From Server.", new[] { Name, Peer.ID.ToUpper() });
            }
#if UNITY
            MainThread.Run(() => {
		        PeerDisconnected?.Invoke(sender, Peer);
            });
#endif
#if WINDOWS
            Application.Current.Dispatcher.Invoke(() => {
                PeerDisconnected?.Invoke(sender, Peer);
            });
#endif
#if !UNITY && !WINDOWS
            PeerDisconnected?.Invoke(sender, Peer);
#endif
        }
        protected internal bool InvokeOnDisconnected(DisconnectedEventArgs e) {
            if (e.Connection != null && !e.Connection.Closed) {
                e.Connection.Closed = true;
                for (int i = 0, loopTo = Peers.Count - 1; i <= loopTo; i++) {
                    var p = Peers.Values.ElementAt(i);
                    if (p.ID == e.Connection.ID.ToString()) {
                        InvokePeerDisconnected(this, p);
                        break;
                    }
                }
                Peers.Clear();
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
                return true;
            }
            return false;
        }
        protected internal void InvokeOnConnected(ConnectedEventArgs e) {
            if (e.Connection != null) {
#if UNITY
            MainThread.Run(() => {
		        OnConnected?.Invoke(e);
            });
#endif
#if WINDOWS
                Application.Current.Dispatcher.Invoke(() => {
                    OnConnected?.Invoke(e);
                });
#endif
#if !UNITY && !WINDOWS
            OnConnected?.Invoke(e);
#endif
            }
        }
        public void InvokeInternalReceivedByteCounter(NetworkConnection Connection, int BytesReceived) {
#if UNITY
            MainThread.Run(() => {
		        InternalReceivedByteCounter?.Invoke(Connection, BytesReceived);
            });
#endif
#if WINDOWS
            Application.Current.Dispatcher.Invoke(() => {
                InternalReceivedByteCounter?.Invoke(Connection, BytesReceived);
            });
#endif
#if !UNITY && !WINDOWS
            InternalReceivedByteCounter?.Invoke(Connection, BytesReceived);
#endif
        }
        public void InvokeInternalSentByteCounter(NetworkConnection connection, int chunkSize) {
#if UNITY
            MainThread.Run(() => {
		        InternalSentByteCounter?.Invoke(connection, chunkSize);
            });
#endif
#if WINDOWS
            Application.Current.Dispatcher.Invoke(() => {
                InternalSentByteCounter?.Invoke(connection, chunkSize);
            });
#endif
#if !UNITY && !WINDOWS
            InternalSentByteCounter?.Invoke(connection, chunkSize);
#endif
        }
        public void InvokeInternalSendEvent(NetworkConnection connection, Type type, object @object, int length) {
#if UNITY
            MainThread.Run(() => {
		        InternalSendEvent?.Invoke(connection, type, @object, length);
            });
#endif
#if WINDOWS
            Application.Current.Dispatcher.Invoke(() => {
                InternalSendEvent?.Invoke(connection, type, @object, length);
            });
#endif
#if !UNITY && !WINDOWS
            InternalSendEvent?.Invoke(connection, type, @object, length);
#endif
        }
        public void InvokeOnReceive(ref IReceivedEventArgs e) {
#if UNITY
            var eArgs = e;
            MainThread.Run(() => {
		        OnReceive?.Invoke(ref eArgs);
            });
#endif
#if WINDOWS
            var eArgs = e;
            Application.Current.Dispatcher.Invoke(() => {
                OnReceive?.Invoke(ref eArgs);
            });
#endif
#if !UNITY && !WINDOWS
            OnReceive?.Invoke(ref e);
#endif
        }
        public void InvokeOnSent(SentEventArgs sentEventArgs) {
#if UNITY
            MainThread.Run(() => {
                OnSent?.Invoke(sentEventArgs);
            });
#endif
#if WINDOWS
            Application.Current.Dispatcher.Invoke(() => {
                OnSent?.Invoke(sentEventArgs);
            });
#endif
#if !UNITY && !WINDOWS
            OnSent?.Invoke(sentEventArgs);
#endif
        }
        public void InvokeLogOutput(string text) {
#if UNITY
            MainThread.Run(() => {
                LogOutput?.Invoke(text);
            });
#endif
#if WINDOWS
            Application.Current.Dispatcher.Invoke(() => {
                LogOutput?.Invoke(text);
            });
#endif
#if !UNITY && !WINDOWS
            LogOutput?.Invoke(text);
#endif
        }
        public void InvokeOnError(NetworkConnection Connection, Exception e) {
            LogAsync(e.Message + Environment.NewLine + e.StackTrace);
#if UNITY
            MainThread.Run(() => {
                OnError?.Invoke(new ErrorEventArgs(this, Connection, e));
            });
#endif
#if WINDOWS
            Application.Current.Dispatcher.Invoke(() => {
                OnError?.Invoke(new ErrorEventArgs(this, Connection, e));
            });
#endif
#if !UNITY && !WINDOWS
            OnError?.Invoke(new ErrorEventArgs(this, Connection, e));
#endif
        }
        public void InvokeOnError(Exception e) {
            LogAsync(e.Message + Environment.NewLine + e.StackTrace);
#if UNITY
            MainThread.Run(() => {
                OnError?.Invoke(new ErrorEventArgs(this, Connection, e));
            });
#endif
#if WINDOWS
            Application.Current.Dispatcher.Invoke(() => {
                OnError?.Invoke(new ErrorEventArgs(this, Connection, e));
            });
#endif
#if !UNITY && !WINDOWS
            OnError?.Invoke(new ErrorEventArgs(this, Connection, e));
#endif
        }
        void InvokeOnIdentified(ISocket sender, ref Identifier Peer) {
#if UNITY
            var p = Peer;
            MainThread.Run(() => {
                OnIdentified?.Invoke(sender, p);
            });
#endif
#if WINDOWS
            var p = Peer;
            Application.Current.Dispatcher.Invoke(() => {
                OnIdentified?.Invoke(sender, p);
            });
#endif
#if !UNITY && !WINDOWS
            OnIdentified?.Invoke(sender, Peer);
#endif
        }
        void ISocket.InvokeBytesPerSecondUpdate(NetworkConnection connection) {
#if UNITY
            MainThread.Run(() => {
                InvokeBytesPerSecondUpdate(connection.BytesPerSecondReceived, connection.BytesPerSecondSent);
            });
#endif
#if WINDOWS
            Application.Current.Dispatcher.Invoke(() => {
                InvokeBytesPerSecondUpdate(connection.BytesPerSecondReceived, connection.BytesPerSecondSent);
            });
#endif
#if !UNITY && !WINDOWS
            InvokeBytesPerSecondUpdate(connection.BytesPerSecondReceived, connection.BytesPerSecondSent);
#endif
        }

        public void InvokeOnDisposing() {
#if UNITY
            MainThread.Run(() => {
                OnDisposing?.Invoke();
            });
#endif
#if WINDOWS
            Application.Current.Dispatcher.Invoke(() => {
                OnDisposing?.Invoke();
            });
#endif
#if !UNITY && !WINDOWS
            OnDisposing?.Invoke();
#endif
        }

        public void CloseConnection() {
            ((ISocket)this).CloseConnection(Connection, DisconnectionReason.LocalSocketClosed);
        }
        public void CloseConnection(NetworkConnection Connection, DisconnectionReason Reason) {
            ((ISocket)this).CloseConnection(Connection, DisconnectionReason.LocalSocketClosed);
        }
        void ISocket.CloseConnection(NetworkConnection Connection, DisconnectionReason Reason) {
            try {
                Connection.Closed = true;
                // Send close frame
                byte[] closeFrame = new byte[] { 0x88, 0x00 };
                Connection.Stream?.Write(closeFrame, 0, closeFrame.Length);
                Connection.Socket?.Shutdown(SocketShutdown.Both);
                Connection.Socket?.Close();
                Connection.Stream?.Dispose();

                InvokeOnDisconnected(new DisconnectedEventArgs(this, Connection, Reason));
            } catch (Exception ex) {
                InvokeOnError(ex);
            }
        }

        public void Send(NetworkConnection connection, object obj) {
            SendAsync(obj);
        }

        public void Send(Identifier recipient, object obj) {
            if (recipient == null) return;
            var redirect = new PeerRedirect(Connection?.ID.ToString(), recipient.ID, obj);
            SendAsync(redirect);
        }

        // Implement required ISocket interface methods
        public void SendBroadcast(object Obj) {
            // For client, broadcast means send to all peers (if any)
            Send(new PeerRedirect("#ALL#", Obj));
        }
        public void SendBroadcast(NetworkConnection[] Clients, object Obj, NetworkConnection Except) {
            throw new NotImplementedException("You can only access SendBroadcast(NetworkConnection Clients, object Obj, NetworkConnection Except) from WebSocketServer.");
        }
        public void SendBroadcast(object Obj, NetworkConnection Except) {
            throw new NotImplementedException("You can only access SendBroadcast(object Obj, NetworkConnection Except) from WebSocketServer.");
        }
        public void SendSegmented(NetworkConnection Client, object Obj) {
            byte[] SerializedBytes = Options.Serializer.Serialize(new Wrapper(Obj, this));
            Segment[] SegmentedObject = SerializedBytes.GetSegments();
            foreach (var s in SegmentedObject) {
                SendAsync(s);
            }
        }

        public async Task SendSegmented(NetworkConnection Client, byte[] SerializedBytes, Type objType, object obj) {
            Segment[] SegmentedObject = SerializedBytes.GetSegments();
            foreach (var s in SegmentedObject) {
                await SendAsync(s);
            }
            if (objType == typeof(PeerRedirect)) {
                PeerRedirect redirect = (PeerRedirect)obj;
                LogFormatAsync("[{0}] Sent PeerRedirect<{1}> - {2}", new[] { Name, redirect.CleanTypeName, SerializedBytes.Length.ByteToString() });
            } else {
                LogFormatAsync("[{0}] Sent {1} - {2}", new[] { Name, objType.Name, SerializedBytes.Length.ByteToString() });
            }

        }

        public void HandleReceive(NetworkConnection connection, object obj, Type objType, int Length) {
            // Use previous logic from original class
            if (objType == typeof(Identifier)) {
                if (Options.UsePeerToPeer)
                    HandlePeer((Identifier)obj, connection);
            } else if (objType == typeof(Identifier[])) {
                if (Options.UsePeerToPeer) {
                    var peerIDs = (Identifier[])obj;
                    foreach (var peer in peerIDs)
                        HandlePeer(peer, connection);
                }
            } else if (objType == typeof(Segment)) {
                Segment s = (Segment)obj;
                if (Segment.Cache.ContainsKey(s.SID)) {
                    Segment.Cache[s.SID].Add(s);
                    if (Segment.SegmentComplete(s)) {
                        byte[] RebuiltSegments = Segment.Rebuild(s);
                        try {
                            var wrapper = (Wrapper)Options.Serializer.Deserialize(RebuiltSegments);
                            object segObj = null;
                            if (wrapper.value == null) {
                                PeerRedirect redirect = (PeerRedirect)Options.Serializer.DeserializeRedirect(this, RebuiltSegments);
                                segObj = redirect.Value;
                                HandleReceive(connection, redirect, redirect.GetType(), RebuiltSegments.Length);
                            } else {
                               segObj = wrapper.Unwrap(this);
                                HandleReceive(connection, segObj, segObj.GetType(), RebuiltSegments.Length);
                            }
                            if (Options.Logging && Options.LogReceiveEvents) {
                                LogFormatAsync("[{0}] Received {1} - {2}", new[] { Name, string.Format("{0}", segObj.GetType().Name), RebuiltSegments.Length.ByteToString() });
                            }
                        } catch (Exception) {
                            PeerRedirect redirect = Options.Serializer.DeserializeRedirect(this, RebuiltSegments);
                            if (redirect == null) {
                                InvokeOnError(Connection, new Exception("Failed to deserialize segment."));
                            } else {
                                HandleReceive(connection, redirect, redirect.GetType(), RebuiltSegments.Length);
                                if (Options.Logging && Options.LogReceiveEvents) {
                                    LogFormatAsync("[{0}] Received {1} - {2}", new[] { Name, string.Format("PeerRedirect<{0}>", redirect.CleanTypeName), RebuiltSegments.Length.ByteToString() });
                                }
                            }  
                        }
                    }
                } else {
                    Segment.Cache.Add(s.SID, new List<Segment>() { s });
                }
            } else if (objType == typeof(PeerServer)) {
                if (Options.UsePeerToPeer) {
                    var pServer = (PeerServer)obj;
                    if (pServer.Shutdown) {
                        PeerServerShutdown?.Invoke(this, pServer);
                    } else {
                        PeerConnectionRequest?.Invoke(this, pServer);
                    }
                }
            } else if (objType == typeof(PeerRedirect)) {
                if (Options.UsePeerToPeer) {
                    var redirect = (PeerRedirect)obj;
                    var From = Peers.Values.FirstOrDefault(p => p.ID == redirect.Sender);
                    if(From != null) {
                        Type redirectType = Type.GetType(redirect.Type);
                        var genericType = typeof(ReceivedEventArgs<>).MakeGenericType(redirectType);
                        var receivedEventArgs = (IReceivedEventArgs)Activator.CreateInstance(genericType);
                        receivedEventArgs.From = From;
                        receivedEventArgs.Initialize(this, Connection, redirect.Value, Length);
                        IReceivedEventArgs NonGenericEventArgs = new ReceivedEventArgs<object>(this, Connection, redirect.Value, Length);
                        NonGenericEventArgs.From = From;
                        InvokeOnReceive(ref NonGenericEventArgs);
                        InvokeAllCallbacks(receivedEventArgs);
                    }
                }
            } else {
                var genericType = typeof(ReceivedEventArgs<>).MakeGenericType(obj.GetType());
                var receivedEventArgs = (IReceivedEventArgs)Activator.CreateInstance(genericType);
                receivedEventArgs.Initialize(this, Connection, obj, Length);
                IReceivedEventArgs NonGenericEventArgs = new ReceivedEventArgs<object>(this, Connection, obj, Length);
                InvokeOnReceive(ref NonGenericEventArgs);
                InvokeAllCallbacks(receivedEventArgs);
            }
        }
#endregion

        #region Logging
        public void Log(string[] lines) {
            if (Options.Logging) {
                string Output = string.Join(Environment.NewLine, lines) + Environment.NewLine;
                if (Options.LogToConsole && Debugger.IsAttached)
                    Debug.Write(Output);
                Console.Write(Output);
                LogOutput?.Invoke(Output);
            }
        }
        public void Log(string text) {
            Log(new[] { text });
        }
        public void LogFormat(string text, string[] args) {
            Log(new[] { string.Format(text, args) });
        }
        public void LogFormat(string[] lines, string[] args) {
            Log(new[] { string.Format(string.Join(Environment.NewLine, lines), args) });
        }
        public void LogAsync(string[] lines) {
            if (Options.Logging) {
                Task.Run(() => {
                    string Output = string.Join(Environment.NewLine, lines) + Environment.NewLine;
                    if (Options.LogToConsole && Debugger.IsAttached)
                        Console.Write(Output);
                    LogOutput?.Invoke(Output);
                }).ConfigureAwait(false);
            }
        }
        public void LogAsync(string text) {
            LogAsync(new[] { text });
        }
        public void LogFormatAsync(string text, string[] args) {
            LogAsync(new[] { string.Format(text, args) });
        }
        public void LogFormatAsync(string[] lines, string[] args) {
            LogAsync(new[] { string.Format(string.Join(Environment.NewLine, lines), args) });
        }
        #endregion

        #region P2P
        public bool PeerToPeerInstance { get; internal set; } = false;
        Identifier ISocket.RemoteIdentity { get => RemoteIdentity; set => RemoteIdentity = value; }

        public async Task<ISocket> StartServer(string ID, NetworkOptions Options, string name = "WebSocketP2PServer", int Port = 0) {
            if (!Options.UsePeerToPeer) {
                InvokeOnError(new InvalidOperationException("P2P is not enabled."));
                return default;
            }
            // For WebSocket, you may need to implement signaling to exchange connection info
            var ServerInfo = await PeerServer.NewServer(Identifier.Create(ID), Identifier.Create(RemoteIdentity.ID), Options.Serializer, Port);
            var newServer = new WebSocketServer( (int)ServerInfo.Port, Name) { Options = Options, _PeerToPeerInstance = true };
            if (P2P_Servers.ContainsKey(ID))
                P2P_Servers[ID].CloseConnection();
            P2P_ServerInformation.AddOrUpdate(ID, ServerInfo);
            if (newServer.Listen()) {
                string RemoteIP = RemoteIdentity.IP;
                LogFormat(new[] { "[{0}] Started P2P Server.", "    EndPoint      = {1}:{2}" }, new[] { newServer.Name + @"\P2P}", RemoteIP, ServerInfo.Port.ToString() });
                P2P_Servers.AddOrUpdate(ID, newServer.Connection);
                Send(ServerInfo);
                return newServer;
            } else {
                InvokeOnError(Connection, new P2PException("Failed to start P2P Server." + Environment.NewLine + "Port may be in use." + Environment.NewLine + "Check if the port is already in use."));
                return null;
            }
        }

        /// <summary>
        /// Start a connection with the specified Remote Client.
        /// </summary>
        /// <param name="RemotePeer">The Remote Client.</param>
        /// <param name="Serializer">Serializer used for this connection.</param>
        /// <param name="Name">Name of the TcpServer (Used for logging)</param>
        /// <returns>new TcpServer; <see langword="null"/> if error occured.</returns>
        public async Task<ISocket> StartServer(Identifier RemotePeer, NetworkOptions Options, string Name = "TcpServer") {
            return await StartServer(RemotePeer.ID, Options, Name);
        }

        /// <summary>
        /// Start a connection with the specified Remote Client.
        /// </summary>
        /// <param name="RemotePeer">The Remote Client.</param>
        /// <param name="Name">Name of the TcpServer (Used for logging)</param>
        /// <returns>new TcpServer; <see langword="null"/> if error occured.</returns>
        public async Task<ISocket> StartServer(Identifier RemotePeer, string Name = "TcpServer") {
            return await StartServer(RemotePeer, Options, Name);
        }

        public async Task<WebSocketClient> AcceptPeerConnection(PeerServer server, string name = "WebSocketP2PClient") {
            if (!Options.UsePeerToPeer) {
                InvokeOnError(new InvalidOperationException("P2P is not enabled."));
                return default;
            }
            var v = await server.Accept(name);
            if (v is WebSocketClient wsClient)
                return wsClient;
            else
                return default;
        }


        private void HandlePeer(Identifier peer, NetworkConnection connection) {
            if (Options.UsePeerToPeer) {
                PeerUpdate?.Invoke(this, peer);
                switch (peer.Action) {
                    case PeerAction.LocalIdentity: {
                            RemoteIdentity = peer;
                            Connection._Identity = peer;
                            Connection.ID = Guid.Parse(peer.ID);
                            LogFormat("[{0}] Local Identity = {1}", new[] { Name, peer.ID.ToUpper() });
                            InvokeOnIdentified(this, ref Connection._Identity);
                            break;
                        }
                    case PeerAction.RemoteIdentity: {
                            InvokePeerConnected(this, peer);
                            Peers.AddOrUpdate(peer);
                            break;
                        }
                    case PeerAction.Dispose: {
                            InvokePeerDisconnected(this, peer);
                            Peers.Remove(peer.ID);
                            P2P_Clients.TryRemove(peer.ID, out _);
                            P2P_ServerInformation.TryRemove(peer.ID, out _);
                            break;
                        }
                    case PeerAction.MetadataUpdate: {
                            PeerMetaDataChanged?.Invoke(this, peer);
                            Peers.UpdateMetaData(peer);
                            break;
                        }
                }
            }
        }
        #endregion

        #region Sending
        public void Send(object obj) {
            SendAsync(obj, default);
        }
        protected internal async Task SendAsync(object Object, CancellationToken cancellationToken = default) {
            if (!Connected) return;
            object obj = Object;
#if UNITY
            MainThread.Run(() => {
		        obj = Object;
            });
#endif
#if WINDOWS
            Application.Current.Dispatcher.Invoke(() => {
                obj = Object;
            });
#endif
#if !UNITY && !WINDOWS
            obj = Object;
#endif
            byte[] serializedBytes = Options.Serializer.Serialize(new Wrapper(obj, this));
            if (serializedBytes.Length > 8192) {
                await SendSegmented(Connection, serializedBytes, obj.GetType(), obj);
            } else {
                byte[] payload = serializedBytes;
                if (Options.UseCompression) {
                    payload = Options.CompressionAlgorithm.Compress(payload);
                }
                await WriteWebSocketFrameAsync(payload, Options.UseCompression ? 0x2 : 0x1, cancellationToken);
                if (Options.Logging && Options.LogSendEvents) {
                    Type objType = obj.GetType();
                    if (Globals.IgnoreLoggedTypes.Contains(objType)) return;
                    InvokeOnSent(new SentEventArgs(this, Connection, objType, serializedBytes.Length));
                    if( objType == typeof(PeerRedirect)) {
                        PeerRedirect redirect = (PeerRedirect)obj;
                        LogFormatAsync("[{0}] Sent PeerRedirect<{1}> - {2}", new[] { Name, redirect.CleanTypeName, serializedBytes.Length.ByteToString() });
                    } else {
                        LogFormatAsync("[{0}] Sent {1} - {2}", new[] { Name, objType.Name, serializedBytes.Length.ByteToString() });
                    }
                      
                }
            }
        }

        private static int GetWebSocketHeaderLength(int payloadLength) {
            if (payloadLength <= 125)
                return 2;
            if (payloadLength <= ushort.MaxValue)
                return 4;
            return 10;
        }

        private static int WriteWebSocketHeader(Span<byte> dest, int opcode, int payloadLength) {
            dest[0] = (byte)(0x80 | (opcode & 0x0F));
            if (payloadLength <= 125) {
                dest[1] = (byte)(0x80 | payloadLength);
                return 2;
            }
            if (payloadLength <= ushort.MaxValue) {
                dest[1] = (byte)(0x80 | 126);
                dest[2] = (byte)((payloadLength >> 8) & 0xFF);
                dest[3] = (byte)(payloadLength & 0xFF);
                return 4;
            }
            dest[1] = (byte)(0x80 | 127);
            ulong len = (ulong)payloadLength;
            dest[2] = (byte)((len >> 56) & 0xFF);
            dest[3] = (byte)((len >> 48) & 0xFF);
            dest[4] = (byte)((len >> 40) & 0xFF);
            dest[5] = (byte)((len >> 32) & 0xFF);
            dest[6] = (byte)((len >> 24) & 0xFF);
            dest[7] = (byte)((len >> 16) & 0xFF);
            dest[8] = (byte)((len >> 8) & 0xFF);
            dest[9] = (byte)(len & 0xFF);
            return 10;
        }

        private async Task WriteWebSocketFrameAsync(byte[] payload, int opcode, CancellationToken cancellationToken) {
            if (_stream == null)
                return;

            await _writeSemaphore.WaitAsync(cancellationToken);
            try {
                var headerLen = GetWebSocketHeaderLength(payload.Length);
                const int maskLen = 4;
                var frameLen = headerLen + maskLen + payload.Length;
                var rented = ArrayPool<byte>.Shared.Rent(frameLen);

                try {
                    // Avoid Span locals in async methods for netstandard2.1 compatibility.
                    var headerBuf = new byte[10];
                    var writtenHeader = WriteWebSocketHeader(headerBuf, opcode, payload.Length);
                    Buffer.BlockCopy(headerBuf, 0, rented, 0, writtenHeader);

                    var maskKey = new byte[maskLen];
                    RandomNumberGenerator.Fill(maskKey);
                    Buffer.BlockCopy(maskKey, 0, rented, writtenHeader, maskLen);

                    var payloadOffset = writtenHeader + maskLen;
                    for (int i = 0; i < payload.Length; i++) {
                        rented[payloadOffset + i] = (byte)(payload[i] ^ maskKey[i % 4]);
                    }

                    await _stream.WriteAsync(rented, 0, frameLen, cancellationToken);
                }
                finally {
                    ArrayPool<byte>.Shared.Return(rented);
                }
            }
            finally {
                _writeSemaphore.Release();
            }
        }

        #endregion

        public class WscObject {
            public object Object;
            public int Length;
            public WscObject(object Object, int Length) {
                this.Object = Object;
                this.Length = Length;
            }
        }

        #region Receiving
        public async Task<WscObject> ReceiveAsync(CancellationTokenSource cts = default) {
            try {
                var header = new byte[2];
                int bytesRead = await _stream.ReadAsync(header, 0, 2, cts.Token);
                bool fin = (header[0] & 0x80) != 0;
                int opcode = header[0] & 0x0F;
                bool mask = (header[1] & 0x80) != 0;
                int payloadLen = header[1] & 0x7F;
                if (opcode == 0x8) {
                    return new WscObject(opcode, -1);
                }
                if (payloadLen == 126) {
                    var ext = new byte[2];
                    bytesRead = await _stream.ReadAsync(ext, 0, 2, cts.Token);
                    payloadLen = (ext[0] << 8) | ext[1];
                } else if (payloadLen == 127) {
                    var ext = new byte[8];
                    bytesRead = await _stream.ReadAsync(ext, 0, 8, cts.Token);
                    payloadLen = (int)BitConverter.ToUInt64(ext, 0);
                }
                byte[] maskKey = null;
                if (mask) {
                    maskKey = new byte[4];
                    bytesRead = await _stream.ReadAsync(maskKey, 0, 4, cts.Token);
                    if (bytesRead == 0)
                        return null;
                }
                var payload = new byte[payloadLen];
                int read = 0;
                while (read < payloadLen) {
                    int r = await _stream.ReadAsync(payload, read, payloadLen - read, cts.Token);
                    read += r;
                }
                if (mask && maskKey != null) {
                    for (int i = 0; i < payload.Length; i++)
                        payload[i] ^= maskKey[i % 4];
                }
                _totalBytesReceived += payload.Length;
                byte[] data = payload;
                if (Options.UseCompression && opcode == 0x2) {
                    data = Options.CompressionAlgorithm.Decompress(data);
                }
                if(data.Length == 0) 
                    return null;
                Wrapper wrapper = Options.Serializer.Deserialize(data);

                var serializer = Options.Serializer;
                if (wrapper == null || wrapper.value == null) {
                    PeerRedirect redirect = serializer.DeserializeRedirect(this, data);
                    if (Options.Logging && Options.LogReceiveEvents) {
                        LogFormatAsync("[{0}] Received {1} - {2}", new[] { Name, string.Format("PeerRedirect<{0}>", ((PeerRedirect)redirect).CleanTypeName), payload.Length.ByteToString() });
                    }
                    if (redirect == null) {
                        InvokeOnError(Connection, new P2PException("Deserialized object returned null."));
                    } else {
                        return new WscObject(redirect, payload.Length);
                    }
                } else {
                    var valueType = wrapper.GetValueType();
                    if (wrapper.value != null || wrapper.Type != "") { //wrapper.Type != typeof(PingObject).AssemblyQualifiedName
                        if (valueType == typeof(PeerRedirect)) {
                            Byte[] redirectBytes = null;
                            object val = wrapper.value;
                            Type type = wrapper.value.GetType();
                            if (type == typeof(string)) {
                                redirectBytes = System.Text.UTF8Encoding.UTF8.GetBytes((string)val);
                            } else if (type == typeof(JsonElement)) {
                                string json = ((JsonElement)val).GetRawText();
                                redirectBytes = System.Text.UTF8Encoding.UTF8.GetBytes(json);
                            }
                            PeerRedirect redirect = serializer.DeserializeRedirect(this, redirectBytes);
                            if (Options.Logging && Options.LogReceiveEvents) {
                                LogFormatAsync("[{0}] Received {1} - {2}", new[] { Name, string.Format("PeerRedirect<{0}>", ((PeerRedirect)redirect).CleanTypeName), payload.Length.ByteToString() });
                            }
                            return new WscObject(redirect, payload.Length);
                        } else {
                            object unwrapped = null;
                            try {
                                unwrapped = wrapper.Unwrap(this);
                            } catch (Exception ex) {
                                InvokeOnError(Connection, ex);
                            }
                            if (unwrapped != null)
                                if (Options.Logging && Options.LogReceiveEvents && !Globals.IgnoreLoggedTypes.Contains(unwrapped.GetType())) {
                                    LogFormatAsync("[{0}] Received {1} - {2}", new[] { Name, valueType.Name, payload.Length.ByteToString() });
                                }
                            return new WscObject(unwrapped, payload.Length);
                        }
                    }
                }



                //if (wrapper != null)
                //    return new WscObject(wrapper.Unwrap(this), payload.Length);
                //else
                    return null;
            } catch (Exception ex) {
                if(!ex.Message.Contains("Unable to read data from the transport connection")) {
                    InvokeOnError(Connection, ex);
                }
                return null;
            }
        }
        #endregion

        #region Callbacks
        private Dictionary<Type, List<Action<IReceivedEventArgs>>> TypeCallbacks = new();
        public void RegisterCallback<T>(Action<ReceivedEventArgs<T>> action) {
            Type type = typeof(T);
            if (!TypeCallbacks.ContainsKey(type))
                TypeCallbacks[type] = new List<Action<IReceivedEventArgs>>();
            TypeCallbacks[type].Add(e => action((ReceivedEventArgs<T>)e));
        }
        public void RemoveCallback<T>(Action<ReceivedEventArgs<T>> action) {
            Type type = typeof(T);
            if (TypeCallbacks.ContainsKey(type)) {
                TypeCallbacks[type].Remove(e => action((ReceivedEventArgs<T>)e));
                if (TypeCallbacks[type].Count == 0)
                    TypeCallbacks.Remove(type);
            }
        }
        public void RemoveCallback<T>() {
            Type type = typeof(T);
            if (TypeCallbacks.ContainsKey(type))
                TypeCallbacks.Remove(type);
        }
        protected internal void InvokeAllCallbacks(IReceivedEventArgs e) {
            if (TypeCallbacks.TryGetValue(e.Type, out var list)) {
                foreach (var cb in list) {
#if UNITY
            MainThread.Run(() => {
                cb(e);
            });
#endif
#if WINDOWS
                    Application.Current.Dispatcher.Invoke(() => {
                        cb(e);
                    });
#endif
#if !UNITY && !WINDOWS
                cb(e);
#endif
                }
            }
        }
        #endregion

        public WebSocketClient() {
            Peers = new PeerList(this);
            OnError += WebSocketClient_OnError;
            var argsClient = (ISocket)this;
            Globals.RegisterClient(ref argsClient);
        }

        private void WebSocketClient_OnError(ErrorEventArgs e) {
            if(Options.Logging) {
                var error = e.Exception.Message + Environment.NewLine + e.Exception.StackTrace;
                LogOutput?.Invoke(error);
                if(Options.LogToConsole) Console.WriteLine(error);
            }
        }

        public WebSocketClient(NetworkOptions Options, string Name = "WebSocketClient") {
            this.Name = Name;
            Peers = new PeerList(this);
            OnError += WebSocketClient_OnError;
            var argsClient = (ISocket)this;
            Globals.RegisterClient(ref argsClient);
        }

        public async Task<bool> Connect(string host, int port) {
            string scheme = Options.UseSsl ? "wss" : "ws";
            return await ConnectAsync(new Uri($"{scheme}://{host}:{port}"));
        }

        private Type[] SkipLoggingTypes = new Type[] {
            typeof(Identifier),
            typeof(Identifier[])
        };
        public async Task<bool> ConnectAsync(Uri uri) {
            try {
                if (Options.Logging) {
                    LogFormatAsync("[{0}] Connecting to -> {1}", new[] { Name, uri.ToString() });
                }
                string host = uri.Host;
                bool secure = uri.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase) || Options.UseSsl;
                int port = uri.IsDefaultPort || uri.Port < 0
                    ? (secure ? 443 : 80)
                    : uri.Port;
                _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                await _socket.ConnectAsync(host, port);
                Connection = new NetworkConnection(this, _socket);
                Connection._Stream = new NetworkStream(_socket, ownsSocket: false);
                if (secure) {
                    Options.UseSsl = true;
                    var sslStream = new SslStream(Connection._Stream, false);
                    await sslStream.AuthenticateAsClientAsync(host);
                    Connection.SslStream = sslStream;
                    _stream = sslStream;
                } else {
                    _stream = Connection._Stream;
                }
                var cts = new CancellationTokenSource();
                // Monitor socket and connection state in a background task
                var monitorTask = Task.Run(async () => {
                    while (Connection.Socket != null && Connection.Socket.Connected && !Connection.Closed) {
                        await Task.Delay(100);
                    }
                    if (!cts.IsCancellationRequested) {
                        cts.Cancel();
                    }
                });
                // Handshake
                string secWebSocketKey = Convert.ToBase64String(Guid.NewGuid().ToByteArray());
                string requestTarget = string.IsNullOrEmpty(uri.PathAndQuery) ? "/" : uri.PathAndQuery;
                string hostHeader = IsDefaultWebSocketPort(secure, port) ? host : host + ":" + port.ToString();
                var request = $"GET {requestTarget} HTTP/1.1\r\n" +
                              $"Host: {hostHeader}\r\n" +
                              "Upgrade: websocket\r\n" +
                              "Connection: Upgrade\r\n" +
                              $"Sec-WebSocket-Key: {secWebSocketKey}\r\n" +
                              "Sec-WebSocket-Version: 13\r\n" +
                              "\r\n";
                var requestBytes = Encoding.UTF8.GetBytes(request);
                await _stream.WriteAsync(requestBytes, 0, requestBytes.Length, cts.Token);
                // Read response
                var responseBuilder = new StringBuilder();
                var buffer = new byte[1024];
                int bytesRead = 0;
                do {
                    bytesRead = await _stream.ReadAsync(buffer, 0, buffer.Length, cts.Token);
                    responseBuilder.Append(Encoding.UTF8.GetString(buffer, 0, bytesRead));
                } while (!responseBuilder.ToString().Contains("\r\n\r\n") && bytesRead > 0);
                string response = responseBuilder.ToString();
                if (!response.StartsWith("HTTP/1.1 101")) {
                    LogFormatAsync("[{0}] Failed to connect to -> {1}", new[] { Name, uri.ToString() });
                    cts.Dispose();
                    return false;
                }
                _handshakeComplete = true;
                InvokeOnConnected(new ConnectedEventArgs(this, Connection));
                if (Options.Logging) {
                    LogFormatAsync("[{0}] Connected to  -> {1}", new[] { Name, uri.ToString() });
                }
                Task.Run(async () => {
                    while (!_handshakeComplete && Connected) {
                        await Task.Delay(100);
                    }
                    while (Connected && !cts.IsCancellationRequested) {
                        try {
                            var receivedObject = await ReceiveAsync(cts);
                            if (receivedObject == null || receivedObject.Length == -1) {
                                // connection closed remotely
                                //break;
                            } else if (receivedObject != null && receivedObject.Object != null) {
                                Type objType = receivedObject.Object.GetType();
                                if (Options.Logging && Options.LogReceiveEvents && !SkipLoggingTypes.Contains(objType)) {
                                    if (ReferenceEquals(objType, typeof(PeerRedirect))) {
                                        LogFormatAsync("[{0}] Received {1} - {2}", new[] { Name, string.Format("PeerRedirect<{0}>", ((PeerRedirect)receivedObject.Object).CleanTypeName), receivedObject.Length.ByteToString() });
                                    } else if(!Globals.IgnoreLoggedTypes.Contains(objType)) {
                                        LogFormatAsync("[{0}] Received {1} - {2}", new[] { Name, objType.Name, receivedObject.Length.ByteToString() });
                                    }
                                }
                                HandleReceive(Connection, receivedObject.Object, objType, receivedObject.Length);
                            }
                        } catch (ObjectDisposedException) {
                            break;
                        } catch (OperationCanceledException) {
                            break;
                        } catch (Exception ex) {
                            InvokeOnError(ex);
                            break;
                        }
                    }
                    // Only disconnect when the loop exits
                    if (InvokeOnDisconnected(new DisconnectedEventArgs(this, Connection, DisconnectionReason.RemoteSocketClosed))) {
                        if (Options.Logging) {
                            LogFormatAsync("[{0}] Disconnected  -> {1}", new[] { Name, uri.ToString() });
                        }
                    }
                    cts.Dispose();
                });
                return true;
            } catch (Exception ex) {
                InvokeOnError(ex);
                return false;
            }
        }

        private static bool IsDefaultWebSocketPort(bool secure, int port) {
            return secure ? port == 443 : port == 80;
        }

        public void Dispose() {
            if(Connected) {
                var argClient = (ISocket)this;
                Globals.UnregisterClient(ref argClient);
                InvokeOnDisposing();
                CloseConnection();
                isDisposed = true;
            }
        }

    }
}
