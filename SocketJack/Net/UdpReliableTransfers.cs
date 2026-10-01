using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SocketJack.Net {
    public sealed class UdpTransferProgress {
        public uint TransferId { get; internal set; }
        public long BytesTransferred { get; internal set; }
        public long? TotalBytes { get; internal set; }
    }
    public sealed class UdpTransferResult {
        public uint TransferId { get; internal set; }
        public long BytesTransferred { get; internal set; }
        public string Sha256 { get; internal set; }
    }
    /// <summary>Accept synchronously from TransferRequested. Remote names are display metadata, never destination paths.</summary>
    public sealed class UdpTransferRequest {
        public uint TransferId { get; internal set; }
        public string Name { get; internal set; }
        public long? Length { get; internal set; }
        public IPEndPoint RemoteEndpoint { get; internal set; }
        internal Stream Destination;
        internal bool LeaveOpen;
        internal string TemporaryPath, FinalPath;
        internal readonly TaskCompletionSource<UdpTransferResult> Done = new TaskCompletionSource<UdpTransferResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<UdpTransferResult> Completion => Done.Task;
        public void Accept(Stream destination, bool leaveOpen = true) {
            if (Destination != null) throw new InvalidOperationException("Transfer already accepted.");
            if (destination == null || !destination.CanWrite) throw new ArgumentException("A writable destination is required.");
            Destination = destination; LeaveOpen = leaveOpen;
        }
        public void AcceptFile(string destinationPath) {
            if (Destination != null) throw new InvalidOperationException("Transfer already accepted.");
            string final = Path.GetFullPath(destinationPath);
            if (File.Exists(final)) throw new IOException("The destination already exists.");
            string temporary = final + "." + TransferId.ToString("x8") + ".partial";
            var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 32768, true);
            Destination = stream; FinalPath = final; TemporaryPath = temporary; LeaveOpen = false;
        }
    }
    internal sealed class UdpReliableTransfers : IDisposable {
        readonly UdpReliablePeer peer;
        readonly Action<UdpTransferRequest> requested;
        readonly object gate = new object();
        readonly Dictionary<uint, Receiving> receivers = new Dictionary<uint, Receiving>();
        readonly Dictionary<uint, Sending> senders = new Dictionary<uint, Sending>();
        bool disposed;
        uint nextId;
        internal UdpReliableTransfers(UdpReliablePeer peer, Action<UdpTransferRequest> requested) { this.peer = peer; this.requested = requested; nextId = peer.IsServer ? 2U : 1U; }
        static byte[] Frame(byte op, uint id, long offset, byte[] body = null, int count = -1) {
            count = count < 0 ? body?.Length ?? 0 : count;
            using (var output = new MemoryStream(13 + count)) using (var writer = new BinaryWriter(output)) {
                writer.Write(op); writer.Write(id); writer.Write(offset);
                if (count > 0) writer.Write(body, 0, count);
                return output.ToArray();
            }
        }
        Task SendFrame(byte[] bytes) => peer.Send(bytes, 1, true, true, CancellationToken.None);
        async void Reply(byte[] bytes) { try { await SendFrame(bytes).ConfigureAwait(false); } catch { /* Session owner reports transport failures. */ } }
        internal async Task<UdpTransferResult> Send(Stream source, string name, IProgress<UdpTransferProgress> progress, CancellationToken token) {
            if (source == null || !source.CanRead) throw new ArgumentException("A readable source is required.");
            if (Encoding.UTF8.GetByteCount(name ?? "stream") > 1024) throw new ArgumentException("Transfer name exceeds 1024 UTF-8 bytes.");
            uint id; var state = new Sending();
            lock (gate) {
                if (disposed) throw new ObjectDisposedException(nameof(UdpReliableTransfers));
                if (senders.Count >= 16) throw new IOException("Too many concurrent stream transfers.");
                if (nextId > uint.MaxValue - 2) throw new IOException("Transfer identifiers exhausted; reconnect.");
                id = nextId; nextId += 2; senders.Add(id, state);
            }
            long? total = source.CanSeek ? source.Length - source.Position : (long?)null;
            long position = 0;
            try {
                token.ThrowIfCancellationRequested();
                await SendFrame(Frame(1, id, total ?? -1, Encoding.UTF8.GetBytes(name ?? "stream"))).ConfigureAwait(false);
                await Await(state.Accepted.Task, token).ConfigureAwait(false);
                using (var hash = SHA256.Create()) {
                    // Fill whole transport payloads. In a 32 KiB LAN datagram a chunk now fits
                    // INLINE, avoiding a tiny second fragment and an OPEN/GRANT round trip.
                    int frameSize = Math.Min(32768, peer.MaxMessage);
                    int payloadSize = peer.Mtu - UdpReliableTransport.Header;
                    if (frameSize >= payloadSize) frameSize -= frameSize % payloadSize;
                    byte[] chunk = new byte[Math.Max(0, frameSize - 13)];
                    if (chunk.Length < 1) throw new IOException("MaximumBufferSize is too small for stream frames.");
                    while (true) {
                        int read = await source.ReadAsync(chunk, 0, chunk.Length, token).ConfigureAwait(false);
                        if (read == 0) break;
                        hash.TransformBlock(chunk, 0, read, null, 0);
                        await SendFrame(Frame(2, id, position, chunk, read)).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        position = checked(position + read);
                        progress?.Report(new UdpTransferProgress { TransferId = id, BytesTransferred = position, TotalBytes = total });
                        if (state.Done.Task.IsFaulted) await state.Done.Task.ConfigureAwait(false);
                    }
                    hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    await SendFrame(Frame(5, id, position, hash.Hash)).ConfigureAwait(false);
                    var result = await Await(state.Done.Task, token).ConfigureAwait(false);
                    if (result.BytesTransferred != position || result.Sha256 != Hex(hash.Hash)) throw new IOException("Remote transfer verification does not match.");
                    return result;
                }
            } catch {
                Reply(Frame(7, id, position)); throw;
            } finally { lock (gate) senders.Remove(id); }
        }
        static async Task<T> Await<T>(Task<T> task, CancellationToken token) {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token)) {
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                var canceled = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
                using (timeout.Token.Register(() => canceled.TrySetCanceled(timeout.Token)))
                    return await await Task.WhenAny(task, canceled.Task).ConfigureAwait(false);
            }
        }
        internal void Receive(byte[] frame) {
            if (frame.Length < 13) throw new InvalidDataException("Truncated stream frame.");
            using (var input = new MemoryStream(frame, false)) using (var reader = new BinaryReader(input)) {
                byte op = reader.ReadByte(); var id = reader.ReadUInt32(); long offset = reader.ReadInt64();
                lock (gate) {
                    if (disposed) return;
                    if (op == 1) {
                        if (frame.Length > 1037 || offset < -1 || receivers.Count >= 16 || receivers.ContainsKey(id)) { Reply(Frame(4, id, 0)); return; }
                        var request = new UdpTransferRequest { TransferId = id, Name = Encoding.UTF8.GetString(frame, 13, frame.Length - 13), Length = offset < 0 ? (long?)null : offset, RemoteEndpoint = peer.Endpoint };
                        try {
                            requested(request);
                            if (request.Destination == null) { Reply(Frame(4, id, 0)); request.Done.TrySetCanceled(); return; }
                            receivers.Add(id, new Receiving { Request = request, Hash = SHA256.Create() });
                            Reply(Frame(3, id, 0));
                        } catch (Exception ex) { Cleanup(request, ex); Reply(Frame(4, id, 0)); }
                        return;
                    }
                    if (senders.TryGetValue(id, out var sender)) {
                        if (op == 3 && frame.Length == 13) { sender.Accepted.TrySetResult(true); return; }
                        if (op == 4 || op == 7) { var error = new IOException("Remote peer rejected or aborted the stream transfer."); sender.Accepted.TrySetException(error); sender.Done.TrySetException(error); return; }
                        if (op == 6 && frame.Length == 45) { sender.Done.TrySetResult(new UdpTransferResult { TransferId = id, BytesTransferred = offset, Sha256 = Hex(reader.ReadBytes(32)) }); return; }
                    }
                    if (!receivers.TryGetValue(id, out var target)) return;
                    target.LastActivity = UdpReliableTransport.Now;
                    try {
                        if (op == 7) throw new OperationCanceledException("Remote stream transfer canceled.");
                        if (offset != target.Position) throw new InvalidDataException("Stream offset mismatch.");
                        if (op == 2) {
                            int count = frame.Length - 13;
                            if (target.Request.Length.HasValue && target.Position + count > target.Request.Length.Value) throw new InvalidDataException("Stream exceeds its declared length.");
                            target.Request.Destination.Write(frame, 13, count);
                            target.Hash.TransformBlock(frame, 13, count, null, 0); target.Position = checked(target.Position + count);
                        } else if (op == 5 && frame.Length == 45) {
                            target.Hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                            var digest = reader.ReadBytes(32);
                            if (Hex(digest) != Hex(target.Hash.Hash) || (target.Request.Length.HasValue && target.Position != target.Request.Length.Value)) throw new InvalidDataException("Stream SHA-256 or length mismatch.");
                            target.Request.Destination.Flush();
                            if (!target.Request.LeaveOpen) target.Request.Destination.Dispose();
                            if (target.Request.FinalPath != null) File.Move(target.Request.TemporaryPath, target.Request.FinalPath);
                            target.Request.Done.TrySetResult(new UdpTransferResult { TransferId = id, BytesTransferred = target.Position, Sha256 = Hex(digest) });
                            receivers.Remove(id); target.Hash.Dispose(); Reply(Frame(6, id, target.Position, digest));
                        } else throw new InvalidDataException("Unknown stream opcode.");
                    } catch (Exception ex) { receivers.Remove(id); target.Hash.Dispose(); Cleanup(target.Request, ex); Reply(Frame(7, id, target.Position)); }
                }
            }
        }
        static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        internal void Expire(double now) {
            // Disk/application work must never stall the transport scheduler.
            if (!Monitor.TryEnter(gate)) return;
            try {
                foreach (var pair in receivers.Where(p => now - p.Value.LastActivity > 30000).ToArray()) {
                    receivers.Remove(pair.Key); pair.Value.Hash.Dispose();
                    Cleanup(pair.Value.Request, new TimeoutException("Stream transfer made no progress for 30 seconds."));
                    Reply(Frame(7, pair.Key, pair.Value.Position));
                }
            } finally { Monitor.Exit(gate); }
        }
        static void Cleanup(UdpTransferRequest request, Exception error) {
            try { if (!request.LeaveOpen) request.Destination?.Dispose(); } catch { }
            try { if (request.TemporaryPath != null) File.Delete(request.TemporaryPath); } catch { }
            request.Done.TrySetException(error);
        }
        public void Dispose() {
            lock (gate) {
                if (disposed) return; disposed = true;
                var error = new IOException("UDP_Reliable session ended during transfer.");
                foreach (var state in senders.Values) { state.Accepted.TrySetException(error); state.Done.TrySetException(error); }
                foreach (var target in receivers.Values) { target.Hash.Dispose(); Cleanup(target.Request, error); }
                receivers.Clear(); senders.Clear();
            }
        }
        sealed class Sending {
            internal readonly TaskCompletionSource<bool> Accepted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly TaskCompletionSource<UdpTransferResult> Done = new TaskCompletionSource<UdpTransferResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        sealed class Receiving { internal UdpTransferRequest Request; internal SHA256 Hash; internal long Position; internal double LastActivity = UdpReliableTransport.Now; }
    }
}
