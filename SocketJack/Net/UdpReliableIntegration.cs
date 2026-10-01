using SocketJack.Serialization;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SocketJack.Net {
    internal static class UdpReliableObjects {
        internal static byte[] Decompress(byte[] data, NetworkOptions options) {
            using (var input = new MemoryStream(data, false)) {
                Stream decoder;
                if (options.CompressionAlgorithm is SocketJack.Compression.GZip2Compression)
                    decoder = new System.IO.Compression.GZipStream(input, System.IO.Compression.CompressionMode.Decompress);
                else if (options.CompressionAlgorithm is SocketJack.Compression.DeflateCompression)
                    decoder = new System.IO.Compression.DeflateStream(input, System.IO.Compression.CompressionMode.Decompress);
                else throw new NotSupportedException("UDP_Reliable requires GZip2 or Deflate for bounded decompression.");
                using (decoder) using (var output = new MemoryStream()) {
                    byte[] buffer = new byte[8192]; int read;
                    while ((read = decoder.Read(buffer, 0, buffer.Length)) > 0) {
                        if (output.Length + read > options.MaximumBufferSize) throw new InvalidDataException("Decompressed object exceeds MaximumBufferSize.");
                        output.Write(buffer, 0, read);
                    }
                    return output.ToArray();
                }
            }
        }
        internal static Task Send(UdpReliablePeer peer, UdpConnection connection, ISocket parent, object value, bool wait, CancellationToken token) {
            if (peer == null || peer.IsClosed) return Task.FromException(new IOException("UDP_Reliable is not connected."));
            byte[] serialized = null;
            bool independent = !parent.Options.UdpReliable.Ordering;
            // Identity, metadata and routing updates always use the ordered lane.
            if (value != null && value.GetType().Namespace == "SocketJack.Net.P2P") independent = false;
            return peer.SendCore(encodedLimit => {
                byte[] bytes = serialized;
                if (parent.Options.UdpReliable.Ordering)
                    bytes = connection.PatternCache.PrepareSend(bytes, parent.Options, candidate => {
                        if (candidate.Length > parent.Options.MaximumBufferSize || candidate.Length > serialized.Length + 64) return false;
                        int encodedLength = parent.Options.UseCompression ? parent.Options.CompressionAlgorithm.Compress(candidate).Length : candidate.Length;
                        return encodedLength <= encodedLimit;
                    });
                return parent.Options.UseCompression ? parent.Options.CompressionAlgorithm.Compress(bytes) : bytes;
            }, 0, 0, independent, wait, token, () => {
                serialized = parent.Options.Serializer.Serialize(new Wrapper(value, parent));
                return serialized.Length;
            });
        }
    }
    public partial class UdpClient {
        internal UdpReliableTransport ReliableTransport;
        internal UdpReliablePeer ReliablePeer;
        public UdpReliableStatistics ReliableStatistics => ReliablePeer?.Statistics;
        public event Action<UdpTransferRequest> TransferRequested;
        public Task SendAsync(object value, CancellationToken cancellationToken = default) {
            if (Options.UdpMode != UdpMode.UDP_Reliable) throw new InvalidOperationException("SendAsync acknowledgments require UDP_Reliable.");
            return SendReliable(value, true, cancellationToken);
        }
        Task SendReliable(object value, bool wait, CancellationToken token) => UdpReliableObjects.Send(ReliablePeer, UdpConn, this, value, wait, token);
        async void ObserveReliable(Task task) { try { await task.ConfigureAwait(false); } catch (Exception ex) { InvokeOnError(Connection, ex); } }
        void ReliableReceive(UdpReliablePeer peer, byte[] data, byte kind) {
            if (ReceiveSafeModeReply(data)) return;
            if (kind == 1) { peer.Transfers?.Receive(data); return; }
            DeserializeAndDispatch(data, data.Length, peer.Endpoint);
        }
        void ReliableFailed(UdpReliablePeer peer, Exception error) {
            if (ReferenceEquals(ReliablePeer, peer)) { InvokeOnError(Connection, error); Disconnect(); }
        }
        public Task<UdpTransferResult> SendStreamAsync(Stream source, string name = "stream", IProgress<UdpTransferProgress> progress = null, CancellationToken cancellationToken = default)
            => RequireTransfers().Send(source, name, progress, cancellationToken);
        public async Task<UdpTransferResult> SendFileAsync(string path, IProgress<UdpTransferProgress> progress = null, CancellationToken cancellationToken = default) {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 32768, true))
                return await SendStreamAsync(stream, Path.GetFileName(path), progress, cancellationToken).ConfigureAwait(false);
        }
        UdpReliableTransfers RequireTransfers() => ReliablePeer?.Transfers ?? throw new InvalidOperationException("Connect with UDP_Reliable first.");
    }
    public partial class UdpServer {
        internal UdpReliableTransport ReliableTransport;
        public event Action<UdpTransferRequest> TransferRequested;
        public Task SendToAsync(UdpConnection client, object value, CancellationToken cancellationToken = default) {
            if (Options.UdpMode != UdpMode.UDP_Reliable) throw new InvalidOperationException("SendToAsync acknowledgments require UDP_Reliable.");
            return SendReliable(client, value, true, cancellationToken);
        }
        Task SendReliable(UdpConnection client, object value, bool wait, CancellationToken token) => UdpReliableObjects.Send(client?.ReliablePeer, client, this, value, wait, token);
        async void ObserveReliable(Task task) { try { await task.ConfigureAwait(false); } catch (Exception ex) { InvokeOnError(Connection, ex); } }
        void ReliableConnected(UdpReliablePeer peer) {
            if (Clients.TryGetValue(peer.Endpoint.ToString(), out var previous) && !ReferenceEquals(previous.ReliablePeer, peer))
                RemoveClient(peer.Endpoint.ToString());
            peer.Transfers = new UdpReliableTransfers(peer, request => TransferRequested?.Invoke(request));
            if (!Options.SafeMode) GetOrCreateClient(peer.Endpoint, peer);
            else _ = Task.Run(async () => {
                await Task.Delay(5000);
                if (!peer.SafeModeVerified) peer.Fail(SafeModeHandshake.Rejected());
            });
        }
        void ReliableReceive(UdpReliablePeer peer, byte[] data, byte kind) {
            if (Options.SafeMode && (!peer.SafeModeVerified || SafeModeHandshake.HasMagic(data))) {
                try {
                    if (kind != 0) throw SafeModeHandshake.Rejected();
                    SafeModeHandshake.Validate(data, Options, null);
                    peer.SafeModeVerified = true;
                    ObserveReliable(peer.Send(SafeModeHandshake.Accepted, 0, false, false, CancellationToken.None));
                    GetOrCreateClient(peer.Endpoint, peer);
                } catch { peer.Fail(SafeModeHandshake.Rejected()); }
                return;
            }
            _clientLastActivity[peer.Endpoint.ToString()] = DateTime.UtcNow;
            if (kind == 1) {
                try {
                    _clientNetworkConnections.TryGetValue(peer.Endpoint.ToString(), out var connection);
                    InboundMessageSecurity.RequireType(this, connection, typeof(UdpTransferRequest));
                    peer.Transfers.Receive(data);
                } catch { peer.Fail(InboundMessageSecurity.Denied()); }
                return;
            }
            DeserializeAndDispatch(data, data.Length, peer.Endpoint);
        }
        void ReliableFailed(UdpReliablePeer peer, Exception error) {
            if (Clients.TryGetValue(peer.Endpoint.ToString(), out var client) && ReferenceEquals(client.ReliablePeer, peer)) {
                InvokeOnError(Connection, error); RemoveClient(peer.Endpoint.ToString());
            }
        }
        public override void SendBroadcast(NetworkConnection[] clients, object value, NetworkConnection except = null) {
            foreach (var connection in clients ?? Array.Empty<NetworkConnection>())
                if (connection != null && connection != except) Send(connection.ID.ToString(), value);
        }
        public Task<UdpTransferResult> SendStreamAsync(UdpConnection client, Stream source, string name = "stream", IProgress<UdpTransferProgress> progress = null, CancellationToken cancellationToken = default)
            => (client?.ReliablePeer?.Transfers ?? throw new InvalidOperationException("Client must use UDP_Reliable.")).Send(source, name, progress, cancellationToken);
        public async Task<UdpTransferResult> SendFileAsync(UdpConnection client, string path, IProgress<UdpTransferProgress> progress = null, CancellationToken cancellationToken = default) {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 32768, true))
                return await SendStreamAsync(client, stream, Path.GetFileName(path), progress, cancellationToken).ConfigureAwait(false);
        }
    }
}
