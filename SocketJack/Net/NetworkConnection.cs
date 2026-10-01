using SocketJack.Extensions;
using SocketJack.Net.P2P;
using SocketJack.Serialization;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Xml.Linq;

namespace SocketJack.Net {
    public partial class NetworkConnection : IDisposable {

        #region Properties

        /// <summary>
        /// The socket associated to the connection.
        /// </summary>
        public Socket Socket {
            get {
                return _Socket;
            }
        }
        private Socket _Socket = null;

        /// <summary>
        /// The stream associated to the connection.
        /// </summary>
        public Stream Stream {
            get {
                return SSL ? (Stream)SslStream : _Stream;
            }
        }
        public NetworkStream _Stream = null;
        public SslStream SslStream { get; set; }

        public static readonly byte[] Terminator = new[] { (byte)192, (byte)128 };
        private static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);
        private static readonly Type ByteArrayType = typeof(byte[]);
        internal InboundSegmentBuffer InboundSegments { get; } = new InboundSegmentBuffer();
        internal bool SafeModeVerified;
        internal string[] SafeModeAllowedTypes;
        private static readonly char[] LengthTrimChars = { '\0', ' ', '\r', '\n' };
        internal SocketJackPatternCache PatternCache { get; } = new SocketJackPatternCache();

        /// <summary>
        /// Whether or not data is compressed.
        /// </summary>
        public bool Compressed {
            get {
                return Parent.Options.UseCompression;
            }
        }

        /// <summary>
        /// Whether or not data is encrypted.
        /// </summary>
        public bool SSL {
            get {
                return Parent.Options.UseSsl;
            }
        }

        /// <summary>
        /// The remote endpoint of the connection.
        /// </summary>
        public IPEndPoint EndPoint { get; set; }

        /// <summary>
        /// Unique identifier for this connection.
        /// </summary>
        public Guid ID { get; set; }

        /// <summary>
        /// Parent of this connection.
        /// </summary>
        public ISocket Parent {
            get {
                return _Parent;
            }
        }
        private ISocket _Parent;

        public bool IsWebSocket {
            get {
                return _isWebSocket;
            }
        }

        /// <summary>
        /// The wire protocol detected for this connection.
        /// Set by <see cref="MutableTcpServer"/> when the first data arrives
        /// and the protocol is identified.
        /// </summary>
        public TcpProtocol Protocol {
            get {
                return _Protocol;
            }
        }
        protected internal TcpProtocol _Protocol = TcpProtocol.Unknown;

        /// <summary>
        /// When set to <see langword="true"/>, the connection-test poll loop
        /// treats every poll as successful.  This prevents false disconnection
        /// when an <see cref="System.Net.Security.SslStream"/> (or similar wrapper)
        /// has consumed all bytes from the raw socket into its internal buffer,
        /// causing <c>Socket.Available == 0</c> even though the connection is alive.
        /// </summary>
        public volatile bool SuppressConnectionTest;

        /// <summary>
        /// When set to <see langword="true"/>, all SocketJack send/receive/poll
        /// processing is disabled for this connection.  The protocol handler
        /// (e.g. TDS) takes full ownership of the <see cref="_Stream"/> and
        /// performs its own I/O.  Byte-count metrics are still updated via
        /// <see cref="TrackBytesSent"/> and <see cref="TrackBytesReceived"/>.
        /// </summary>
        public volatile bool RawTcpMode;

        /// <summary>
        /// True if connection sending or receiving.
        /// </summary>
        public bool Active {
            get {
                return IsReceiving || IsSending;
            }
        }

        /// <summary>
        /// Bytes per second sent on this connection (rolling average over the last 10 seconds).
        /// </summary>
        public int BytesPerSecondSent {
            get {
                return GetRollingBpsAverage(isSent: true);
            }
        }
        protected internal int _SentBytesPerSecond = 0;
        protected internal int SentBytesCounter = 0;

        /// <summary>
        /// Bytes per second received on this connection (rolling average over the last 10 seconds).
        /// </summary>
        public int BytesPerSecondReceived {
            get {
                return GetRollingBpsAverage(isSent: false);
            }
        }
        protected internal int _ReceivedBytesPerSecond = 0;
        protected internal int ReceivedBytesCounter = 0;

        private const int BpsRollingWindowSeconds = 10;
        private readonly object _bpsSamplesLock = new object();
        private readonly Queue<(DateTime time, int sent, int recv)> _bpsSamples = new Queue<(DateTime, int, int)>();

        protected internal void RecordBpsSample() {
            lock (_bpsSamplesLock) {
                _bpsSamples.Enqueue((DateTime.UtcNow, _SentBytesPerSecond, _ReceivedBytesPerSecond));
                PruneBpsSamples();
            }
        }

        private void PruneBpsSamples() {
            var cutoff = DateTime.UtcNow.AddSeconds(-BpsRollingWindowSeconds);
            while (_bpsSamples.Count > 0 && _bpsSamples.Peek().time < cutoff)
                _bpsSamples.Dequeue();
        }

        private int GetRollingBpsAverage(bool isSent) {
            lock (_bpsSamplesLock) {
                PruneBpsSamples();
                if (_bpsSamples.Count == 0)
                    return 0;
                long sum = 0;
                foreach (var s in _bpsSamples)
                    sum += isSent ? s.sent : s.recv;
                return (int)(sum / _bpsSamples.Count);
            }
        }

        public long TotalBytesSent {
            get {
                return _TotalBytesSent;
            }
        }
        protected internal long _TotalBytesSent = 0;
        protected internal long _TotalBytesSent_l = 0;

        public long TotalBytesReceived {
            get {
                return _TotalBytesReceived;
            }
        }
        protected internal long _TotalBytesReceived = 0;
        protected internal long _TotalBytesReceived_l = 0;
        DateTime LastSendFrame = DateTime.UtcNow;
        DateTime LastReceiveFrame = DateTime.UtcNow;

        /// <summary>
        /// Remote Peer identifier for peer to peer interactions used to determine the Server's Client GUID.
        /// </summary>
        /// <returns>NULL if accessed before the Server identifies the Client.
        /// To avoid problems please do not acccess this via OnConnected Event.</returns>
        public Identifier Identity {
            get {
                return _Identity;
            }
        }
        public Identifier _Identity;

        /// <summary>
        /// True if the connection is receiving data.
        /// </summary>
        public bool IsReceiving {
            get {
                return _IsReceiving == 1;
            }
            set {
                Interlocked.Exchange(ref _IsReceiving, value ? 1 : 0);
            }
        }
        private int _IsReceiving = 0;

        /// <summary>
        /// True if the connection is sending data.
        /// </summary>
        public bool IsSending {
            get {
                return _IsSending == 1;
            }
            set {
                Interlocked.Exchange(ref _IsSending, value ? 1 : 0);
            }
        }
        private int _IsSending = 0;

        /// <summary>
        /// Buffer used to receive data.
        /// </summary>
        public List<byte> DownloadBuffer {
            get {
                return _DownloadBuffer;
            }
        }
        protected internal List<byte> _DownloadBuffer = new();

        /// <summary>
        /// Buffer used to send data.
        /// </summary>
        public List<byte> UploadBuffer {
            get {
                return _UploadBuffer;
            }
        }
        protected internal List<byte> _UploadBuffer = new();

        /// <summary>
        /// True if the connection is closed.
        /// </summary>
        public bool Closed {
            get {
                return _Closed;
            }
            set {
                _Closed = value;
                if (Closed) {
                    Interlocked.Exchange(ref _TotalBytesReceived, 0);
                    Interlocked.Exchange(ref _TotalBytesSent, 0);
                }
            }
        }

        /// <summary>
        /// True if the connection is closing.
        /// </summary>
        public bool Closing {
            get {
                return _Closing;
            }
        }
        #endregion

        #region Internal
        private volatile bool _Closed = false;
        public volatile bool _Closing = false;
        private readonly object _closeLock = new();
        private readonly string _parentTypeName;
        private readonly bool _isWebSocket;
        private readonly byte[] _lengthHeaderBuffer = new byte[15];
        private volatile bool _connectionCounted = false;
        private static int _activeConnectionCount = 0;
        protected internal SemaphoreSlim _SendSignal = new(0);
        protected internal long _nextChunkFlushAt = 0;

        /// <summary>
        /// Updates sent-byte metrics without going through the SocketJack send pipeline.
        /// Used by protocol handlers running in <see cref="RawTcpMode"/>.
        /// </summary>
        public void TrackBytesSent(int count) {
            Interlocked.Add(ref _TotalBytesSent, count);
            Parent?.InvokeInternalSentByteCounter(this, count);
        }

        /// <summary>
        /// Updates received-byte metrics without going through the SocketJack receive pipeline.
        /// Used by protocol handlers running in <see cref="RawTcpMode"/>.
        /// </summary>
        public void TrackBytesReceived(int count) {
            Interlocked.Add(ref _TotalBytesReceived, count);
            Parent?.InvokeInternalReceivedByteCounter(this, count);
        }

        protected internal ConcurrentQueue<SendQueueItem> SendQueue = new();
        protected internal List<byte> SendQueueRaw = new();

        protected internal bool TryEnqueueSendBytes(byte[] bytes) {
            if (bytes == null || bytes.Length == 0 || Closed || Closing)
                return false;

            NetworkOptions options = Parent?.Options;
            int maxQueuedBytes = options?.MaximumSendQueueBytes ?? 0;
            lock (SendQueueRaw) {
                if (maxQueuedBytes > 0 && SendQueueRaw.Count + bytes.Length > maxQueuedBytes) {
                    Parent?.InvokeOnError(this, new IOException("Send queue exceeded NetworkOptions.MaximumSendQueueBytes."));
                    Parent?.CloseConnection(this, DisconnectionReason.LocalSocketClosed);
                    return false;
                }
                SendQueueRaw.AddRange(bytes);
            }

            try { _SendSignal.Release(); } catch (ObjectDisposedException) { }
            return true;
        }
        /// <summary>
        /// Adjusts <see cref="ThreadPool"/> minimum threads so worker/IO capacity
        /// scales with the number of active connections.  Without this, the default
        /// pool ramp-up (~1 thread per 500 ms) causes server-side deserialization
        /// and callback dispatch to fall behind under heavy bot load.
        /// </summary>
        private static void AdjustThreadPool(int connectionDelta) {
            int connections = Interlocked.Add(ref _activeConnectionCount, connectionDelta);
            if (connections < 0) {
                Interlocked.Exchange(ref _activeConnectionCount, 0);
                connections = 0;
            }
            int processorCount = Environment.ProcessorCount;
            int required = processorCount + (connections * 2);
            ThreadPool.GetMinThreads(out int currentWorker, out int currentIo);
            if (currentWorker < required || currentIo < required) {
                ThreadPool.SetMinThreads(
                    Math.Max(currentWorker, required),
                    Math.Max(currentIo, required));
            }
        }

        /// <summary>
        /// <see langword="True"/> if created by a TcpServer.
        /// </summary>
        protected internal bool IsServer = false;

        /// <summary>
        /// When set, this connection uses the raw-byte receive path regardless
        /// of <see cref="NetworkOptions.UseTerminatedStreams"/>. This is
        /// activated automatically when the first bytes on the connection look
        /// like an HTTP request rather than a SocketJack length header.
        /// </summary>
        protected internal volatile bool _forceRawMode = false;

        /// <summary>
        /// Sends the remote client their remote Identity and IP.
        /// </summary>
        internal void SendLocalIdentity() {
            Send(Identifier.Create(ID, true, EndPoint.Address.ToString()));
        }

        /// <summary>
        /// Polls a socket to see if it is connected.
        /// </summary>
        /// <param name="socket"></param>
        /// <returns><see langword="True"/> if poll successful.</returns>
        public bool Poll() {
            try {
                if (Socket == null) {
                    return false;
                } else if (!Socket.Connected || Closed || Closing) {
                    return false;
                } else if (Socket.Poll(10000, SelectMode.SelectRead) && Socket.Available == 0) {
                    return false;
                } else {
                    return true;
                }
            } catch (SocketException) {
                return false;
            } catch (ObjectDisposedException) {
                return false;
            }
        }

        public void CloseConnection() {
            Close(Parent);
        }

        public void Close(ISocket sender, DisconnectionReason Reason = DisconnectionReason.LocalSocketClosed) {
            ClearAuthentication();
            InboundSegments.Clear();
            DisconnectedEventArgs disconnectArgs = null;
            lock (_closeLock) {
                if (!Closed && !Closing) {
                    _Closing = true;
                    if (_connectionCounted) {
                        _connectionCounted = false;
                        AdjustThreadPool(-1);
                    }
                    disconnectArgs = new DisconnectedEventArgs(sender, this, Reason);
                    if (Socket != null && Socket.Connected) {
                        MethodExtensions.TryInvoke(() => Socket.Shutdown(SocketShutdown.Both));
                        MethodExtensions.TryInvoke(() => Socket.Close());
                    }
                    SendQueue.Clear();
                    lock (SendQueueRaw) {
                        SendQueueRaw.Clear();
                    }
                    _UploadBuffer.Clear();
                    _DownloadBuffer.Clear();
                    sender.EndPoint = null;
                    UnsubscribePeerUpdate();
                    try { _SendSignal.Release(); } catch (ObjectDisposedException) { }
                }
            }
            // Invoke outside the lock to avoid deadlock with UI dispatcher.
            if (disconnectArgs != null) {
                InvokeDisconnected(sender, disconnectArgs);
            }
        }

        public void StartConnectionTester() {
            Task.Factory.StartNew(async () => {
                if (_parentTypeName == "WebSocketClient")
                    return;

                int failedPollCount = 0;
                const int maxFailedPolls = 3;

                while (!isDisposed && !Closed && !Closing) {
                    // When a protocol handler owns the connection (RawTcpMode /
                    // SuppressConnectionTest), skip Socket.Connected checks —
                    // the handler manages the lifecycle and will detect
                    // disconnects on its own read/write path.
                    if (RawTcpMode || SuppressConnectionTest) {
                        failedPollCount = 0;
                        await Task.Delay(1000);
                        continue;
                    }
                    if (!Socket.Connected) break;
                    if (Poll()) {
                        failedPollCount = 0;
                    } else {
                        failedPollCount++;
                        if (failedPollCount >= maxFailedPolls && !SuppressConnectionTest) {
                            if (!Closed) {
                                Parent.CloseConnection(this, DisconnectionReason.LocalSocketClosed);
                            }
                            break;
                        }
                    }
                    await Task.Delay(1000);
                }
                if (!Closed && !Closing && !SuppressConnectionTest) {
                    Parent.CloseConnection(this, DisconnectionReason.LocalSocketClosed);
                }
            }, TaskCreationOptions.LongRunning);
        }

        #endregion

        #region Peer To Peer

        private void SetPeerID(ISocket sender, Identifier RemotePeer) {
            switch (RemotePeer.Action) {
                case PeerAction.LocalIdentity: {
                        _Identity = RemotePeer;
                        TcpClient Client = (TcpClient)Parent;
                        if (Client != null) {
                            Client.LogFormat("[{0}] Local Identity = {1}", new[] { Client.Name, RemotePeer.ID.ToUpper() });
                            Client.InvokeOnIdentified(sender, _Identity);
                        }
                        break;
                    }
            }
        }
        private void ResetPeerID(DisconnectedEventArgs args) {
            _Identity = null;
            UnsubscribePeerUpdate();
        }
        private void SubscribePeerUpdate() {
            if (IsServer) return;
            var Client = (TcpClient)Parent;
            if (Client != null && !Client.isPeerUpdateSubscribed) {
                Client.isPeerUpdateSubscribed = true;
                Client.OnDisconnected += ResetPeerID;
                Client.PeerUpdate += SetPeerID;
            }
        }
        private void UnsubscribePeerUpdate() {
            if (IsServer) return;
            var Client = (TcpClient)Parent;
            if (Client != null && Client.isPeerUpdateSubscribed) {
                Client.OnDisconnected -= ResetPeerID;
                Client.PeerUpdate -= SetPeerID;
                Client.isPeerUpdateSubscribed = false;
            }
        }

        #endregion

        #region Events

        public event ClientDisconnectedEventHandler OnDisconnected;
        public delegate void ClientDisconnectedEventHandler(NetworkConnection sender, DisconnectedEventArgs e);

		protected internal void InvokeDisconnected(ISocket sender, DisconnectedEventArgs e) {
			if (e.Connection.Closed) return;
			if (sender is TcpServer tcpServer) {
				e.Connection.Closed = true;
				tcpServer.InvokeOnDisconnected(e);
			} else {
				var senderTypeName = sender.GetType().Name;
				if (!(senderTypeName == "WebSocketClient") && !(senderTypeName == "WebSocketServer")) {
					((ISocket)sender).InvokeOnDisconnected(sender, e.Connection);
					e.Connection.Closed = true;
				}
			}
#if UNITY
			MainThread.Run(() => {
				OnDisconnected?.Invoke(this, e);
			});
#endif
#if WINDOWS
			Application.Current.Dispatcher.BeginInvoke(new Action(() => {
				OnDisconnected?.Invoke(this, e);
			}));
#endif
#if !UNITY && !WINDOWS
			OnDisconnected?.Invoke(this, e);
#endif
		}

        #endregion

        #region IDisposable

        private bool isDisposed = false;
        public void Dispose() {
            if (!isDisposed) {
                isDisposed = true;
                UnsubscribePeerUpdate();
                Close(Parent);
                _SendSignal.Dispose();
                GC.SuppressFinalize(this);
            } else {
                throw new ObjectDisposedException(ID.ToString().ToUpper());
            }

        }

        #endregion

        #region SSL

        /// <summary>
        /// Initializes the SSL stream for this connection.
        /// </summary>
        /// <param name="serverCertificate">The server certificate (required for server-side).</param>
        /// <param name="targetHost">Target host name (required for client-side).</param>
        protected internal void InitializeSslStream(X509Certificate serverCertificate, string targetHost) {
            InitializeSslStream(serverCertificate, targetHost, null);
        }

        /// <summary>
        /// Initializes the SSL stream for this connection.
        /// </summary>
        /// <param name="serverCertificate">The default server certificate (required for server-side).</param>
        /// <param name="targetHost">Fallback target host name.</param>
        /// <param name="certificateSelector">Optional SNI certificate selector.</param>
        protected internal void InitializeSslStream(X509Certificate serverCertificate, string targetHost, Func<string, X509Certificate> certificateSelector) {
            if (_Stream == null)
                throw new InvalidOperationException("NetworkStream must be initialized before SSL.");

            if (SslStream != null)
                throw new InvalidOperationException("SSL stream already initialized.");

            if (serverCertificate == null)
                throw new ArgumentNullException(nameof(serverCertificate), "Server certificate required for SSL server mode.");

            ValidateServerCertificate(serverCertificate);

            SslStream = new SslStream(_Stream, true);
            if (certificateSelector == null) {
                SslStream.AuthenticateAsServer(serverCertificate, false, SslProtocols.None, false);
                return;
            }

            var options = new SslServerAuthenticationOptions {
                ServerCertificate = serverCertificate,
                ClientCertificateRequired = false,
                EnabledSslProtocols = SslProtocols.None,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                ServerCertificateSelectionCallback = (sender, hostName) => {
                    string requestedHost = string.IsNullOrWhiteSpace(hostName) ? targetHost : hostName;
                    X509Certificate selected = certificateSelector(requestedHost);
                    ValidateServerCertificate(selected ?? serverCertificate);
                    return selected ?? serverCertificate;
                }
            };
            if (!TryAuthenticateAsServerWithOptions(SslStream, options))
                SslStream.AuthenticateAsServer(serverCertificate, false, SslProtocols.None, false);

        }


        private static bool TryAuthenticateAsServerWithOptions(SslStream sslStream, SslServerAuthenticationOptions options) {
            var method = typeof(SslStream).GetMethod("AuthenticateAsServer", new[] { typeof(SslServerAuthenticationOptions) });
            if (method == null)
                return false;

            try {
                method.Invoke(sslStream, new object[] { options });
                return true;
            } catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException != null) {
                throw ex.InnerException;
            }
        }
        private static void ValidateServerCertificate(X509Certificate certificate) {
            if (certificate == null)
                throw new ArgumentNullException(nameof(certificate), "Server certificate required for SSL server mode.");

            if (certificate is X509Certificate2 certificate2 && !certificate2.HasPrivateKey)
                throw new InvalidOperationException("Server certificate must include an accessible private key.");

            if (certificate is X509Certificate2 certificateWithKey) {
                try {
                    using RSA rsa = certificateWithKey.GetRSAPrivateKey();
                    using ECDsa ecdsa = certificateWithKey.GetECDsaPrivateKey();
                    if (rsa == null && ecdsa == null)
                        throw new InvalidOperationException("Server certificate private key is not RSA or ECDSA.");
                } catch (Exception ex) {
                    throw new InvalidOperationException("Server certificate private key could not be opened by this process.", ex);
                }
            }
        }
        /// <summary>
        /// Initializes the SSL stream for this connection.
        /// </summary>
        /// <param name="targetHost">Target host name (required for client-side).</param>
        protected internal void InitializeSslStream(string targetHost) {
            if (_Stream == null)
                throw new InvalidOperationException("NetworkStream must be initialized before SSL.");

            if (SslStream != null)
                throw new InvalidOperationException("SSL stream already initialized.");

            if (string.IsNullOrEmpty(targetHost))
                throw new ArgumentNullException(nameof(targetHost), "Target host required for SSL client mode.");

            SslStream = new SslStream(_Stream, true, null);
            SslStream.AuthenticateAsClient(targetHost, null, SslProtocols.None, false);
        }
        #endregion

        #region Receive

        protected internal void StartReceiving() {
            if (!_connectionCounted) {
                _connectionCounted = true;
                AdjustThreadPool(1);
            }
            Task.Factory.StartNew(async () => {
                var options = Parent.Options;
                if (options.Fps > 0) {
                    DateTime Time = DateTime.UtcNow;
                    while (!isDisposed && !Closed && Socket.Connected) {
                        var now = DateTime.UtcNow;
                        var nextFrame = Time.AddMilliseconds(options.Timeout);
                        if (now >= nextFrame) {
                            Time = now;
                            await Receive();
                        } else {
                            int waitMs = (int)(nextFrame - now).TotalMilliseconds;
                            if (waitMs > 0) await Task.Delay(waitMs);
                        }
                    }
                } else {
                    while (!isDisposed && !Closed && Socket.Connected) {
                        if (RawTcpMode) {
                            // Protocol handler owns the stream — just idle until
                            // the connection closes or RawTcpMode is cleared.
                            await Task.Delay(250);
                            continue;
                        }
                        if (Interlocked.CompareExchange(ref _IsReceiving, 1, 0) == 0) {
                            try {
                                await ReceiveData();
                            } catch (Exception ex) {
                                var Reason = ex.Interpret();
                                if (Reason.ShouldLogReason())
                                    Parent.InvokeOnError(this, ex);
                                Parent.CloseConnection(this, Reason);
                            } finally {
                                IsReceiving = false;
                            }
                        }
                    }
                }
            }, TaskCreationOptions.LongRunning);
        }

        internal async Task Receive() {
            while (Socket != null && Stream != null && _Stream != null && !Closed && _Stream.DataAvailable) {
                if (Interlocked.CompareExchange(ref _IsReceiving, 1, 0) == 0) {
                    try {
                        await ReceiveData();
                    } catch (Exception ex) {
                        var Reason = ex.Interpret();
                        if (Reason.ShouldLogReason())
                            Parent.InvokeOnError(this, ex);
                        Parent.CloseConnection(this, Reason);
                        return;
                    } finally {
                        IsReceiving = false;
                    }
                }
            }
        }

        private async Task ReceiveData() {
                // If a protocol handler (e.g. TDS) has taken ownership of the
                // stream, bail out immediately so we don't compete for reads.
                if (RawTcpMode) return;

                var options = Parent.Options;
                var stream = Stream;
                // If terminated streams are disabled (or this connection was switched
                // to raw mode after detecting non-SocketJack data) treat as a raw TCP stream.
                if (_forceRawMode || !options.UseTerminatedStreams) {
                    var bufSize = options.DownloadBufferSize > 0 ? options.DownloadBufferSize : 8192;
                    var buffer = ArrayPool<byte>.Shared.Rent(bufSize);
                    try {
                        int bytesRead = 0;
                        do {
                            try {
                                bytesRead = await stream.ReadAsync(buffer, 0, bufSize);
                            } catch (Exception ex) {
                                var Reason = ex.Interpret();
                                if (Reason.ShouldLogReason()) Parent.InvokeOnError(this, ex);
                                break;
                            }

                            // A protocol handler took ownership while we were blocking.
                            // Yield without closing — the handler owns the stream now.
                            if (RawTcpMode) {
                                IsReceiving = false;
                                return;
                            }

                            if (bytesRead <= 0) {
                                IsReceiving = false;
                                Parent.CloseConnection(this, DisconnectionReason.RemoteSocketClosed);
                                return;
                            }

                            Interlocked.Add(ref _TotalBytesReceived, bytesRead);
                            Parent.InvokeInternalReceivedByteCounter(this, bytesRead);

                            var payload = new List<byte>(bytesRead);
                            for (int i = 0; i < bytesRead; i++)
                                payload.Add(buffer[i]);
                            Parent.HandleReceive(this, payload, ByteArrayType, bytesRead);
                        } while (_Stream.DataAvailable);
                    } catch (Exception ex) {
                        var Reason = ex.Interpret();
                        if (Reason.ShouldLogReason()) Parent.InvokeOnError(this, ex);
                        Parent.CloseConnection(this, Reason);
                    } finally {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }
                    IsReceiving = false;
                    return;
                }

                // Read message length header (15 bytes) then read body in buffered chunks
                int lengthRead;
                try {
                    lengthRead = await stream.ReadAsync(_lengthHeaderBuffer, 0, 15);
                    Interlocked.Add(ref _TotalBytesReceived, lengthRead);
                } catch (Exception ex) {
                    var Reason = ex.Interpret();
                    if (Reason.ShouldLogReason())
                        Parent.InvokeOnError(this, ex);
                    Parent.CloseConnection(this, Reason);
                    IsReceiving = false;
                    return;
                }

                // A protocol handler took ownership while we were blocking.
                // Yield without closing — the handler owns the stream now.
                if (RawTcpMode) {
                    IsReceiving = false;
                    return;
                }

                if (lengthRead <= 0) {
                    // Stream ended — remote side closed the connection.
                    Parent.CloseConnection(this, DisconnectionReason.RemoteSocketClosed);
                    IsReceiving = false;
                    return;
                }

                string lengthStr = Encoding.UTF8.GetString(_lengthHeaderBuffer, 0, lengthRead).Trim(LengthTrimChars);
                if (!int.TryParse(lengthStr, out int Length)) {
                    // The first bytes are not a valid SocketJack length header.
                    // If they look like an HTTP request, switch this connection to
                    // raw-byte mode and re-dispatch so the protocol detection layer
                    // (e.g. MutableTcpServer / HttpServer) can route it correctly.
                    if (IsHttpPrefix(_lengthHeaderBuffer, lengthRead)) {
                        _forceRawMode = true;
                        var raw = new List<byte>(lengthRead + 256);
                        raw.AddRange(new ArraySegment<byte>(_lengthHeaderBuffer, 0, lengthRead));
                        if (_Stream != null) {
                            var bufSize = options.DownloadBufferSize > 0 ? options.DownloadBufferSize : 8192;
                            var extraBuf = ArrayPool<byte>.Shared.Rent(bufSize);
                            try {
                                while (_Stream.DataAvailable) {
                                    int n = await stream.ReadAsync(extraBuf, 0, bufSize);
                                    if (n <= 0) break;
                                    Interlocked.Add(ref _TotalBytesReceived, n);
                                    Parent.InvokeInternalReceivedByteCounter(this, n);
                                    raw.AddRange(new ArraySegment<byte>(extraBuf, 0, n));
                                }
                            } catch { } finally {
                                ArrayPool<byte>.Shared.Return(extraBuf);
                            }
                        }
                        if (raw.Count > 0) {
                            var rawCopy = raw;
                            Task.Run(() => {
                                Parent.HandleReceive(this, rawCopy, ByteArrayType, rawCopy.Count);
                            });
                        }
                        IsReceiving = false;
                        return;
                    }
                    Parent.InvokeOnError(this, new FormatException("Failed to parse message length."));
                    Parent.CloseConnection(this, DisconnectionReason.Unknown);
                    IsReceiving = false;
                    return;
                }

                if (Length < 0 || Length > options.MaximumBufferSize) {
                    Parent.InvokeOnError(this, new IOException("SocketJack frame length " + Length + " exceeds NetworkOptions.MaximumBufferSize " + options.MaximumBufferSize + "."));
                    Parent.CloseConnection(this, DisconnectionReason.Unknown);
                    IsReceiving = false;
                    return;
                }

                byte[] Body = new byte[Length];
                int totalRead = 0;
                try {
                    while (totalRead < Length) {
                        if (Socket is null || stream is null || !Socket.Connected || Closed || (SSL && SslStream is null)) {
                            if (!Closed) Parent.CloseConnection(this);
                            IsReceiving = false;
                            return;
                        }

                        int chunkSize = options.isDownloadBuffered ? options.DownloadBufferSize : (Length - totalRead);
                        chunkSize = Math.Min(chunkSize, Length - totalRead);

                        int bytesRead = await stream.ReadAsync(Body, totalRead, chunkSize);
                        if (bytesRead <= 0) break;
                        totalRead += bytesRead;
                        Interlocked.Add(ref _TotalBytesReceived, bytesRead);
                        Parent.InvokeInternalReceivedByteCounter(this, bytesRead);
                        await ThrottleReceiveIfNeededAsync(options);
                    }
                } catch (Exception ex) {
                    var Reason = ex.Interpret();
                    if (Reason.ShouldLogReason())
                        Parent.InvokeOnError(this, ex);
                    Parent.CloseConnection(this, Reason);
                    IsReceiving = false;
                    return;
                }

                if (totalRead == Length) {
                    ParseBuffer(Body, this, Parent);
                } else {
                    Parent.InvokeOnError(this, new IOException("Unexpected end of stream while reading message body."));
                    Parent.CloseConnection(this, DisconnectionReason.RemoteSocketClosed);
                }

                IsReceiving = false;
        }

        /// <summary>
        /// Returns <see langword="true"/> when <paramref name="data"/> begins with
        /// a known HTTP method keyword (GET, POST, PUT, …).
        /// </summary>
        private static bool IsHttpPrefix(byte[] data, int length) {
            if (data == null || length < 3) return false;
            byte b0 = data[0];
            if (b0 != (byte)'G' && b0 != (byte)'P' && b0 != (byte)'H' &&
                b0 != (byte)'D' && b0 != (byte)'O' && b0 != (byte)'T' &&
                b0 != (byte)'C')
                return false;
            string prefix = Encoding.UTF8.GetString(data, 0, Math.Min(length, 10));
            return prefix.StartsWith("GET ") || prefix.StartsWith("POST ") ||
                   prefix.StartsWith("PUT ") || prefix.StartsWith("DELETE ") ||
                   prefix.StartsWith("HEAD ") || prefix.StartsWith("OPTIONS") ||
                   prefix.StartsWith("PATCH ") || prefix.StartsWith("TRACE ") ||
                   prefix.StartsWith("CONNECT ");
        }

        private async Task ReceiveData_old(bool DataAvailable) {
            DateTime DownloadStartTime = DateTime.UtcNow;
            long TotalBytesRead = 0L;

            // SSL does not support Socket.Available, so we need to read the data until no more bytes received.
            if (SSL | Parent.Options.isDownloadBuffered) {
                int bytesRead = 0;
                do {
                    // Not Yet Implemented
                    //if (Parent.Options.MaximumDownloadMbps > 0) {
                    // Limit download bandwidth.
                    //while (LimitDownloadBandwidth(TotalDownloadedBytes, DownloadStartTime))
                    //    Thread.Sleep(1);
                    //DownloadStartTime = DateTime.UtcNow;
                    //}

                    if (Socket is null || Stream is null || !Socket.Connected || Closed || (SSL && SslStream is null)) {
                        if (!Closed) Parent.CloseConnection(this);
                        return;
                    }

                    try {
                        byte[] temp = new byte[Parent.Options.DownloadBufferSize];
                        bytesRead = await Stream.ReadAsync(temp, 0, Parent.Options.DownloadBufferSize);
                        if (bytesRead != temp.Length) Array.Resize(ref temp, bytesRead);
                        if (bytesRead > 0) _DownloadBuffer.AddRange(temp);
                        temp = null;
                    } catch (Exception ex) {
                        var Reason = ex.Interpret();
                        if (Reason.ShouldLogReason())
                            Parent.InvokeOnError(this, ex);
                        Parent.CloseConnection(this, Reason);
                    }

                    if (bytesRead > 0) {
                        TotalBytesRead += bytesRead;
                        Parent.InvokeInternalReceivedByteCounter(this, bytesRead);
                        _DownloadBuffer = await ParseBuffer(_DownloadBuffer, this, Parent);
                    }
                } while (bytesRead > 0);
            } else {
                try {
                    if (Socket is null)
                        return;
                    int AvailableBytes = Socket.Available;
                    var temp = new byte[AvailableBytes];
                    if (Stream is null) return;
                    int bytesRead = await Stream.ReadAsync(temp, 0, AvailableBytes);
                    if (bytesRead != temp.Length) Array.Resize(ref temp, bytesRead);

                    if (bytesRead > 0) {
                        if (_DownloadBuffer == null)
                            _DownloadBuffer = new List<byte>(temp);
                        else
                            _DownloadBuffer.AddRange(temp);
                        temp = null;
                        Parent.InvokeInternalReceivedByteCounter(this, bytesRead);
                        _DownloadBuffer = await ParseBuffer(_DownloadBuffer, this, Parent);
                    } else {
                        _DownloadBuffer.Clear();
                    }
                } catch (Exception ex) {
                    var Reason = ex.Interpret();
                    if (Reason.ShouldLogReason())
                        Parent.InvokeOnError(this, ex);
                    Parent.CloseConnection(this, Reason);
                }
            }
            IsReceiving = false;
        }

        private static void DeserializeAndDispatch(byte[] Bytes, int ByteLength, NetworkConnection Sender, ISocket Target) {
          try {
            SafeModeHandshake.RequireVerified(Sender, Target.Options);
            if (Target.Options.UseCompression) {
                var decompressionResult = MethodExtensions.TryInvoke(b => UdpReliableObjects.Decompress(b, Target.Options), ref Bytes);
                if (decompressionResult.Success) {
                    Bytes = decompressionResult.Result;
                } else {
                    Target.CloseConnection(Sender, DisconnectionReason.CompressionError);
                    Target.InvokeOnError(Sender, decompressionResult.Exception);
                    return;
                }
            }
            if (Sender != null && Sender.PatternCache != null) {
                if (!Sender.PatternCache.TryResolveReceived(Bytes, Target.Options, out Bytes, out string cacheError)) {
                    Target.InvokeOnError(Sender, new P2PException(cacheError));
                    return;
                }
            }
            object message = InboundMessageDecoder.Read(Target, Sender, Bytes);
            if (message != null) Target.HandleReceive(Sender, message, message.GetType(), ByteLength);
          } catch (Exception ex) {
              if (Sender != null) Target.CloseConnection(Sender, DisconnectionReason.Unknown);
              Target.InvokeOnError(Sender, ex);
          }
        }

        private static void ParseBuffer(byte[] Bytes, NetworkConnection Sender, ISocket Target) {
            Task.Run(() => DeserializeAndDispatch(Bytes, Bytes.Length, Sender, Target));
        }

        private static async Task<List<byte>> ParseBuffer(List<byte> Buffer, NetworkConnection Sender, ISocket Target) {
            if (Buffer == null || Buffer.Count == 0) return Buffer;

            if (Target.Options.UseTerminatedStreams) {
                var TerminatorIndices = Buffer.IndexOfAll(Terminator);

                if (TerminatorIndices.Count > 0) {
                    int lastTerminatorIndex = TerminatorIndices[TerminatorIndices.Count - 1];

                    // Pre-extract all message segments as independent byte arrays
                    // so parallel tasks operate on isolated data instead of the shared List<byte>
                    var segments = new byte[TerminatorIndices.Count][];
                    for (int i = 0; i < TerminatorIndices.Count; i++) {
                        int prevEnd = i > 0 ? TerminatorIndices[i - 1] + Terminator.Length : 0;
                        segments[i] = Buffer.Part(prevEnd, TerminatorIndices[i]);
                    }

                    // Process segments sequentially to preserve TCP message ordering.
                    // Parallel dispatch can reorder callbacks (e.g. MouseUp before MouseDown)
                    // when multiple messages arrive in the same TCP read.
                    for (int i = 0; i < segments.Length; i++) {
                        var segment = segments[i];
                        await Task.Run(() => DeserializeAndDispatch(segment, segment.Length, Sender, Target));
                    }

                    // Clean up consumed bytes from buffer
                    int consumedEnd = lastTerminatorIndex + Terminator.Length;
                    if (consumedEnd >= Buffer.Count) {
                        Buffer.Clear();
                    } else {
                        Buffer.RemoveRange(0, consumedEnd);
                    }
                }
                return Buffer;
            } else {
                var tempBuffer = new List<byte>(Buffer);
                Buffer.Clear();
                // Dispatch synchronously so that protocol-detection layers
                // (MutableTcpServer.RouteReceive, HttpServer.GetRequestAsync)
                // finish assigning a handler before the next ReceiveData
                // iteration reads more data from the socket.
                Target.HandleReceive(Sender, tempBuffer, ByteArrayType, tempBuffer.Count);

                return Buffer;
            }
        }

        #endregion

        #region Send

        protected internal void StartSending() {
            Task.Factory.StartNew(async () => {
                var options = Parent.Options;
                DateTime Time = DateTime.UtcNow;
                while (!isDisposed && !Closed && Socket.Connected) {
                    if (options.Chunking) {
                        var intervalMs = options.ChunkingIntervalMs;
                        if (intervalMs < 100) intervalMs = 100;
                        await Task.Delay(intervalMs);
                        await ProcessQueue();
                    } else if (options.Fps > 0) {
                        var now = DateTime.UtcNow;
                        var nextFrame = Time.AddMilliseconds(options.Timeout);
                        if (now >= nextFrame) {
                            Time = now;
                            await ProcessQueue();
                        } else {
                            int waitMs = (int)(nextFrame - now).TotalMilliseconds;
                            if (waitMs > 0) await Task.Delay(waitMs);
                        }
                    } else {
                        await ProcessQueue();
                    }
                }
            }, TaskCreationOptions.LongRunning);
        }

        protected async internal Task ProcessQueue() {
            if (RawTcpMode || Socket == null || Stream == null || !Socket.Connected || Closed) {
                await Task.Delay(50);
                return;
            }

            if (Interlocked.CompareExchange(ref _IsSending, 1, 0) != 0) {
                await Task.Delay(1);
                return;
            }

            byte[] chunk = null;
            lock (SendQueueRaw) {
                if (SendQueueRaw.Count > 0) {
                    chunk = SendQueueRaw.ToArray();
                    SendQueueRaw.Clear();
                }
            }

            var options = Parent.Options;
            if (chunk == null || chunk.Length == 0) {
                IsSending = false;
                if (!options.Chunking)
                    await _SendSignal.WaitAsync(500);
                return;
            }

#if UNITY
                        MainThread.Run(() => {
                           SendSerializedBytes(chunk);
                           IsSending = false;
                        });
#endif
#if WINDOWS
            try {
                SendSerializedBytes(chunk);
            } finally {
                IsSending = false;
            }
#endif
#if !UNITY && !WINDOWS
            SendSerializedBytes(chunk);
            IsSending = false;
#endif
        }

        //        protected async internal Task ProcessQueue() {
        //            if (Socket != null && Stream != null && (Socket.Connected) && !Closed && !IsSending && SendQueue.Count > 0) {
        //                IsSending = true;
        //                var Items = new List<SendQueueItem>();
        //                while (SendQueue.Count > 0) {
        //                    SendQueueItem Item;
        //                    SendQueue.TryDequeue(out Item);
        //                    if (Item != null)
        //                        Items.Add(Item);
        //                }
        //                if (Items.Count == 0) {
        //                    IsSending = false;
        //                    return;
        //                }
        //                for (int i = 0; i < Items.Count; i++) {
        //                    var item = Items[i];
        //                    if (Socket == null || !Socket.Connected || Closed)
        //                        break;
        //                    if (item != null) {
        //                        bool okay = false;
        //#if UNITY
        //                        MainThread.Run(() => {
        //                            okay = TrySendQueueItem(item).Result;
        //                        });
        //#endif
        //#if WINDOWS
        //                       okay = await Application.Current.Dispatcher.InvokeAsync(async () => { return await TrySendQueueItem(item); }).Result;
        //#endif
        //#if !UNITY && !WINDOWS
        //            okay = await TrySendQueueItem(item);
        //#endif
        //                        if (okay) {
        //                            item.Complete = true;
        //                        }
        //                    }
        //                }
        //                IsSending = false;
        //            } else {
        //                await Task.Delay(1);
        //            }
        //        }

        private async Task ThrottleReceiveIfNeededAsync(NetworkOptions options) {
            if (options == null || !options.isDownloadBuffered || options.MaximumDownloadBytesPerSecond <= 0)
                return;

            long total = TotalBytesReceived;
            var now = DateTime.UtcNow;
            var elapsed = now - LastReceiveFrame;
            if (elapsed >= OneSecond) {
                _ReceivedBytesPerSecond = (int)Math.Min(int.MaxValue, Math.Max(0, total - _TotalBytesReceived_l));
                _TotalBytesReceived_l = total;
                LastReceiveFrame = now;
                return;
            }

            long bytesThisWindow = total - _TotalBytesReceived_l;
            if (bytesThisWindow <= options.MaximumDownloadBytesPerSecond)
                return;

            int waitMs = Math.Max(1, (int)(OneSecond - elapsed).TotalMilliseconds);
            await Task.Delay(waitMs);
            _ReceivedBytesPerSecond = (int)Math.Min(int.MaxValue, Math.Max(0, TotalBytesReceived - _TotalBytesReceived_l));
            _TotalBytesReceived_l = TotalBytesReceived;
            LastReceiveFrame = DateTime.UtcNow;
        }

        private void ThrottleSendIfNeeded(NetworkOptions options) {
            if (options == null || !options.isUploadBuffered || options.MaximumUploadBytesPerSecond <= 0)
                return;

            long total = TotalBytesSent;
            var now = DateTime.UtcNow;
            var elapsed = now - LastSendFrame;
            if (elapsed >= OneSecond) {
                _SentBytesPerSecond = (int)Math.Min(int.MaxValue, Math.Max(0, total - _TotalBytesSent_l));
                _TotalBytesSent_l = total;
                LastSendFrame = now;
                return;
            }

            long bytesThisWindow = total - _TotalBytesSent_l;
            if (bytesThisWindow <= options.MaximumUploadBytesPerSecond)
                return;

            int waitMs = Math.Max(1, (int)(OneSecond - elapsed).TotalMilliseconds);
            Thread.Sleep(waitMs);
            _SentBytesPerSecond = (int)Math.Min(int.MaxValue, Math.Max(0, TotalBytesSent - _TotalBytesSent_l));
            _TotalBytesSent_l = TotalBytesSent;
            LastSendFrame = DateTime.UtcNow;
        }

        private async Task ThrottleSendIfNeededAsync(NetworkOptions options) {
            if (options == null || !options.isUploadBuffered || options.MaximumUploadBytesPerSecond <= 0)
                return;

            long total = TotalBytesSent;
            var now = DateTime.UtcNow;
            var elapsed = now - LastSendFrame;
            if (elapsed >= OneSecond) {
                _SentBytesPerSecond = (int)Math.Min(int.MaxValue, Math.Max(0, total - _TotalBytesSent_l));
                _TotalBytesSent_l = total;
                LastSendFrame = now;
                return;
            }

            long bytesThisWindow = total - _TotalBytesSent_l;
            if (bytesThisWindow <= options.MaximumUploadBytesPerSecond)
                return;

            int waitMs = Math.Max(1, (int)(OneSecond - elapsed).TotalMilliseconds);
            await Task.Delay(waitMs);
            _SentBytesPerSecond = (int)Math.Min(int.MaxValue, Math.Max(0, TotalBytesSent - _TotalBytesSent_l));
            _TotalBytesSent_l = TotalBytesSent;
            LastSendFrame = DateTime.UtcNow;
        }
        protected internal bool SendSerializedBytes(byte[] SerializedBytes) {
            //return await Task.Run(async () => {
            //});
            var stream = Stream;
            if (isDisposed || stream == null || Closed) return false;
            var options = Parent.Options;
            bool useSsl = SSL;
            bool sentSuccessful = false;
            try {
                byte[] ProcessedBytes = SerializedBytes;
                int totalBytes = ProcessedBytes.Length;

                if (!options.isUploadBuffered || options.UploadBufferSize <= 0) {
                    if (useSsl) {
                        SslStream.Write(ProcessedBytes, 0, totalBytes);
                    } else {
                        stream.Write(ProcessedBytes, 0, totalBytes);
                    }
                    Interlocked.Add(ref _TotalBytesSent, totalBytes);
                    Parent.InvokeInternalSentByteCounter(this, totalBytes);
                    ThrottleSendIfNeeded(options);
                } else {
                    int chunkUnit = Math.Max(1, options.UploadBufferSize);

                    for (int offset = 0; offset < totalBytes; offset += chunkUnit) {
                        if (Socket is null || stream is null || !Socket.Connected || Closed || (useSsl && SslStream is null)) {
                            SerializedBytes = null;
                            ProcessedBytes = null;
                            if (!Closed) Parent.CloseConnection(this);
                            return false;
                        }

                        int chunkSize = Math.Min(chunkUnit, totalBytes - offset);
                        if (useSsl) {
                            SslStream.Write(ProcessedBytes, offset, chunkSize);
                        } else {
                            stream.Write(ProcessedBytes, offset, chunkSize);
                        }

                        Interlocked.Add(ref _TotalBytesSent, chunkSize);
                        Parent.InvokeInternalSentByteCounter(this, chunkSize);
                        ThrottleSendIfNeeded(options);
                    }
                }

                sentSuccessful = true;
                Parent.InvokeInternalSendEvent(this, ByteArrayType, "[CHUNK]", ProcessedBytes.Length);
                Parent.InvokeOnSent(new SentEventArgs(this.Parent, this, ByteArrayType, ProcessedBytes.Length));
            } catch (Exception ex) {
                if (Closed) return false;
                var Reason = ex.Interpret();
                if (Reason.ShouldLogReason()) {
                    Parent.InvokeOnError(this, ex);
                }
                Parent.CloseConnection(this, Reason);
            }
            return sentSuccessful;
        }
        protected internal async Task<bool> TrySendQueueItem(SendQueueItem Item) {
            if (isDisposed || Stream == null || Closed || Item.Complete) return false;
            bool sentSuccessful = false;
            try {
                byte[] SerializedBytes = default;
                Wrapper wrapped = default;
                Type type = default;
                lock (Item.Object) {
                    type = Item.Object.GetType();
                    wrapped = new Wrapper(Item.Object, Parent);
                    SerializedBytes = Parent.Options.Serializer.Serialize(wrapped);
                }
                // if (NIC.MTU == -1 || SerializedBytes.Length < NIC.MTU && SerializedBytes.Length < 65535) {
                //    Object smaller than MTU.
                SerializedBytes = Item.Connection.PatternCache.PrepareSend(SerializedBytes, Parent.Options);
                byte[] ProcessedBytes = Item.Connection.Compressed ? Parent.Options.CompressionAlgorithm.Compress(SerializedBytes).Terminate() : SerializedBytes.Terminate();
                int totalBytes = ProcessedBytes.Length;
                NetworkConnection Client = Item.Connection;
                byte[] SentBytes = new byte[totalBytes];

                if (Parent.Options.isUploadBuffered) {
                    for (int offset = 0, loopTo = ProcessedBytes.Length - 1; Parent.Options.UploadBufferSize >= 0 ? offset <= loopTo : offset >= loopTo; offset += Parent.Options.UploadBufferSize) {
                        if (Socket is null || Stream is null || !Socket.Connected || Closed || (SSL && SslStream is null)) {
                            SerializedBytes = null;
                            ProcessedBytes = null;
                            wrapped = null;
                            if (!Closed) Parent.CloseConnection(this);
                            return false;
                        }
                        int chunkSize = Math.Min(Parent.Options.UploadBufferSize, totalBytes - offset);
                        if (SSL) {
                            await SslStream.WriteAsync(ProcessedBytes, offset, chunkSize);
                        } else {
                            await Stream.WriteAsync(ProcessedBytes, offset, chunkSize);
                        }
                        Interlocked.Add(ref _TotalBytesSent, chunkSize);
                        Parent.InvokeInternalSentByteCounter(Item.Connection, chunkSize);
                        await ThrottleSendIfNeededAsync(Parent.Options);
                    }
                } else {
                    if (SSL) {
                        await SslStream.WriteAsync(ProcessedBytes, 0, ProcessedBytes.Length);
                    } else {
                        await Stream.WriteAsync(ProcessedBytes, 0, ProcessedBytes.Length);
                    }
                    Interlocked.Add(ref _TotalBytesSent, ProcessedBytes.Length);
                    Parent.InvokeInternalSentByteCounter(Item.Connection, ProcessedBytes.Length);
                    await ThrottleSendIfNeededAsync(Parent.Options);
                }
                sentSuccessful = true;
                //if (!Globals.IgnoreLoggedTypes.Contains(type)) {
                Parent.InvokeInternalSendEvent(Item.Connection, type, Item.Object, ProcessedBytes.Length);
                Parent.InvokeOnSent(new SentEventArgs(this.Parent, Item.Connection, type, ProcessedBytes.Length));
                //}

                //} else if (SerializedBytes.Length > NIC.MTU) {
                //    // Object larger than MTU, we have to Segment the object.
                //    Item.Connection.Parent.SendSegmented(Item.Connection, SerializedBytes);
                // }
            } catch (Exception ex) {
                if (Closed) return false;
                var Reason = ex.Interpret();
                if (Reason.ShouldLogReason()) {
                    Parent.InvokeOnError(this, ex);
                }
                Parent.CloseConnection(this, Reason);
            }
            return sentSuccessful;
        }
        public void Send(object Obj) {
            if (Parent is TcpServer Server) {
                if (Server is null) return;
                Server.Send(this, Obj);
            } else if (Parent is UdpServer udpServer) {
                foreach (var kvp in udpServer.Clients) {
                    if (kvp.Value.ID == this.ID) {
                        udpServer.SendTo(kvp.Value, Obj);
                        break;
                    }
                }
            } else if (Parent is TcpClient Client) {
                Client.Send(Obj);
            } else if (Parent is UdpClient udpClient) {
                udpClient.Send(Obj);
            }
        }

        /// <summary>
        /// Send an object to a Remote Client on the server.
        /// <para>This will <see langword="NOT"/> expose your remote IP.</para>
        /// </summary>
        /// <param name="Recipient"></param>
        /// <param name="Obj"></param>
        public void Send(Identifier Recipient, object Obj) {
            if (!IsServer) {
                if (Parent is TcpClient Client) {
                    if (ID == default) {
                        Client.InvokeOnError(this, new P2PException("P2P Client not yet initialized." + Environment.NewLine +
                                                                          "     ConnectedSocket.Identifier property cannot equal null." + Environment.NewLine +
                                                                          "     Invoke via TcpClient.OnIdentified Event instead of TcpClient.OnConnected."));
                    } else {
                        Client.Send(Recipient, Obj);
                    }
                }
            }
        }

        #endregion

        /// <summary>
        /// Set metadata for the connection serialized with Json.
        /// <para>WARNING: This information will be sent to all connected clients.</para>
        /// <para>Set the `Private` <see langword="bool"/> parameter to <see langword="true"/> to retain private metadata only on server.</para>
        /// <paramref name="key">Metadata Key.</paramref>
        /// <paramref name="value">Metadata Value.</paramref>
        /// <paramref name="Private"><see langword="true"/> to keep the metadata only on server; <see langword="false"/> shares with all peers.</paramref>
        /// <paramref name="Restricted"><see langword="true"/> to restrict the client from updating the metadata value.</paramref>
        /// </summary>
        public void SetMetaData(string key, string value, bool Private = false, bool Restricted = false) {
            if (Parent == null) return;
            if (string.IsNullOrEmpty(key)) return;
            if (Parent is TcpServer server) {
                if (Restricted && !server.RestrictedMetadataKeys.Contains(key.ToLower()))
                    server.RestrictedMetadataKeys.Add(key.ToLower());
                if (!Parent.Peers.ContainsKey(Identity.ID)) {
                    Parent.Peers.AddOrUpdate(Identity.RemoteReady(Parent));
                }
                Parent.Peers[Identity.ID].SetMetaData(server, key.ToLower(), value, Private);
                //Identity.SetMetaData(server, key.ToLower(), value, Private);
            } else if (Parent is UdpServer udpServer) {
                if (Restricted && !udpServer.RestrictedMetadataKeys.Contains(key.ToLower()))
                    udpServer.RestrictedMetadataKeys.Add(key.ToLower());
                if (!Parent.Peers.ContainsKey(Identity.ID)) {
                    Parent.Peers.AddOrUpdate(Identity.RemoteReady(Parent));
                }
                Parent.Peers[Identity.ID].SetMetaData(udpServer, key.ToLower(), value, Private);
            } else if (_parentTypeName == "WebSocketServer") {
                // Use reflection to locate the type and members
                var wsServerType = Parent.GetType();
                var restrictedKeysProp = wsServerType.GetProperty("RestrictedMetadataKeys");

                if (restrictedKeysProp != null) {
                    var restrictedKeys = restrictedKeysProp.GetValue(Parent) as List<string>;
                    if (restrictedKeys != null && Restricted) {
                        var containsMethod = restrictedKeys.GetType().GetMethod("Contains");
                        var addMethod = restrictedKeys.GetType().GetMethod("Add");
                        bool contains = false;
                        if (containsMethod != null)
                            contains = (bool)containsMethod.Invoke(restrictedKeys, new object[] { key.ToLower() });
                        if (!contains && addMethod != null)
                            addMethod.Invoke(restrictedKeys, new object[] { key.ToLower() });
                    }
                }
                if (!Parent.Peers.ContainsKey(Identity.ID)) {
                    Parent.Peers.AddOrUpdate(Identity.RemoteReady(Parent));
                }
                Parent.Peers[Identity.ID].SetMetaData(Parent, key.ToLower(), value, Private);
                //Identity.SetMetaData(Parent, key.ToLower(), value, Private);
            } else if (Parent is TcpClient client) {
                client.Send(new MetadataKeyValue() { Key = key.ToLower(), Value = value });
            } else if (Parent is UdpClient udpClient) {
                udpClient.Send(new MetadataKeyValue() { Key = key.ToLower(), Value = value });
            } else if (_parentTypeName == "WebSocketClient") {
                // Use reflection to resolve and invoke Send on Parent
                var sendMethod = Parent.GetType().GetMethod("Send", new[] { typeof(object) });
                if (sendMethod != null) {
                    sendMethod.Invoke(Parent, new object[] { new MetadataKeyValue() { Key = key.ToLower(), Value = value } });
                } else {
                    Parent.InvokeOnError(this, new MissingMethodException("Send method not found on WebSocketClient."));
                }
            }
        }

        /// <summary>
        /// Get metadata value by key for the connection.
        /// <para>Can only be called from server.</para>
        /// <paramref name="key">Metadata Key.</paramref>
        /// <returns>Value as string.</returns>
        /// </summary>
        public async Task<string> GetMetaData(string key, bool Private = false, bool WaitForValueIfNull = true) {
            if (Identity == null && !WaitForValueIfNull) {
                return default;
            } else if (Identity == null && WaitForValueIfNull) {
                while (Identity == null) {
                    await Task.Delay(50);
                }
            }
            return await Identity.GetMetaData(key, Private, WaitForValueIfNull);
        }

        public NetworkConnection(ISocket Parent, Socket Socket) {
            _Parent = Parent;
            _parentTypeName = Parent.GetType().Name;
            _isWebSocket = _parentTypeName == "WebSocketClient" || _parentTypeName == "WebSocketServer";
            if (Parent is TcpClient) {
                SubscribePeerUpdate();
                IsServer = false;
            } else if (Parent is UdpClient) {
                // UdpClient manages its own PeerUpdate subscriptions.
                IsServer = false;
            } else {
                IsServer = true;
            }
            if (Socket != null) {
                _Socket = Socket;
                EndPoint = (IPEndPoint)Socket.RemoteEndPoint;
            }
        }
    }
}