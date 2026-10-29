using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SocketJack.Net {
    public enum HttpTransferState { Uploading, Paused, Canceled, Stalled, Failed, Completed, Expired }
    public enum HttpOverwritePolicy { Ask, Always, Never }
    public sealed class HttpTransferConflictException : IOException {
        public string Generation { get; }
        public HttpTransferConflictException(string generation) : base("The destination exists. Confirm replacement of this generation.") { Generation = generation; }
    }
    public sealed class HttpTransferOptions {
        public string Directory { get; set; }
        public int MaxChunkBytes { get; set; } = 1024 * 1024;
        public TimeSpan Retention { get; set; } = TimeSpan.FromHours(1);
        public TimeSpan StallAfter { get; set; } = TimeSpan.FromSeconds(30);
        public Func<DateTimeOffset> UtcNow { get; set; } = () => DateTimeOffset.UtcNow;
        public Func<NetworkConnection, HttpRequest, string> Authorize { get; set; }
        // Called under the store lock, before durable target replacement. Throw to reject.
        public Action<string, long> Reserve { get; set; }
        public Action<string, long> Release { get; set; }
        public Action<HttpTransferSession, string> Commit { get; set; }
        public Func<string, string, string> ExistingGeneration { get; set; }
        public Func<HttpTransferSession, string> ContentType { get; set; }
    }
    public sealed class HttpTransferSession {
        public string Id { get; set; }
        public string Owner { get; set; }
        public string Target { get; set; }
        public string Sha256 { get; set; }
        public long Length { get; set; }
        public long Offset { get; set; }
        public string Generation { get; set; }
        public HttpTransferState State { get; set; }
        public DateTimeOffset LastProgressUtc { get; set; }
        public DateTimeOffset CreatedUtc { get; set; }
        public long CreatedSequence { get; set; }
        public string Error { get; set; }
        public string ReplacesGeneration { get; set; }
        public string ETag => "\"" + Generation + "\"";
    }
    public sealed class HttpTransferCreateRequest {
        public string Target { get; set; }
        public string Sha256 { get; set; }
        public long Length { get; set; }
        public HttpOverwritePolicy Overwrite { get; set; } = HttpOverwritePolicy.Ask;
        public string ConfirmGeneration { get; set; }
    }
    /// <summary>Durable, owner-scoped, sequential uploads with immutable download generations.</summary>
    public sealed class HttpTransferStore : IDisposable {
        readonly HttpTransferOptions options;
        readonly object gate = new object();
        readonly Dictionary<string, HttpTransferSession> sessions = new Dictionary<string, HttpTransferSession>(StringComparer.Ordinal);
        readonly Dictionary<string, string> targets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly Timer cleanup;
        long sequence;
        public HttpTransferStore(HttpTransferOptions options) {
            this.options = options ?? throw new ArgumentNullException(nameof(options));
            if (string.IsNullOrWhiteSpace(options.Directory) || options.MaxChunkBytes < 1 || options.Retention <= TimeSpan.Zero) throw new ArgumentException("Transfer directory, chunk size and retention are required.");
            options.Directory = Path.GetFullPath(options.Directory);
            Directory.CreateDirectory(options.Directory);
            foreach (string file in Directory.EnumerateFiles(options.Directory, "*.json")) {
                var session = JsonSerializer.Deserialize<HttpTransferSession>(File.ReadAllText(file));
                ValidateId(session.Id);
                if (Path.GetFileNameWithoutExtension(file) != session.Id) throw new InvalidDataException("Transfer identity mismatch.");
                if (session.State != HttpTransferState.Completed && session.State != HttpTransferState.Expired && options.UtcNow() - session.LastProgressUtc >= options.Retention) {
                    session.State = HttpTransferState.Expired; Save(session);
                    if (File.Exists(DataPath(session.Id))) File.Delete(DataPath(session.Id));
                }
                if (session.State != HttpTransferState.Expired) {
                    if (!File.Exists(DataPath(session.Id)) || new FileInfo(DataPath(session.Id)).Length < session.Offset) throw new InvalidDataException("Transfer payload is missing or truncated.");
                    using (var stream = new FileStream(DataPath(session.Id), FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) stream.SetLength(session.Offset);
                    if (session.State == HttpTransferState.Uploading) session.State = HttpTransferState.Stalled;
                }
                sessions.Add(session.Id, session);
            }
            foreach (var session in sessions.Values.OrderBy(s => s.CreatedSequence).ThenBy(s => s.CreatedUtc)) { targets[Key(session.Owner, session.Target)] = session.Id; sequence = Math.Max(sequence, session.CreatedSequence); }
            foreach (var session in sessions.Values.Where(s => s.State != HttpTransferState.Completed && s.State != HttpTransferState.Expired)) options.Reserve?.Invoke(session.Owner, session.Length);
            Sweep();
            cleanup = new Timer(_ => { try { Sweep(); } catch { /* Persisted state remains available for host diagnostics. */ } }, null, 15000, 15000);
        }
        string Key(string owner, string target) => owner + "\n" + target;
        string DataPath(string id) => Path.Combine(options.Directory, id + ".part");
        string MetadataPath(string id) => Path.Combine(options.Directory, id + ".json");
        static void ValidateId(string id) { if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Invalid transfer identity."); }
        static void ValidateOwner(string owner) { if (string.IsNullOrWhiteSpace(owner) || owner.Contains("\n")) throw new UnauthorizedAccessException(); }
        static void ValidateTarget(string target) { if (string.IsNullOrWhiteSpace(target) || target.Length > 240 || target.IndexOfAny(new[] { '/', '\\', '\r', '\n', '\0', ':' }) >= 0 || target == "." || target == "..") throw new ArgumentException("A file name without a path is required."); }
        static bool ValidHash(string hash) => hash != null && hash.Length == 64 && hash.All(c => Uri.IsHexDigit(c));
        static string Hash(byte[] bytes) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        static HttpTransferSession Copy(HttpTransferSession s) => JsonSerializer.Deserialize<HttpTransferSession>(JsonSerializer.Serialize(s));
        void Save(HttpTransferSession session) {
            string path = MetadataPath(session.Id), temp = path + ".tmp";
            using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None)) {
                var bytes = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(session)); file.Write(bytes, 0, bytes.Length); file.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
        }
        HttpTransferSession Owned(string owner, string id) { ValidateOwner(owner); ValidateId(id); if (!sessions.TryGetValue(id, out var session) || !string.Equals(owner, session.Owner, StringComparison.OrdinalIgnoreCase)) throw new FileNotFoundException("Transfer not found."); return session; }
        void Active(HttpTransferSession s) {
            if (s.State == HttpTransferState.Expired) throw new InvalidOperationException("Transfer expired. Start a new upload.");
            if (s.State == HttpTransferState.Failed) throw new InvalidOperationException(s.Error ?? "Transfer failed.");
        }
        public HttpTransferSession Create(string owner, HttpTransferCreateRequest request) {
            if (request == null) throw new ArgumentNullException(nameof(request));
            ValidateOwner(owner); ValidateTarget(request.Target);
            if (!ValidHash(request.Sha256) || request.Length < 0 || !Enum.IsDefined(typeof(HttpOverwritePolicy), request.Overwrite)) throw new ArgumentException("A valid length, SHA-256 and overwrite policy are required.");
            lock (gate) {
                SweepCore();
                string key = Key(owner, request.Target), prior = null;
                if (targets.TryGetValue(key, out string existingId)) {
                    var existing = sessions[existingId];
                    if (existing.State != HttpTransferState.Expired && existing.State != HttpTransferState.Failed && existing.Length == request.Length && string.Equals(existing.Sha256, request.Sha256, StringComparison.OrdinalIgnoreCase)) {
                        if (existing.State != HttpTransferState.Completed) { existing.State = HttpTransferState.Uploading; Save(existing); }
                        return Copy(existing);
                    }
                    if (existing.State != HttpTransferState.Expired) prior = existing.Generation;
                } else prior = options.ExistingGeneration?.Invoke(owner, request.Target);
                if (prior != null && request.Overwrite != HttpOverwritePolicy.Always && !(request.Overwrite == HttpOverwritePolicy.Ask && request.ConfirmGeneration == prior)) {
                    if (request.Overwrite == HttpOverwritePolicy.Never) throw new IOException("Overwrite policy is Never. The destination already exists.");
                    throw new HttpTransferConflictException(prior);
                }
                options.Reserve?.Invoke(owner, request.Length);
                var session = new HttpTransferSession { Id = Guid.NewGuid().ToString("N"), Generation = Guid.NewGuid().ToString("N"), Owner = owner, Target = request.Target, Length = request.Length, Sha256 = request.Sha256.ToLowerInvariant(), State = HttpTransferState.Uploading, LastProgressUtc = options.UtcNow(), CreatedUtc = options.UtcNow(), ReplacesGeneration = prior };
                session.CreatedSequence = ++sequence;
                try { using (File.Create(DataPath(session.Id))) { } Save(session); sessions.Add(session.Id, session); targets[key] = session.Id; }
                catch { options.Release?.Invoke(owner, request.Length); throw; }
                return Copy(session);
            }
        }
        public HttpTransferSession Get(string owner, string id) { lock (gate) { SweepCore(); return Copy(Owned(owner, id)); } }
        public HttpTransferSession Find(string owner, string target) { lock (gate) { SweepCore(); return targets.TryGetValue(Key(owner, target), out string id) ? Copy(sessions[id]) : null; } }
        public HttpTransferSession Append(string owner, string id, long offset, byte[] bytes, string sha256) {
            if (bytes == null || bytes.Length == 0 || bytes.Length > options.MaxChunkBytes || !ValidHash(sha256) || !string.Equals(Hash(bytes), sha256, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Chunk length or SHA-256 is invalid.");
            lock (gate) {
                SweepCore(); var s = Owned(owner, id); Active(s);
                if (offset < 0 || offset > s.Length || bytes.LongLength > s.Length - offset || offset > s.Offset) throw new InvalidOperationException("Chunk offset does not match durable progress.");
                using (var file = new FileStream(DataPath(id), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete)) {
                    file.Position = offset;
                    if (offset < s.Offset) {
                        if (offset + bytes.Length > s.Offset) throw new InvalidOperationException("Chunk overlaps durable progress.");
                        var prior = new byte[bytes.Length]; int read = 0; while (read < prior.Length) { int n = file.Read(prior, read, prior.Length - read); if (n == 0) throw new EndOfStreamException(); read += n; }
                        if (!prior.SequenceEqual(bytes)) throw new InvalidOperationException("Retried chunk differs from committed bytes.");
                        return Copy(s);
                    }
                    if (s.State == HttpTransferState.Completed) throw new InvalidOperationException("Completed transfers are immutable.");
                    file.Write(bytes, 0, bytes.Length); file.Flush(true);
                }
                s.Offset += bytes.Length; s.State = HttpTransferState.Uploading; s.LastProgressUtc = options.UtcNow(); Save(s); return Copy(s);
            }
        }
        public HttpTransferSession Complete(string owner, string id) {
            lock (gate) {
                SweepCore(); var s = Owned(owner, id); Active(s); if (s.State == HttpTransferState.Completed) return Copy(s);
                if (s.Offset != s.Length) throw new InvalidOperationException("Upload has missing bytes.");
                string actual; using (var sha = SHA256.Create()) using (var input = File.OpenRead(DataPath(id))) actual = BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").ToLowerInvariant();
                if (actual != s.Sha256) { s.State = HttpTransferState.Failed; s.Error = "File SHA-256 mismatch."; Save(s); throw new InvalidDataException(s.Error); }
                options.Commit?.Invoke(Copy(s), DataPath(id));
                s.State = HttpTransferState.Completed; s.LastProgressUtc = options.UtcNow(); Save(s); options.Release?.Invoke(owner, s.Length); return Copy(s);
            }
        }
        public HttpTransferSession Cancel(string owner, string id, bool paused = false) { lock (gate) { var s = Owned(owner, id); Active(s); if (s.State != HttpTransferState.Completed) { s.State = paused ? HttpTransferState.Paused : HttpTransferState.Canceled; Save(s); } return Copy(s); } }
        /// <summary>
        /// Revokes every retained generation of an owner's target, including completed
        /// transfers. Call this before deleting the application's canonical file.
        /// Revocation is persisted before payload removal, and a subsequent upload
        /// creates a fresh generation instead of replaying a deleted completion.
        /// </summary>
        /// <returns>The number of newly revoked generations.</returns>
        public int RevokeTarget(string owner, string target) {
            ValidateOwner(owner); ValidateTarget(target);
            lock (gate) {
                int revoked = 0;
                foreach (var session in sessions.Values.Where(s => string.Equals(s.Owner, owner, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(s.Target, target, StringComparison.OrdinalIgnoreCase)).ToArray()) {
                    var previousState = session.State; var previousError = session.Error;
                    if (previousState != HttpTransferState.Expired) {
                        session.State = HttpTransferState.Expired; session.Error = "Transfer revoked.";
                        try { Save(session); }
                        catch { session.State = previousState; session.Error = previousError; throw; }
                        string key = Key(session.Owner, session.Target);
                        if (targets.TryGetValue(key, out string current) && current == session.Id) targets.Remove(key);
                        if (previousState != HttpTransferState.Completed) options.Release?.Invoke(session.Owner, session.Length);
                        revoked++;
                    }
                    // Retry payload cleanup even if a previous call persisted the
                    // revocation but could not remove a temporarily locked file.
                    if (File.Exists(DataPath(session.Id))) File.Delete(DataPath(session.Id));
                }
                return revoked;
            }
        }
        public void Sweep() { lock (gate) SweepCore(); }
        void SweepCore() {
            foreach (var s in sessions.Values) {
                if (s.State == HttpTransferState.Completed || s.State == HttpTransferState.Expired) continue;
                var idle = options.UtcNow() - s.LastProgressUtc;
                if (idle >= options.Retention) { s.State = HttpTransferState.Expired; Save(s); if (File.Exists(DataPath(s.Id))) File.Delete(DataPath(s.Id)); options.Release?.Invoke(s.Owner, s.Length); }
                else if (s.State == HttpTransferState.Uploading && idle >= options.StallAfter) { s.State = HttpTransferState.Stalled; Save(s); }
            }
        }
        /// <summary>Streams a pinned generation, waiting for newly committed prefixes. The last byte waits for final verification.</summary>
        public async Task CopyToAsync(string owner, string id, Stream output, long start = 0, long? end = null, CancellationToken cancellationToken = default) {
            var initial = Get(owner, id); if (start < 0 || start > initial.Length || (end.HasValue && (end < start || end >= initial.Length))) throw new ArgumentOutOfRangeException(nameof(start));
            long limit = end.HasValue ? end.Value + 1 : initial.Length, position = start;
            var buffer = new byte[65536];
            while (position < limit || initial.Length == 0) {
                cancellationToken.ThrowIfCancellationRequested();
                HttpTransferSession s = Get(owner, id); Active(s);
                long available = s.State == HttpTransferState.Completed ? s.Offset : Math.Min(s.Offset, Math.Max(0, s.Length - 1));
                int count = (int)Math.Min(buffer.Length, Math.Min(limit, available) - position);
                if (count > 0) {
                    using (var input = new FileStream(DataPath(id), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) { input.Position = position; count = await input.ReadAsync(buffer, 0, count, cancellationToken).ConfigureAwait(false); }
                    if (count == 0) throw new EndOfStreamException();
                    await output.WriteAsync(buffer, 0, count, cancellationToken).ConfigureAwait(false); await output.FlushAsync(cancellationToken).ConfigureAwait(false); position += count;
                    lock (gate) { var live = Owned(owner, id); if (live.State != HttpTransferState.Completed) { live.LastProgressUtc = options.UtcNow(); Save(live); } }
                } else if (s.State == HttpTransferState.Completed) break;
                else if (s.State == HttpTransferState.Canceled || s.State == HttpTransferState.Paused) throw new IOException("Upload is " + s.State + "; resume this generation to continue.");
                else await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }
        public void Dispose() => cleanup.Dispose();
    }
}
