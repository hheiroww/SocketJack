using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SocketJack.Multiplayer.Chat;
using SocketJack.Multiplayer.Web;

namespace SocketJack.Multiplayer.Voice.Web
{
    public sealed class AccountVoiceRelayOptions
    {
        public int MaxConnections { get; set; } = 4096;
        public int MaxActiveSpeakers { get; set; } = 32;
        public int MaxRecipientsPerFrame { get; set; } = 4096;
        public int MaxQueuedFramesPerSocket { get; set; } = 8;
        public int MaxForwardsPerSecond { get; set; } = 262144;
    }
    /// <summary>Authenticated /ws/voice role for a canonical account hub. Attach to the hub and each linked
    /// leaf before starting them; leaves proxy this fixed binary role to their configured hub.
    /// Audio is ephemeral, bounded and excluded from chat history, persistence and logs.</summary>
    public sealed class AccountVoiceRelay : IDisposable
    {
        private sealed class Connection
        {
            internal string Account;
            internal ulong SessionId;
            internal WebSocket Socket;
            internal CancellationTokenSource Lifetime;
            internal readonly Queue<byte[]> Out = new Queue<byte[]>();
            internal readonly SemaphoreSlim Wake = new SemaphoreSlim(0, 1);
            internal uint Stream, Highest; internal ulong Mask; internal bool HasStream;
            internal double Tokens = 10, Last, LastSpoke = double.NegativeInfinity;
        }
        private readonly object gate = new object();
        private readonly Dictionary<string, Connection> clients = new Dictionary<string, Connection>(StringComparer.Ordinal);
        private readonly SocketJackWebServer server;
        private readonly VoiceOptions voice;
        private readonly AccountVoiceRelayOptions options;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private double window;
        private int forwards;
        private bool disposed;
        private bool enabled = true;
        private long accepted, dropped, rejected, sent, nextSession;
        public long AcceptedFrames => Interlocked.Read(ref accepted);
        public long RejectedFrames => Interlocked.Read(ref rejected);
        public long DroppedFrames => Interlocked.Read(ref dropped);
        public long ForwardedFrames => Interlocked.Read(ref sent);
        public int Connections { get { lock (gate) return clients.Count; } }
        public bool Enabled { get { lock (gate) return enabled && !disposed; } set { lock (gate) { if (disposed && value) throw new ObjectDisposedException(nameof(AccountVoiceRelay)); enabled = value; if (!value) foreach (var connection in clients.Values) connection.Lifetime.Cancel(); } } }
        public AccountVoiceRelay(SocketJackWebServer server, VoiceOptions voice = null, AccountVoiceRelayOptions options = null)
        {
            this.server = server ?? throw new ArgumentNullException(nameof(server)); this.voice = (voice ?? new VoiceOptions()).Snapshot(); this.options = options ?? new AccountVoiceRelayOptions();
            _ = new OpusVoiceCodec(this.voice);
            if (this.options.MaxConnections < 1 || this.options.MaxConnections > 65535 || this.options.MaxActiveSpeakers < 1 || this.options.MaxActiveSpeakers > 256 || this.options.MaxRecipientsPerFrame < 1 || this.options.MaxRecipientsPerFrame > 65535 || this.options.MaxQueuedFramesPerSocket < 1 || this.options.MaxQueuedFramesPerSocket > 50 || this.options.MaxForwardsPerSecond < 1 || this.options.MaxForwardsPerSecond > 1048576) throw new ArgumentOutOfRangeException(nameof(options));
            server.RegisterAuthenticatedWebSocketHandler("/ws/voice", Handle);
        }
        private async Task Handle(WebAccount account, WebSocket socket, CancellationToken token)
        {
            var connection = new Connection { Account = account.Id, SessionId = unchecked((ulong)Interlocked.Increment(ref nextSession)), Socket = socket, Lifetime = CancellationTokenSource.CreateLinkedTokenSource(token), Last = clock.Elapsed.TotalSeconds };
            lock (gate)
            {
                if (disposed || !enabled || (clients.Count >= options.MaxConnections && !clients.ContainsKey(account.Id))) { connection.Lifetime.Dispose(); return; }
                if (clients.TryGetValue(account.Id, out var previous)) previous.Lifetime.Cancel(); clients[account.Id] = connection;
            }
            Task sender = null;
            try
            {
                byte[] hello = Encoding.UTF8.GetBytes("{\"protocol\":\"SocketJack.Voice/1\",\"sampleRate\":" + voice.SampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"utcMilliseconds\":" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture) + "}");
                await socket.SendAsync(new ArraySegment<byte>(hello), WebSocketMessageType.Text, true, connection.Lifetime.Token).ConfigureAwait(false);
                sender = SendLoop(connection);
                while (!connection.Lifetime.IsCancellationRequested && socket.State == WebSocketState.Open)
                {
                    byte[] bytes = await AccountVoiceWire.Receive(socket, WebSocketMessageType.Binary, AccountVoiceWire.MaximumPacket, connection.Lifetime.Token).ConfigureAwait(false);
                    if (bytes == null) break;
                    try { Route(connection, AccountVoiceWire.Decode(bytes, false, voice)); }
                    catch (Exception error) when (error is ArgumentException || error is IOException || error is InvalidDataException || error is SocialException) { Interlocked.Increment(ref rejected); }
                }
            }
            catch (Exception error) when (error is OperationCanceledException || error is WebSocketException || error is IOException || error is InvalidDataException) { }
            finally
            {
                connection.Lifetime.Cancel(); socket.Abort();
                if (sender != null) try { await sender.ConfigureAwait(false); } catch (OperationCanceledException) { }
                lock (gate) { if (clients.TryGetValue(account.Id, out var current) && ReferenceEquals(current, connection)) clients.Remove(account.Id); }
                lock (connection.Out) connection.Out.Clear(); connection.Lifetime.Dispose(); connection.Wake.Dispose();
            }
        }
        private void Route(Connection source, AccountVoiceFrame frame)
        {
            double now = clock.Elapsed.TotalSeconds;
            if (frame.Channel != VoiceChannel.Party && frame.Channel != VoiceChannel.Raid && frame.Channel != VoiceChannel.Community) frame.ScopeId = "";
            if (frame.Channel != VoiceChannel.Whisper) frame.TargetId = "";
            long age = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - frame.TimestampMilliseconds;
            if (age > 500 || age < -1000) { Interlocked.Increment(ref dropped); return; }
            lock (gate)
            {
                if (disposed || !enabled || source.Lifetime.IsCancellationRequested || !clients.TryGetValue(source.Account, out var current) || !ReferenceEquals(source, current)) return;
                source.Tokens = Math.Min(10, source.Tokens + Math.Max(0, now - source.Last) * 55); source.Last = now;
                if (source.Tokens < 1 || !AcceptSequence(source, frame)) { Interlocked.Increment(ref rejected); return; } source.Tokens--;
                if (now - source.LastSpoke > .5 && clients.Values.Count(value => now - value.LastSpoke < .5) >= options.MaxActiveSpeakers) { Interlocked.Increment(ref dropped); return; }
                source.LastSpoke = now;
            }
            ChatChannel channel = frame.Channel == VoiceChannel.Proximity ? ChatChannel.Proximity : frame.Channel == VoiceChannel.Party ? ChatChannel.Party : frame.Channel == VoiceChannel.Raid ? ChatChannel.Raid : frame.Channel == VoiceChannel.Community ? ChatChannel.Community : frame.Channel == VoiceChannel.Whisper ? ChatChannel.Whisper : ChatChannel.Global;
            string[] audience = server.ResolveVoiceAudience(source.Account, channel, frame.ScopeId, frame.TargetId);
            if (audience.Length > options.MaxRecipientsPerFrame) { Interlocked.Increment(ref dropped); return; }
            frame.AccountId = source.Account; frame.SessionId = source.SessionId;
            if (frame.Channel == VoiceChannel.Proximity && server.Presence.TryGetPresence(source.Account, out var presence)) frame.Position = presence.Position;
            byte[] canonical = AccountVoiceWire.Encode(frame, true); Interlocked.Increment(ref accepted);
            lock (gate)
            {
                if (disposed || source.Lifetime.IsCancellationRequested || !clients.TryGetValue(source.Account, out var current) || !ReferenceEquals(source, current)) return;
                if (now - window >= 1) { window = now; forwards = 0; }
                foreach (string account in audience)
                {
                    if (account == source.Account || !clients.TryGetValue(account, out var target)) continue;
                    if (++forwards > options.MaxForwardsPerSecond) { Interlocked.Increment(ref dropped); continue; }
                    lock (target.Out)
                    {
                        while (target.Out.Count >= options.MaxQueuedFramesPerSocket) { target.Out.Dequeue(); Interlocked.Increment(ref dropped); }
                        target.Out.Enqueue(canonical); try { target.Wake.Release(); } catch (SemaphoreFullException) { }
                    }
                }
            }
        }
        private async Task SendLoop(Connection connection)
        {
            try
            {
                while (!connection.Lifetime.IsCancellationRequested)
                {
                    await connection.Wake.WaitAsync(connection.Lifetime.Token).ConfigureAwait(false);
                    while (true)
                    {
                        byte[] bytes; lock (connection.Out) { if (connection.Out.Count == 0) break; bytes = connection.Out.Dequeue(); }
                        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(connection.Lifetime.Token))
                        { deadline.CancelAfter(1000); await connection.Socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Binary, true, deadline.Token).ConfigureAwait(false); }
                        Interlocked.Increment(ref sent);
                    }
                }
            }
            catch (Exception error) when (error is OperationCanceledException || error is WebSocketException || error is IOException) { connection.Lifetime.Cancel(); connection.Socket.Abort(); }
        }
        private static bool AcceptSequence(Connection source, AccountVoiceFrame frame)
        {
            if (!source.HasStream || (source.Stream != frame.StreamId && AccountVoiceWire.Newer(frame.StreamId, source.Stream))) { source.HasStream = true; source.Stream = frame.StreamId; source.Highest = frame.Sequence; source.Mask = 1; return true; }
            if (source.Stream != frame.StreamId) return false; int distance = unchecked((int)(frame.Sequence - source.Highest));
            if (distance > 0) { source.Mask = distance >= 64 ? 1 : (source.Mask << distance) | 1UL; source.Highest = frame.Sequence; return true; }
            int age = -distance; if (age < 0 || age >= 64) return false; ulong bit = 1UL << age; if ((source.Mask & bit) != 0) return false; source.Mask |= bit; return true;
        }
        public void Dispose() { lock (gate) { if (disposed) return; disposed = true; foreach (var connection in clients.Values) connection.Lifetime.Cancel(); } }
    }
}
