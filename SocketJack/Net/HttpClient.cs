using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Security;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SocketJack.Extensions;
using SocketJack.Serialization;

namespace SocketJack.Net {

    /// <summary>
    /// Minimal HTTP/HTTPS client supporting headers, content-length, chunked transfer, and redirects.
    /// </summary>
    public class HttpClient : TcpClient, IDisposable {

        // Callback storage (mirrors TcpBase functionality for registering type callbacks)
        private new Dictionary<Type, List<Action<IReceivedEventArgs>>> TypeCallbacks = new();
        private Dictionary<Delegate, Action<IReceivedEventArgs>> CallbackMap = new();
        private readonly object _callbackLock = new object();

        new public void RegisterCallback<T>(Action<ReceivedEventArgs<T>> Action) {
            var Type = typeof(T);
            Options.Whitelist.Add(Type);
            Action<IReceivedEventArgs> wrapped = e => Action((ReceivedEventArgs<T>)e);
            lock (_callbackLock) {
                if (TypeCallbacks.ContainsKey(Type)) {
                    TypeCallbacks[Type].Add(wrapped);
                } else {
                    TypeCallbacks[Type] = new List<Action<IReceivedEventArgs>>() { wrapped };
                }
                CallbackMap[Action] = wrapped;
            }
            // Ensure serializer whitelist updated globally
            if (!Options.Whitelist.Contains(Type)) Options.Whitelist.Add(Type);
        }

        new public void RemoveCallback<T>(Action<ReceivedEventArgs<T>> Action) {
            var Type = typeof(T);
            lock (_callbackLock) {
                if (CallbackMap.TryGetValue(Action, out var wrapped)) {
                    if (TypeCallbacks.ContainsKey(Type)) {
                        TypeCallbacks[Type].Remove(wrapped);
                        if (TypeCallbacks[Type].Count == 0) {
                            TypeCallbacks.Remove(Type);
                            if (Options.Whitelist.Contains(Type)) Options.Whitelist.Remove(Type);
                        }
                    }
                    CallbackMap.Remove(Action);
                }
            }
        }

        new public void RemoveCallback<T>() {
            var Type = typeof(T);
            lock (_callbackLock) {
                if (TypeCallbacks.ContainsKey(Type)) {
                    var wrappers = TypeCallbacks[Type];
                    // remove any mapped delegates that point to these wrappers
                    var keysToRemove = new List<Delegate>();
                    foreach (var kv in CallbackMap) {
                        if (wrappers.Contains(kv.Value)) keysToRemove.Add(kv.Key);
                    }
                    foreach (var k in keysToRemove) CallbackMap.Remove(k);
                    TypeCallbacks.Remove(Type);
                    if (Options.Whitelist.Contains(Type)) Options.Whitelist.Remove(Type);
                }
            }
        }

        private void InvokeCallbacks(IReceivedEventArgs e) {
            if (e == null || e.Obj == null) return;
            var t = e.Obj.GetType();
            List<Action<IReceivedEventArgs>> list;
            lock (_callbackLock) {
                if (!TypeCallbacks.TryGetValue(t, out list) || list == null || list.Count == 0)
                    return;
                // snapshot to avoid enumeration issues if callbacks mutate registrations
                list = new List<Action<IReceivedEventArgs>>(list);
            }

            for (int i = 0; i < list.Count; i++) {
                try { list[i].Invoke(e); } catch { }
            }
        }
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
        public int MaxRedirects { get; set; } = 5;
        public IDictionary<string,string> DefaultHeaders { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public HttpClient() : base(NetworkOptions.NewDefault(), "HttpClient") {
            DefaultHeaders["User-Agent"] = "SocketJack-HttpClient/1.0";
            // HTTP is a raw TCP protocol; disable SocketJack framing/terminators.
            Options.UseTerminatedStreams = false;
            Options.UsePeerToPeer = false;
        }

        private readonly List<IProtocolHandler> _protocolHandlers = new List<IProtocolHandler>();

        /// <summary>
        /// Registers a protocol handler used for detecting response formats.
        /// </summary>
        public void RegisterProtocol(IProtocolHandler handler) {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            _protocolHandlers.Add(handler);
        }

        /// <summary>
        /// Removes a previously registered protocol handler.
        /// </summary>
        public bool RemoveProtocol(IProtocolHandler handler) {
            return _protocolHandlers.Remove(handler);
        }

        public new void Dispose() { }

        public async Task<Net.HttpResponse> GetAsync(string url, Stream responseStream = null, Action<byte[], int> onChunk = null, CancellationToken cancellationToken = default) {
            return await SendAsync("GET", url, null, null, responseStream, onChunk, cancellationToken);
        }

        public async Task<Net.HttpResponse> PostAsync(string url, string contentType, byte[] body, Stream responseStream = null, Action<byte[], int> onChunk = null, CancellationToken cancellationToken = default) {
            var headers = new Dictionary<string, string> {
                ["Content-Type"] = contentType ?? "application/octet-stream"
            };
            return await SendAsync("POST", url, headers, body, responseStream, onChunk, cancellationToken);
        }

        public async Task<Net.HttpResponse> SendAsync(string method, string url, IDictionary<string,string> headers = null, byte[] body = null, Stream responseStream = null, Action<byte[], int> onChunk = null, CancellationToken cancellationToken = default) {
            var redirects = 0;
            var currentUrl = url;
            while (true) {
                if (redirects++ > MaxRedirects) throw new Exception("Too many redirects");
                var uri = new Uri(currentUrl);
                var resp = await SendOnce(method, uri, headers, body, responseStream, onChunk, cancellationToken);
                if (resp == null) throw new Exception("No response");
                if (resp.Context != null && resp.Context.StatusCode != null && resp.Context.StatusCode.StartsWith("3") && resp.Headers.TryGetValue("Location", out var loc)) {
                    currentUrl = new Uri(uri, loc).ToString();
                    continue;
                }
                return resp;
            }
        }

        private async Task<Net.HttpResponse> SendOnce(string method, Uri uri, IDictionary<string,string> headers, byte[] body, Stream responseStream, Action<byte[], int> onChunk, CancellationToken cancellationToken) {
            string host = uri.Host;
            int port = uri.Port > 0 ? uri.Port : (uri.Scheme == "https" ? 443 : 80);

            // Use a dedicated raw TCP connection for HTTP.
            // This avoids interference from SocketJack framing/receive loops and makes results deterministic.
            using (var tcp = new System.Net.Sockets.TcpClient()) {
                tcp.NoDelay = true;

                var connectTask = tcp.ConnectAsync(host, port);
                var connectTimeout = Timeout;
                if (connectTimeout <= TimeSpan.Zero)
                    connectTimeout = TimeSpan.FromSeconds(30);

                var completed = await Task.WhenAny(connectTask, Task.Delay(connectTimeout, cancellationToken));
                if (!ReferenceEquals(completed, connectTask))
                    throw new TimeoutException("Connect timed out");

                // propagate connect exceptions
                await connectTask;

                Stream stream = tcp.GetStream();

                if (uri.Scheme == "https") {
                    var ssl = new SslStream(stream, false);
                    await ssl.AuthenticateAsClientAsync(host);
                    stream = ssl;
                }

                // Build request
                var sb = new StringBuilder();
                sb.AppendFormat("{0} {1} HTTP/1.1\r\n", method ?? "GET", uri.PathAndQuery);
                sb.AppendFormat("Host: {0}\r\n", host + (uri.IsDefaultPort ? "" : ":" + uri.Port.ToString()));
                if (Options.SafeMode && SafeModeHandshake.LooksLikeWrapper(body))
                    sb.AppendFormat("{0}: {1}\r\n", SafeModeHandshake.HeaderName, Convert.ToBase64String(SafeModeHandshake.Create(Options)));
                // default headers
                foreach (var kv in DefaultHeaders) sb.AppendFormat("{0}: {1}\r\n", kv.Key, kv.Value);
                if (headers != null) {
                    foreach (var kv in headers) sb.AppendFormat("{0}: {1}\r\n", kv.Key, kv.Value);
                }

                if (body != null && body.Length > 0) {
                    sb.AppendFormat("Content-Length: {0}\r\n", body.Length);
                }
                sb.Append("Connection: close\r\n");
                sb.Append("\r\n");

                var headerBytes = Encoding.UTF8.GetBytes(sb.ToString());
                await stream.WriteAsync(headerBytes, 0, headerBytes.Length, cancellationToken);
                if (body != null && body.Length > 0) {
                    await stream.WriteAsync(body, 0, body.Length, cancellationToken);
                }

                // Read response headers
                var ms = new MemoryStream();
                var buffer = new byte[8192];
                int read;
                int headerEndPos = -1;
                while (true) {
                    try {
                        read = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                    } catch (IOException) {
                        // remote closed/reset connection, treat as end of stream
                        break;
                    }
                    if (read <= 0) break;
                    ms.Write(buffer, 0, read);
                    if (ms.Length >= 4) {
                        var arr = ms.ToArray();
                        for (int i = 0; i < arr.Length - 3; i++) {
                            if (arr[i] == (byte)'\r' && arr[i+1] == (byte)'\n' && arr[i+2] == (byte)'\r' && arr[i+3] == (byte)'\n') {
                                headerEndPos = i + 4;
                                break;
                            }
                        }
                    }
                    if (headerEndPos != -1) break;
                }
                if (ms.Length == 0) return null;

                var totalBytes = ms.ToArray();
                var headerText = Encoding.UTF8.GetString(totalBytes, 0, headerEndPos == -1 ? totalBytes.Length : headerEndPos);
                using (var reader = new StringReader(headerText)) {
                    var statusLine = reader.ReadLine();
                    if (string.IsNullOrEmpty(statusLine)) throw new InvalidDataException("Invalid response status line");
                    var parts = statusLine.Split(' ');
                    var version = parts.Length > 0 ? parts[0] : "HTTP/1.1";
                    var status = parts.Length > 1 ? string.Join(' ', parts, 1, parts.Length - 1) : "";
                    var resp = new Net.HttpResponse();
                    resp.Version = version;
                    resp.Path = uri.PathAndQuery;
                    resp.Headers = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);

                    string line;
                    while (!string.IsNullOrEmpty(line = reader.ReadLine())) {
                        var idx = line.IndexOf(':');
                        if (idx > 0) {
                            var hn = line.Substring(0, idx).Trim();
                            var hv = line.Substring(idx+1).Trim();
                            resp.Headers[hn] = hv;
                        }
                    }

                    resp.Context = new Net.HttpContext();
                    resp.Context.Request = new Net.HttpRequest() { Path = uri.PathAndQuery, Method = method };
                    resp.Context.StatusCode = status;

                    // Invoke callbacks for received types if any
                    // We only have the full body here; we'll attempt to deserialize into whitelisted types afterwards

                    var bodyStart = headerEndPos == -1 ? totalBytes.Length : headerEndPos;
                    byte[] initialBody = null;
                    if (totalBytes.Length - bodyStart > 0) {
                        initialBody = new byte[totalBytes.Length - bodyStart];
                        Array.Copy(totalBytes, bodyStart, initialBody, 0, initialBody.Length);
                    }

                    byte[] finalBody = null;
                    if (responseStream == null) {
                        // Read fully into memory
                        if (resp.Headers.TryGetValue("Content-Length", out var cl2) && long.TryParse(cl2, out var contentLength2)) {
                            if (contentLength2 < 0)
                                contentLength2 = 0;

                            // If the first read contained body bytes beyond Content-Length, clamp to avoid over-reading
                            // and accidentally consuming bytes that belong to a subsequent response/connection close.
                            if (initialBody != null && initialBody.Length > contentLength2) {
                                var clamped = new byte[(int)contentLength2];
                                if (contentLength2 > 0)
                                    Array.Copy(initialBody, 0, clamped, 0, (int)contentLength2);
                                initialBody = clamped;
                            }

                            finalBody = await ReadFixedBody(stream, initialBody, contentLength2, cancellationToken);
                        } else if (resp.Headers.TryGetValue("Transfer-Encoding", out var te2) && te2?.ToLowerInvariant().Contains("chunked") == true) {
                            finalBody = await ReadChunkedBody(stream, initialBody, cancellationToken);
                        } else {
                            finalBody = await ReadToEnd(stream, initialBody, cancellationToken);
                        }

                        resp.BodyBytes = finalBody;
                        // Provide a best-effort text body for debugging/logging.
                        // For SocketJack wrapped payloads (binary), keep Body empty to avoid misleading output.
                        var setTextBody = true;
                        if (resp.Headers.TryGetValue("Content-Type", out var ctForBody) && !string.IsNullOrEmpty(ctForBody)) {
                            var semi2 = ctForBody.IndexOf(';');
                            var mediaType2 = semi2 >= 0 ? ctForBody.Substring(0, semi2) : ctForBody;
                            mediaType2 = mediaType2.Trim();
                            if (mediaType2.Equals("application/json", StringComparison.OrdinalIgnoreCase)) {
                                setTextBody = false;
                            }
                        }
                        if (setTextBody) {
                            resp.Body = finalBody == null ? string.Empty : Encoding.UTF8.GetString(finalBody);
                        } else {
                            resp.Body = string.Empty;
                        }
                        resp.Headers.TryGetValue("Content-Type", out var ct);
                        resp.ContentType = ct ?? resp.ContentType;

                        // Try to dispatch to any registered callbacks based on Content-Type or by detecting a SocketJack wrapped payload
                        try {
                            var bodyToDeserialize = finalBody;
                            if (bodyToDeserialize != null && bodyToDeserialize.Length > 0) {
                                bool shouldTryDeserialize = false;
                                if (resp.Headers.TryGetValue("Content-Type", out var ctype)) {
                                    if (!string.IsNullOrEmpty(ctype)) {
                                        var semi = ctype.IndexOf(';');
                                        var mediaType = semi >= 0 ? ctype.Substring(0, semi) : ctype;
                                        mediaType = mediaType.Trim();
                                        if (mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)) {
                                        shouldTryDeserialize = true;
                                        }
                                    }
                                }

                                if (!shouldTryDeserialize) {
                                    // Even if Content-Type isn't set as json, SocketJack may return wrapped objects.
                                    // We'll try deserialize anyway and just ignore failures.
                                    shouldTryDeserialize = true;
                                }

                                if (shouldTryDeserialize) {
                                    var wrapped = Options.Serializer.Deserialize(bodyToDeserialize);
                                    if (wrapped != null) {
                                        var obj = wrapped.Unwrap(this as ISocket);
                                        if (obj != null) {
                                            var objType = obj.GetType();
                                            if (TypeCallbacks.ContainsKey(objType)) {
                                                var genericType = typeof(ReceivedEventArgs<>).MakeGenericType(objType);
                                                var e = (IReceivedEventArgs)Activator.CreateInstance(genericType);
                                                e.Initialize(this as ISocket, null, obj, bodyToDeserialize.Length);
                                                InvokeCallbacks(e);
                                            }
                                        }
                                    }
                                }
                            }
                        } catch {
                            // ignore callback errors
                        }

                        return resp;
                    } else {
                        // Stream to destination as it arrives
                        if (resp.Headers.TryGetValue("Content-Length", out var cl3) && long.TryParse(cl3, out var contentLength3)) {
                            await ReadFixedBodyToStream(stream, initialBody, contentLength3, responseStream, onChunk, cancellationToken);
                        } else if (resp.Headers.TryGetValue("Transfer-Encoding", out var te3) && te3?.ToLowerInvariant().Contains("chunked") == true) {
                            await ReadChunkedBodyToStream(stream, initialBody, responseStream, onChunk, cancellationToken);
                        } else {
                            await ReadToEndToStream(stream, initialBody, responseStream, onChunk, cancellationToken);
                        }

                        resp.Body = string.Empty;
                        resp.BodyBytes = null;
                        resp.Headers.TryGetValue("Content-Type", out var ct2);
                        resp.ContentType = ct2 ?? resp.ContentType;
                        return resp;
                    }
                }
            }

        }


        private async Task<byte[]> ReadFixedBody(Stream stream, byte[] initial, long contentLength, CancellationToken cancellationToken) {
            var ms = new MemoryStream();
            if (initial != null) ms.Write(initial, 0, initial.Length);
            while (ms.Length < contentLength) {
                var buf = new byte[8192];
                var toRead = (int)Math.Min(buf.Length, contentLength - ms.Length);
                var read = await stream.ReadAsync(buf, 0, toRead, cancellationToken);
                if (read <= 0) break;
                ms.Write(buf, 0, read);
            }
            return ms.ToArray();
        }

        private async Task ReadFixedBodyToStream(Stream stream, byte[] initial, long contentLength, Stream destination, Action<byte[], int> onChunk, CancellationToken cancellationToken) {
            long written = 0;
            if (initial != null && initial.Length > 0) {
                if (destination != null) await destination.WriteAsync(initial, 0, initial.Length, cancellationToken);
                onChunk?.Invoke(initial, initial.Length);
                written += initial.Length;
            }
            var buf = new byte[8192];
            while (written < contentLength) {
                var toRead = (int)Math.Min(buf.Length, contentLength - written);
                int r;
                try {
                    r = await stream.ReadAsync(buf, 0, toRead, cancellationToken);
                } catch (IOException) {
                    break;
                }
                if (r <= 0) break;
                if (destination != null) await destination.WriteAsync(buf, 0, r, cancellationToken);
                onChunk?.Invoke(buf, r);
                written += r;
            }
        }

        private async Task<byte[]> ReadChunkedBody(Stream stream, byte[] initial, CancellationToken cancellationToken) {
            var ms = new MemoryStream();
            if (initial != null && initial.Length > 0) ms.Write(initial, 0, initial.Length);
            byte[] remaining = null;
            try {
                remaining = await ReadToEnd(stream, null, cancellationToken);
            } catch (IOException) {
                remaining = new byte[0];
            }
            byte[] combined;
            if (ms.Length > 0) {
                var a = ms.ToArray();
                combined = new byte[a.Length + (remaining?.Length ?? 0)];
                Array.Copy(a, 0, combined, 0, a.Length);
                if (remaining != null) Array.Copy(remaining, 0, combined, a.Length, remaining.Length);
            } else {
                combined = remaining ?? new byte[0];
            }

            using (var reader = new MemoryStream(combined)) {
                var outMs = new MemoryStream();
                while (true) {
                    var sizeLine = await ReadLineFromStream(reader);
                    if (string.IsNullOrEmpty(sizeLine)) break;
                    if (!int.TryParse(sizeLine.Split(';')[0].Trim(), System.Globalization.NumberStyles.HexNumber, null, out int chunkSize)) break;
                    if (chunkSize == 0) break;
                    var chunk = new byte[chunkSize];
                    var offset = 0;
                    while (offset < chunkSize) {
                        var read = await reader.ReadAsync(chunk, offset, chunkSize - offset);
                        if (read <= 0) break;
                        offset += read;
                    }
                    outMs.Write(chunk, 0, chunk.Length);
                    await reader.ReadAsync(new byte[2], 0, 2);
                }
                return outMs.ToArray();
            }
        }

        private async Task ReadChunkedBodyToStream(Stream stream, byte[] initial, Stream destination, Action<byte[], int> onChunk, CancellationToken cancellationToken) {
            // Read any initial bytes then continue reading from stream until chunked transfer complete
            var buffer = new MemoryStream();
            if (initial != null && initial.Length > 0) buffer.Write(initial, 0, initial.Length);

            // read remaining into buffer until stream ends
            var tmp = new byte[8192];
            int r;
            while (true) {
                try {
                    r = await stream.ReadAsync(tmp, 0, tmp.Length, cancellationToken);
                } catch (IOException) {
                    break;
                }
                if (r <= 0) break;
                buffer.Write(tmp, 0, r);
            }

            buffer.Position = 0;
            while (true) {
                var sizeLine = await ReadLineFromStream(buffer);
                if (string.IsNullOrEmpty(sizeLine)) break;
                if (!int.TryParse(sizeLine.Split(';')[0].Trim(), System.Globalization.NumberStyles.HexNumber, null, out int chunkSize)) break;
                if (chunkSize == 0) break;
                var chunk = new byte[chunkSize];
                var offset = 0;
                while (offset < chunkSize) {
                    var read = await buffer.ReadAsync(chunk, offset, chunkSize - offset);
                    if (read <= 0) break;
                    offset += read;
                }
                if (destination != null) await destination.WriteAsync(chunk, 0, chunk.Length, cancellationToken);
                onChunk?.Invoke(chunk, chunk.Length);
                // consume CRLF
                await buffer.ReadAsync(new byte[2], 0, 2);
            }
        }

        private async Task<byte[]> ReadToEnd(Stream stream, byte[] initial, CancellationToken cancellationToken) {
            var ms = new MemoryStream();
            if (initial != null) ms.Write(initial, 0, initial.Length);
            var buf = new byte[8192];
            while (true) {
                int r;
                try {
                    r = await stream.ReadAsync(buf, 0, buf.Length, cancellationToken);
                } catch (IOException) {
                    break;
                }
                if (r <= 0) break;
                ms.Write(buf, 0, r);
            }
            return ms.ToArray();
        }

        private async Task ReadToEndToStream(Stream stream, byte[] initial, Stream destination, Action<byte[], int> onChunk, CancellationToken cancellationToken) {
            if (initial != null && initial.Length > 0) {
                if (destination != null) await destination.WriteAsync(initial, 0, initial.Length, cancellationToken);
                onChunk?.Invoke(initial, initial.Length);
            }
            var buf = new byte[8192];
            while (true) {
                int r;
                try {
                    r = await stream.ReadAsync(buf, 0, buf.Length, cancellationToken);
                } catch (IOException) {
                    break;
                }
                if (r <= 0) break;
                if (destination != null) await destination.WriteAsync(buf, 0, r, cancellationToken);
                onChunk?.Invoke(buf, r);
            }
        }

        private async Task<string> ReadLineFromStream(Stream stream) {
            var sb = new StringBuilder();
            var buf = new byte[1];
            while (true) {
                int r;
                try {
                    r = await stream.ReadAsync(buf, 0, 1);
                } catch (IOException) {
                    break;
                }
                if (r <= 0) break;
                var b = buf[0];
                if (b == (byte)'\n') break;
                if (b == (byte)'\r') continue;
                sb.Append((char)b);
            }
            return sb.ToString();
        }
    }
}
