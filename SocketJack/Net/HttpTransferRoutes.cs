using System;
using System.IO;
using System.Linq;
using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SocketJack.Net {
    public partial class HttpServer {
        readonly ConcurrentDictionary<string, long> _transferBodyLimits = new ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        long TransferBodyLimit(string method, string path) => _transferBodyLimits.TryGetValue(method + " " + path.Split('?')[0], out long limit) ? limit : long.MaxValue;
        /// <summary>Maps authenticated resumable transfer operations under a caller-selected prefix.</summary>
        public HttpTransferStore MapTransfers(string prefix, HttpTransferOptions options) {
            if (options.Authorize == null) throw new ArgumentException("An owner authorization callback is required.");
            prefix = "/" + prefix.Trim('/'); var store = new HttpTransferStore(options);
            _transferBodyLimits["POST " + prefix + "/start"] = 65536;
            _transferBodyLimits["POST " + prefix + "/append"] = options.MaxChunkBytes;
            _transferBodyLimits["POST " + prefix + "/complete"] = 1024;
            _transferBodyLimits["POST " + prefix + "/cancel"] = 1024;
            string Owner(NetworkConnection c, HttpRequest r) { string owner = options.Authorize(c, r); if (string.IsNullOrWhiteSpace(owner)) throw new UnauthorizedAccessException(); return owner; }
            string Query(HttpRequest r, string name) { foreach (string part in (r.QueryString ?? "").TrimStart('?').Split('&')) { int eq = part.IndexOf('='); if (eq > 0 && Uri.UnescapeDataString(part.Substring(0, eq)) == name) return Uri.UnescapeDataString(part.Substring(eq + 1)); } return null; }
            object Run(HttpRequest r, Func<object> body) {
                try { return JsonSerializer.Serialize(body()); }
                catch (HttpTransferConflictException ex) { r.Context.StatusCodeNumber = 409; return JsonSerializer.Serialize(new { error = ex.Message, generation = ex.Generation }); }
                catch (Exception ex) when (ex is ArgumentException || ex is IOException || ex is InvalidOperationException || ex is UnauthorizedAccessException || ex is JsonException || ex is FormatException) {
                    r.Context.StatusCodeNumber = ex is UnauthorizedAccessException ? 401 : ex is FileNotFoundException ? 404 : ex is InvalidOperationException ? 409 : 400;
                    return JsonSerializer.Serialize(new { error = ex.Message });
                }
            }
            Map("POST", prefix + "/start", (c, r, ct) => Run(r, () => store.Create(Owner(c, r), JsonSerializer.Deserialize<HttpTransferCreateRequest>(r.BodyBytes ?? Array.Empty<byte>(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }))));
            Map("GET", prefix + "/status", (c, r, ct) => Run(r, () => store.Get(Owner(c, r), Query(r, "id"))));
            Map("POST", prefix + "/append", (c, r, ct) => Run(r, () => store.Append(Owner(c, r), Query(r, "id"), long.Parse(Query(r, "offset")), r.BodyBytes, Query(r, "sha256"))));
            Map("POST", prefix + "/complete", (c, r, ct) => Run(r, () => store.Complete(Owner(c, r), Query(r, "id"))));
            Map("POST", prefix + "/cancel", (c, r, ct) => Run(r, () => store.Cancel(Owner(c, r), Query(r, "id"), Query(r, "pause") == "1")));
            MapStream("GET", prefix + "/download", async (c, r, output, ct) => {
                bool started = false;
                try {
                    string owner = Owner(c, r); var session = store.Get(owner, Query(r, "id"));
                    long start = 0, end = session.Length - 1;
                    bool range = r.Headers != null && r.Headers.TryGetValue("Range", out string value) && (!r.Headers.TryGetValue("If-Range", out string tag) || tag == session.ETag);
                    if (range) {
                        value = r.Headers["Range"];
                        if (!TryParseSingleByteRange(value, session.Length, out start, out end)) { r.Context.StatusCodeNumber = 416; output.SetHeader("Content-Range", "bytes */" + session.Length); output.SetHeader("Content-Length", "0"); return; }
                        r.Context.StatusCodeNumber = 206; r.Context.ReasonPhrase = "Partial Content";
                        output.SetHeader("Content-Range", $"bytes {start}-{end}/{session.Length}");
                    }
                    output.ContentType = options.ContentType?.Invoke(session) ?? "application/octet-stream"; output.LowLatencyMode = true;
                    output.SetHeader("Content-Length", Math.Max(0, end - start + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
                    output.SetHeader("ETag", session.ETag); output.SetHeader("Accept-Ranges", "bytes");
                    output.SetHeader("X-Content-SHA256", session.Sha256);
                    output.SetHeader("Content-Disposition", "attachment; filename*=UTF-8''" + Uri.EscapeDataString(session.Target));
                    output.Write(Array.Empty<byte>(), 0, 0); started = true;
                    using (var stream = new TransferOutputStream(output)) await store.CopyToAsync(owner, session.Id, stream, start, session.Length == 0 ? (long?)null : end, ct).ConfigureAwait(false);
                } catch (Exception ex) when (!started && (ex is UnauthorizedAccessException || ex is FileNotFoundException || ex is ArgumentException)) {
                    r.Context.StatusCodeNumber = ex is UnauthorizedAccessException ? 401 : ex is FileNotFoundException ? 404 : 400;
                    var bytes = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { error = ex.Message }));
                    output.ContentType = "application/json"; output.SetHeader("Content-Length", bytes.Length.ToString()); output.Write(bytes, 0, bytes.Length);
                } catch { output.Abort(); throw; }
            });
            return store;
        }
        sealed class TransferOutputStream : Stream {
            readonly ChunkedStream output;
            public TransferOutputStream(ChunkedStream output) { this.output = output; }
            public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override void Write(byte[] b, int o, int n) => output.Write(b, o, n);
            public override int Read(byte[] b, int o, int n) => throw new NotSupportedException();
            public override long Seek(long p, SeekOrigin s) => throw new NotSupportedException(); public override void SetLength(long n) => throw new NotSupportedException();
        }
    }
}
