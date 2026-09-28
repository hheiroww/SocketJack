using SocketJack.Extensions;
using SocketJack.Net.P2P;
using SocketJack.Serialization;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace SocketJack.Net {

    /// <summary>
    /// Represents a UDP connection endpoint with send/receive capabilities.
    /// Modeled after NetworkConnection but adapted for connectionless UDP datagrams.
    /// </summary>
    public class UdpConnection : IDisposable {

        internal UdpReliablePeer ReliablePeer;
        public UdpReliableStatistics ReliableStatistics => ReliablePeer?.Statistics;
        public Task SendAsync(object value, CancellationToken cancellationToken = default) {
            if (Parent is UdpClient client) return client.SendAsync(value, cancellationToken);
            if (Parent is UdpServer server) return server.SendToAsync(this, value, cancellationToken);
            throw new InvalidOperationException("Unknown UDP parent.");
        }

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
        /// UDP is connectionless, so Stream is not used. Always returns null.
        /// </summary>
        public Stream Stream {
            get {
                return null;
            }
        }

        /// <summary>
        /// Whether or not data is compressed.
        /// </summary>
        public bool Compressed {
            get {
                return Parent.Options.UseCompression;
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

        /// <summary>
        /// True if connection sending or receiving.
        /// </summary>
        public bool Active {
            get {
                return IsReceiving || IsSending;
            }
        }

        /// <summary>
        /// Bytes per second sent on this connection.
        /// </summary>
        public int BytesPerSecondSent {
            get {
                return _SentBytesPerSecond;
            }
        }
        protected internal int _SentBytesPerSecond = 0;
        protected internal int SentBytesCounter = 0;

        /// <summary>
        /// Bytes per second received on this connection.
        /// </summary>
        public int BytesPerSecondReceived {
            get {
                return _ReceivedBytesPerSecond;
            }
        }
        protected internal int _ReceivedBytesPerSecond = 0;
        protected internal int ReceivedBytesCounter = 0;

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

        /// <summary>
        /// Remote Peer identifier for peer to peer interactions.
        /// </summary>
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

        /// <summary>
        /// Maximum safe UDP payload size to avoid IP fragmentation.
        /// </summary>
        public const int MaxDatagramSize = 65507;

        internal SocketJackPatternCache PatternCache { get; } = new SocketJackPatternCache();

        #endregion

        #region Internal

        private volatile bool _Closed = false;
        public volatile bool _Closing = false;
        private readonly object _closeLock = new object();
        protected internal SemaphoreSlim _SendSignal = new SemaphoreSlim(0);

        protected internal ConcurrentQueue<UdpSendItem> SendQueue = new ConcurrentQueue<UdpSendItem>();

        /// <summary>
        /// <see langword="True"/> if created by a UdpServer.
        /// </summary>
        protected internal bool IsServer = false;

        /// <summary>
        /// Sends the remote client their remote Identity.
        /// </summary>
        internal void SendLocalIdentity() {
            var identity = Identifier.Create(ID, true, EndPoint.Address.ToString());
            var parent = Parent;
            if (parent is UdpServer server) {
                server.SendTo(this, identity);
            } else if (parent is UdpClient client) {
                client.Send(identity);
            }
        }

        public void CloseConnection() {
            Close(Parent);
        }

        public void Close(ISocket sender, DisconnectionReason Reason = DisconnectionReason.LocalSocketClosed) {
            lock (_closeLock) {
                if (!Closed && !Closing) {
                    _Closing = true;
                    ReliablePeer?.Fail(new IOException("Local UDP connection closed."));
                    var e = new DisconnectedEventArgs(sender, null, Reason);
                    SendQueue.Clear();
                    InvokeDisconnected(sender, e);
                    try { _SendSignal.Release(); } catch (ObjectDisposedException) { }
                }
            }
        }

        #endregion

        #region Events

        public event ClientDisconnectedEventHandler OnDisconnected;
        public delegate void ClientDisconnectedEventHandler(UdpConnection sender, DisconnectedEventArgs e);

        protected internal void InvokeDisconnected(ISocket sender, DisconnectedEventArgs e) {
            if (Closed) return;
            Closed = true;
#if UNITY
            MainThread.Run(() => {
                OnDisconnected?.Invoke(this, e);
            });
#endif
#if WINDOWS
            Application.Current.Dispatcher.Invoke(() => {
                OnDisconnected?.Invoke(this, e);
            });
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
                Close(Parent);
                _SendSignal.Dispose();
                GC.SuppressFinalize(this);
            }
        }

        #endregion

        #region Send

        /// <summary>
        /// Queue a datagram for sending to this connection's endpoint.
        /// </summary>
        public void Send(object Obj) {
            if (Parent is UdpServer server) {
                server.SendTo(this, Obj);
            } else if (Parent is UdpClient client) {
                client.Send(Obj);
            }
        }

        #endregion

        /// <summary>
        /// Set metadata for the connection.
        /// </summary>
        public void SetMetaData(string key, string value, bool Private = false, bool Restricted = false) {
            if (Parent == null) return;
            if (string.IsNullOrEmpty(key)) return;
            if (Parent is UdpServer server) {
                if (Restricted && !server.RestrictedMetadataKeys.Contains(key.ToLower()))
                    server.RestrictedMetadataKeys.Add(key.ToLower());
                if (!Parent.Peers.ContainsKey(Identity.ID)) {
                    Parent.Peers.AddOrUpdate(Identity.RemoteReady(Parent));
                }
                Parent.Peers[Identity.ID].SetMetaData(server, key.ToLower(), value, Private);
            } else if (Parent is UdpClient client) {
                client.Send(new MetadataKeyValue() { Key = key.ToLower(), Value = value });
            }
        }

        public UdpConnection(ISocket Parent, Socket Socket) {
            _Parent = Parent;
            if (Parent is UdpClient) {
                IsServer = false;
            } else {
                IsServer = true;
            }
            _Socket = Socket;
        }

        public UdpConnection(ISocket Parent, Socket Socket, IPEndPoint endPoint) {
            _Parent = Parent;
            if (Parent is UdpClient) {
                IsServer = false;
            } else {
                IsServer = true;
            }
            _Socket = Socket;
            EndPoint = endPoint;
        }
    }

    /// <summary>
    /// Represents a queued UDP send operation.
    /// </summary>
    public class UdpSendItem {
        public byte[] Data { get; set; }
        public IPEndPoint EndPoint { get; set; }

        public UdpSendItem(byte[] data, IPEndPoint endPoint) {
            Data = data;
            EndPoint = endPoint;
        }
    }
}
