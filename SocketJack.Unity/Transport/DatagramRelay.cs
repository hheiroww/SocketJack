using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace SocketJack.Multiplayer.Transport
{
    public sealed class DatagramRelayAllocation
    {
        private readonly byte[] secret;
        public ulong AllocationId { get; }
        public string SessionId { get; }
        public bool IsHost { get; }
        public DateTime ExpiresUtc { get; }
        public byte[] Secret => (byte[])secret.Clone();
        public DatagramRelayAllocation(ulong allocationId, string sessionId, bool isHost, byte[] secret, DateTime expiresUtc)
        {
            if (allocationId == 0 || string.IsNullOrWhiteSpace(sessionId) || sessionId.Length > 128 || secret == null || secret.Length != 32 || expiresUtc <= DateTime.UtcNow)
                throw new ArgumentException("Invalid authenticated datagram relay allocation.");
            AllocationId = allocationId; SessionId = sessionId; IsHost = isHost; this.secret = (byte[])secret.Clone(); ExpiresUtc = expiresUtc.ToUniversalTime();
        }
    }
    public sealed class DatagramRelayOptions
    {
        public int MaxAllocations { get; set; } = 8192;
        public int MaxSessions { get; set; } = 1024;
        public int MaxClientsPerSession { get; set; } = 256;
        public int BytesPerSecondPerAllocation { get; set; } = 256 * 1024;
        public int PacketsPerSecondPerAllocation { get; set; } = 1000;
        public int BytesPerSecondPerHost { get; set; } = 32 * 1024 * 1024;
        public int PacketsPerSecondPerHost { get; set; } = 65536;
        public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(2);
        public TimeSpan MaxLifetime { get; set; } = TimeSpan.FromMinutes(30);
        internal DatagramRelayOptions Snapshot()
        {
            if (MaxAllocations < 2 || MaxAllocations > 65535 || MaxSessions < 1 || MaxSessions > MaxAllocations || MaxClientsPerSession < 1 ||
                MaxClientsPerSession > MaxAllocations || BytesPerSecondPerAllocation < 1024 || PacketsPerSecondPerAllocation < 1 || BytesPerSecondPerHost < 1024 || PacketsPerSecondPerHost < 1 ||
                IdleTimeout <= TimeSpan.Zero || MaxLifetime <= TimeSpan.Zero || MaxLifetime > TimeSpan.FromDays(1)) throw new ArgumentOutOfRangeException(nameof(DatagramRelayOptions));
            return (DatagramRelayOptions)MemberwiseClone();
        }
    }
    internal sealed class RelaySequenceWindow
    {
        private ulong newest, bits;
        internal bool Accept(ulong value)
        {
            if (value == 0) return false;
            if (bits == 0) { newest = value; bits = 1; return true; }
            if (value > newest) { ulong distance = value - newest; bits = distance >= 64 ? 1 : (bits << (int)distance) | 1; newest = value; return true; }
            ulong age = newest - value; if (age >= 64) return false;
            ulong flag = 1UL << (int)age; if ((bits & flag) != 0) return false; bits |= flag; return true;
        }
    }
    internal sealed class RelayDatagram
    {
        internal const int HeaderBytes = 48;
        internal const int MaximumPayload = 902; // IPv6(40) + UDP(8) + relay(48) + payload <= 998.
        internal byte Kind;
        internal ulong From, To, Sequence;
        internal byte[] Payload;
        internal static byte[] Encode(byte[] key, byte kind, ulong from, ulong to, ulong sequence, byte[] payload)
        {
            payload = payload ?? Array.Empty<byte>();
            if (payload.Length > MaximumPayload) throw new ArgumentOutOfRangeException(nameof(payload), "Relayed datagram exceeds the full packet budget.");
            var packet = new byte[HeaderBytes + payload.Length];
            using (var stream = new MemoryStream(packet, true)) using (var writer = new BinaryWriter(stream))
            { writer.Write(0x52444a53u); writer.Write((byte)1); writer.Write(kind); writer.Write((ushort)payload.Length); writer.Write(from); writer.Write(to); writer.Write(sequence); }
            Buffer.BlockCopy(payload, 0, packet, HeaderBytes, payload.Length);
            byte[] digest = Digest(key, packet); Buffer.BlockCopy(digest, 0, packet, 32, 16); return packet;
        }
        internal static bool TryDecode(byte[] key, byte[] packet, out RelayDatagram value)
        {
            value = null;
            if (packet == null || packet.Length < HeaderBytes || packet.Length > HeaderBytes + MaximumPayload || BitConverter.ToUInt32(packet, 0) != 0x52444a53u || packet[4] != 1 || packet[5] < 1 || packet[5] > 6 || BitConverter.ToUInt16(packet, 6) != packet.Length - HeaderBytes) return false;
            byte[] digest = Digest(key, packet);
            if (!CryptographicOperations.FixedTimeEquals(new ReadOnlySpan<byte>(digest, 0, 16), new ReadOnlySpan<byte>(packet, 32, 16))) return false;
            var payload = new byte[packet.Length - HeaderBytes]; Buffer.BlockCopy(packet, HeaderBytes, payload, 0, payload.Length);
            value = new RelayDatagram { Kind = packet[5], From = BitConverter.ToUInt64(packet, 8), To = BitConverter.ToUInt64(packet, 16), Sequence = BitConverter.ToUInt64(packet, 24), Payload = payload }; return true;
        }
        private static byte[] Digest(byte[] key, byte[] packet)
        {
            using (var hmac = new HMACSHA256(key))
            {
                hmac.TransformBlock(packet, 0, 32, null, 0);
                hmac.TransformFinalBlock(packet, HeaderBytes, packet.Length - HeaderBytes); return hmac.Hash;
            }
        }
        internal static byte[] Random(int length) { var result = new byte[length]; using (var random = RandomNumberGenerator.Create()) random.GetBytes(result); return result; }
    }
    /// <summary>Self-hosted, authenticated UDP forwarding. Credentials are issued over an authenticated TLS API.
    /// An endpoint must answer a random challenge before receiving forwarded traffic. Sessions never route arbitrary addresses.
    /// Payloads remain opaque native SocketJack UDP datagrams; this authenticates routing, not game-payload encryption.</summary>
    public sealed class DatagramRelayServer : IDisposable
    {
        public const int HeaderBytes = RelayDatagram.HeaderBytes;
        public const int MaximumPayloadBytes = RelayDatagram.MaximumPayload;
        private sealed class Allocation
        {
            internal DatagramRelayAllocation Public;
            internal byte[] Key, Challenge;
            internal IPEndPoint Endpoint, PendingEndpoint;
            internal DateTime LastSeen, LastBudget, ChallengeAt;
            internal double Bytes, Packets;
            internal ulong SendSequence;
            internal readonly RelaySequenceWindow Sequence = new RelaySequenceWindow();
        }
        private sealed class Session { internal ulong Host; internal readonly HashSet<ulong> Clients = new HashSet<ulong>(); }
        private readonly object sync = new object();
        private readonly DatagramRelayOptions options;
        private readonly Dictionary<ulong, Allocation> allocations = new Dictionary<ulong, Allocation>();
        private readonly Dictionary<string, Session> sessions = new Dictionary<string, Session>(StringComparer.Ordinal);
        private UdpClient socket;
        private CancellationTokenSource cancellation;
        private Task receive, maintenance;
        private ulong nextAllocation;
        private bool stopping;
        public IPEndPoint LocalEndPoint { get; private set; }
        public bool IsRunning { get { lock (sync) return socket != null; } }
        public int AllocationCount { get { lock (sync) return allocations.Count; } }
        public DatagramRelayServer(DatagramRelayOptions options = null) { this.options = (options ?? new DatagramRelayOptions()).Snapshot(); }
        public void Start(IPEndPoint endpoint)
        {
            if (endpoint == null) throw new ArgumentNullException(nameof(endpoint));
            lock (sync)
            {
                if (stopping || socket != null || (receive != null && !receive.IsCompleted) || (maintenance != null && !maintenance.IsCompleted)) throw new InvalidOperationException("Await StopAsync before starting or restarting the datagram relay.");
                socket = new UdpClient(endpoint); socket.Client.ReceiveBufferSize = 1024 * 1024; socket.Client.SendBufferSize = 1024 * 1024;
                LocalEndPoint = (IPEndPoint)socket.Client.LocalEndPoint; cancellation = new CancellationTokenSource();
                receive = ReceiveAsync(socket, cancellation.Token); maintenance = MaintainAsync(cancellation.Token);
            }
        }
        public DatagramRelayAllocation CreateAllocation(string sessionId, bool isHost, TimeSpan? lifetime = null)
        {
            if (string.IsNullOrWhiteSpace(sessionId) || sessionId.Length > 128) throw new ArgumentException("Supply a bounded session identifier.");
            var lease = lifetime ?? options.MaxLifetime; if (lease <= TimeSpan.Zero || lease > options.MaxLifetime) throw new ArgumentOutOfRangeException(nameof(lifetime));
            lock (sync)
            {
                if (socket == null) throw new InvalidOperationException("Start the relay before issuing allocations.");
                Prune(DateTime.UtcNow);
                if (allocations.Count >= options.MaxAllocations) throw new InvalidOperationException("Relay allocation capacity reached.");
                if (!sessions.TryGetValue(sessionId, out var session))
                { if (!isHost || sessions.Count >= options.MaxSessions) throw new InvalidOperationException("Create a host session first or free relay capacity."); sessions.Add(sessionId, session = new Session()); }
                if (isHost ? session.Host != 0 : session.Clients.Count >= options.MaxClientsPerSession) throw new InvalidOperationException("Duplicate host or relay session capacity reached.");
                ulong id = checked(++nextAllocation); var now = DateTime.UtcNow;
                var credentials = new DatagramRelayAllocation(id, sessionId, isHost, RelayDatagram.Random(32), now + lease);
                allocations.Add(id, new Allocation { Public = credentials, Key = credentials.Secret, LastSeen = now, LastBudget = now,
                    Bytes = isHost ? options.BytesPerSecondPerHost : options.BytesPerSecondPerAllocation, Packets = isHost ? options.PacketsPerSecondPerHost : options.PacketsPerSecondPerAllocation });
                if (isHost) session.Host = id; else session.Clients.Add(id); return credentials;
            }
        }
        public void Revoke(ulong id) { lock (sync) Remove(id); }
        public DatagramRelayAllocation Renew(ulong allocationId, TimeSpan? lifetime = null)
        {
            var lease = lifetime ?? options.MaxLifetime; if (lease <= TimeSpan.Zero || lease > options.MaxLifetime) throw new ArgumentOutOfRangeException(nameof(lifetime));
            lock (sync)
            {
                if (!allocations.TryGetValue(allocationId, out var allocation) || Expired(allocation, DateTime.UtcNow))
                { Remove(allocationId); throw new InvalidOperationException("Relay allocation expired or was revoked."); }
                allocation.Public = new DatagramRelayAllocation(allocationId, allocation.Public.SessionId, allocation.Public.IsHost, allocation.Key, DateTime.UtcNow + lease);
                return allocation.Public;
            }
        }
        public void CloseSession(string sessionId)
        {
            lock (sync) if (sessions.TryGetValue(sessionId, out var session))
            { foreach (ulong id in session.Clients.ToArray()) Remove(id); if (session.Host != 0) Remove(session.Host); }
        }
        private async Task ReceiveAsync(UdpClient listener, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                UdpReceiveResult received;
                try { received = await listener.ReceiveAsync().ConfigureAwait(false); }
                catch (Exception error) when (error is ObjectDisposedException || error is SocketException) { if (token.IsCancellationRequested) break; continue; }
                byte[] reply = null; IPEndPoint target = null;
                lock (sync)
                {
                    byte[] packet = received.Buffer;
                    if (packet.Length < HeaderBytes || packet.Length > HeaderBytes + MaximumPayloadBytes || !allocations.TryGetValue(BitConverter.ToUInt64(packet, 8), out var source)) continue;
                    var now = DateTime.UtcNow;
                    if (Expired(source, now)) { Remove(source.Public.AllocationId); continue; }
                    if (!RelayDatagram.TryDecode(source.Key, packet, out var frame) || frame.From != source.Public.AllocationId || !source.Sequence.Accept(frame.Sequence)) continue;
                    double elapsed = Math.Max(0, (now - source.LastBudget).TotalSeconds); source.LastBudget = now;
                    int byteRate = source.Public.IsHost ? options.BytesPerSecondPerHost : options.BytesPerSecondPerAllocation;
                    int packetRate = source.Public.IsHost ? options.PacketsPerSecondPerHost : options.PacketsPerSecondPerAllocation;
                    source.Bytes = Math.Min(byteRate, source.Bytes + elapsed * byteRate);
                    source.Packets = Math.Min(packetRate, source.Packets + elapsed * packetRate);
                    if (source.Bytes < packet.Length || source.Packets < 1) continue;
                    source.Bytes -= packet.Length; source.Packets--; source.LastSeen = now;
                    if (frame.Kind == 1)
                    {
                        if (frame.To != 0 || frame.Payload.Length != 16) continue;
                        target = received.RemoteEndPoint;
                        if (source.Endpoint != null && source.Endpoint.Equals(received.RemoteEndPoint)) reply = Reply(source, 3, 0, Array.Empty<byte>());
                        else
                        {
                            if (source.Challenge == null || !received.RemoteEndPoint.Equals(source.PendingEndpoint) || now - source.ChallengeAt > TimeSpan.FromSeconds(10))
                            { source.PendingEndpoint = received.RemoteEndPoint; source.Challenge = RelayDatagram.Random(16); source.ChallengeAt = now; }
                            reply = Reply(source, 2, 0, source.Challenge);
                        }
                    }
                    else if (frame.Kind == 3)
                    {
                        if (source.Challenge == null || !received.RemoteEndPoint.Equals(source.PendingEndpoint) || now - source.ChallengeAt > TimeSpan.FromSeconds(10) ||
                            frame.Payload.Length != 16 || !CryptographicOperations.FixedTimeEquals(frame.Payload, source.Challenge)) continue;
                        source.Endpoint = received.RemoteEndPoint; source.PendingEndpoint = null; source.Challenge = null;
                        target = source.Endpoint; reply = Reply(source, 3, 0, Array.Empty<byte>());
                    }
                    else
                    {
                        if (frame.Kind == 6 && frame.Payload.Length == 16)
                        {
                            target = received.RemoteEndPoint;
                            if (source.Endpoint != null && source.Endpoint.Equals(received.RemoteEndPoint)) reply = Reply(source, 6, 0, Array.Empty<byte>());
                            else
                            {
                                if (source.Challenge == null || !received.RemoteEndPoint.Equals(source.PendingEndpoint) || now - source.ChallengeAt > TimeSpan.FromSeconds(10))
                                { source.PendingEndpoint = received.RemoteEndPoint; source.Challenge = RelayDatagram.Random(16); source.ChallengeAt = now; }
                                reply = Reply(source, 2, 0, source.Challenge);
                            }
                        }
                        else
                        {
                        if (source.Endpoint == null || !source.Endpoint.Equals(received.RemoteEndPoint)) continue;
                        if (frame.Kind == 5) { Remove(source.Public.AllocationId); continue; }
                        if (frame.Kind != 4 || frame.Payload.Length == 0 || !sessions.TryGetValue(source.Public.SessionId, out var session)) continue;
                        ulong destination = source.Public.IsHost ? frame.To : session.Host;
                        if (!allocations.TryGetValue(destination, out var recipient) || recipient.Endpoint == null || recipient.Public.SessionId != source.Public.SessionId ||
                            recipient.Public.IsHost == source.Public.IsHost || Expired(recipient, now) || (!source.Public.IsHost && frame.To != 0 && frame.To != session.Host)) continue;
                        target = recipient.Endpoint; reply = Reply(recipient, 4, source.Public.AllocationId, frame.Payload);
                        }
                    }
                }
                if (reply != null && target != null)
                    try { await listener.SendAsync(reply, reply.Length, target).ConfigureAwait(false); } catch (Exception error) when (error is ObjectDisposedException || error is SocketException) { }
            }
        }
        private byte[] Reply(Allocation recipient, byte kind, ulong from, byte[] payload)
            => RelayDatagram.Encode(recipient.Key, kind, from, recipient.Public.AllocationId, checked(++recipient.SendSequence), payload);
        private bool Expired(Allocation allocation, DateTime now) => now >= allocation.Public.ExpiresUtc || now - allocation.LastSeen > options.IdleTimeout;
        private async Task MaintainAsync(CancellationToken token)
        {
            try { while (!token.IsCancellationRequested) { await Task.Delay(1000, token).ConfigureAwait(false); lock (sync) Prune(DateTime.UtcNow); } }
            catch (OperationCanceledException) { }
        }
        private void Prune(DateTime now) { foreach (var item in allocations.Values.Where(value => Expired(value, now)).Select(value => value.Public.AllocationId).ToArray()) Remove(item); }
        private void Remove(ulong id)
        {
            if (!allocations.TryGetValue(id, out var allocation)) return;
            allocations.Remove(id); CryptographicOperations.ZeroMemory(allocation.Key);
            if (!sessions.TryGetValue(allocation.Public.SessionId, out var session)) return;
            if (allocation.Public.IsHost)
            {
                session.Host = 0; foreach (ulong client in session.Clients.ToArray()) Remove(client);
            }
            else session.Clients.Remove(id);
            if (session.Host == 0 && session.Clients.Count == 0) sessions.Remove(allocation.Public.SessionId);
        }
        public void Dispose()
        {
            lock (sync)
            {
                stopping = true;
                cancellation?.Cancel(); socket?.Dispose(); socket = null;
                foreach (var allocation in allocations.Values) CryptographicOperations.ZeroMemory(allocation.Key);
                allocations.Clear(); sessions.Clear();
            }
        }
        public async Task StopAsync()
        {
            Task[] tasks; CancellationTokenSource oldCancellation;
            lock (sync) { tasks = new[] { receive, maintenance }.Where(task => task != null).ToArray(); oldCancellation = cancellation; Dispose(); }
            await Task.WhenAll(tasks).ConfigureAwait(false);
            lock (sync)
            {
                if (ReferenceEquals(oldCancellation, cancellation))
                { oldCancellation?.Dispose(); cancellation = null; receive = maintenance = null; stopping = false; }
            }
        }
    }
}
