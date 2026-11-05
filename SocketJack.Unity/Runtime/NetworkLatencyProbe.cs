using System;
using System.Collections.Generic;
using System.Linq;
using SocketJack.Multiplayer.Transport;
namespace SocketJack.Multiplayer
{
    public sealed class NetworkLatencySample
    {
        public ulong PeerId { get; internal set; }
        public double? RttMilliseconds { get; internal set; }
        public double? P95Milliseconds { get; internal set; }
        public long Timeouts { get; internal set; }
    }
    /// <summary>Bounded application-path latency, including queuing. Measures local elapsed time; never trusts a remote RTT claim.</summary>
    public sealed class NetworkLatencyProbe : IDisposable
    {
        public const ushort ClientPing = 60012, ClientPong = 60013, ServerPing = 60014, ServerPong = 60015;
        private sealed class Sample
        {
            internal long Nonce, Timeouts;
            internal double SentAt, NextAt, LastAt;
            internal bool Pending;
            internal readonly Queue<double> Values = new Queue<double>();
        }
        private NetworkServer server;
        private NetworkClient client;
        private readonly Dictionary<ulong, Sample> samples = new Dictionary<ulong, Sample>();
        private readonly Sample local = new Sample();
        private double now;
        private long nonce;
        private int cursor;
        private bool disposed;
        public NetworkLatencyProbe(NetworkServer server = null, NetworkClient client = null) { AttachServer(server); AttachClient(client); }
        public void AttachServer(NetworkServer value)
        {
            if (server == value) return;
            if (server != null) { server.MessageReceived -= ServerMessage; server.PeerDisconnected -= Disconnected; }
            server = value; if (server != null) { server.MessageReceived += ServerMessage; server.PeerDisconnected += Disconnected; }
        }
        public void AttachClient(NetworkClient value)
        {
            if (client == value) return;
            if (client != null) client.MessageReceived -= ClientMessage;
            client = value; if (client != null) client.MessageReceived += ClientMessage;
        }
        public void Update(double deltaSeconds)
        {
            SessionWire.ValidateDelta(deltaSeconds); if (disposed) return; now += deltaSeconds;
            if (client != null && client.IsConnected) Send(local, (id, bytes) => client.SendMessage(id, bytes, DeliveryMode.Unreliable), ClientPing);
            if (server == null || !server.IsRunning) return;
            var peers = server.Peers.ToArray();
            for (int count = 0; count < Math.Min(64, peers.Length); count++)
            {
                if (cursor >= peers.Length) cursor = 0; ulong peer = peers[cursor++];
                if (!samples.TryGetValue(peer, out var sample)) samples.Add(peer, sample = new Sample());
                Send(sample, (id, bytes) => server.SendMessage(peer, id, bytes, DeliveryMode.Unreliable), ServerPing);
            }
        }
        private void Send(Sample sample, Func<ushort, byte[], bool> send, ushort messageId)
        {
            if (sample.Pending)
            {
                if (now - sample.SentAt < 5) return;
                sample.Timeouts++; sample.Pending = false;
            }
            if (now < sample.NextAt) return;
            long next = unchecked(++nonce);
            if (!send(messageId, BitConverter.GetBytes(next))) return;
            sample.Nonce = next; sample.SentAt = now; sample.NextAt = now + 1; sample.Pending = true;
        }
        private void ServerMessage(NetworkMessage message)
        {
            if (message.Payload.Length != 8 || message.Channel != 3 || message.Delivery != DeliveryMode.Unreliable) return;
            if (message.MessageId == ClientPing) server.SendMessage(message.PeerId, ClientPong, message.Payload, DeliveryMode.Unreliable);
            else if (message.MessageId == ServerPong && samples.TryGetValue(message.PeerId, out var sample)) Complete(sample, message.Payload);
        }
        private void ClientMessage(NetworkMessage message)
        {
            if (message.Payload.Length != 8 || message.Channel != 3 || message.Delivery != DeliveryMode.Unreliable) return;
            if (message.MessageId == ServerPing) client.SendMessage(ServerPong, message.Payload, DeliveryMode.Unreliable);
            else if (message.MessageId == ClientPong) Complete(local, message.Payload);
        }
        private void Complete(Sample sample, byte[] payload)
        {
            if (!sample.Pending || BitConverter.ToInt64(payload, 0) != sample.Nonce) return;
            sample.Pending = false; sample.LastAt = now; sample.Values.Enqueue(Math.Max(0, (now - sample.SentAt) * 1000));
            while (sample.Values.Count > 64) sample.Values.Dequeue();
        }
        public NetworkLatencySample GetLocalSample() => Snapshot(0, local);
        public IReadOnlyList<NetworkLatencySample> GetServerSamples(int maximum = 64)
        {
            if (maximum < 0 || maximum > 4096) throw new ArgumentOutOfRangeException(nameof(maximum));
            return samples.OrderBy(pair => pair.Key).Take(maximum).Select(pair => Snapshot(pair.Key, pair.Value)).ToArray();
        }
        private NetworkLatencySample Snapshot(ulong id, Sample sample)
        {
            var result = new NetworkLatencySample { PeerId = id, Timeouts = sample.Timeouts };
            if (sample.Values.Count == 0 || now - sample.LastAt > 10) return result;
            var values = sample.Values.OrderBy(value => value).ToArray(); result.RttMilliseconds = sample.Values.Last();
            result.P95Milliseconds = values[Math.Max(0, (int)Math.Ceiling(values.Length * .95) - 1)]; return result;
        }
        private void Disconnected(ulong peer, string reason) => samples.Remove(peer);
        public void Dispose() { if (disposed) return; disposed = true; AttachServer(null); AttachClient(null); samples.Clear(); local.Values.Clear(); }
    }
}
