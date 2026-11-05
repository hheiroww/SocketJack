using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SocketJack.Multiplayer.Transport
{
    public enum WebSocketRelayRole { Host, Client }

    /// <summary>Account-service authorization result for one opaque connection ticket. Expiry is
    /// checked on admission, not used to disconnect an established game when a short ticket expires.</summary>
    public sealed class RelayAccess
    {
        public string SessionId { get; }
        public WebSocketRelayRole Role { get; }
        public DateTimeOffset ExpiresAtUtc { get; }
        public string Subject { get; }
        public RelayAccess(string sessionId, WebSocketRelayRole role, DateTimeOffset expiresAtUtc, string subject = null)
        {
            if (!RelayWire.SafeIdentifier(sessionId, 64) || !Enum.IsDefined(typeof(WebSocketRelayRole), role) || (subject != null && !RelayWire.SafeIdentifier(subject, 128)))
                throw new ArgumentException("Use bounded ASCII relay session and subject identifiers and a defined role.");
            SessionId = sessionId; Role = role; ExpiresAtUtc = expiresAtUtc; Subject = subject;
        }
    }

    public sealed class WebSocketRelayOptions
    {
        public int MaxSessions { get; set; } = 256;
        public int MaxConnections { get; set; } = 4096;
        public int MaxClientsPerSession { get; set; } = 256;
        /// <summary>Maximum game WebSocket message, excluding this relay's ten-byte envelope.</summary>
        public int MaxMessageBytes { get; set; } = 1536;
        public int MaxQueuedMessages { get; set; } = 256;
        public int MaxQueuedBytesPerConnection { get; set; } = 256 * 1024;
        public int ClientBytesPerSecond { get; set; } = 1024 * 1024;
        public int HostBytesPerSecond { get; set; } = 8 * 1024 * 1024;
        public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(10);
        public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(30);
        internal WebSocketRelayOptions Snapshot()
        {
            if (MaxSessions < 1 || MaxSessions > 65535 || MaxConnections < 1 || MaxConnections > 65535 || MaxClientsPerSession < 1 || MaxClientsPerSession > 65535 ||
                MaxMessageBytes < 256 || MaxMessageBytes > 65536 || MaxQueuedMessages < 1 || MaxQueuedMessages > 65536 || MaxQueuedBytesPerConnection < MaxMessageBytes + 10 || MaxQueuedBytesPerConnection > 16 * 1024 * 1024 ||
                ClientBytesPerSecond < MaxMessageBytes || ClientBytesPerSecond > 64 * 1024 * 1024 || HostBytesPerSecond < MaxMessageBytes || HostBytesPerSecond > 256 * 1024 * 1024 ||
                HandshakeTimeout <= TimeSpan.Zero || HandshakeTimeout > TimeSpan.FromMinutes(2) || IdleTimeout < TimeSpan.FromSeconds(2) || IdleTimeout > TimeSpan.FromMinutes(10))
                throw new ArgumentOutOfRangeException(nameof(WebSocketRelayOptions));
            return (WebSocketRelayOptions)MemberwiseClone();
        }
    }

    /// <summary>Optional self-hosted WSS game relay. An authenticated account service issues opaque,
    /// short-lived role/session-bound tickets and validates them through AuthorizeTicket. This class
    /// never resolves an arbitrary proxy destination. Route only /ws/relay here through a listener
    /// enforcing TLS and Origin policy. WebSocket/TCP relaying retains stream head-of-line blocking.</summary>
    public sealed class WebSocketRelayService : IDisposable, IAsyncDisposable
    {
        private sealed class Session
        {
            internal string Id; internal RelayConnection Host; internal ulong NextRoute;
            internal readonly Dictionary<ulong, RelayConnection> Clients = new Dictionary<ulong, RelayConnection>();
        }
        private readonly object sync = new object();
        private readonly WebSocketRelayOptions options;
        private readonly Dictionary<string, Session> sessions = new Dictionary<string, Session>(StringComparer.Ordinal);
        private readonly HashSet<RelayConnection> connections = new HashSet<RelayConnection>();
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private bool disposed;
        private long forwarded;
        /// <summary>Called once per connection, after the bounded Join and before session allocation.
        /// Return null for denied, consumed, expired, revoked, or unknown tickets. The account service
        /// must enforce account/session creation quotas and, when required, consume tickets atomically.
        /// This callback runs on I/O continuations and must not call Unity APIs.</summary>
        public Func<string, RelayAccess> AuthorizeTicket { get; set; }
        public int SessionCount { get { lock (sync) return sessions.Count; } }
        public int ConnectionCount { get { lock (sync) return connections.Count; } }
        public long BytesForwarded => Interlocked.Read(ref forwarded);
        public WebSocketRelayService(WebSocketRelayOptions options = null) { this.options = (options ?? new WebSocketRelayOptions()).Snapshot(); }

        public async Task HandleWebSocketAsync(WebSocketHttpRequest request, WebSocket socket, CancellationToken cancellationToken)
        {
            if (socket == null) throw new ArgumentNullException(nameof(socket));
            var connection = new RelayConnection(socket, options.MaxQueuedMessages, options.MaxQueuedBytesPerConnection, cancellationToken, lifetime.Token);
            Session session = null; ulong route = 0; bool host = false;
            lock (sync)
            {
                if (disposed || connections.Count >= options.MaxConnections) { connection.Stop(); connection.Complete(); return; }
                connections.Add(connection);
            }
            connection.Start();
            try
            {
                byte[] join = await RelayWire.ReceiveDeadline(socket, 516, options.HandshakeTimeout, connection.Token).ConfigureAwait(false);
                string ticket = RelayWire.ReadJoin(join);
                RelayAccess access = AuthorizeTicket?.Invoke(ticket);
                if (access == null || access.ExpiresAtUtc <= DateTimeOffset.UtcNow) throw new InvalidDataException("Relay admission denied.");
                host = access.Role == WebSocketRelayRole.Host;
                lock (sync)
                {
                    connection.Token.ThrowIfCancellationRequested();
                    if (host)
                    {
                        if (sessions.ContainsKey(access.SessionId) || sessions.Count >= options.MaxSessions) throw new InvalidDataException("Relay host unavailable.");
                        session = new Session { Id = access.SessionId, Host = connection }; sessions.Add(session.Id, session);
                    }
                    else
                    {
                        if (!sessions.TryGetValue(access.SessionId, out session) || !session.Host.Active || session.Clients.Count >= options.MaxClientsPerSession)
                            throw new InvalidDataException("Relay session unavailable.");
                        route = ++session.NextRoute;
                        if (route == 0) throw new InvalidDataException("Relay route space exhausted.");
                        session.Clients.Add(route, connection);
                    }
                    if (!connection.Enqueue(RelayWire.Frame(RelayWire.Ready, route)) || (!host && !session.Host.Enqueue(RelayWire.Frame(RelayWire.Open, route))))
                        throw new InvalidDataException("Relay queue capacity exceeded.");
                }
                var bucket = new RelayRate(host ? options.HostBytesPerSecond : options.ClientBytesPerSecond);
                while (!connection.Token.IsCancellationRequested)
                {
                    byte[] message = await RelayWire.ReceiveDeadline(socket, options.MaxMessageBytes + 10, options.IdleTimeout, connection.Token).ConfigureAwait(false);
                    if (message == null) break;
                    if (message.Length < 10 || message[0] != 1) throw new InvalidDataException("Invalid relay message.");
                    byte kind = message[1]; ulong target = RelayWire.Read64(message, 2);
                    if (kind == RelayWire.Ping && message.Length == 10 && target == 0)
                    { if (!connection.Enqueue(RelayWire.Frame(RelayWire.Pong, 0))) break; continue; }
                    if (kind == RelayWire.Close && message.Length == 10 && host)
                    {
                        RelayConnection closing = null; lock (sync) session.Clients.TryGetValue(target, out closing); closing?.Stop(); continue;
                    }
                    if (kind != RelayWire.Data || message.Length == 10 || (!host && target != 0) || !bucket.Take(message.Length))
                        throw new InvalidDataException("Invalid relay data or rate exceeded.");
                    RelayConnection destination = null;
                    lock (sync)
                    {
                        if (host) session.Clients.TryGetValue(target, out destination);
                        else if (session.Clients.TryGetValue(route, out var current) && ReferenceEquals(current, connection)) destination = session.Host;
                    }
                    // A host packet already in flight may race a client closing. It must never reach
                    // a replacement route or another session; monotonically unique routes are not reused.
                    if (destination == null) { if (host) continue; break; }
                    RelayWire.Write64(message, 2, host ? 0 : route);
                    if (!destination.Enqueue(message)) { destination.Stop(); break; }
                    Interlocked.Add(ref forwarded, message.Length - 10);
                }
            }
            catch (Exception error) when (RelayWire.Expected(error)) { }
            finally
            {
                RelayConnection[] dependents = Array.Empty<RelayConnection>();
                lock (sync)
                {
                    if (session != null)
                    {
                        if (host && ReferenceEquals(session.Host, connection))
                        {
                            if (sessions.TryGetValue(session.Id, out var current) && ReferenceEquals(current, session)) sessions.Remove(session.Id);
                            dependents = session.Clients.Values.ToArray(); session.Clients.Clear();
                        }
                        else if (route != 0 && session.Clients.TryGetValue(route, out var current) && ReferenceEquals(current, connection))
                        { session.Clients.Remove(route); if (session.Host.Active && !session.Host.Enqueue(RelayWire.Frame(RelayWire.Close, route))) session.Host.Stop(); }
                    }
                }
                foreach (var dependent in dependents) dependent.Stop();
                connection.Stop(); await connection.FinishAsync().ConfigureAwait(false);
                lock (sync) connections.Remove(connection);
                connection.Complete();
            }
        }

        public void RevokeSession(string sessionId)
        {
            RelayConnection[] close;
            lock (sync)
            {
                if (!sessions.TryGetValue(sessionId, out var session)) return;
                // Keep the host registered until its finally block completes: an old host cannot
                // erase a concurrently admitted replacement session.
                close = session.Clients.Values.Concat(new[] { session.Host }).ToArray();
            }
            foreach (var connection in close) connection.Stop();
        }
        public async Task StopAsync()
        {
            RelayConnection[] close;
            lock (sync) { disposed = true; lifetime.Cancel(); close = connections.ToArray(); }
            foreach (var connection in close) connection.Stop();
            await Task.WhenAll(close.Select(x => x.Completion)).ConfigureAwait(false);
        }
        public void Dispose() { _ = StopAsync(); }
        public async ValueTask DisposeAsync() { await StopAsync().ConfigureAwait(false); }

        /// <summary>Wraps the platform's native or JavaScript WebSocket connector. Configure the
        /// game transport with the relay URI and this connector; tickets never appear in URLs.</summary>
        public static Func<Uri, CancellationToken, Task<WebSocket>> CreateClientConnector(string ticket, Func<Uri, CancellationToken, Task<WebSocket>> connector = null)
        {
            byte[] join = RelayWire.Join(ticket);
            return async (uri, token) =>
            {
                WebSocket socket = await (connector ?? RelayWire.Connect)(uri, token).ConfigureAwait(false);
                try
                {
                    await socket.SendAsync(new ArraySegment<byte>(join), WebSocketMessageType.Binary, true, token).ConfigureAwait(false);
                    byte[] ready = await RelayWire.Receive(socket, 10, token).ConfigureAwait(false);
                    if (ready == null || ready.Length != 10 || ready[0] != 1 || ready[1] != RelayWire.Ready || RelayWire.Read64(ready, 2) == 0)
                        throw new InvalidDataException("Relay client admission denied.");
                    return new RelayClientSocket(socket);
                }
                catch { socket.Abort(); socket.Dispose(); throw; }
            };
        }
    }

    /// <summary>One authenticated host connection multiplexing outbound-only game clients into an
    /// already running WebSocketGameTransport. Dispose this tunnel when the host game stops.
    /// Browsers may join the relay; hosting requires a native process. The relay chooses no TCP target.</summary>
    public sealed class WebSocketRelayHost : IDisposable, IAsyncDisposable
    {
        private readonly object sync = new object();
        private readonly WebSocketGameTransport game;
        private readonly RelayConnection connection;
        private readonly Dictionary<ulong, RelayVirtualSocket> clients = new Dictionary<ulong, RelayVirtualSocket>();
        private readonly HashSet<Task> handlers = new HashSet<Task>();
        private Task receive, heartbeat;
        private int stopped;
        private WebSocketRelayHost(WebSocketGameTransport game, WebSocket socket, CancellationToken token)
        { this.game = game; connection = new RelayConnection(socket, 1024, 1024 * 1024, token, CancellationToken.None); }
        public bool IsConnected => connection.Active && Volatile.Read(ref stopped) == 0;
        public int ClientCount { get { lock (sync) return clients.Count; } }
        public static async Task<WebSocketRelayHost> ConnectAsync(WebSocketGameTransport gameServer, Uri relayUri, string hostTicket, CancellationToken cancellationToken = default)
        {
            if (gameServer == null || !gameServer.IsRunning || !gameServer.IsServer) throw new ArgumentException("Start the native WebSocket game server before connecting its relay tunnel.", nameof(gameServer));
            byte[] join = RelayWire.Join(hostTicket);
            WebSocket socket = await RelayWire.Connect(relayUri, cancellationToken).ConfigureAwait(false);
            try
            {
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    deadline.CancelAfter(TimeSpan.FromSeconds(10));
                    using (deadline.Token.Register(socket.Abort))
                    {
                        await socket.SendAsync(new ArraySegment<byte>(join), WebSocketMessageType.Binary, true, deadline.Token).ConfigureAwait(false);
                        byte[] ready = await RelayWire.Receive(socket, 10, deadline.Token).ConfigureAwait(false);
                        if (ready == null || ready.Length != 10 || ready[0] != 1 || ready[1] != RelayWire.Ready || RelayWire.Read64(ready, 2) != 0)
                            throw new InvalidDataException("Relay host admission denied.");
                    }
                }
                var host = new WebSocketRelayHost(gameServer, socket, cancellationToken); host.connection.Start();
                host.receive = host.ReceiveAsync(); host.heartbeat = host.HeartbeatAsync(); return host;
            }
            catch { socket.Abort(); socket.Dispose(); throw; }
        }
        private async Task ReceiveAsync()
        {
            try
            {
                while (!connection.Token.IsCancellationRequested)
                {
                    byte[] message = await RelayWire.ReceiveDeadline(connection.Socket, game.MaxPayloadSize + 26, TimeSpan.FromSeconds(30), connection.Token).ConfigureAwait(false);
                    if (message == null) break;
                    if (message.Length < 10 || message[0] != 1) throw new InvalidDataException("Invalid relay host message.");
                    ulong route = RelayWire.Read64(message, 2); byte kind = message[1];
                    if (kind == RelayWire.Pong && message.Length == 10 && route == 0) continue;
                    if (route == 0) throw new InvalidDataException("Invalid relay host route.");
                    if (kind == RelayWire.Open && message.Length == 10)
                    {
                        RelayVirtualSocket client;
                        lock (sync)
                        {
                            if (clients.ContainsKey(route) || clients.Count >= 65535) throw new InvalidDataException("Duplicate relay client.");
                            client = new RelayVirtualSocket(this, route); clients.Add(route, client);
                        }
                        var request = new WebSocketHttpRequest("GET", "/ws/game", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), Array.Empty<byte>(), null, true);
                        Task handler = HandleClient(request, client);
                        lock (sync) { handlers.Add(handler); }
                        _ = ObserveHandler(handler);
                    }
                    else if (kind == RelayWire.Close && message.Length == 10)
                    { RelayVirtualSocket client; lock (sync) clients.TryGetValue(route, out client); client?.RemoteClose(); }
                    else if (kind == RelayWire.Data && message.Length > 10)
                    {
                        RelayVirtualSocket client; lock (sync) clients.TryGetValue(route, out client);
                        if (client != null && !client.Push(RelayWire.Payload(message))) client.Abort();
                    }
                    else throw new InvalidDataException("Invalid relay host frame.");
                }
            }
            catch (Exception error) when (RelayWire.Expected(error)) { }
            finally { Stop(); }
        }
        private async Task HandleClient(WebSocketHttpRequest request, RelayVirtualSocket client)
        { try { await game.AcceptWebSocketAsync(request, client, connection.Token).ConfigureAwait(false); } catch (Exception error) when (RelayWire.Expected(error)) { } finally { client.Abort(); } }
        private async Task ObserveHandler(Task handler)
        { try { await handler.ConfigureAwait(false); } finally { lock (sync) handlers.Remove(handler); } }
        private async Task HeartbeatAsync()
        {
            try { while (!connection.Token.IsCancellationRequested) { await Task.Delay(TimeSpan.FromSeconds(5), connection.Token).ConfigureAwait(false); if (!connection.Enqueue(RelayWire.Frame(RelayWire.Ping, 0))) break; } }
            catch (OperationCanceledException) { }
            finally { Stop(); }
        }
        internal bool Send(ulong route, ArraySegment<byte> data)
        { return connection.Enqueue(RelayWire.Frame(RelayWire.Data, route, data)); }
        internal void Remove(ulong route, RelayVirtualSocket socket, bool notify)
        {
            bool removed; lock (sync) { removed = clients.TryGetValue(route, out var current) && ReferenceEquals(current, socket); if (removed) clients.Remove(route); }
            if (removed && notify && connection.Active && !connection.Enqueue(RelayWire.Frame(RelayWire.Close, route))) Stop();
        }
        private void Stop()
        {
            if (Interlocked.Exchange(ref stopped, 1) != 0) return;
            connection.Stop(); RelayVirtualSocket[] close; lock (sync) { close = clients.Values.ToArray(); clients.Clear(); }
            foreach (var client in close) client.RemoteClose();
        }
        public void Dispose() { Stop(); }
        public async ValueTask DisposeAsync()
        {
            Stop(); await Task.WhenAll(receive ?? Task.CompletedTask, heartbeat ?? Task.CompletedTask).ConfigureAwait(false);
            Task[] pending; lock (sync) pending = handlers.ToArray(); await Task.WhenAll(pending).ConfigureAwait(false);
            await connection.FinishAsync().ConfigureAwait(false); connection.Complete();
        }
    }

    internal sealed class RelayConnection
    {
        private readonly object sync = new object();
        private readonly Queue<byte[]> outgoing = new Queue<byte[]>();
        private readonly WebSocketAsyncSignal signal = new WebSocketAsyncSignal();
        private readonly CancellationTokenSource cancellation;
        private readonly int messageCap, byteCap;
        private int bytes; private bool active = true; private Task sending = Task.CompletedTask;
        private readonly TaskCompletionSource<bool> completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        internal WebSocket Socket { get; }
        internal CancellationToken Token => cancellation.Token;
        internal bool Active { get { lock (sync) return active; } }
        internal Task Completion => completion.Task;
        internal RelayConnection(WebSocket socket, int messageCap, int byteCap, CancellationToken first, CancellationToken second)
        { Socket = socket; this.messageCap = messageCap; this.byteCap = byteCap; cancellation = CancellationTokenSource.CreateLinkedTokenSource(first, second); }
        internal void Start() { sending = SendAsync(); }
        internal bool Enqueue(byte[] message)
        {
            lock (sync)
            {
                if (!active || Token.IsCancellationRequested || outgoing.Count >= messageCap || message.Length > byteCap - bytes) return false;
                outgoing.Enqueue(message); bytes += message.Length;
            }
            signal.Release(); return true;
        }
        private async Task SendAsync()
        {
            try
            {
                while (!Token.IsCancellationRequested)
                {
                    await signal.WaitAsync(Token).ConfigureAwait(false); byte[] message;
                    lock (sync) { if (!active || outgoing.Count == 0) break; message = outgoing.Dequeue(); /* retain in-flight bytes until sent */ }
                    await Socket.SendAsync(new ArraySegment<byte>(message), WebSocketMessageType.Binary, true, Token).ConfigureAwait(false);
                    lock (sync) bytes -= message.Length;
                }
            }
            catch (Exception error) when (RelayWire.Expected(error)) { }
            finally { Stop(); }
        }
        internal void Stop()
        {
            lock (sync) { if (!active) return; active = false; outgoing.Clear(); }
            try { cancellation.Cancel(); Socket.Abort(); } catch (ObjectDisposedException) { }
            signal.Dispose();
        }
        internal async Task FinishAsync() { await sending.ConfigureAwait(false); Socket.Dispose(); }
        internal void Complete() { completion.TrySetResult(true); }
    }

    internal sealed class RelayRate
    {
        private readonly int rate; private double tokens; private long last = Stopwatch.GetTimestamp();
        internal RelayRate(int bytesPerSecond) { rate = bytesPerSecond; tokens = rate; }
        internal bool Take(int bytes)
        {
            long now = Stopwatch.GetTimestamp(); tokens = Math.Min(rate, tokens + (now - last) * (rate / (double)Stopwatch.Frequency)); last = now;
            if (bytes > tokens) return false; tokens -= bytes; return true;
        }
    }

    internal static class RelayWire
    {
        internal const byte JoinKind = 1, Ready = 2, Open = 3, Data = 4, Close = 5, Ping = 6, Pong = 7;
        internal static bool SafeIdentifier(string text, int max)
            => !string.IsNullOrEmpty(text) && text.Length <= max && text.All(c => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-' || c == '.');
        internal static byte[] Join(string ticket)
        {
            if (string.IsNullOrEmpty(ticket) || ticket.Length > 512 || ticket.Any(c => c < 33 || c > 126)) throw new ArgumentException("Use an opaque printable ASCII relay ticket of 1..512 bytes.", nameof(ticket));
            var frame = new byte[ticket.Length + 4]; frame[0] = 1; frame[1] = JoinKind; frame[2] = (byte)ticket.Length; frame[3] = (byte)(ticket.Length >> 8); Encoding.ASCII.GetBytes(ticket, 0, ticket.Length, frame, 4); return frame;
        }
        internal static string ReadJoin(byte[] frame)
        {
            if (frame == null || frame.Length < 5 || frame.Length > 516 || frame[0] != 1 || frame[1] != JoinKind || (frame[2] | frame[3] << 8) != frame.Length - 4)
                throw new InvalidDataException("Invalid relay Join.");
            for (int i = 4; i < frame.Length; i++) if (frame[i] < 33 || frame[i] > 126) throw new InvalidDataException("Invalid relay ticket encoding.");
            return Encoding.ASCII.GetString(frame, 4, frame.Length - 4);
        }
        internal static byte[] Frame(byte kind, ulong route, ArraySegment<byte> payload = default)
        {
            if (payload.Count > 65536) throw new ArgumentOutOfRangeException(nameof(payload));
            var result = new byte[10 + payload.Count]; result[0] = 1; result[1] = kind; Write64(result, 2, route);
            if (payload.Count != 0) Buffer.BlockCopy(payload.Array, payload.Offset, result, 10, payload.Count); return result;
        }
        internal static byte[] Payload(byte[] frame) { var result = new byte[frame.Length - 10]; Buffer.BlockCopy(frame, 10, result, 0, result.Length); return result; }
        internal static ulong Read64(byte[] bytes, int offset) { ulong result = 0; for (int i = 0; i < 8; i++) result |= (ulong)bytes[offset + i] << (8 * i); return result; }
        internal static void Write64(byte[] bytes, int offset, ulong value) { for (int i = 0; i < 8; i++) bytes[offset + i] = (byte)(value >> (8 * i)); }
        internal static bool Expected(Exception error) => error is OperationCanceledException || error is WebSocketException || error is IOException || error is ObjectDisposedException || error is InvalidOperationException;
        internal static async Task<WebSocket> Connect(Uri uri, CancellationToken token)
        {
            if (uri == null || !uri.IsAbsoluteUri || (uri.Scheme != "ws" && uri.Scheme != "wss") || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0) throw new ArgumentException("Use an absolute ws/wss relay URI without credentials or fragment.", nameof(uri));
            var socket = new ClientWebSocket(); try { await socket.ConnectAsync(uri, token).ConfigureAwait(false); return socket; } catch { socket.Dispose(); throw; }
        }
        internal static async Task<byte[]> ReceiveDeadline(WebSocket socket, int maximum, TimeSpan timeout, CancellationToken token)
        {
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            { deadline.CancelAfter(timeout); using (deadline.Token.Register(socket.Abort)) return await Receive(socket, maximum, deadline.Token).ConfigureAwait(false); }
        }
        internal static async Task<byte[]> Receive(WebSocket socket, int maximum, CancellationToken token)
        {
            var buffer = new byte[Math.Min(maximum + 1, 2048)]; int length = 0;
            do
            {
                if (length == buffer.Length) Array.Resize(ref buffer, Math.Min(maximum + 1, buffer.Length * 2));
                var received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, length, buffer.Length - length), token).ConfigureAwait(false);
                if (received.MessageType == WebSocketMessageType.Close) return null;
                if (received.MessageType != WebSocketMessageType.Binary || received.Count > maximum - length) throw new InvalidDataException("Relay requires bounded binary messages.");
                length += received.Count;
                if (received.EndOfMessage) { var result = new byte[length]; Buffer.BlockCopy(buffer, 0, result, 0, length); return result; }
            } while (length <= maximum);
            throw new InvalidDataException("Relay message limit exceeded.");
        }
    }

    internal sealed class RelayClientSocket : WebSocket
    {
        private readonly WebSocket socket; private byte[] current; private int offset;
        internal RelayClientSocket(WebSocket socket) { this.socket = socket; }
        public override WebSocketCloseStatus? CloseStatus => socket.CloseStatus;
        public override string CloseStatusDescription => socket.CloseStatusDescription;
        public override WebSocketState State => socket.State;
        public override string SubProtocol => null;
        public override void Abort() => socket.Abort();
        public override void Dispose() => socket.Dispose();
        public override Task CloseAsync(WebSocketCloseStatus status, string description, CancellationToken token) => socket.CloseAsync(status, description, token);
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string description, CancellationToken token) => socket.CloseOutputAsync(status, description, token);
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (type != WebSocketMessageType.Binary || !end || buffer.Count < 1 || buffer.Count > 65536) throw new InvalidOperationException("Relay sends one complete bounded binary game message.");
            return socket.SendAsync(new ArraySegment<byte>(RelayWire.Frame(RelayWire.Data, 0, buffer)), WebSocketMessageType.Binary, true, token);
        }
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken token)
        {
            if (current == null)
            {
                byte[] frame = await RelayWire.Receive(socket, 65546, token).ConfigureAwait(false);
                if (frame == null) return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
                if (frame.Length <= 10 || frame[0] != 1 || frame[1] != RelayWire.Data || RelayWire.Read64(frame, 2) != 0) throw new InvalidDataException("Invalid relayed game frame.");
                current = frame; offset = 10;
            }
            int count = Math.Min(buffer.Count, current.Length - offset); Buffer.BlockCopy(current, offset, buffer.Array, buffer.Offset, count); offset += count;
            bool end = offset == current.Length; if (end) { current = null; offset = 0; } return new WebSocketReceiveResult(count, WebSocketMessageType.Binary, end);
        }
    }

    internal sealed class RelayVirtualSocket : WebSocket
    {
        private readonly object sync = new object(); private readonly WebSocketRelayHost host; private readonly ulong route;
        private readonly Queue<byte[]> incoming = new Queue<byte[]>();
        private TaskCompletionSource<bool> ready; private int bytes, offset; private byte[] current;
        private WebSocketState state = WebSocketState.Open;
        internal RelayVirtualSocket(WebSocketRelayHost host, ulong route) { this.host = host; this.route = route; }
        public override WebSocketCloseStatus? CloseStatus => state == WebSocketState.Open ? (WebSocketCloseStatus?)null : WebSocketCloseStatus.NormalClosure;
        public override string CloseStatusDescription => null;
        public override WebSocketState State { get { lock (sync) return state; } }
        public override string SubProtocol => null;
        internal bool Push(byte[] message)
        {
            TaskCompletionSource<bool> signal;
            lock (sync)
            {
                if (state != WebSocketState.Open || incoming.Count >= 256 || message.Length > 256 * 1024 - bytes) return false;
                incoming.Enqueue(message); bytes += message.Length; signal = ready; ready = null;
            }
            signal?.TrySetResult(true); return true;
        }
        internal void RemoteClose() => Close(false);
        private void Close(bool notify)
        {
            TaskCompletionSource<bool> signal;
            lock (sync) { if (state != WebSocketState.Open) return; state = WebSocketState.Closed; incoming.Clear(); current = null; bytes = 0; signal = ready; ready = null; }
            host.Remove(route, this, notify); signal?.TrySetResult(false);
        }
        public override void Abort() => Close(true);
        public override void Dispose() => Close(true);
        public override Task CloseAsync(WebSocketCloseStatus status, string description, CancellationToken token) { Close(true); return Task.CompletedTask; }
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string description, CancellationToken token) { Close(true); return Task.CompletedTask; }
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (type != WebSocketMessageType.Binary || !end || buffer.Count < 1 || buffer.Count > 65536 || State != WebSocketState.Open) throw new WebSocketException("Relay game socket is unavailable.");
            if (!host.Send(route, buffer)) { Abort(); throw new WebSocketException("Relay host send queue is full."); }
            return Task.CompletedTask;
        }
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken token)
        {
            while (true)
            {
                Task wait;
                lock (sync)
                {
                    token.ThrowIfCancellationRequested();
                    if (state != WebSocketState.Open) return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
                    if (current == null && incoming.Count > 0) { current = incoming.Dequeue(); offset = 0; }
                    if (current != null)
                    {
                        int count = Math.Min(buffer.Count, current.Length - offset); Buffer.BlockCopy(current, offset, buffer.Array, buffer.Offset, count); offset += count;
                        bool end = offset == current.Length; if (end) { bytes -= current.Length; current = null; offset = 0; } return new WebSocketReceiveResult(count, WebSocketMessageType.Binary, end);
                    }
                    if (ready != null) throw new InvalidOperationException("Only one relay receive may be outstanding.");
                    ready = new TaskCompletionSource<bool>(); wait = ready.Task;
                }
                using (token.Register(Abort)) await wait.ConfigureAwait(false);
            }
        }
    }
}
