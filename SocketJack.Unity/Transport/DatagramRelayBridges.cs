using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace SocketJack.Multiplayer.Transport
{
    internal sealed class RelayDatagramConnection : IDisposable
    {
        private readonly DatagramRelayAllocation allocation;
        private readonly IPEndPoint endpoint;
        private readonly byte[] key;
        private readonly RelaySequenceWindow window = new RelaySequenceWindow();
        private readonly TaskCompletionSource<bool> opened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private UdpClient socket;
        private Task receive, keepAlive;
        private long sequence;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private double lastAcknowledged;
        private volatile bool reachable;
        internal bool IsReady => reachable && !cancellation.IsCancellationRequested;
        internal Func<ulong, byte[], Task> DataReceived;
        internal RelayDatagramConnection(DatagramRelayAllocation allocation, IPEndPoint endpoint)
        { this.allocation = allocation ?? throw new ArgumentNullException(nameof(allocation)); this.endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint)); key = allocation.Secret; }
        internal async Task StartAsync(CancellationToken token)
        {
            if (socket != null) throw new InvalidOperationException("Relay connection already started.");
            socket = new UdpClient(endpoint.AddressFamily); socket.Connect(endpoint); receive = ReceiveAsync();
            var deadline = Stopwatch.StartNew(); byte[] nonce = RelayDatagram.Random(16);
            try
            {
                while (!opened.Task.IsCompleted)
                {
                    token.ThrowIfCancellationRequested(); if (deadline.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Datagram relay endpoint challenge timed out.");
                    await SendAsync(1, 0, nonce).ConfigureAwait(false);
                    await Task.WhenAny(opened.Task, Task.Delay(200, token)).ConfigureAwait(false);
                }
                await opened.Task.ConfigureAwait(false); keepAlive = KeepAliveAsync();
            }
            catch { Dispose(); throw; }
        }
        internal async Task SendDataAsync(ulong target, byte[] bytes)
        {
            if (!IsReady) return;
            await SendAsync(4, target, bytes).ConfigureAwait(false);
        }
        private async Task SendAsync(byte kind, ulong target, byte[] bytes)
        {
            var active = socket; if (active == null || cancellation.IsCancellationRequested) return;
            var packet = RelayDatagram.Encode(key, kind, allocation.AllocationId, target, unchecked((ulong)Interlocked.Increment(ref sequence)), bytes);
            try { await active.SendAsync(packet, packet.Length).ConfigureAwait(false); }
            catch (Exception error) when (error is ObjectDisposedException || error is SocketException) { if (!cancellation.IsCancellationRequested) throw; }
        }
        private async Task ReceiveAsync()
        {
            while (!cancellation.IsCancellationRequested)
            {
                UdpReceiveResult packet;
                try { packet = await socket.ReceiveAsync().ConfigureAwait(false); }
                catch (Exception error) when (error is ObjectDisposedException || error is SocketException) { if (cancellation.IsCancellationRequested) break; continue; }
                if (!RelayDatagram.TryDecode(key, packet.Buffer, out var frame) || frame.To != allocation.AllocationId || !window.Accept(frame.Sequence)) continue;
                if (frame.Kind == 2 && frame.From == 0 && frame.Payload.Length == 16) await SendAsync(3, 0, frame.Payload).ConfigureAwait(false);
                else if ((frame.Kind == 3 || frame.Kind == 6) && frame.From == 0 && frame.Payload.Length == 0)
                { lastAcknowledged = clock.Elapsed.TotalSeconds; reachable = true; opened.TrySetResult(true); }
                else if (frame.Kind == 4 && opened.Task.IsCompletedSuccessfully && DataReceived != null)
                    try { await DataReceived(frame.From, frame.Payload).ConfigureAwait(false); } catch (Exception error) when (error is SocketException || error is ObjectDisposedException) { }
            }
            opened.TrySetCanceled();
        }
        private async Task KeepAliveAsync()
        {
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    await Task.Delay(5000, cancellation.Token).ConfigureAwait(false);
                    if (clock.Elapsed.TotalSeconds - lastAcknowledged > 10) reachable = false;
                    await SendAsync(reachable ? (byte)6 : (byte)1, 0, RelayDatagram.Random(16)).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
        }
        public void Dispose() { reachable = false; cancellation.Cancel(); socket?.Dispose(); }
        internal async Task StopAsync()
        {
            Dispose(); await Task.WhenAll(new[] { receive, keepAlive }.Where(task => task != null)).ConfigureAwait(false);
            CryptographicOperations.ZeroMemory(key);
        }
    }
    /// <summary>Local UDP endpoint for one native game client. The relay preserves the original datagram bytes.</summary>
    public sealed class DatagramRelayClientBridge : IDisposable
    {
        private readonly UdpClient local;
        private readonly RelayDatagramConnection relay;
        private readonly object sync = new object();
        private IPEndPoint gameClient;
        private Task receive;
        private bool disposed;
        public IPEndPoint LocalEndPoint { get; }
        public bool IsReady => !disposed && relay.IsReady;
        public DatagramRelayClientBridge(DatagramRelayAllocation allocation, IPEndPoint relayEndPoint)
        {
            if (allocation == null || allocation.IsHost) throw new ArgumentException("A client allocation is required.");
            local = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); LocalEndPoint = (IPEndPoint)local.Client.LocalEndPoint;
            relay = new RelayDatagramConnection(allocation, relayEndPoint); relay.DataReceived = FromRelay;
        }
        public async Task StartAsync(CancellationToken cancellationToken = default)
        { if (disposed || receive != null) throw new InvalidOperationException("Bridge is disposed or already started."); await relay.StartAsync(cancellationToken).ConfigureAwait(false); receive = ReceiveAsync(); }
        private async Task ReceiveAsync()
        {
            while (!disposed)
            {
                UdpReceiveResult packet;
                try { packet = await local.ReceiveAsync().ConfigureAwait(false); }
                catch (Exception error) when (error is ObjectDisposedException || error is SocketException) { if (disposed) break; continue; }
                if (packet.Buffer.Length == 0 || packet.Buffer.Length > DatagramRelayServer.MaximumPayloadBytes || !IPAddress.IsLoopback(packet.RemoteEndPoint.Address)) continue;
                lock (sync) { if (gameClient == null) gameClient = packet.RemoteEndPoint; if (!gameClient.Equals(packet.RemoteEndPoint)) continue; }
                await relay.SendDataAsync(0, packet.Buffer).ConfigureAwait(false);
            }
        }
        private async Task FromRelay(ulong source, byte[] bytes)
        {
            IPEndPoint target; lock (sync) target = gameClient;
            if (source != 0 && target != null && !disposed) await local.SendAsync(bytes, bytes.Length, target).ConfigureAwait(false);
        }
        public void Dispose() { if (disposed) return; disposed = true; local.Dispose(); relay.Dispose(); }
        public async Task StopAsync() { Dispose(); if (receive != null) await receive.ConfigureAwait(false); await relay.StopAsync().ConfigureAwait(false); }
    }
    /// <summary>Host-side gateway. Every remote allocation gets a distinct loopback UDP socket, preserving native cookies and sequence histories.</summary>
    public sealed class DatagramRelayHostBridge : IDisposable
    {
        private sealed class Peer { internal ulong Id; internal UdpClient Socket; internal Task Receive; internal DateTime Activity; }
        private readonly object sync = new object();
        private readonly Dictionary<ulong, Peer> peers = new Dictionary<ulong, Peer>();
        private readonly RelayDatagramConnection relay;
        private readonly IPEndPoint gameServer;
        private readonly int maximumClients;
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private Task maintenance;
        private bool disposed;
        public bool IsReady => !disposed && relay.IsReady;
        public int ConnectedGateways { get { lock (sync) return peers.Count; } }
        public DatagramRelayHostBridge(DatagramRelayAllocation allocation, IPEndPoint relayEndPoint, IPEndPoint localGameServer, int maxClients = 256)
        {
            if (allocation == null || !allocation.IsHost || localGameServer == null || maxClients < 1 || maxClients > 65535) throw new ArgumentException("A host allocation and bounded local game endpoint are required.");
            if (!IPAddress.IsLoopback(localGameServer.Address) && !localGameServer.Address.Equals(IPAddress.Any) && !localGameServer.Address.Equals(IPAddress.IPv6Any))
                throw new ArgumentException("The host gateway may route only to its local game listener.");
            gameServer = new IPEndPoint(localGameServer.Address.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Loopback : IPAddress.Loopback, localGameServer.Port);
            maximumClients = maxClients; relay = new RelayDatagramConnection(allocation, relayEndPoint); relay.DataReceived = FromRelay;
        }
        public async Task StartAsync(CancellationToken cancellationToken = default)
        { if (disposed || maintenance != null) throw new InvalidOperationException("Bridge is disposed or already started."); await relay.StartAsync(cancellationToken).ConfigureAwait(false); maintenance = MaintainAsync(); }
        private async Task FromRelay(ulong source, byte[] bytes)
        {
            Peer peer;
            lock (sync)
            {
                if (disposed || source == 0) return;
                if (!peers.TryGetValue(source, out peer))
                {
                    if (peers.Count >= maximumClients) return;
                    var socket = new UdpClient(new IPEndPoint(gameServer.Address, 0)); socket.Connect(gameServer);
                    peer = new Peer { Id = source, Socket = socket }; peers.Add(source, peer); peer.Receive = ReceivePeerAsync(peer);
                }
                peer.Activity = DateTime.UtcNow;
            }
            await peer.Socket.SendAsync(bytes, bytes.Length).ConfigureAwait(false);
        }
        private async Task ReceivePeerAsync(Peer peer)
        {
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    UdpReceiveResult packet = await peer.Socket.ReceiveAsync().ConfigureAwait(false);
                    if (packet.Buffer.Length == 0 || packet.Buffer.Length > DatagramRelayServer.MaximumPayloadBytes) continue;
                    lock (sync) peer.Activity = DateTime.UtcNow; await relay.SendDataAsync(peer.Id, packet.Buffer).ConfigureAwait(false);
                }
            }
            catch (Exception error) when (error is ObjectDisposedException || error is SocketException) { }
            finally { lock (sync) if (peers.TryGetValue(peer.Id, out var current) && ReferenceEquals(current, peer)) peers.Remove(peer.Id); peer.Socket.Dispose(); }
        }
        private async Task MaintainAsync()
        {
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    await Task.Delay(1000, cancellation.Token).ConfigureAwait(false);
                    lock (sync) foreach (var peer in peers.Values.Where(peer => DateTime.UtcNow - peer.Activity > TimeSpan.FromSeconds(30)).ToArray())
                    { peers.Remove(peer.Id); peer.Socket.Dispose(); }
                }
            }
            catch (OperationCanceledException) { }
        }
        public void Dispose()
        { lock (sync) { if (disposed) return; disposed = true; cancellation.Cancel(); foreach (var peer in peers.Values) peer.Socket.Dispose(); } relay.Dispose(); }
        public async Task StopAsync()
        {
            Task[] tasks; lock (sync) tasks = peers.Values.Select(peer => peer.Receive).Where(task => task != null).ToArray();
            Dispose(); if (maintenance != null) await maintenance.ConfigureAwait(false); await Task.WhenAll(tasks).ConfigureAwait(false); await relay.StopAsync().ConfigureAwait(false);
        }
    }
    /// <summary>Game-side client route. Configure and await a bridge before starting the session.</summary>
    public sealed class DatagramRelayGameTransport : IGameTransport, IPacketBudgetSource, ITransportMetricsSource
    {
        private readonly UdpGameTransport inner;
        private readonly DatagramRelayClientBridge bridge;
        public DatagramRelayGameTransport(DatagramRelayClientBridge bridge, TransportOptions options = null)
        { this.bridge = bridge ?? throw new ArgumentNullException(nameof(bridge)); inner = new UdpGameTransport(ReserveOverhead(options)); inner.EventReceived += Forward; }
        public static TransportOptions ReserveOverhead(TransportOptions options = null)
        {
            var copy = (options ?? new TransportOptions()).Snapshot(); copy.IpHeaderBytes = 40; copy.ExtraPacketOverheadBytes = checked(copy.ExtraPacketOverheadBytes + DatagramRelayServer.HeaderBytes);
            copy.MaxPacketSize = Math.Min(DatagramRelayServer.MaximumPayloadBytes, Math.Min(copy.MaxPacketSize, copy.PathMtu - copy.IpHeaderBytes - PacketBudget.UdpHeaderSize - copy.ExtraPacketOverheadBytes)); return copy;
        }
        public TransportType Type => TransportType.Udp;
        public bool IsServer => inner.IsServer;
        public bool IsRunning => inner.IsRunning;
        public ulong LocalPeerId => inner.LocalPeerId;
        public IPEndPoint LocalEndPoint => inner.LocalEndPoint;
        public int MaxPayloadSize => inner.MaxPayloadSize;
        public PacketBudget Budget => inner.Budget;
        public event Action<TransportEvent> EventReceived;
        private void Forward(TransportEvent value) => EventReceived?.Invoke(value);
        public void StartServer(IPEndPoint endpoint) => throw new InvalidOperationException("Use a reserved-budget UDP listener and DatagramRelayHostBridge for a host.");
        public void StartClient(IPEndPoint endpoint, byte[] connectionData = null)
        { if (!bridge.IsReady) throw new InvalidOperationException("Await the authenticated relay bridge before joining."); inner.StartClient(bridge.LocalEndPoint, connectionData); }
        public void Poll(int maxEvents = 256) => inner.Poll(maxEvents);
        public bool Send(ulong peerId, ArraySegment<byte> payload, DeliveryMode delivery = DeliveryMode.ReliableUnordered, byte channel = 0) => inner.Send(peerId, payload, delivery, channel);
        public void Disconnect(ulong peerId, string reason = "Disconnected") => inner.Disconnect(peerId, reason);
        public void Stop() => inner.Stop();
        public void Dispose() { inner.EventReceived -= Forward; inner.Dispose(); }
        public TransportMetricsSnapshot GetTransportMetrics(int maxPeerDetails = 64) => inner.GetTransportMetrics(maxPeerDetails);
    }
}
