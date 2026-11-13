using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Schema;
using System.Threading;
using System.Threading.Tasks;

namespace SocketJack.Net {
    /// <summary>Host options for an explicitly mounted browser application socket.</summary>
    public sealed class ApplicationWebSocketOptions {
        public string StorageDirectory { get; set; }
        public IReadOnlyCollection<string> AllowedOrigins { get; set; } = Array.Empty<string>();
        public Func<string, string, CancellationToken, ValueTask<string>> Authenticate { get; set; }
        public Func<string, bool> IsAccountActive { get; set; } = _ => true;
        public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromHours(1);
    }

    /// <summary>A verified application account and its current socket session.</summary>
    public sealed class ApplicationCaller {
        internal ApplicationCaller(string subject, string session) { Subject = subject; Session = session; }
        public string Subject { get; }
        internal string Session { get; }
    }

    /// <summary>Typed browser RPC over an accepted, origin-checked WebSocket.</summary>
    public sealed partial class ApplicationWebSocket {
        public const string Subprotocol = "socketjack.application.v1";
        private const int MaximumMessageBytes = 128 * 1024;
        private const int MaximumConnections = 128;
        private const int MaximumSessions = 4096;
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { MaxDepth = 32 };
        private readonly Func<string, string, CancellationToken, ValueTask<string>> authenticate;
        private readonly Func<string, bool> active;
        private readonly TimeSpan lifetime;
        private readonly HashSet<string> origins;
        private readonly string root;
        private readonly SemaphoreSlim connections = new SemaphoreSlim(MaximumConnections);
        private readonly SemaphoreSlim signIns = new SemaphoreSlim(4);
        private readonly object gate = new object();
        private readonly Dictionary<string, Session> sessions = new Dictionary<string, Session>(StringComparer.Ordinal);
        private readonly Dictionary<string, Operation> operations = new Dictionary<string, Operation>(StringComparer.Ordinal);
        private readonly Dictionary<string, Type> collections = new Dictionary<string, Type>(StringComparer.Ordinal);

        private sealed class Session {
            internal string Subject;
            internal DateTimeOffset Expires;
        }

        private sealed class Operation {
            internal Type Input;
            internal Type Output;
            internal Func<ApplicationCaller, JsonElement, CancellationToken, ValueTask<object>> Invoke;
        }

        private sealed class Request {
            public string Id { get; set; }
            public string Operation { get; set; }
            public JsonElement Input { get; set; }
        }

        private sealed class SignIn {
            public string Username { get; set; }
            public string Password { get; set; }
        }

        private sealed class Resume {
            public string Token { get; set; }
        }

        private sealed class ApplicationError : Exception {
            internal ApplicationError(string code, string message) : base(message) { Code = code; }
            internal string Code { get; }
        }

        public ApplicationWebSocket(ApplicationWebSocketOptions options) {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (string.IsNullOrWhiteSpace(options.StorageDirectory) || !Path.IsPathRooted(options.StorageDirectory))
                throw new ArgumentException("An absolute private application storage directory is required.", nameof(options));
            if (options.SessionLifetime <= TimeSpan.Zero || options.SessionLifetime > TimeSpan.FromDays(1))
                throw new ArgumentOutOfRangeException(nameof(options.SessionLifetime));
            root = Path.GetFullPath(options.StorageDirectory);
            Directory.CreateDirectory(root);
            for (var path = new DirectoryInfo(root); path != null; path = path.Parent) {
                if ((path.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Application storage cannot traverse filesystem links.");
            }
            authenticate = options.Authenticate;
            active = options.IsAccountActive ?? throw new ArgumentException("Account status callback is required.", nameof(options));
            lifetime = options.SessionLifetime;
            origins = new HashSet<string>(options.AllowedOrigins.Select(NormalizeOrigin), StringComparer.Ordinal);
            storageLease = new FileStream(Path.Combine(root, ".application.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            try {
                RegisterBuiltIns();
                RegisterMedia();
                LoadRevision(ReadRevisions().Current);
            } catch {
                storageLease.Dispose();
                throw;
            }
        }

        /// <summary>Check before accepting an upgrade. Set secureTransport only from trusted host state.</summary>
        public bool CanUpgrade(string origin, bool secureTransport) {
            if (string.IsNullOrEmpty(origin)) return false;
            string value;
            try { value = NormalizeOrigin(origin); }
            catch (ArgumentException) { return false; }
            var uri = new Uri(value);
            return origins.Contains(value) && (secureTransport || (uri.Scheme == "http" && uri.IsLoopback));
        }

        private static string NormalizeOrigin(string value) {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
                (uri.Scheme != "https" && uri.Scheme != "http") || uri.UserInfo.Length != 0 ||
                uri.PathAndQuery != "/" || uri.Fragment.Length != 0 || (uri.Scheme == "http" && !uri.IsLoopback))
                throw new ArgumentException("Specify an HTTPS origin or a loopback HTTP origin.", nameof(value));
            return uri.GetLeftPart(UriPartial.Authority);
        }

        /// <summary>Registers a browser operation with explicit request and response types.</summary>
        public void Register<TInput, TOutput>(string name, Func<ApplicationCaller, TInput, CancellationToken, ValueTask<TOutput>> handler) {
            if (!IsName(name) || name.StartsWith("session.", StringComparison.Ordinal))
                throw new ArgumentException("Invalid or reserved operation name.", nameof(name));
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            var operation = new Operation {
                Input = typeof(TInput),
                Output = typeof(TOutput),
                Invoke = async (caller, input, cancellation) => await handler(caller, Read<TInput>(input), cancellation).ConfigureAwait(false)
            };
            lock (gate) operations.Add(name, operation);
        }

        /// <summary>Exports registered contracts using the JSON serializer metadata used by the socket.</summary>
        public string GenerateManifest() {
            lock (gate) {
                var definitions = operations.Select(pair => (Name: pair.Key, Input: pair.Value.Input, Output: pair.Value.Output))
                    .Concat(publishedRoutes.Values.Select(route => {
                        var target = route.Operation != null && operations.TryGetValue(route.Operation, out var operation) ? operation : null;
                        return (Name: route.Name, Input: route.InputProperty == null && route.RequiredBooleanProperty == null ? target?.Input : null, Output: target?.Output);
                    })).OrderBy(item => item.Name, StringComparer.Ordinal).Select(item => new {
                    name = item.Name,
                    input = item.Input == null ? null : Json.GetJsonSchemaAsNode(item.Input),
                    output = item.Output == null ? null : Json.GetJsonSchemaAsNode(item.Output)
                });
                return JsonSerializer.Serialize(new {
                    protocol = Subprotocol, revision = revision?.Id, operations = definitions,
                    collections = collections.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new {
                        name = pair.Key, schema = Json.GetJsonSchemaAsNode(pair.Value)
                    })
                }, Json);
            }
        }

        /// <summary>Revokes all current socket sessions for a canonical subject.</summary>
        public void RevokeSessions(string subject) {
            lock (gate) {
                foreach (var id in sessions.Where(pair => pair.Value.Subject == subject).Select(pair => pair.Key).ToArray())
                    sessions.Remove(id);
            }
        }

        /// <summary>Runs an accepted socket. A host may supply a validated principal resolver for existing sessions.</summary>
        public async Task RunAsync(WebSocket socket, string origin, bool secureTransport, CancellationToken cancellation = default,
            Func<CancellationToken, ValueTask<string>> resolveHostSubject = null) {
            if (socket == null) throw new ArgumentNullException(nameof(socket));
            if (!CanUpgrade(origin, secureTransport) || socket.SubProtocol != Subprotocol || !connections.Wait(0)) {
                await socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "Application socket unavailable.", cancellation).ConfigureAwait(false);
                return;
            }
            string token = null;
            try {
                var buffer = new byte[16 * 1024];
                while (socket.State == WebSocketState.Open && !cancellation.IsCancellationRequested) {
                    using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                    requestTimeout.CancelAfter(TimeSpan.FromMinutes(2));
                    using var message = new MemoryStream();
                    WebSocketReceiveResult part;
                    do {
                        part = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), requestTimeout.Token).ConfigureAwait(false);
                        if (part.MessageType == WebSocketMessageType.Close) {
                            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", cancellation).ConfigureAwait(false);
                            return;
                        }
                        if (part.MessageType != WebSocketMessageType.Text || message.Length + part.Count > MaximumMessageBytes) {
                            await socket.CloseOutputAsync(WebSocketCloseStatus.MessageTooBig, "Invalid message.", cancellation).ConfigureAwait(false);
                            return;
                        }
                        message.Write(buffer, 0, part.Count);
                    } while (!part.EndOfMessage);

                    Request request = null;
                    try {
                        request = JsonSerializer.Deserialize<Request>(message.ToArray(), Json);
                        if (!IsName(request?.Id) || !IsName(request.Operation))
                            throw new ApplicationError("invalidRequest", "Invalid operation or request ID.");
                        string subject = resolveHostSubject == null ? GetSubject(token) : await resolveHostSubject(requestTimeout.Token).ConfigureAwait(false);
                        var caller = subject != null && active(subject) ? new ApplicationCaller(subject, token) : null;
                        if (request.Operation == "media.appendBinary") {
                            if (caller == null) throw new ApplicationError("unauthenticated", "Sign in required.");
                            var append = Read<MediaRequest>(request.Input);
                            if (append == null || append.Length < 1 || append.Length > MaximumMediaChunk)
                                throw new ApplicationError("invalidInput", "Invalid binary chunk length.");
                            append.Data = await ReadBinaryChunkAsync(socket, buffer, (int)append.Length, requestTimeout.Token).ConfigureAwait(false);
                            if (resolveHostSubject != null &&
                                await resolveHostSubject(requestTimeout.Token).ConfigureAwait(false) != caller.Subject)
                                throw new ApplicationError("unauthenticated", "Sign in required.");
                            var progress = AppendMedia(caller, append);
                            await SendAsync(socket, new { request.Id, ok = true, value = progress }, requestTimeout.Token).ConfigureAwait(false);
                            continue;
                        }
                        object result;
                        switch (request.Operation) {
                            case "session.describe":
                                result = new { authenticated = caller != null, subject = caller?.Subject };
                                break;
                            case "session.signIn":
                                if (resolveHostSubject != null) throw new ApplicationError("unavailable", "Sign in through the host.");
                                token = await SignInAsync(Read<SignIn>(request.Input), requestTimeout.Token).ConfigureAwait(false);
                                result = new { authenticated = true, subject = GetSubject(token), token };
                                break;
                            case "session.resume":
                                if (resolveHostSubject != null) throw new ApplicationError("unavailable", "Sign in through the host.");
                                string resumed = Read<Resume>(request.Input)?.Token;
                                subject = GetSubject(resumed);
                                if (subject == null || !active(subject)) throw new ApplicationError("unauthenticated", "Sign in required.");
                                token = resumed;
                                result = new { authenticated = true, subject };
                                break;
                            case "session.signOut":
                                if (resolveHostSubject != null) throw new ApplicationError("unavailable", "Sign out through the host.");
                                lock (gate) { if (token != null) sessions.Remove(Hash(token)); }
                                token = null;
                                result = new { authenticated = false };
                                break;
                            default:
                                if (caller == null) throw new ApplicationError("unauthenticated", "Sign in required.");
                                result = await InvokeAsync(caller, request.Operation, request.Input, requestTimeout.Token).ConfigureAwait(false);
                                break;
                        }
                        if (result is MediaReadResult media) {
                            if (resolveHostSubject != null &&
                                await resolveHostSubject(requestTimeout.Token).ConfigureAwait(false) != caller.Subject)
                                throw new ApplicationError("unauthenticated", "Sign in required.");
                            EnsureCurrent(caller);
                            await SendAsync(socket, new { request.Id, ok = true, value = media.Metadata }, requestTimeout.Token).ConfigureAwait(false);
                            if (media.Bytes.Length != 0)
                                await socket.SendAsync(new ArraySegment<byte>(media.Bytes), WebSocketMessageType.Binary, true, requestTimeout.Token).ConfigureAwait(false);
                        } else {
                            if (!request.Operation.StartsWith("session.", StringComparison.Ordinal)) {
                                if (resolveHostSubject != null &&
                                    await resolveHostSubject(requestTimeout.Token).ConfigureAwait(false) != caller.Subject)
                                    throw new ApplicationError("unauthenticated", "Sign in required.");
                                EnsureCurrent(caller);
                            }
                            await SendAsync(socket, new { request.Id, ok = true, value = result }, requestTimeout.Token).ConfigureAwait(false);
                        }
                    } catch (ApplicationError error) {
                        await SendAsync(socket, new { id = request?.Id, ok = false, error = new { code = error.Code, message = error.Message } }, cancellation).ConfigureAwait(false);
                    } catch (JsonException) {
                        await SendAsync(socket, new { id = request?.Id, ok = false, error = new { code = "invalidInput", message = "Invalid JSON input." } }, cancellation).ConfigureAwait(false);
                    } catch (Exception error) when (!(error is OperationCanceledException || error is WebSocketException)) {
                        await SendAsync(socket, new { id = request?.Id, ok = false, error = new { code = "operationFailed", message = "The operation failed." } }, cancellation).ConfigureAwait(false);
                    }
                }
            } catch (WebSocketException) {
                socket.Abort();
            } catch (OperationCanceledException) {
                socket.Abort();
            } finally {
                connections.Release();
            }
        }

        private async ValueTask<string> SignInAsync(SignIn input, CancellationToken cancellation) {
            if (authenticate == null || string.IsNullOrWhiteSpace(input?.Username) || input.Username.Length > 256 ||
                string.IsNullOrEmpty(input.Password) || input.Password.Length > 1024)
                throw new ApplicationError("unauthenticated", "Sign-in failed.");
            if (!signIns.Wait(0)) throw new ApplicationError("busy", "Try again shortly.");
            string subject;
            try { subject = await authenticate(input.Username, input.Password, cancellation).ConfigureAwait(false); }
            finally { signIns.Release(); }
            if (string.IsNullOrWhiteSpace(subject) || subject.Length > 256 || !active(subject))
                throw new ApplicationError("unauthenticated", "Sign-in failed.");
            var bytes = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            string token = Convert.ToBase64String(bytes);
            lock (gate) {
                var now = DateTimeOffset.UtcNow;
                foreach (var id in sessions.Where(pair => pair.Value.Expires <= now).Select(pair => pair.Key).ToArray())
                    sessions.Remove(id);
                if (sessions.Count >= MaximumSessions) throw new ApplicationError("busy", "Try again shortly.");
                sessions.Add(Hash(token), new Session { Subject = subject, Expires = now + lifetime });
            }
            return token;
        }

        private string GetSubject(string token) {
            if (token == null || token.Length != 44) return null;
            lock (gate) {
                return sessions.TryGetValue(Hash(token), out var session) && session.Expires > DateTimeOffset.UtcNow
                    ? session.Subject : null;
            }
        }

        private static string Hash(string value) {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").ToLowerInvariant();
        }

        private static bool IsName(string value) => !string.IsNullOrEmpty(value) && value.Length <= 96 &&
            value.All(c => c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '-' || c == '_' || c == '.');

        private static T Read<T>(JsonElement input) => input.ValueKind == JsonValueKind.Undefined ? default : input.Deserialize<T>(Json);

        private static async Task<byte[]> ReadBinaryChunkAsync(WebSocket socket, byte[] buffer, int length, CancellationToken cancellation) {
            var bytes = new byte[length];
            int offset = 0;
            WebSocketReceiveResult part;
            do {
                part = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellation).ConfigureAwait(false);
                if (part.MessageType != WebSocketMessageType.Binary || part.Count > length - offset) {
                    socket.Abort();
                    throw new WebSocketException("Invalid binary upload frame.");
                }
                Buffer.BlockCopy(buffer, 0, bytes, offset, part.Count);
                offset += part.Count;
            } while (!part.EndOfMessage);
            if (offset != length) {
                socket.Abort();
                throw new WebSocketException("Incomplete binary upload frame.");
            }
            return bytes;
        }

        private static async Task SendAsync(WebSocket socket, object message, CancellationToken cancellation) {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(message, Json);
            if (bytes.Length > MaximumMessageBytes) throw new ApplicationError("tooLarge", "Response exceeds the application message limit.");
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellation).ConfigureAwait(false);
        }
    }
}
