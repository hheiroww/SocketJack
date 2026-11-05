using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SocketJack.Multiplayer.Web;

namespace SocketJack.Multiplayer.Voice.Web
{
    /// <summary>Separate authenticated WSS audio role. Uses an existing in-memory account session and never
    /// places bearer tokens in endpoint URLs. Inject a platform connector for a non-native WebSocket implementation.</summary>
    public sealed class AccountVoiceClient : IDisposable
    {
        // A single coalesced wake and inline-capable TCS keep browser sends independent of a worker pool.
        private sealed class SendSignal
        {
            private readonly object gate = new object(); private bool available; private TaskCompletionSource<bool> waiter;
            internal Task WaitAsync(CancellationToken token)
            {
                TaskCompletionSource<bool> source;
                lock (gate)
                {
                    if (token.IsCancellationRequested) return Task.FromCanceled(token);
                    if (available) { available = false; return Task.CompletedTask; }
                    if (waiter != null) throw new InvalidOperationException("Only one voice sender can wait.");
                    waiter = source = new TaskCompletionSource<bool>();
                }
                var registration = token.Register(() => { lock (gate) if (ReferenceEquals(waiter, source)) waiter = null; source.TrySetCanceled(); });
                return Await(source.Task, registration);
            }
            private static async Task Await(Task task, CancellationTokenRegistration registration)
            { try { await task.ConfigureAwait(true); } finally { registration.Dispose(); } }
            internal void Release()
            {
                TaskCompletionSource<bool> source;
                lock (gate) { source = waiter; waiter = null; if (source == null) available = true; }
                source?.TrySetResult(true);
            }
        }
        private readonly SocketJackWebClient account;
        private readonly VoiceOptions options;
        private readonly Queue<byte[]> outgoing = new Queue<byte[]>();
        private readonly SendSignal wake = new SendSignal();
        private WebSocket socket;
        private CancellationTokenSource lifetime;
        private Task sending, receiving;
        private uint stream, sequence;
        private bool reset = true, disposed, connecting;
        private VoiceChannel previousChannel;
        private string previousScope, previousTarget;
        private int generation;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private long serverTime;
        private double serverTimeAt;
        public bool IsConnected { get; private set; }
        public long DroppedFrames { get; private set; }
        public Func<Uri, CancellationToken, Task<WebSocket>> SocketConnector { get; set; }
        public event Action<AccountVoiceFrame> FrameReceived;
        public event Action<string> ConnectionChanged;
        public AccountVoiceClient(SocketJackWebClient account, VoiceOptions options = null)
        { this.account = account ?? throw new ArgumentNullException(nameof(account)); this.options = (options ?? new VoiceOptions()).Snapshot(); }
        public async Task ConnectAsync(CancellationToken token = default)
        {
            if (disposed) throw new ObjectDisposedException(nameof(AccountVoiceClient)); if (IsConnected || connecting) throw new InvalidOperationException("Voice is already connected/connecting.");
            if (account.User == null || string.IsNullOrEmpty(account.Token)) throw new InvalidOperationException("Sign in before connecting voice.");
            string expectedAccount = account.User.Id; int current = ++generation; connecting = true;
            var active = CancellationTokenSource.CreateLinkedTokenSource(token); lifetime = active;
            WebSocket ownedSocket = null;
            long connectedServerTime = 0; double connectedServerTimeAt = 0;
            try
            {
                var endpoint = new UriBuilder(account.EndPoint) { Scheme = account.EndPoint.Scheme == "https" ? "wss" : "ws", Path = "/ws/voice", Query = "", Fragment = "" }.Uri;
                WebSocket connected;
                if (SocketConnector != null) connected = await SocketConnector(endpoint, active.Token).ConfigureAwait(true);
                else { var native = new ClientWebSocket(); try { await native.ConnectAsync(endpoint, active.Token).ConfigureAwait(true); } catch { native.Dispose(); throw; } connected = native; }
                ownedSocket = connected;
                if (current != generation || account.User?.Id != expectedAccount) { connected.Dispose(); throw new OperationCanceledException("Account changed during voice connection."); }
                socket = connected;
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(active.Token))
                {
                    deadline.CancelAfter(TimeSpan.FromSeconds(10)); byte[] authentication = JsonSerializer.SerializeToUtf8Bytes(new { token = account.Token });
                    await connected.SendAsync(new ArraySegment<byte>(authentication), WebSocketMessageType.Text, true, deadline.Token).ConfigureAwait(true);
                    byte[] hello = await AccountVoiceWire.Receive(connected, WebSocketMessageType.Text, 256, deadline.Token).ConfigureAwait(true);
                    if (hello == null) throw new InvalidDataException("Voice authentication/endpoint was not accepted.");
                    var data = JsonSerializer.Deserialize<JsonElement>(hello);
                    if (data.GetProperty("protocol").GetString() != "SocketJack.Voice/1" || data.GetProperty("sampleRate").GetInt32() != options.SampleRate) throw new InvalidDataException("Voice protocol/profile mismatch.");
                    connectedServerTime = data.GetProperty("utcMilliseconds").GetInt64(); connectedServerTimeAt = clock.Elapsed.TotalMilliseconds;
                }
                if (current != generation || account.User?.Id != expectedAccount) throw new OperationCanceledException("Account changed during voice connection.");
                active.Token.ThrowIfCancellationRequested();
                serverTime = connectedServerTime; serverTimeAt = connectedServerTimeAt;
                IsConnected = true; reset = true; sending = SendLoop(connected, active); receiving = ReceiveLoop(connected, active, expectedAccount, current); ConnectionChanged?.Invoke("Voice connected");
            }
            catch
            {
                try { active.Cancel(); } catch (ObjectDisposedException) { }
                ownedSocket?.Dispose();
                if (ReferenceEquals(lifetime, active)) { lifetime = null; if (ReferenceEquals(socket, ownedSocket)) socket = null; }
                active.Dispose(); throw;
            }
            finally { if (current == generation) connecting = false; }
        }
        public void ResetStream() => reset = true;
        public bool SendEncoded(byte[] opus, VoiceChannel channel = VoiceChannel.Proximity, string scopeId = "", string targetAccountId = "")
        {
            if (!IsConnected || disposed) return false;
            if (!Enum.IsDefined(typeof(VoiceChannel), channel) || !OpusVoiceCodec.IsValidPacket(opus, options.SampleRate, options.MaxEncodedBytes)) throw new ArgumentException("Invalid bounded mono Opus frame/channel.");
            scopeId = channel == VoiceChannel.Party || channel == VoiceChannel.Raid || channel == VoiceChannel.Community ? Normalize(scopeId) : "";
            targetAccountId = channel == VoiceChannel.Whisper ? Normalize(targetAccountId) : "";
            if (reset || previousChannel != channel || previousScope != scopeId || previousTarget != targetAccountId)
            { reset = false; stream = unchecked(stream + 1); if (stream == 0) stream = 1; sequence = 0; previousChannel = channel; previousScope = scopeId; previousTarget = targetAccountId; }
            byte[] packet = AccountVoiceWire.Encode(new AccountVoiceFrame { Channel = channel, ScopeId = scopeId, TargetId = targetAccountId, SampleRate = options.SampleRate, StreamId = stream, Sequence = unchecked(++sequence), TimestampMilliseconds = EstimatedServerTime(), EncodedAudio = opus }, false);
            lock (outgoing) { while (outgoing.Count >= 4) { outgoing.Dequeue(); DroppedFrames++; } outgoing.Enqueue(packet); }
            wake.Release();
            return true;
        }
        private async Task SendLoop(WebSocket activeSocket, CancellationTokenSource active)
        {
            try
            {
                while (!active.IsCancellationRequested)
                {
                    await wake.WaitAsync(active.Token).ConfigureAwait(true);
                    while (true)
                    {
                        byte[] packet; lock (outgoing) { if (outgoing.Count == 0) break; packet = outgoing.Dequeue(); }
                        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(active.Token))
                        { deadline.CancelAfter(1000); await activeSocket.SendAsync(new ArraySegment<byte>(packet), WebSocketMessageType.Binary, true, deadline.Token).ConfigureAwait(true); }
                    }
                }
            }
            catch (Exception error) when (error is OperationCanceledException || error is WebSocketException || error is IOException || error is ObjectDisposedException) { active.Cancel(); activeSocket.Abort(); }
        }
        private async Task ReceiveLoop(WebSocket activeSocket, CancellationTokenSource active, string expectedAccount, int current)
        {
            try
            {
                while (!active.IsCancellationRequested)
                {
                    byte[] bytes = await AccountVoiceWire.Receive(activeSocket, WebSocketMessageType.Binary, AccountVoiceWire.MaximumPacket, active.Token).ConfigureAwait(true); if (bytes == null) break;
                    AccountVoiceFrame frame = AccountVoiceWire.Decode(bytes, true, options);
                    if (current != generation || account.User?.Id != expectedAccount) break;
                    long age = EstimatedServerTime() - frame.TimestampMilliseconds; if (age > 500 || age < -1000) continue;
                    if (frame.AccountId != expectedAccount) FrameReceived?.Invoke(frame);
                }
            }
            catch (Exception error) when (error is OperationCanceledException || error is WebSocketException || error is IOException || error is InvalidDataException || error is ObjectDisposedException) { }
            finally { active.Cancel(); activeSocket.Abort(); if (current == generation) { IsConnected = false; ConnectionChanged?.Invoke("Voice disconnected"); } }
        }
        public async Task StopAsync()
        {
            var stopped = Stop();
            try { if (stopped.Sending != null) await stopped.Sending.ConfigureAwait(true); if (stopped.Receiving != null) await stopped.Receiving.ConfigureAwait(true); }
            finally { stopped.Lifetime?.Dispose(); }
        }
        private (CancellationTokenSource Lifetime, Task Sending, Task Receiving) Stop()
        {
            var stopped = (lifetime, sending, receiving); generation++; connecting = false; IsConnected = false;
            lifetime?.Cancel(); socket?.Abort(); socket?.Dispose(); socket = null; lifetime = null; sending = receiving = null;
            lock (outgoing) outgoing.Clear(); return stopped;
        }
        public void Dispose() { if (disposed) return; disposed = true; _ = StopAsync(); }
        private static string Normalize(string value)
        { if (string.IsNullOrWhiteSpace(value)) return ""; if (!Guid.TryParse(value, out var id)) throw new ArgumentException("Voice scope/target must be an account/group ID."); return id.ToString("N"); }
        private long EstimatedServerTime() => serverTime + (long)(clock.Elapsed.TotalMilliseconds - serverTimeAt);
    }
}
