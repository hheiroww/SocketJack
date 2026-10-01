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
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Xml.Linq;
using System.Buffers;

namespace SocketJack.Net.WebSockets {
    public class WebSocketServer : IDisposable, ISocket {

        private static int GetWebSocketHeaderLength(int payloadLength)
            => WebSocketFrameHelper.GetHeaderLength(payloadLength);

        private static int WriteWebSocketHeader(Span<byte> dest, bool useBinaryFrame, int payloadLength)
            => WebSocketFrameHelper.WriteHeader(dest, useBinaryFrame, payloadLength);

        #region Internal
        private readonly int _port;
        private Socket _listenerSocket;
        private CancellationToken _ct;
        private bool _isListening = false;
        public Guid InternalID {
            get {
                return _InternalID;
            }
        }
        private Guid _InternalID = Guid.NewGuid();
        bool ISocket.isDisposed { get => isDisposed; set => isDisposed = value; }
        bool ISocket.Connected { get => IsListening; }

        #endregion

        #region Properties
        public Socket Socket { get { return _Socket; } set { } }
        private Socket _Socket = null;
        public Stream Stream { get { return _Stream; } set { } }
        private Stream _Stream = null;
        public IPEndPoint EndPoint { get { return (IPEndPoint)Socket?.RemoteEndPoint; } set { } }
        public bool isDisposed { get; internal set; } = false;
        public bool PeerToPeerInstance { get; internal set; }
        /// <summary>
        /// Server certificate for SSL connections.
        /// </summary>
        public X509Certificate SslCertificate { get; set; }
        public NetworkConnection Connection { get; private set; }
        public string Name { get; set; } = "WebSocketServer";
        public bool IsListening => _isListening;
        public ConcurrentDictionary<Guid, NetworkConnection> Clients { get; } = new();
        public NetworkOptions Options { get; set; } = NetworkOptions.DefaultOptions.Clone<NetworkOptions>();
        #endregion

        #region Internal
        protected internal bool _PeerToPeerInstance = false;

        private ConcurrentDictionary<Guid, bool> _handshakeCompleted = new();
        private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _clientWriteSemaphores = new();

        private async Task HandleRawSocketClient(NetworkConnection connection) {
            if (connection._Stream == null) {
                connection._Stream = new NetworkStream(connection.Socket, ownsSocket: false);
                _Stream = connection._Stream;
            }
            if (Options.UseSsl) {
                if (SslCertificate == null)
                    throw new InvalidOperationException("SSL is enabled but no certificate is set in SslCertificate property.");
                connection.SslStream = new System.Net.Security.SslStream(connection._Stream, false);
                await connection.SslStream.AuthenticateAsServerAsync(SslCertificate, false, System.Security.Authentication.SslProtocols.None, false);
            }

            var buffer = new byte[Options.DownloadBufferSize];
            int totalRead = 0;
            int bytesRead = 0;
            var requestBuilder = new StringBuilder();
            using var handshakeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var timeoutRegistration = handshakeTimeout.Token.Register(() => { try { connection.Socket.Close(); } catch { } });

            while (true) {
                bytesRead = await connection.Stream.ReadAsync(buffer, 0, buffer.Length);
                if (bytesRead <= 0) break;
                if (requestBuilder.Length + bytesRead > SafeModeHandshake.MaximumBytes * 2) { connection.Close(this); return; }
                requestBuilder.Append(Encoding.UTF8.GetString(buffer, 0, bytesRead));
                totalRead += bytesRead;
                if (requestBuilder.ToString().Contains("\r\n\r\n")) break;
                if (totalRead > 8192) break;
            }
            string request = requestBuilder.ToString();
            if (!request.StartsWith("GET") || !request.Contains("Upgrade: websocket", StringComparison.OrdinalIgnoreCase)) {
                connection.Socket.Close();
                return;
            }
            string secWebSocketKey = null;
            foreach (var line in request.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries)) {
                if (line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase)) {
                    secWebSocketKey = line.Substring(line.IndexOf(":") + 1).Trim();
                    break;
                }
            }
            if (string.IsNullOrEmpty(secWebSocketKey)) {
                connection.Socket.Close();
                return;
            }
            try { SafeModeHandshake.ValidateHttp(request, Options, connection); }
            catch { connection.Close(this, DisconnectionReason.Unknown); return; }
            string acceptKey = ComputeWebSocketAcceptKey(secWebSocketKey);
            string response =
                "HTTP/1.1 101 Switching Protocols\r\n" +
                "Upgrade: websocket\r\n" +
                "Connection: Upgrade\r\n" +
                $"Sec-WebSocket-Accept: {acceptKey}\r\n" +
                "\r\n";
            byte[] responseBytes = Encoding.UTF8.GetBytes(response);
            await connection.Stream.WriteAsync(responseBytes, 0, responseBytes.Length);

            handshakeTimeout.CancelAfter(Timeout.Infinite);
            ClientConnected?.Invoke(new ConnectedEventArgs(this, connection));
            InitializePeer(connection);
            await HandleWebSocketFrames(connection);
        }

        private static string ComputeWebSocketAcceptKey(string secWebSocketKey) {
            string concat = secWebSocketKey + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
            using (SHA1 sha1 = SHA1.Create()) {
                byte[] hash = sha1.ComputeHash(Encoding.UTF8.GetBytes(concat));
                return Convert.ToBase64String(hash);
            }
        }

        private async Task<int> ReadByteAsync(Stream stream, CancellationToken token = default) {
            var buffer = new byte[1];
            int read = await stream.ReadAsync(buffer, 0, 1, token);
            return read == 1 ? buffer[0] : -1;
        }

        private static async ValueTask<bool> ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken token = default) {
            int readTotal = 0;
            while (readTotal < buffer.Length) {
                var read = await stream.ReadAsync(buffer.Slice(readTotal), token).ConfigureAwait(false);
                if (read <= 0)
                    return false;
                readTotal += read;
            }
            return true;
        }

        // Add constants for opcodes
        private const int TextFrameOpcode = 0x1;
        private const int BinaryFrameOpcode = 0x2;
        private const int CloseFrameOpcode = 0x8;
        private const int BrowserClientOpcode = 0xB; // 0xB is unused in RFC6455

        private async Task HandleWebSocketFrames(NetworkConnection connection) {
            try {
                bool exitLoop = false;
                while (connection.Socket.Connected && (!connection.Closed || connection.Closing)) {

                    int header = await ReadByteAsync(connection.Stream).ConfigureAwait(false);
                    if (header == -1) break;
                    bool fin = (header & 0x80) != 0;
                    int opcode = header & 0x0F;
                    int lengthByte = await ReadByteAsync(connection.Stream).ConfigureAwait(false);
                    if (lengthByte == -1) break;
                    bool mask = (lengthByte & 0x80) != 0;
                    int payloadLen = lengthByte & 0x7F;
                    if (payloadLen == 126) {
                        var ext = new byte[2];
                        if (!await ReadExactAsync(connection.Stream, ext, default).ConfigureAwait(false)) break;
                        payloadLen = (ext[0] << 8) | ext[1];
                    } else if (payloadLen == 127) {
                        var ext = new byte[8];
                        if (!await ReadExactAsync(connection.Stream, ext, default).ConfigureAwait(false)) break;
                        ulong advertised = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(ext);
                        if (advertised > (ulong)Options.MaximumBufferSize) throw new System.IO.InvalidDataException("WebSocket frame exceeds MaximumBufferSize.");
                        payloadLen = checked((int)advertised);
                    }

                    if (payloadLen > Options.MaximumBufferSize) throw new System.IO.InvalidDataException("WebSocket frame exceeds MaximumBufferSize.");
                    var maskKey = new byte[4];
                    if (mask) {
                        if (!await ReadExactAsync(connection.Stream, maskKey, default).ConfigureAwait(false)) break;
                    }

                    var payload = ArrayPool<byte>.Shared.Rent(payloadLen);
                    try {
                        if (!await ReadExactAsync(connection.Stream, payload.AsMemory(0, payloadLen), default).ConfigureAwait(false))
                            break;

                        if (mask) {
                            for (int i = 0; i < payloadLen; i++)
                                payload[i] ^= maskKey[i % 4];
                        }

                    switch (opcode) {
                        case CloseFrameOpcode: // Close
                            exitLoop = true;
                            break;
                        case BrowserClientOpcode:
                            SendConstructors(ref connection);
                            continue;
                        case TextFrameOpcode:
                        case BinaryFrameOpcode:
                            _handshakeCompleted[connection.ID] = true;

                            var payloadCopy = payload.AsSpan(0, payloadLen).ToArray();
                            byte[] data = payloadCopy;
                            if (Options.UseCompression && opcode == BinaryFrameOpcode) {
                                data = UdpReliableObjects.Decompress(payloadCopy, Options);
                            }
                            var serializer = Options.Serializer;

                            object message = InboundMessageDecoder.Read(this, connection, data);
                            if (message != null) HandleReceive(connection, message, message.GetType(), payloadLen);
                            break;
                        default:
                            // Unknown or unsupported opcode, ignore
                            break;
                    }
                    if(exitLoop) {
                        break;
                    }
                    }
                    finally {
                        ArrayPool<byte>.Shared.Return(payload);
                    }
                }
            } catch (Exception ex) {
                if (Options.SafeMode) connection.Close(this, DisconnectionReason.Unknown);
                if (connection.Socket != null && Options.Logging && connection.Socket.Connected) {
                    InvokeOnError(ex);
                    InvokeOnError(connection, ex);
                }
            }
        }


private NetworkConnection NewConnection(ref Socket handler) {
    var newConnection = new NetworkConnection(this, handler);
            newConnection.StartConnectionTester();
            newConnection.ID = Guid.NewGuid();
            _handshakeCompleted[newConnection.ID] = false;
            newConnection._Identity = Identifier.Create(Guid.NewGuid(), true, handler.RemoteEndPoint.ToString());
            Clients.AddOrUpdate(newConnection.ID, newConnection);
            return newConnection;
        }

        private void SendConstructors(ref NetworkConnection connection) {
            var script = new JSContructors(WebSocketFrameHelper.GenerateJSConstructors(Options.Whitelist));
            if (connection != null) {
                Send(connection, script);
            }
        }

        public class JSContructors {
            public string Script { get; set; }
            public JSContructors(string script) {
                Script = script;
            }
        }

        internal void SafeInvoke(Action action) {
#if UNITY
            MainThread.Run(() => {
                action.Invoke();
            });
#endif
#if WINDOWS
            Application.Current.Dispatcher.InvokeAsync(() => {
                action.Invoke();
            });
#endif
#if !UNITY && !WINDOWS
            action.Invoke();
#endif
        }

        internal async Task<IAsyncResult> SafeInvokeAsync(Action action) {
#if UNITY
            return Task.Run(() => { MainThread.Run(action); });
#endif
#if WINDOWS
            return Application.Current.Dispatcher.InvokeAsync(action).Task;
#endif
#if !UNITY && !WINDOWS
           return action.BeginInvoke(null, null);
#endif
        }

        #endregion

        #region Events

        public event OnStoppedListeningEventHandler OnStoppedListening;
        public delegate void OnStoppedListeningEventHandler();

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

        public event ClientIdentifiedEventHandler ClientIdentified;
        public delegate void ClientIdentifiedEventHandler(ConnectedEventArgs e);

        public event ClientConnectedEventHandler ClientConnected;
        public delegate void ClientConnectedEventHandler(ConnectedEventArgs e);

        public event OnClientDisconnectedEventHandler ClientDisconnected;
        public delegate void OnClientDisconnectedEventHandler(DisconnectedEventArgs e);

        /// <summary>
        /// Fired when a user connects to the server.
        /// /// </summary>
        /// <param name="sender"></param>
        /// <param name="Peer"></param>
        public event PeerConnectedEventHandler PeerConnected;
        public delegate void PeerConnectedEventHandler(ISocket sender, Identifier Peer);

        /// <summary>
        /// Fired when a user disconnects from the server.
        /// /// </summary>
        /// <param name="sender"></param>
        /// <param name="Peer"></param>
        public event PeerDisconnectedEventHandler PeerDisconnected;
        public delegate void PeerDisconnectedEventHandler(ISocket sender, Identifier Peer);

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
        public void InvokePeerConnectionRequest(ISocket sender, ref PeerServer Server) {
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
        protected internal void InvokeOnDisconnected(DisconnectedEventArgs e) {
            if (e.Connection == null) return;
            if (e.Connection.Identity == null) return;
            if (e.Connection.Closed) return;
            Identifier dcPeer = Identifier.Create(e.Connection.Identity.ID, false);
            dcPeer.Action = PeerAction.Dispose;
            SendBroadcast(dcPeer, e.Connection);
            if (Peers.ContainsKey(e.Connection.Identity.ID)) {
                Peers.Remove(e.Connection.Identity.ID);
            }
            if(Clients.ContainsKey(e.Connection.ID)) {
                Clients.Remove(e.Connection.ID);
            }
            _clientWriteSemaphores.TryRemove(e.Connection.ID, out _);
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
        public void CloseConnection(NetworkConnection Connection, DisconnectionReason Reason) {
            try {
                if (Connection.Socket != null && Connection.Socket.Connected) {
                    Connection._Closing = true;
                    // Send close frame
                    byte[] closeFrame = new byte[] { 0x88, 0x00 };
                    Connection.Stream.Write(closeFrame, 0, closeFrame.Length);
                    Connection.Socket.Shutdown(SocketShutdown.Both);
                    Connection.Socket.Close();
                }
                Connection.Stream?.Dispose();
                InvokeOnDisconnected(new DisconnectedEventArgs(this, Connection, Reason));
                Connection.CloseConnection();
            } catch (Exception ex) {
                InvokeOnError(ex);
            }
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

        public virtual void InvokeOnDisconnected(ISocket sender, NetworkConnection Connection) {
            throw new NotImplementedException();
        }

        public virtual void InvokeOnConnected(ISocket sender, NetworkConnection Connection) {
            throw new NotImplementedException();
        }


        #endregion

        #region P2P
        public PeerList Peers { get; internal set; }
        PeerList ISocket.Peers { get => Peers; set => Peers = value; }
        ConcurrentDictionary<string, PeerServer> ISocket.P2P_ServerInformation { get => P2P_ServerInformation; set => P2P_ServerInformation = value; }
        ConcurrentDictionary<string, NetworkConnection> ISocket.P2P_Servers { get => P2P_Servers; set => P2P_Servers = value; }
        ConcurrentDictionary<string, NetworkConnection> ISocket.P2P_Clients { get => P2P_Clients; set => P2P_Clients = value; }
        Identifier ISocket.RemoteIdentity { get => null; set => throw new NotImplementedException(); }

        protected internal ConcurrentDictionary<string, PeerServer> P2P_ServerInformation = new ConcurrentDictionary<string, PeerServer>();
        protected internal ConcurrentDictionary<string, NetworkConnection> P2P_Servers = new ConcurrentDictionary<string, NetworkConnection>();
        protected internal ConcurrentDictionary<string, NetworkConnection> P2P_Clients = new ConcurrentDictionary<string, NetworkConnection>();

        private void InitializePeer(NetworkConnection newConnection) {
            if (Options.UsePeerToPeer) {
                var peer = newConnection._Identity;
                if (!Peers.ContainsKey(newConnection.Identity.ID)) {
                    Peers.AddOrUpdate(peer);
                } else {
                    peer = Peers[newConnection.Identity.ID];
                    newConnection._Identity = peer;
                }
                PeerConnected?.Invoke(this, peer);
                var peerArray = Peers.ToArrayWithLocal(newConnection);
                Send(newConnection, peerArray);
                SendBroadcast(peer.RemoteReady(this), newConnection);
                ClientIdentified?.Invoke(new ConnectedEventArgs(this, newConnection));
            }
        }

        Task<ISocket> ISocket.StartServer(Identifier identifier, NetworkOptions options, string name) {
            throw new NotImplementedException();
        }

        #endregion

        #region Callbacks
        private Dictionary<Type, List<Action<IReceivedEventArgs>>> TypeCallbacks = new();

        public void RegisterCallback<T>(Action<ReceivedEventArgs<T>> action) {
            Type type = typeof(T);
            Options.Whitelist.Add(type);
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

        #region Logging

        private void WireUpConnectionEvents() {
            ClientConnected += (e) => {
                if (Options.Logging) {
                    LogAsync($"[{Name}] Client Connected    -> {e.Connection.Identity.ID.ToUpper()} ({e.Connection.Socket.RemoteEndPoint})");
                }
            };
            ClientDisconnected += (e) => {
                if (Options.Logging) {
                    LogAsync($"[{Name}] Client Disconnected -> {e.Connection.Identity.ID.ToUpper()} ({e.Connection.EndPoint.ToString()})");
                }
            };
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
                });
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

        #region Sending

        public void Send(NetworkConnection Client, object Obj) {
            SendAsync(Client, Obj);
        }

        public async Task SendAsync(NetworkConnection Client, object Object) {
            if (Client == null || Client.Closed || Client.Socket == null || !Client.Socket.Connected) return;
            var Obj = Object;
#if UNITY
            MainThread.Run(() => {
		        Obj = Object;
            });
#endif
#if WINDOWS
            Application.Current.Dispatcher.InvokeAsync(() => {
                Obj = Object;
            });
#endif
#if !UNITY && !WINDOWS
            Obj = Object;
#endif
            if (Client.Stream == null) Client._Stream = new NetworkStream(Client.Socket, ownsSocket: false);
            try {
                if (Obj == null) return;
                Type objType = Obj.GetType();
                if (Options.Blacklist.Contains(objType)) {
                    if (Options.Logging) {
                        InvokeOnError(Client, new Exception($"Attempted to send blacklisted type {objType.Name}"));
                    }
                    return;
                }

                byte[] payload = objType == typeof(PeerRedirect) ? Options.Serializer.Serialize(Obj) : Options.Serializer.Serialize(new Wrapper(Obj, this));

                if (payload.Length > 8192) {
                    await SendSegmentedAsync(Client, payload, objType, Obj);
                } else {
                    Client.IsSending = true;
                    bool useBinaryFrame = Options.UseCompression;
                    if (useBinaryFrame) {
                        payload = Options.CompressionAlgorithm.Compress(payload);
                    }
                    if (Client.Stream == null) return;

                    var writeSemaphore = _clientWriteSemaphores.GetOrAdd(Client.ID, _ => new SemaphoreSlim(1, 1));
                    await writeSemaphore.WaitAsync();
                    try {
                        var headerLen = GetWebSocketHeaderLength(payload.Length);
                        var frameLen = headerLen + payload.Length;
                        var rented = ArrayPool<byte>.Shared.Rent(frameLen);
                        try {
                            // Avoid Span locals in async methods for netstandard2.1 compatibility.
                            var headerBuf = new byte[10];
                            var headerWritten = WriteWebSocketHeader(headerBuf, useBinaryFrame, payload.Length);
                            Buffer.BlockCopy(headerBuf, 0, rented, 0, headerWritten);
                            Buffer.BlockCopy(payload, 0, rented, headerWritten, payload.Length);

                            await Client.Stream.WriteAsync(rented, 0, frameLen);
                            await Client.Stream.FlushAsync();
                        }
                        finally {
                            ArrayPool<byte>.Shared.Return(rented);
                        }
                    }
                    finally {
                        writeSemaphore.Release();
                    }

                    InvokeInternalSendEvent(Client, objType, Obj, payload.Length);
                    InvokeOnSent(new SentEventArgs(this, Client, objType, payload.Length));

                    if (Options.Logging && Options.LogSendEvents && !Globals.IgnoreLoggedTypes.Contains(objType)) {
                        if (ReferenceEquals(objType, typeof(PeerRedirect))) {
                            LogFormatAsync("[{0}] Sent {1} - {2}", new[] { Name, string.Format("PeerRedirect<{0}>", ((PeerRedirect)Obj).CleanTypeName), payload.Length.ByteToString() });
                        } else {
                            LogFormatAsync("[{0}] Sent {1} - {2}", new[] { Name, objType.Name, payload.Length.ByteToString() });
                        }
                    }

                    Client.IsSending = false;
                }
            } catch (Exception ex) {
                Client.IsSending = false;
                InvokeOnError(Client, ex);
            }
        }


        public void Send(Identifier recipient, object Obj) {
            SendAsync(recipient, Obj);
        }

        public async Task SendAsync(Identifier recipient, object Obj) {
            if (recipient == null) return;
            var client = Clients.Values.FirstOrDefault(c => c.Identity.ID == recipient.ID);
            var redirect = new PeerRedirect(client?.ID.ToString(), recipient.ID, Obj);
            await SendAsync(client, redirect);
        }

        public void SendSegmented(NetworkConnection Client, object Obj) {
            SendSegmentedAsync(Client, Obj);
        }

        public async Task SendSegmentedAsync(NetworkConnection Client, object Obj) {
            byte[] SerializedBytes = Options.Serializer.Serialize(new Wrapper(Obj, this));
            Segment[] SegmentedObject = SerializedBytes.GetSegments();
            foreach (var s in SegmentedObject) {
                await SendAsync(Client, s);
            }
        }

        public void SendSegmented(NetworkConnection Client, byte[] SerializedBytes, Type objType, object obj) {
            SendSegmentedAsync(Client, SerializedBytes, objType, obj);
        }

        public async Task SendSegmentedAsync(NetworkConnection Client, byte[] SerializedBytes, Type objType, object obj) {
            Segment[] SegmentedObject = SerializedBytes.GetSegments();
            foreach (var s in SegmentedObject) {
                await SendAsync(Client, s);
            }
            if (objType == typeof(PeerRedirect)) {
                PeerRedirect redirect = (PeerRedirect)obj;
                LogFormatAsync("[{0}] Sent {1} - {2}", new[] { Name, string.Format("PeerRedirect<{0}>", (redirect.CleanTypeName)), SerializedBytes.Length.ByteToString() });
            } else {
                LogFormatAsync("[{0}] Sent {1} - {2}", new[] { Name, objType.Name, SerializedBytes.Length.ByteToString() });

            }
           
        }

        public void SendBroadcast(object Obj) {
            NetworkConnection[] clients = null;
            lock (Clients.Values) { clients = Clients.Values.ToArray(); }

            foreach (var client in clients) {
                if (client != null && !client.Closed && client.Socket.Connected) {
                    SafeInvokeAsync(() => { Send(client, Obj); });
                    
                }
            }
            //Parallel.ForEach(Clients.Values, (client) => {
            //    if (client != null && !client.Closed && client.Socket.Connected) {
            //        Send(client, Obj);
            //    }
            //});

        }

        public void SendBroadcast(NetworkConnection[] Clients, object Obj, NetworkConnection Except) {
            Parallel.ForEach(Clients, (client) => {
                if (client != null && client.Socket != null && client.Socket.Connected && client != Except) {
                    Send(client, Obj);
                }
            });

        }

        public void SendBroadcast(object Obj, NetworkConnection Except) {
            lock (Clients.Values) {
                Parallel.ForEach(Clients.Values, (client) => {
                    if (client != null && client.Socket != null && client.Socket.Connected && client != Except) {
                        Send(client, Obj);
                    }
                });
            }
        }
        #endregion

        #region Receiving
        public void HandleReceive(NetworkConnection connection, object obj, Type objType, int Length) {
            InboundMessageSecurity.BeforeDispatch(this, connection, obj, objType);
            if (objType == typeof(PeerServer)) {
                var pServer = (PeerServer)obj;
                if (Options.UsePeerToPeer) {
                    if (pServer.Shutdown) {
                        PeerServerShutdown?.Invoke(this, pServer);
                    } else {
                        PeerConnectionRequest?.Invoke(this, pServer);
                    }
                }
            } else if(objType == typeof(Segment)) {
                object assembled = InboundMessageDecoder.ReadSegment(this, connection, (Segment)obj);
                if (assembled != null) HandleReceive(connection, assembled, assembled.GetType(), Length);
            } else if(objType == typeof(PeerRedirect)) {
                var redirect = (PeerRedirect)obj;
                redirect.Sender = connection.Identity.ID.ToString();
                if (redirect.Recipient == "#ALL#") {
                    Type redirectType = Options.Whitelist.Resolve(redirect.Type) ?? throw InboundMessageSecurity.Denied();
                    var genericType = typeof(ReceivedEventArgs<>).MakeGenericType(redirectType);
                    var receivedEventArgs = (IReceivedEventArgs)Activator.CreateInstance(genericType);
                    var from = Peers[redirect.Sender];
                    receivedEventArgs.From = from;
                    receivedEventArgs.Initialize(this, connection, obj, Length);
                    IReceivedEventArgs NonGenericEventArgs = new ReceivedEventArgs<object>(this, Connection, obj, Length);
                    NonGenericEventArgs.From = from;
                    InvokeOnReceive(ref NonGenericEventArgs);
                    InvokeAllCallbacks(receivedEventArgs);

                    if (receivedEventArgs.IsPeerRedirect && !NonGenericEventArgs.CancelPeerRedirect && !receivedEventArgs.CancelPeerRedirect) {
                        SendBroadcast(redirect, connection);
                    }
                } else {
                    var recipientConnection = Clients.Where((p) => p.Value.Identity.ID == redirect.Recipient && p.Value.Identity.ID != redirect.Sender)?.FirstOrDefault().Value;
                    if(recipientConnection != null) {
                        Type redirectType = Options.Whitelist.Resolve(redirect.Type) ?? throw InboundMessageSecurity.Denied();
                        var genericType = typeof(ReceivedEventArgs<>).MakeGenericType(redirectType);
                        var receivedEventArgs = (IReceivedEventArgs)Activator.CreateInstance(genericType);
                        var from = Peers[redirect.Sender];
                        receivedEventArgs.From = from;
                        receivedEventArgs.Initialize(this, connection, obj, Length);
                        IReceivedEventArgs NonGenericEventArgs = new ReceivedEventArgs<object>(this, Connection, obj, Length);
                        NonGenericEventArgs.From = from;
                        InvokeOnReceive(ref NonGenericEventArgs);
                        InvokeAllCallbacks(receivedEventArgs);

                        if (receivedEventArgs.IsPeerRedirect && !NonGenericEventArgs.CancelPeerRedirect && !receivedEventArgs.CancelPeerRedirect) {
                            Send(recipientConnection, redirect);
                        }
                    }
                }
            } else {
                var genericType = typeof(ReceivedEventArgs<>).MakeGenericType(objType);
                var receivedEventArgs = (IReceivedEventArgs)Activator.CreateInstance(genericType);
                receivedEventArgs.Initialize(this, connection, obj, Length);
                IReceivedEventArgs NonGenericEventArgs = new ReceivedEventArgs<object>(this, Connection, obj, Length);
                InvokeOnReceive(ref NonGenericEventArgs);
                InvokeAllCallbacks(receivedEventArgs);
            }
        }
        /// <summary>
        /// Stop the client from updating metadata keys that are restricted, such as `Username`.
        /// <para>Not case sensitive.</para>
        /// </summary>
        public List<string> RestrictedMetadataKeys = new List<string>();
        private void OnReceived_MetadataKeyValue(ReceivedEventArgs<MetadataKeyValue> e) {
            if (string.IsNullOrEmpty(e.Object.Key)) return;
            if (!RestrictedMetadataKeys.Contains(e.Object.Key.ToLower())) {
                Clients[e.Connection.ID].SetMetaData(e.Object.Key, e.Object.Value);
                Peers[e.Connection.Identity.ID].SetMetaData(this, e.Object.Key, e.Object.Value);
            }
        }
        #endregion

        public WebSocketServer(int port, string name) {
            Peers = new PeerList(this);
            Name = name;
            _port = port;
            var argServer = (ISocket)this;
            Globals.RegisterServer(ref argServer);
            RegisterCallback<MetadataKeyValue>(OnReceived_MetadataKeyValue);
            WireUpConnectionEvents();
        }

        public WebSocketServer(int port) {
            Peers = new PeerList(this);
            _port = port;
            var argServer = (ISocket)this;
            Globals.RegisterServer(ref argServer);
            RegisterCallback<MetadataKeyValue>(OnReceived_MetadataKeyValue);
            WireUpConnectionEvents();
        }

        private bool AcceptNextConnection = true;
        public bool Listen() {
            _ct = new CancellationToken(IsListening);
            _listenerSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            _Socket = _listenerSocket;
            var connection = new NetworkConnection(this, _listenerSocket);
            Connection = connection;
            _listenerSocket.Bind(new IPEndPoint(IPAddress.Any, _port));
            _listenerSocket.Listen(Options.Backlog > 0 ? Options.Backlog : 100);
            _isListening = true;

            if (Options.Logging) {
                LogAsync($"{Name} started listening on port {_port}.");
            }
            Task.Run(() => Listener());
            return _isListening;
        }

        private void Listener() {
            while (!_ct.IsCancellationRequested || !IsListening) {
                if (AcceptNextConnection && _listenerSocket != null) {
                    AcceptNextConnection = false;
                    _listenerSocket.BeginAccept(async (ar) => {
                        AcceptNextConnection = true;
                        if (_listenerSocket == null) return;
                        try {
                            Socket handler = _listenerSocket.EndAccept(ar);
                            var newConnection = NewConnection(ref handler);
                            await HandleRawSocketClient(newConnection);
                        } catch (Exception ex) {
                            InvokeOnError(ex);
                        }
                    }, null);
                } else {
                    Thread.Sleep(5);
                }
            }
        }

        public void StopListening() {
            if (_isListening) {
                //var argServer = (ISocket)this;
                //argServer.CloseConnection(argServer.Connection, DisconnectionReason.LocalSocketClosed);
                lock (Clients.Values) {
                    foreach (var client in Clients.Values) {
                        if(!client.Closed) {
                            client.Close(this, DisconnectionReason.LocalSocketClosed);
                            MethodExtensions.TryInvoke(client.Socket.Close);
                        }
                    }
                }
                Peers.Clear();
                Clients.Clear();
                Connection.Close(this, DisconnectionReason.LocalSocketClosed);
                _listenerSocket?.Close();
                _listenerSocket.Dispose();
                _listenerSocket = null;
                _handshakeCompleted.Clear();
                _isListening = false;
                LogAsync(new[] { $"[{Name}] stopped listening." });
                OnStoppedListening?.Invoke();
            }
        }

        public void Dispose() {
            if(IsListening) {
                InvokeOnDisposing();
                var argServer = (ISocket)this;
                Globals.UnregisterServer(ref argServer);
                StopListening();
                isDisposed = true;
            }
        }
    }
}
