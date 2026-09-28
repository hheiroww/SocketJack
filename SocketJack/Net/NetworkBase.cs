using SocketJack.Extensions;
using SocketJack.Net.P2P;
using SocketJack.Serialization;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
#if WINDOWS
using System.Windows;
#endif

namespace SocketJack.Net {

    /// <summary>
    /// Base class for a Client / Server.
    /// </summary>
    /// <remarks></remarks>
    public abstract class NetworkBase : IDisposable, ISocket {

        #region Events

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

        protected internal event PeerRefusedConnectionEventHandler PeerRefusedConnection;
        protected internal delegate void PeerRefusedConnectionEventHandler(ISocket sender, ConnectionRefusedArgs e);

        protected internal event PeerUpdateEventHandler PeerUpdate;
        protected internal delegate void PeerUpdateEventHandler(ISocket sender, Identifier Peer);

        /// <summary>
        /// Fired when a user connects to the server.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="Peer"></param>
        public event PeerConnectedEventHandler PeerConnected;
        public delegate void PeerConnectedEventHandler(ISocket sender, Identifier Peer);

        /// <summary>
        /// Fired when a user disconnects from the server.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="Peer"></param>
        public event PeerDisconnectedEventHandler PeerDisconnected;
        public delegate void PeerDisconnectedEventHandler(ISocket sender, Identifier Peer);

        protected internal event InternalPeerRedirectEventHandler InternalPeerRedirect;
        protected internal delegate void InternalPeerRedirectEventHandler(string Recipient, string Sender, object Obj, int BytesReceived);

        protected internal event InternalReceiveEventEventHandler InternalReceiveEvent;
        protected internal delegate void InternalReceiveEventEventHandler(NetworkConnection Connection, Type objType, object obj, int BytesReceived);

        protected internal event InternalReceivedByteCounterEventHandler InternalReceivedByteCounter;
        protected internal delegate void InternalReceivedByteCounterEventHandler(NetworkConnection Connection, int BytesReceived);

        protected internal event InternalSentByteCounterEventHandler InternalSentByteCounter;
        protected internal delegate void InternalSentByteCounterEventHandler(NetworkConnection Connection, int BytesSent);

		protected internal event InternalSendEventEventHandler InternalSendEvent;
		protected internal delegate void InternalSendEventEventHandler(NetworkConnection Connection, Type objType, object obj, int BytesSent);

		/// <summary>
		/// Dispatches <paramref name="action"/> on the UI / main thread.
		/// On WPF, verifies <see cref="Application.Current"/> is still alive;
		/// if the application has already shut down, calls <see cref="ThreadManager.Shutdown"/> instead.
		/// </summary>
		protected internal static void Invoke(Action action) {
#if UNITY
			MainThread.Run(action);
#endif
#if WINDOWS
			if (Application.Current != null) {
				Application.Current?.Dispatcher.Invoke(action);
			} else {
				ThreadManager.Shutdown();
			}
#endif
#if !UNITY && !WINDOWS
			action();
#endif
		}

		void ISocket.InvokePeerConnectionRequest(ISocket sender, ref PeerServer Server) {
			var s = Server;
			Invoke(() => {
				Internal_PeerConnectionRequest?.Invoke(sender, ref s);
			});
		}
		void ISocket.InvokeBytesPerSecondUpdate(int ReceivedPerSecond, int SentPerSecond) {
			Invoke(() => {
				BytesPerSecondUpdate?.Invoke(ReceivedPerSecond, SentPerSecond);
			});
		}
		void ISocket.InvokePeerServerShutdown(ISocket sender, PeerServer Server) {
			Invoke(() => {
				PeerServerShutdown?.Invoke(sender, Server);
			});
		}
		void ISocket.InvokePeerUpdate(ISocket sender, Identifier Peer) {
			Invoke(() => {
				PeerUpdate?.Invoke(sender, Peer);
			});
		}
		void ISocket.InvokePeerConnected(ISocket sender, Identifier Peer) {
			// If this is a client that hasn't been identified yet, defer the
			// event until RemoteIdentity is available so that subscribers can
			// safely use Peers.FirstNotMe(), Send(peer, …), etc.
			if (Connection != null && !Connection.IsServer && RemoteIdentity == null) {
				Task.Run(async () => {
					while (RemoteIdentity == null && Connected && !isDisposed) {
						await Task.Delay(10);
					}
					if (RemoteIdentity != null && Connected && !isDisposed) {
						((ISocket)this).InvokePeerConnected(sender, Peer);
					}
				});
				return;
			}
			Invoke(() => {
				PeerConnected?.Invoke(sender, Peer);
			});
		}
		void ISocket.InvokePeerDisconnected(ISocket sender, Identifier Peer) {
			Invoke(() => {
				PeerDisconnected?.Invoke(sender, Peer);
			});
		}
		void ISocket.InvokeInternalReceivedByteCounter(NetworkConnection Connection, int BytesReceived) {
			// Byte counters use Interlocked.Add and are thread-safe;
			// skip Dispatcher marshaling to avoid blocking the receive path.
			InternalReceivedByteCounter?.Invoke(Connection, BytesReceived);
		}
		void ISocket.InvokeInternalSentByteCounter(NetworkConnection connection, int chunkSize) {
			// Byte counters use Interlocked.Add and are thread-safe;
			// skip Dispatcher marshaling to avoid blocking the send path.
			InternalSentByteCounter?.Invoke(connection, chunkSize);
		}
		void ISocket.InvokeInternalSendEvent(NetworkConnection connection, Type type, object @object, int length) {
			Invoke(() => {
				InternalSendEvent?.Invoke(connection, type, @object, length);
			});
		}
		void ISocket.InvokeOnReceive(ref IReceivedEventArgs e) {
			var args = e;
			Invoke(() => {
				OnReceive?.Invoke(ref args);
			});
		}
		void ISocket.InvokeOnSent(SentEventArgs sentEventArgs) {
			Invoke(() => {
				OnSent?.Invoke(sentEventArgs);
			});
		}
		void ISocket.InvokeLogOutput(string text) {
			Invoke(() => {
				LogOutput?.Invoke(text);
			});
		}
		void ISocket.InvokeOnError(NetworkConnection Connection, Exception e) {
			Invoke(() => {
				OnError?.Invoke(new ErrorEventArgs(this, Connection, e));
			});
		}
		void ISocket.CloseConnection(NetworkConnection Connection, DisconnectionReason Reason) {
			Connection.Close(this, Reason);
		}
		void ISocket.InvokeOnDisposing() {
			Invoke(() => {
				OnDisposing?.Invoke();
			});
		}
		protected internal void InvokeOnReceive(IReceivedEventArgs e) {
			Invoke(() => {
				OnReceive?.Invoke(ref e);
			});
		}
		protected internal void InvokeBytesPerSecondUpdate(NetworkConnection Connection) {
			Connection.RecordBpsSample();
			Invoke(() => {
				BytesPerSecondUpdate?.Invoke(Connection.BytesPerSecondReceived, Connection.BytesPerSecondSent);
			});
		}
		protected internal void InvokeOnError(NetworkConnection Connection, Exception e) {
			LogFormat("[{0}] ERROR: {1}", [Name + (Connection != null && Connection.Identity != null ? @"\" + Connection.Identity.ID.ToUpper() : @"\Null"), e.Message]);
			if (e.StackTrace != string.Empty) LogAsync(e.StackTrace);
			Invoke(() => {
				OnError?.Invoke(new ErrorEventArgs(this, Connection, e));
				if (e.InnerException != null) InvokeOnError(Connection, e.InnerException);
			});
		}
		protected internal void InvokeAllCallbacks(IReceivedEventArgs e) {
			if (TypeCallbacks.TryGetValue(e.Type, out var l)) {
				for (int i = 0, loopTo = l.Count - 1; i <= loopTo; i++) {
					int index = i;
					Invoke(() => {
						l.ElementAt(index).Invoke(e);
					});
				}
			}
		}
		protected internal void InvokeAllCallbacks(Type Type, IReceivedEventArgs e) {
			if (TypeCallbacks.TryGetValue(Type, out var l)) {
				for (int i = 0, loopTo = l.Count - 1; i <= loopTo; i++) {
					int index = i;
					Invoke(() => {
						l.ElementAt(index).Invoke(e);
					});
				}
			}
		}

        #endregion

        #region Internal
        public PeerList Peers { get; internal set; }
        PeerList ISocket.Peers { get => Peers; set => Peers = value; }
        NetworkConnection ISocket.Connection { get => Connection; }
        ConcurrentDictionary<string, PeerServer> ISocket.P2P_ServerInformation { get => P2P_ServerInformation; set => P2P_ServerInformation = value; }
        ConcurrentDictionary<string, NetworkConnection> ISocket.P2P_Servers { get => P2P_Servers; set => P2P_Servers = value; }
        ConcurrentDictionary<string, NetworkConnection> ISocket.P2P_Clients { get => P2P_Clients; set => P2P_Clients = value; }

        protected internal ConcurrentDictionary<string, PeerServer> P2P_ServerInformation = new();
        protected internal ConcurrentDictionary<string, NetworkConnection> P2P_Servers = new();
        protected internal ConcurrentDictionary<string, NetworkConnection> P2P_Clients = new();
        protected internal ConcurrentDictionary<Guid, byte[]> Buffers = new();
        protected internal Dictionary<Type, List<Action<IReceivedEventArgs>>> TypeCallbacks = new();
#if WINDOWS
        /// <summary>
        /// Tracks active control-share sessions on the server: (SharerID, ViewerID).
        /// Populated when a ControlShareFrame redirect passes through.
        /// Used to block unauthorized ControlShareRemoteAction messages.
        /// </summary>
        protected internal ConcurrentDictionary<(string Sharer, string Viewer), byte> _ControlShareSessions = new();
#endif

        void ISocket.HandleReceive(NetworkConnection connection, object obj, Type objType, int Length) {
            HandleReceive(connection, obj, objType, Length);
        }
        public void HandleReceive(NetworkConnection Connection, object obj, Type objType, int Length) {
            switch (objType) {
                case var @case when @case == typeof(Identifier): {
                        if (Options.UsePeerToPeer)
                            PeerUpdate?.Invoke(this, (Identifier)obj);
                        break;
                    }
                case var @case when @case == typeof(Identifier[]): {
                        if (Options.UsePeerToPeer) {
                            var PeerIDs = (Identifier[])obj;
                            foreach (var item in PeerIDs) {
                                PeerUpdate?.Invoke(this, item);
                            }
                        }

                        break;
                    }
                case var case1 when case1 == typeof(PeerServer): {
                        PeerServer pServer = (PeerServer)obj;
                        if (this.Connection.IsServer)
                            pServer.LocalClient.IP = Connection.EndPoint.ToString();
                        if (Options.UsePeerToPeer) {
                            if (pServer.Shutdown) {
                                PeerServerShutdown?.Invoke(this, pServer);
                            } else {
                                Internal_PeerConnectionRequest?.Invoke(this, ref pServer);
                                PeerConnectionRequest?.Invoke(this, pServer);
                            }
                        } else { Send(this.Connection, new ConnectionRefusedArgs("Peer to peer is disabled on this connection.", pServer)); }
                        break;
                    }
                case var case2 when case2 == typeof(ConnectionRefusedArgs): {
                        ConnectionRefusedArgs refusedArgs = (ConnectionRefusedArgs)obj;
                        PeerRefusedConnection?.Invoke(this, refusedArgs);
                        break;
                    }
                case var case3 when case3 == typeof(Segment): {
                        Segment s = (Segment)obj;
                        if (Segment.Cache.TryGetValue(s.SID, out var segmentList)) {
                            segmentList.Add(s);
                            if (Segment.SegmentComplete(s)) {
                                byte[] RebuiltSegments = Segment.Rebuild(s);
                                try {
                                    var segObj = ((Wrapper)Options.Serializer.Deserialize(RebuiltSegments)).Unwrap(this);
                                    var segObjType = segObj.GetType();
                                    var e = new ReceivedEventArgs<Segment>(this, Connection, segObj, RebuiltSegments.Length);
                                    InternalReceiveEvent?.Invoke(Connection, segObjType, segObj, RebuiltSegments.Length);
                                    IReceivedEventArgs args = e;
                                    OnReceive?.Invoke(ref args);
                                    InvokeAllCallbacks(e);
                                } catch (Exception) {
                                    InvokeOnError(Connection, new Exception("Failed to deserialize segment."));
                                }
                            }
                        } else {
                            Segment.Cache.Add(s.SID, new List<Segment> { s });
                        }

                        break;
                    }
                case var case4 when case4 == typeof(PeerRedirect): {
                        PeerRedirect redirect = (PeerRedirect)obj;
                        if (redirect == null) return;
                        if (this.Connection.IsServer)
                            redirect.Sender = Connection.ID.ToString();
                        //var e = new ReceivedEventArgs<PeerRedirect>(this, Connection, redirect, Length);

                        if (this.Connection.IsServer) {
                            Type redirectType = Wrapper.GetValueType(redirect.Type);
                            if (redirectType == null) {
                                // Inner type cannot be resolved — forward the redirect
                                // without invoking typed callbacks.
                                if (Connection.Identity == null || !Peers.TryGetValue(Connection.Identity.ID, out _)) break;
                                bool allowUntyped = true;
                                if (allowUntyped) {
                                    if (redirect.Recipient == "#ALL#") {
                                        SendBroadcast(redirect, Connection);
                                    } else if (Peers.TryGetValue(redirect.Recipient, out Identifier rIDUntyped)) {
                                        Send(rIDUntyped, redirect);
                                    }
                                }
                                break;
                            }
                            var genericType = typeof(ReceivedEventArgs<>).MakeGenericType(redirectType);
                            var receivedEventArgs = (IReceivedEventArgs)Activator.CreateInstance(genericType);
                            if (!Peers.TryGetValue(Connection.Identity.ID, out var from)) return;
                            receivedEventArgs.From = from;
                            receivedEventArgs.Initialize(this, Connection, obj, Length);
                            var NonGenericEventArgs = new ReceivedEventArgs<object>(this, Connection, obj, Length) {
                                From = from
                            };
                            InvokeOnReceive(NonGenericEventArgs);
                            InvokeAllCallbacks(receivedEventArgs);

                            if (receivedEventArgs.IsPeerRedirect && !NonGenericEventArgs.CancelPeerRedirect && !receivedEventArgs.CancelPeerRedirect) {
                                bool allowRedirect = true;
#if WINDOWS
                                if (redirectType != null) {
                                    if (redirectType.Name == "ControlShareFrame") {
                                        _ControlShareSessions[(redirect.Sender, redirect.Recipient)] = 0;
                                    } else if (redirectType.Name == "ControlShareRemoteAction") {
                                        allowRedirect = _ControlShareSessions.ContainsKey((redirect.Recipient, redirect.Sender));
                                    }
                                }
#endif
                                if (allowRedirect) {
                                    if (redirect.Recipient == "#ALL#") {
                                        SendBroadcast(redirect, Connection);
                                    } else if (Peers.TryGetValue(redirect.Recipient, out Identifier rID)) {
                                        Send(rID, redirect);
                                    }
                                }
                            }

                            //if (this.Connection.IsServer) {
                            //    IReceivedEventArgs args = e;
                            //    args.From = Connection.Identity;
                            //    OnReceive?.Invoke(ref args);
                        } else {
                            InternalReceiveEvent?.Invoke(Connection, objType, obj, Length);
                            InternalPeerRedirect?.Invoke(redirect.Recipient, redirect.Sender, redirect.Value, Length);
                        }
                        //if (!e.CancelPeerRedirect) {
                        //    //if (Options.LogReceiveEvents && !Globals.IgnoreLoggedTypes.Contains(objType))
                        //    InternalReceiveEvent?.Invoke(Connection, objType, obj, Length);
                        //    InternalPeerRedirect?.Invoke(redirect.Recipient, Connection.Identity.ID, redirect.value, Length);
                        //}
                        break;
                    }
                default: {
                        //if (Options.LogReceiveEvents && !Globals.IgnoreLoggedTypes.Contains(objType))
                        InternalReceiveEvent?.Invoke(Connection, objType, obj, Length);
                        var genericType = typeof(ReceivedEventArgs<>).MakeGenericType(obj.GetType());

                        var receivedEventArgs = (IReceivedEventArgs)Activator.CreateInstance(genericType);
                        receivedEventArgs.Initialize(this, Connection, obj, Length);
                        var NonGenericEventArgs = new ReceivedEventArgs<object>(this, Connection, obj, Length);
                        IReceivedEventArgs nonGenericArgs = NonGenericEventArgs;
                        OnReceive?.Invoke(ref nonGenericArgs);
                        InvokeAllCallbacks(receivedEventArgs);
                        break;
                    }
            }
        }
        public void CloseConnection(NetworkConnection Connection, DisconnectionReason Reason = DisconnectionReason.Unknown) {
            Connection.Close(this, Reason);
        }
        protected internal void Bind(int Port) {
            var bindAddress = Options?.BindAddress ?? IPAddress.Any;
            this.Socket.Bind(new IPEndPoint(bindAddress, Port));
        }
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
                        Debug.Write(Output);
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
        protected internal void Send(NetworkConnection Connection, object Obj) {
            if (this is UdpClient udpClient) { udpClient.Send(Obj); return; }
            if (this is UdpServer udpServer) {
                if (Connection != null) udpServer.Send(Connection.ID.ToString(), Obj);
                return;
            }
            if (Connection.Socket == null)
                return;
            if (Connection.Socket.Connected) {
                var wrapped = new Wrapper(Obj, Connection.Parent);
                var Bytes = Connection.Parent.Options.Serializer.Serialize(wrapped);
                Bytes = Connection.PatternCache.PrepareSend(Bytes, Connection.Parent.Options);
                byte[] ProcessedBytes = Connection.Compressed ? Options.CompressionAlgorithm.Compress(Bytes) : Bytes;
                var SerializedBytes = ProcessedBytes.Terminate();
                Connection.TryEnqueueSendBytes(SerializedBytes);
            }
            //Connection.SendQueue.Enqueue(new SendQueueItem(Obj, Connection));
        }

        void ISocket.Send(NetworkConnection connection, object Obj) {
            if (this is UdpServer || this is UdpClient) { Send(connection, Obj); return; }
            if (!Connected) return;
            Send(connection, Obj);
        }

        public virtual void Send(Identifier Recipient, object Obj) {
            if (Recipient == null || !Connected) return;
            Send(Connection, new PeerRedirect(Recipient.ID, Obj));
        }

        public void Send(NetworkConnection connection, Identifier Recipient, object Obj) {
            if (Recipient == null || !Connected) return;
            if (RemoteIdentity is null) {
                InvokeOnError(Connection, new P2PException("P2P Client not yet initialized." + Environment.NewLine +
                                                                      "    ConnectedSocket.Identifier property cannot equal null." + Environment.NewLine +
                                                                      "    Invoke via TcpClient.OnIdentified Event instead of TcpClient.OnConnected."));
            } else {
                Send(connection, new PeerRedirect(RemoteIdentity.ID, Recipient.ID, Obj));
            }
        }

        protected internal void StartReceiving() {
            Connection.StartReceiving();
        }

        protected internal void StartSending() {
            Connection.StartSending();
        }

        protected internal void StartConnectionTester() {
            Connection.StartConnectionTester();
        }

        #endregion

        #region Properties

        /// <summary>
        /// (Optional) Name used for logging purposes.
        /// </summary>
        /// <returns></returns>
        public virtual string Name { get; set; } = "TcpBase";
        public virtual Stream Stream { get; set; }
        public virtual IPEndPoint EndPoint { get; set; }

        public NetworkOptions Options = NetworkOptions.NewDefault();
        NetworkOptions ISocket.Options { get => Options; set => Options = value; }

        /// <summary>
        /// Remote identifier used for peer-to-peer interactions used to determine the server-side client ID.
        /// </summary>
        /// <returns><see langword="null"/> if accessed before the server identifies the client.
        /// <para>To avoid problems please do not acccess this via OnConnected Event.</para></returns>
        public Identifier RemoteIdentity {
            get {
                if (Connection != null) {
                    return Connection.Identity;
                } else {
                    return null;
                }
            }
        }

        /// <summary>
        /// Not to be confused with RemoteIdentity, InternalID is used for internally identifying the client.
        /// </summary>
        /// <returns></returns>
        public Guid InternalID {
            get {
                return _InternalID;
            }
        }
        private readonly Guid _InternalID = Guid.NewGuid();

        /// <summary>
        /// True if sending or receiving.
        /// </summary>
        public bool Active {
            get {
                return Connection != null && Connection.Active;
            }
        }

        /// <summary>
        /// The base connection for identification.
        /// </summary>
        public NetworkConnection Connection { get; set; }

        /// <summary>
        /// The main socket that listens to all requests. Using the TCP protocol.
        /// </summary>
        /// <remarks>Uses AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp</remarks>
        public virtual Socket Socket { get; set; }

        /// <summary>
        /// The bound port.
        /// </summary>
        /// <value>Integer</value>
        /// <returns></returns>
        /// <remarks></remarks>
        public virtual int Port {
            get {
                return _Port;
            }
            set {
                _Port = value;
            }
        }
        protected internal int _Port;

        /// <summary>
        /// Returns the bound IPEndPoint.
        /// </summary>
        /// <value>IPEndPoint</value>
        /// <returns></returns>
        /// <remarks></remarks>
        public IPEndPoint LocalIPEndPoint {
            get {
                try {
                    return (IPEndPoint)Socket?.LocalEndPoint;
                } catch {
                    return default;
                }
            }
        }

        /// <summary>
        /// Current connection State.
        /// </summary>
        /// <returns><see langword="true"/> if Socket.Connected or Socket.Listening; otherwise <see langword="false"/></returns>
        public virtual bool Connected {
            get {
                return Socket != null && Socket.Connected;
            }
        }

        /// <summary>
        /// True if is Peer to Peer Client or Server instance.
        /// </summary>
        /// <returns></returns>
        public bool PeerToPeerInstance { get; internal set; }

        public bool IsReceiving {
            get {
                return Connection != null && Connection.IsReceiving;
            }
        }
        public bool IsSending {
            get {
                return Connection != null && Connection.IsSending;
            }
        }

        /// <summary>
        /// Host name for SSL connections.
        /// <para>Required.</para>
        /// </summary>
        public string SslTargetHost { get; set; }
        Identifier ISocket.RemoteIdentity { get => RemoteIdentity; set => throw new NotImplementedException(); }
        bool ISocket.isDisposed { get => isDisposed; set => isDisposed = value; }
        bool ISocket.Connected { get => Connected; }

        /// <summary>
        /// Measured one-way latency in milliseconds (RTT / 2).
        /// Updated automatically by the built-in ping loop.
        /// </summary>
        public long LatencyMs => Interlocked.Read(ref _LatencyMs);
        public long _LatencyMs;

        private CancellationTokenSource _pingCts;
        private const int PingIntervalMs = 10000;

        #endregion

        #region Ping / Pong

        /// <summary>
        /// Override in subclasses to route the ping datagram through the correct send path.
        /// Default implementation uses TCP-style <see cref="Send(NetworkConnection, object)"/>.
        /// </summary>
        protected virtual void SendPingInternal(object obj) {
            if (Connection != null && Connected)
                Send(Connection, obj);
        }

        /// <summary>
        /// Starts a background loop that sends a <see cref="P2P.Ping"/> every 10 seconds
        /// and registers a <see cref="P2P.Pong"/> callback to measure latency.
        /// Called automatically by client types after a successful connection.
        /// </summary>
        protected void StartPingLoop() {
            StopPingLoop();
            RegisterPongCallback();

            // Send first ping immediately so UDP servers discover this client
            // before any broadcasts are sent.
            if (Connected) {
                try { SendPingInternal(new P2P.Ping { TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }); }
                catch { }
            }

            _pingCts = new CancellationTokenSource();
            var token = _pingCts.Token;
            Task.Run(async () => {
                while (!token.IsCancellationRequested) {
                    try {
                        await Task.Delay(PingIntervalMs, token);
                    } catch (TaskCanceledException) {
                        break;
                    }
                    if (!Connected) continue;
                    try {
                        SendPingInternal(new P2P.Ping { TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
                    } catch { }
                }
            });
        }

        /// <summary>
        /// Stops the background ping loop.
        /// </summary>
        protected void StopPingLoop() {
            var cts = _pingCts;
            if (cts == null) return;
            _pingCts = null;
            try { cts.Cancel(); } catch { }
            cts.Dispose();
        }

        private bool _pongCallbackRegistered;

        private void RegisterPongCallback() {
            if (_pongCallbackRegistered) return;
            _pongCallbackRegistered = true;
            RegisterCallback<P2P.Pong>(e => {
                if (e.Object != null) {
                    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    var rtt = now - e.Object.TimestampMs;
                    if (rtt < 0) rtt = 0;
                    Interlocked.Exchange(ref _LatencyMs, rtt / 2);
                    ApplyLatencyScaling();
                }
            });
        }

        private void ApplyLatencyScaling() {
            if (!Options.Chunking || !Options.ChunkingAutomaticLatencyScaling)
                return;
            var latency = LatencyMs;
            // Minimum is the tick rate (1000/Fps) so flushes never outpace the
            // send loop. Falls back to 100 ms when Fps is 0 (disabled).
            var tickRate = Options.Timeout > 0 ? (int)Math.Ceiling(Options.Timeout) : 100;
            var scaled = (int)Math.Max(tickRate, Math.Min(2000, latency * 2));
            Options.ChunkingIntervalMs = scaled;
        }
        #endregion

        #region Callbacks

        /// <summary>
        /// <para>Registers a type callback using generic types.</para>
        /// <para>Action of type invoked when type is received.</para>
        /// </summary>
        public void RegisterCallback<T>(Action<ReceivedEventArgs<T>> Action) {
            Type Type = typeof(T);
            Options.Whitelist.Add(Type);
            if (TypeCallbacks.TryGetValue(Type, out var list)) {
                list.Add(e => Action((ReceivedEventArgs<T>)e));
            } else {
                var l = new List<Action<IReceivedEventArgs>> {
                    e => Action((ReceivedEventArgs<T>)e)
                };
                TypeCallbacks.Add(Type, l);
            }
        }

        /// <summary>
        /// Removes a type callback.
        /// </summary>
        public void RemoveCallback<T>(Action<ReceivedEventArgs<T>> Action) {
            Type Type = typeof(T);
            if (TypeCallbacks.TryGetValue(Type, out var list)) {
                Action<IReceivedEventArgs> wrappedAction = e => Action((ReceivedEventArgs<T>)e);
                list.Remove(wrappedAction);
                if (list.Count == 0) {
                    Options.Whitelist.Remove(Type);
                    TypeCallbacks.Remove(Type);
                }
            }
        }

        /// <summary>
        /// Removes all type callbacks of T and it's respective white-list entry.
        /// </summary>
        public void RemoveCallback<T>() {
            Type Type = typeof(T);
            Options.Whitelist.Remove(Type);
            TypeCallbacks.Remove(Type);
        }

        #endregion

        #region IDisposable Implementation
        private bool disposedValue;
        protected internal bool isDisposed = false;

        protected virtual void Dispose(bool disposing) {
            if (!disposedValue) {
                isDisposed = true;
                if (disposing) {
                    StopPingLoop();
                    try { OnDisposing?.Invoke(); } catch { }
                    try { Connection?.Close(this, DisconnectionReason.ObjectDisposed); } catch { }
                    try { Socket?.Shutdown(SocketShutdown.Both); } catch { }
                    try { Socket?.Close(); } catch { }
                    try { Stream?.Dispose(); } catch { }
                    Buffers.Clear();
                    P2P_ServerInformation.Clear();
                    P2P_Servers.Clear();
                    P2P_Clients.Clear();
                    TypeCallbacks.Clear();
#if WINDOWS
                    _ControlShareSessions.Clear();
#endif
                    if (Options?.Whitelist != null)
                        Options.Whitelist.Clear();
                }

                Connection = null;
                Socket = null;
                Stream = null;
                Peers = null;
                disposedValue = true;
            }
        }

        public void Dispose() {
            // Do not change this code. Put cleanup code in 'Dispose(disposing As Boolean)' method
            isDisposed = true;
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
        #endregion

        #region ISocket
        void ISocket.SendSegmented(NetworkConnection Client, object Obj) {
            SendSegmented(Client, Obj);
        }

        protected internal void SendSegmented(NetworkConnection Client, object Obj) {
            if (this is UdpClient || this is UdpServer) { Send(Client, Obj); return; }
            Task.Run(() => {
                byte[] SerializedBytes = Options.Serializer.Serialize(new Wrapper(Obj, this));
                Segment[] SegmentedObject = SerializedBytes.GetSegments();
                Parallel.ForEach(SegmentedObject, (s) => {
                    //var state = new SendQueueItem(s, Client);

                    Client.TryEnqueueSendBytes(Options.Serializer.Serialize(s));
                });
            });
        }

        public virtual void SendBroadcast(object Obj) {
            throw new NotImplementedException();
        }

        public virtual void SendBroadcast(object Obj, NetworkConnection Except) {
            throw new NotImplementedException();
        }

        public virtual void SendBroadcast(NetworkConnection[] Clients, object Obj, NetworkConnection Except = null) {
            throw new NotImplementedException();
        }

        public virtual Task<ISocket> StartServer(Identifier identifier, NetworkOptions options, string name) {
            throw new NotImplementedException();
        }

        public virtual Task<ISocket> StartServer(Identifier identifier, NetworkOptions options) {
            throw new NotImplementedException();
        }

        public virtual Task<ISocket> StartServer(Identifier identifier) {
            throw new NotImplementedException();
        }

        void ISocket.InvokeBytesPerSecondUpdate(NetworkConnection connection) {
            InvokeBytesPerSecondUpdate(connection);
        }

        public virtual void InvokeOnDisconnected(ISocket sender, NetworkConnection Connection) {
            throw new NotImplementedException();
        }

        public virtual void InvokeOnConnected(ISocket sender, NetworkConnection Connection) {
            throw new NotImplementedException();
        }

        #endregion

        public NetworkBase() {
            Peers = new PeerList(this);
            NIC.Initialize();
        }
        public NetworkBase(ISerializer Serializer) {
            Peers = new PeerList(this);
            Options.Serializer = Serializer;
            NIC.Initialize();
        }
    }
}