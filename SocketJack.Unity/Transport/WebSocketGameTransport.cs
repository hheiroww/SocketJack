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
    /// <summary>Native WebSocket game transport. Socket I/O is asynchronous; Poll alone invokes game
    /// callbacks and admission validation. Every requested delivery mode travels over an ordered reliable
    /// WebSocket/TCP stream and therefore shares head-of-line blocking. No silent fallback occurs.
    /// Browser builds may supply ClientConnector backed by a JavaScript WebSocket implementation.
    /// Async paths preserve a caller's synchronization context so single-threaded WebGL does not
    /// schedule continuations onto an unavailable managed worker pool. Native callers without a
    /// context continue on asynchronous I/O completions; game events still dispatch only from Poll.
    /// A shared listener is caller-owned and must already be started, with /ws/game routed to
    /// AcceptWebSocketAsync. A standalone listener is owned and stopped by this transport.</summary>
    public sealed class WebSocketGameTransport : IGameTransport, ITransportMetricsSource, IPacketBudgetSource
    {
        private enum Kind : byte { Hello = 1, Welcome = 2, Data = 3, Heartbeat = 4, Disconnect = 5, Reject = 6 }
        private sealed class Peer
        {
            internal ulong Id;
            internal WebSocket Socket;
            internal IPEndPoint Endpoint;
            internal bool Active = true, Ready;
            internal long LastReceive, LastSend, Started;
            internal double RttMilliseconds;
            internal int OutBytes, InBytes;
            internal readonly Queue<byte[]> Out = new Queue<byte[]>(), In = new Queue<byte[]>();
            internal readonly WebSocketAsyncSignal Signal = new WebSocketAsyncSignal();
            internal CancellationTokenSource Cancellation;
        }
        private sealed class QueuedEvent { internal TransportEvent Value; internal Peer Peer; internal uint Generation; }
        private readonly object sync = new object();
        private readonly TransportOptions options;
        private readonly Uri configuredUri;
        private readonly WebSocketListener externalListener;
        private readonly WebSocketListenerOptions listenerOptions;
        private readonly Dictionary<ulong, Peer> peers = new Dictionary<ulong, Peer>();
        private readonly List<Peer> schedule = new List<Peer>();
        private readonly Queue<QueuedEvent> events = new Queue<QueuedEvent>();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private CancellationTokenSource lifetime;
        private bool running, disposed, polling;
        private ulong nextPeer;
        private int cursor;
        private uint generation;
        private long connectingAt, sentBytes, receivedBytes, sentPackets, receivedPackets, sendRejected, invalidPackets;
        private bool connecting;
        private Func<Uri, CancellationToken, Task<WebSocket>> clientConnector;
        public TransportType Type => TransportType.WebSocket;
        public bool IsServer { get; private set; }
        public bool IsRunning { get { lock (sync) return running; } }
        public ulong LocalPeerId { get; private set; }
        public IPEndPoint LocalEndPoint { get; private set; }
        public int MaxPayloadSize => options.MaxPacketSize - 16;
        public PacketBudget Budget { get; }
        public WebSocketListener Listener { get; private set; }
        public long BytesSent => Interlocked.Read(ref sentBytes);
        public long BytesReceived => Interlocked.Read(ref receivedBytes);
        public long SendsRejected => Interlocked.Read(ref sendRejected);
        public long InvalidPackets => Interlocked.Read(ref invalidPackets);
        public int PeerCount { get { lock (sync) return peers.Values.Count(x => x.Ready); } }
        public event Action<TransportEvent> EventReceived;
        /// <summary>Optional platform connector. Supply before starting; it returns an open WebSocket.
        /// This supports browser bridges without invoking unsupported native socket APIs.</summary>
        public Func<Uri, CancellationToken, Task<WebSocket>> ClientConnector
        { get => clientConnector; set { EnsureStopped(); clientConnector = value; } }
        public WebSocketGameTransport(TransportOptions options = null, Uri clientUri = null, WebSocketListener listener = null, WebSocketListenerOptions listenerOptions = null)
        {
            this.options = (options ?? new TransportOptions()).Snapshot(); if (clientUri != null) ValidateUri(clientUri);
            configuredUri = clientUri; externalListener = listener;
            this.listenerOptions = listenerOptions ?? new WebSocketListenerOptions { MaxConnections = this.options.MaxPeers };
            Budget = new PacketBudget(this.options.PathMtu, this.options.IpHeaderBytes == 20 ? System.Net.Sockets.AddressFamily.InterNetwork : System.Net.Sockets.AddressFamily.InterNetworkV6, this.options.ExtraPacketOverheadBytes, this.options.MaxPacketSize);
        }
        public void StartServer(IPEndPoint endpoint)
        {
            EnsureStopped(); if (endpoint == null) throw new ArgumentNullException(nameof(endpoint));
            if (externalListener != null && !externalListener.IsRunning) throw new InvalidOperationException("Start the caller-owned shared WebSocket listener before the game transport.");
            Begin(true);
            try
            {
                if (externalListener != null) Listener = externalListener;
                else
                {
                    Listener = new WebSocketListener(listenerOptions);
                    Listener.UpgradeAuthorizer = (request, token) => Task.FromResult(request.Path == "/ws/game");
                    Listener.WebSocketAccepted = AcceptWebSocketAsync; Listener.Start(endpoint);
                }
                LocalEndPoint = Listener.LocalEndPoint;
            }
            catch { Stop(); throw; }
        }
        public void StartClient(IPEndPoint endpoint, byte[] connectionData = null)
        {
            if (endpoint == null) throw new ArgumentNullException(nameof(endpoint));
            StartClient(configuredUri ?? new UriBuilder("ws", endpoint.Address.ToString(), endpoint.Port, "/ws/game").Uri, connectionData);
        }
        public void StartClient(Uri endpoint, byte[] connectionData = null)
        {
            EnsureStopped(); ValidateUri(endpoint);
            if ((connectionData?.Length ?? 0) > options.MaxConnectionDataSize) throw new ArgumentOutOfRangeException(nameof(connectionData));
            Begin(false); connecting = true; connectingAt = clock.ElapsedTicks;
            _ = ConnectClientAsync(endpoint, connectionData == null ? Array.Empty<byte>() : (byte[])connectionData.Clone(), generation, lifetime.Token);
        }
        private void Begin(bool server)
        {
            lock (sync)
            {
                lifetime?.Dispose(); lifetime = new CancellationTokenSource(); running = true; IsServer = server;
                LocalPeerId = 0; LocalEndPoint = null; generation++; nextPeer = 0; cursor = 0; events.Clear();
            }
        }
        private static void ValidateUri(Uri uri)
        { if (uri == null || !uri.IsAbsoluteUri || (uri.Scheme != "ws" && uri.Scheme != "wss") || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0) throw new ArgumentException("Supply an absolute ws:// or wss:// URI without credentials or fragment.", nameof(uri)); }
        private async Task ConnectClientAsync(Uri uri, byte[] data, uint expected, CancellationToken token)
        {
            WebSocket socket = null;
            try
            {
                if (clientConnector != null) socket = await clientConnector(uri, token).ConfigureAwait(true);
                else { var native = new ClientWebSocket(); socket = native; await native.ConnectAsync(uri, token).ConfigureAwait(true); }
                if (socket == null || socket.State != WebSocketState.Open) throw new WebSocketException();
                Peer peer;
                lock (sync)
                {
                    if (!running || generation != expected || token.IsCancellationRequested) { socket.Dispose(); return; }
                    connecting = false; peer = AddPeer(0, socket, null, token);
                    Enqueue(peer, Packet(Kind.Hello, Settings(data)));
                }
                await RunPeerAsync(peer).ConfigureAwait(true);
            }
            catch (Exception)
            {
                socket?.Dispose();
                lock (sync)
                    if (running && generation == expected && connecting)
                    { connecting = false; QueueEvent(new TransportEvent(TransportEventType.Disconnected, 0, reason: token.IsCancellationRequested ? "Connection cancelled" : "WebSocket connection failed"), null); }
            }
        }
        /// <summary>Route one authorized game upgrade here from a caller-owned shared listener.
        /// The returned task lasts until this game connection closes. Admission tickets are still
        /// checked through ConnectionValidator on Poll before a Connected event is delivered.</summary>
        public async Task AcceptWebSocketAsync(WebSocketHttpRequest request, WebSocket socket, CancellationToken token)
        {
            Peer peer;
            lock (sync)
            {
                if (!running || !IsServer || peers.Count >= options.MaxPeers) { socket.Abort(); return; }
                peer = AddPeer(checked(++nextPeer), socket, request?.RemoteEndPoint, token);
            }
            await RunPeerAsync(peer).ConfigureAwait(true);
        }
        private Peer AddPeer(ulong id, WebSocket socket, IPEndPoint endpoint, CancellationToken token)
        {
            var peer = new Peer { Id = id, Socket = socket, Endpoint = endpoint, Started = clock.ElapsedTicks, LastReceive = clock.ElapsedTicks, LastSend = clock.ElapsedTicks,
                Cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, token) };
            peers.Add(id, peer); schedule.Add(peer); return peer;
        }
        private async Task RunPeerAsync(Peer peer)
        {
            try { await Task.WhenAll(ReceiveAsync(peer), SendAsync(peer)).ConfigureAwait(true); }
            finally { Drop(peer, "WebSocket closed"); peer.Socket.Dispose(); peer.Cancellation.Dispose(); peer.Signal.Dispose(); }
        }
        private async Task ReceiveAsync(Peer peer)
        {
            var buffer = new byte[options.MaxPacketSize + 1]; int size = 0;
            try
            {
                while (peer.Active)
                {
                    var received = await peer.Socket.ReceiveAsync(new ArraySegment<byte>(buffer, size, buffer.Length - size), peer.Cancellation.Token).ConfigureAwait(true);
                    if (received.MessageType == WebSocketMessageType.Close) break;
                    if (received.MessageType != WebSocketMessageType.Binary || received.Count > options.MaxPacketSize - size) throw new InvalidDataException();
                    size += received.Count;
                    if (!received.EndOfMessage) { if (size >= options.MaxPacketSize) throw new InvalidDataException(); continue; }
                    if (size < 4) throw new InvalidDataException();
                    var packet = new byte[size]; Buffer.BlockCopy(buffer, 0, packet, 0, size); size = 0;
                    lock (sync)
                    {
                        if (!peer.Active) break;
                        if (peer.In.Count >= options.MaxPendingReliablePerPeer || packet.Length > options.MaxQueuedBytesPerPeer - peer.InBytes) throw new InvalidDataException();
                        peer.In.Enqueue(packet); peer.InBytes += packet.Length; peer.LastReceive = clock.ElapsedTicks;
                    }
                    Interlocked.Add(ref receivedBytes, packet.Length);
                    Interlocked.Increment(ref receivedPackets);
                }
            }
            catch (Exception error) { if (error is InvalidDataException) Interlocked.Increment(ref invalidPackets); }
            finally { Drop(peer, "WebSocket receive ended"); }
        }
        private async Task SendAsync(Peer peer)
        {
            try
            {
                while (peer.Active)
                {
                    await peer.Signal.WaitAsync(peer.Cancellation.Token).ConfigureAwait(true);
                    byte[] packet; lock (sync) { if (!peer.Active) break; if (peer.Out.Count == 0) continue; packet = peer.Out.Peek(); }
                    await peer.Socket.SendAsync(new ArraySegment<byte>(packet), WebSocketMessageType.Binary, true, peer.Cancellation.Token).ConfigureAwait(true);
                    lock (sync) { if (peer.Out.Count > 0 && ReferenceEquals(peer.Out.Peek(), packet)) { peer.Out.Dequeue(); peer.OutBytes -= packet.Length; } peer.LastSend = clock.ElapsedTicks; }
                    Interlocked.Add(ref sentBytes, packet.Length);
                    Interlocked.Increment(ref sentPackets);
                }
            }
            catch (Exception) { }
            finally { Drop(peer, "WebSocket send ended"); }
        }
        public void Poll(int maxEvents = 256)
        {
            if (disposed) throw new ObjectDisposedException(nameof(WebSocketGameTransport)); if (maxEvents < 1) throw new ArgumentOutOfRangeException(nameof(maxEvents));
            if (polling) throw new InvalidOperationException("Poll cannot be called recursively."); if (!IsRunning) return; polling = true;
            try
            {
                int emitted = Dispatch(maxEvents), work = 0;
                while (IsRunning && emitted < maxEvents && work++ < maxEvents)
                {
                    Peer peer; byte[] packet = null;
                    lock (sync)
                    {
                        if (schedule.Count == 0) break;
                        if (cursor >= schedule.Count) cursor = 0; peer = schedule[cursor++];
                        if (peer.In.Count > 0) { packet = peer.In.Dequeue(); peer.InBytes -= packet.Length; }
                    }
                    if (packet != null) Process(peer, packet);
                    lock (sync)
                    {
                        if (peer.Active)
                        {
                            double silence = Seconds(clock.ElapsedTicks - (peer.Ready ? peer.LastReceive : peer.Started));
                            if (silence > (peer.Ready ? options.PeerTimeout : options.ConnectTimeout).TotalSeconds) Drop(peer, "WebSocket peer timed out");
                            else if (peer.Ready && Seconds(clock.ElapsedTicks - peer.LastSend) >= options.HeartbeatInterval.TotalSeconds)
                            { var data = new byte[9]; Write64(data, 1, (ulong)clock.ElapsedTicks); if (Enqueue(peer, Packet(Kind.Heartbeat, data))) peer.LastSend = clock.ElapsedTicks; }
                        }
                    }
                    emitted += Dispatch(maxEvents - emitted);
                }
                lock (sync)
                    if (connecting && Seconds(clock.ElapsedTicks - connectingAt) >= options.ConnectTimeout.TotalSeconds)
                    { connecting = false; lifetime.Cancel(); QueueEvent(new TransportEvent(TransportEventType.Disconnected, 0, reason: "WebSocket connection timed out"), null); }
                Dispatch(maxEvents - emitted);
            }
            finally { polling = false; }
        }
        private void Process(Peer peer, byte[] packet)
        {
            try
            {
                if (packet[0] != 1 || packet[2] >= options.Channels || packet[3] > (byte)DeliveryMode.ReliableUnordered) throw new InvalidDataException();
                var kind = (Kind)packet[1];
                if (kind != Kind.Data && (packet[2] != 0 || packet[3] != 0)) throw new InvalidDataException();
                if (!peer.Ready)
                {
                    if (IsServer && kind == Kind.Hello)
                    {
                        if (packet.Length < 11 || Read16(packet, 9) > options.MaxConnectionDataSize || packet.Length != 11 + Read16(packet, 9) || !Compatible(packet, 4)) throw new InvalidDataException();
                        byte[] data = packet.Skip(11).ToArray(); bool accepted;
                        try { accepted = options.ConnectionValidator?.Invoke(peer.Endpoint, data) ?? true; } catch { accepted = false; }
                        lock (sync)
                        {
                            if (!running || !peer.Active || !peers.TryGetValue(peer.Id, out var active) || !ReferenceEquals(peer, active)) return;
                            if (!accepted) { Drop(peer, "Connection rejected"); return; }
                            var payload = new byte[13]; Write64(payload, 0, peer.Id); Buffer.BlockCopy(Settings(null), 0, payload, 8, 5);
                            if (!Enqueue(peer, Packet(Kind.Welcome, payload))) { Drop(peer, "Handshake queue exceeded"); return; }
                            peer.Ready = true; QueueEvent(new TransportEvent(TransportEventType.Connected, peer.Id), peer);
                        }
                        return;
                    }
                    if (!IsServer && kind == Kind.Welcome && packet.Length == 17 && Read64(packet, 4) != 0 && Compatible(packet, 12))
                    { lock (sync) { if (!peer.Active) return; LocalPeerId = Read64(packet, 4); peer.Ready = true; QueueEvent(new TransportEvent(TransportEventType.Connected, 0), peer); } return; }
                    throw new InvalidDataException();
                }
                if (kind == Kind.Data)
                {
                    if (packet.Length - 4 > MaxPayloadSize) throw new InvalidDataException();
                    lock (sync) { if (peer.Active) QueueEvent(new TransportEvent(TransportEventType.Data, peer.Id, packet.Skip(4).ToArray(), (DeliveryMode)packet[3], packet[2]), peer); }
                    return;
                }
                if (kind == Kind.Heartbeat && packet.Length == 13 && packet[4] <= 1)
                {
                    lock (sync)
                    {
                        if (packet[4] == 0) { packet[4] = 1; Enqueue(peer, packet); }
                        else { ulong stamp = Read64(packet, 5); if (stamp <= (ulong)clock.ElapsedTicks) peer.RttMilliseconds = Seconds(clock.ElapsedTicks - (long)stamp) * 1000; }
                    }
                    return;
                }
                if (kind == Kind.Disconnect && packet.Length <= 132) { Drop(peer, "Remote disconnected"); return; }
                throw new InvalidDataException();
            }
            catch (Exception error) when (error is InvalidDataException || error is IndexOutOfRangeException || error is ArgumentException)
            { Interlocked.Increment(ref invalidPackets); Drop(peer, "Invalid WebSocket game packet"); }
        }
        private byte[] Settings(byte[] connectionData)
        {
            var data = connectionData ?? Array.Empty<byte>(); var bytes = new byte[7 + data.Length];
            Write16(bytes, 0, options.MaxPacketSize); bytes[2] = (byte)options.Channels; Write16(bytes, 3, Budget.MaxFramePayloadBytes); Write16(bytes, 5, data.Length);
            Buffer.BlockCopy(data, 0, bytes, 7, data.Length); return bytes;
        }
        private bool Compatible(byte[] packet, int offset) => Read16(packet, offset) == options.MaxPacketSize && packet[offset + 2] == options.Channels && Read16(packet, offset + 3) == Budget.MaxFramePayloadBytes;
        public bool Send(ulong peerId, ArraySegment<byte> payload, DeliveryMode delivery = DeliveryMode.ReliableUnordered, byte channel = 0)
        {
            if (disposed) throw new ObjectDisposedException(nameof(WebSocketGameTransport));
            if (payload.Count > MaxPayloadSize || (payload.Array == null && payload.Count != 0)) throw new ArgumentOutOfRangeException(nameof(payload));
            if (channel >= options.Channels || (byte)delivery > (byte)DeliveryMode.ReliableUnordered) throw new ArgumentOutOfRangeException(nameof(channel));
            lock (sync)
            {
                if (!running || !peers.TryGetValue(peerId, out var peer) || !peer.Active || !peer.Ready) { Interlocked.Increment(ref sendRejected); return false; }
                var bytes = new byte[payload.Count + 4]; bytes[0] = 1; bytes[1] = (byte)Kind.Data; bytes[2] = channel; bytes[3] = (byte)delivery;
                if (payload.Count > 0) Buffer.BlockCopy(payload.Array, payload.Offset, bytes, 4, payload.Count);
                if (!Enqueue(peer, bytes)) { Interlocked.Increment(ref sendRejected); return false; } return true;
            }
        }
        private bool Enqueue(Peer peer, byte[] packet)
        {
            if (!peer.Active || peer.Out.Count >= options.MaxPendingReliablePerPeer || packet.Length > options.MaxQueuedBytesPerPeer - peer.OutBytes) return false;
            peer.Out.Enqueue(packet); peer.OutBytes += packet.Length; peer.Signal.Release(); return true;
        }
        private void QueueEvent(TransportEvent value, Peer peer)
        {
            // Data is produced only by bounded Poll work. Closed, unadmitted server sockets never queue an event.
            if (events.Count >= options.MaxPeers * 2 + 16) return;
            events.Enqueue(new QueuedEvent { Value = value, Peer = peer, Generation = generation });
        }
        private int Dispatch(int count)
        {
            int emitted = 0;
            while (emitted < count && IsRunning)
            {
                QueuedEvent item;
                lock (sync) { if (events.Count == 0) break; item = events.Dequeue(); if (item.Generation != generation || (item.Value.Type != TransportEventType.Disconnected && item.Peer != null && !item.Peer.Active)) continue; }
                EventReceived?.Invoke(item.Value); emitted++;
                lock (sync)
                    if (item.Generation == generation && item.Value.Type == TransportEventType.Disconnected && !IsServer && peers.Count == 0 && !connecting)
                        running = false;
            }
            return emitted;
        }
        private void Drop(Peer peer, string reason)
        {
            lock (sync)
            {
                if (!peer.Active) return; peer.Active = false; peers.Remove(peer.Id); schedule.Remove(peer);
                if (!IsServer) LocalPeerId = 0;
                peer.Out.Clear(); peer.In.Clear(); peer.OutBytes = peer.InBytes = 0;
                try { peer.Cancellation.Cancel(); peer.Socket.Abort(); } catch { }
                if (running && (peer.Ready || !IsServer)) QueueEvent(new TransportEvent(TransportEventType.Disconnected, peer.Id, reason: reason), peer);
            }
        }
        public bool TryGetPeerStatistics(ulong peerId, out double rttMilliseconds, out int queuedBytes)
        {
            lock (sync) { if (peers.TryGetValue(peerId, out var peer)) { rttMilliseconds = peer.RttMilliseconds; queuedBytes = peer.OutBytes; return true; } rttMilliseconds = 0; queuedBytes = 0; return false; }
        }
        public TransportMetricsSnapshot GetTransportMetrics(int maxPeerDetails = 64)
        {
            TransportMetrics.ValidateDetailLimit(maxPeerDetails);
            lock (sync)
            {
                var details = new List<PeerTransportMetrics>(); long queued = 0; int active = 0;
                foreach (var entry in peers)
                {
                    queued += entry.Value.OutBytes; if (!entry.Value.Ready) continue; active++;
                    if (details.Count < maxPeerDetails) details.Add(new PeerTransportMetrics(entry.Key, entry.Value.RttMilliseconds > 0 ? (double?)entry.Value.RttMilliseconds : null, entry.Value.Out.Count, entry.Value.OutBytes));
                }
                return new TransportMetricsSnapshot(Type, BytesSent, BytesReceived, Interlocked.Read(ref sentPackets), Interlocked.Read(ref receivedPackets), 0, InvalidPackets, SendsRejected, active, queued, details);
            }
        }
        public void Disconnect(ulong peerId, string reason = "Disconnected") { lock (sync) if (peers.TryGetValue(peerId, out var peer)) Drop(peer, reason); }
        public void Stop()
        {
            WebSocketListener owned = null;
            lock (sync)
            {
                if (!running) return; running = false; connecting = false; generation++; lifetime.Cancel();
                foreach (var peer in peers.Values.ToArray()) Drop(peer, "Stopped"); peers.Clear(); schedule.Clear(); events.Clear(); LocalPeerId = 0;
                if (externalListener == null) owned = Listener; Listener = null;
            }
            owned?.Dispose();
        }
        private void EnsureStopped() { if (disposed) throw new ObjectDisposedException(nameof(WebSocketGameTransport)); if (IsRunning) throw new InvalidOperationException("Stop the transport before starting or reconfiguring it."); }
        public void Dispose() { if (disposed) return; Stop(); disposed = true; }
        private static double Seconds(long ticks) => ticks / (double)Stopwatch.Frequency;
        private static byte[] Packet(Kind kind, byte[] payload) { var bytes = new byte[4 + payload.Length]; bytes[0] = 1; bytes[1] = (byte)kind; Buffer.BlockCopy(payload, 0, bytes, 4, payload.Length); return bytes; }
        private static int Read16(byte[] bytes, int at) => bytes[at] | bytes[at + 1] << 8;
        private static void Write16(byte[] bytes, int at, int value) { bytes[at] = (byte)value; bytes[at + 1] = (byte)(value >> 8); }
        private static ulong Read64(byte[] bytes, int at) { ulong value = 0; for (int i = 0; i < 8; i++) value |= (ulong)bytes[at + i] << (i * 8); return value; }
        private static void Write64(byte[] bytes, int at, ulong value) { for (int i = 0; i < 8; i++) bytes[at + i] = (byte)(value >> (i * 8)); }
    }

    // Synchronous continuations let the browser bridge operate entirely from JavaScript's main
    // event loop. SemaphoreSlim's async waiters may require managed worker threads on WebGL.
    internal sealed class WebSocketAsyncSignal : IDisposable
    {
        private readonly object sync = new object();
        private int available;
        private TaskCompletionSource<bool> waiting;
        internal Task WaitAsync(CancellationToken token)
        {
            TaskCompletionSource<bool> source;
            lock (sync)
            {
                if (token.IsCancellationRequested) return Task.FromCanceled(token);
                if (available > 0) { available--; return Task.CompletedTask; }
                if (waiting != null) throw new InvalidOperationException("Only one sender can wait for this signal.");
                waiting = source = new TaskCompletionSource<bool>();
            }
            var registration = token.Register(() =>
            {
                lock (sync) { if (ReferenceEquals(waiting, source)) waiting = null; }
                source.TrySetCanceled();
            });
            return Await(source.Task, registration);
        }
        private static async Task Await(Task task, CancellationTokenRegistration registration)
        { try { await task.ConfigureAwait(true); } finally { registration.Dispose(); } }
        internal void Release()
        {
            TaskCompletionSource<bool> source;
            lock (sync) { source = waiting; waiting = null; if (source == null) available++; }
            source?.TrySetResult(true);
        }
        public void Dispose()
        { TaskCompletionSource<bool> source; lock (sync) { source = waiting; waiting = null; available = 0; } source?.TrySetCanceled(); }
    }
}
