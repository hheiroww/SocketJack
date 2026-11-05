using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using SocketJack.Multiplayer.Transport;

namespace SocketJack.Multiplayer.Voice
{
    /// <summary>Game-session voice relay. Recreate for each session; pump Update once before the session Update.
    /// Unknown identities are denied. A speaker must own a live visible object with its current authority epoch/view.
    /// Voice uses unreliable delivery and bounded work; congestion drops audio instead of accumulating latency.</summary>
    public sealed class NetworkVoiceServer : IDisposable
    {
        private sealed class Sender { internal uint Stream, Highest, Epoch; internal ulong View, Mask; internal bool HasStream; internal double Tokens, Last; }
        private readonly NetworkServer server;
        private readonly IVoiceMembershipProvider membership;
        private readonly VoiceOptions options;
        private readonly Dictionary<ulong, Sender> senders = new Dictionary<ulong, Sender>();
        private double now;
        private int forwards;
        private bool disposed;
        public long AcceptedFrames { get; private set; }
        public long RejectedFrames { get; private set; }
        public long ForwardedFrames { get; private set; }
        public long DroppedFrames { get; private set; }
        public event Action<VoiceFrame> FrameAccepted;
        public NetworkVoiceServer(NetworkServer server, IVoiceMembershipProvider membership, VoiceOptions options = null)
        {
            this.server = server ?? throw new ArgumentNullException(nameof(server)); this.membership = membership ?? throw new ArgumentNullException(nameof(membership)); this.options = (options ?? new VoiceOptions()).Snapshot();
            server.MessageReceived += OnMessage; server.PeerDisconnected += OnDisconnected;
        }
        public void Update(double monotonicSeconds)
        { if (double.IsNaN(monotonicSeconds) || double.IsInfinity(monotonicSeconds) || monotonicSeconds < now) throw new ArgumentOutOfRangeException(nameof(monotonicSeconds)); now = monotonicSeconds; forwards = 0; if (!server.IsRunning) senders.Clear(); }
        private void OnDisconnected(ulong peer, string reason) => senders.Remove(peer);
        private void OnMessage(NetworkMessage message)
        {
            if (disposed || message.MessageId != VoiceWire.InputMessage) return;
            VoiceFrame frame; ulong[] recipients;
            try
            {
                if (message.Delivery != DeliveryMode.Unreliable || message.Channel != 3) throw new InvalidDataException();
                frame = VoiceWire.Decode(message.Payload, false, options);
                if (frame.Channel != VoiceChannel.Party && frame.Channel != VoiceChannel.Raid && frame.Channel != VoiceChannel.Community) frame.ScopeId = "";
                if (frame.Channel != VoiceChannel.Whisper) frame.TargetId = "";
                if (!IsCurrent(message.PeerId, frame)) { RejectedFrames++; return; }
                if (!senders.TryGetValue(message.PeerId, out var sender))
                {
                    if (senders.Count >= server.Peers.Count) { RejectedFrames++; return; }
                    senders.Add(message.PeerId, sender = new Sender { Tokens = options.PacketBurst, Last = now });
                }
                sender.Tokens = Math.Min(options.PacketBurst, sender.Tokens + Math.Max(0, now - sender.Last) * options.PacketsPerSecond); sender.Last = now;
                if (sender.Tokens < 1 || !AcceptSequence(sender, frame)) { RejectedFrames++; return; } sender.Tokens--;
                if (!membership.TryGetParticipant(message.PeerId, out var participant) || participant == null || participant.PeerId != message.PeerId || !Guid.TryParse(participant.AccountId, out var account) || account == Guid.Empty || !VoiceWire.Finite(participant.Position)) { RejectedFrames++; return; }
                recipients = (membership.ResolveAudience(participant, frame.Channel, frame.ScopeId, frame.TargetId) ?? Array.Empty<ulong>()).Take(options.MaxRecipientsPerFrame + 1).Distinct().ToArray();
                if (recipients.Length > options.MaxRecipientsPerFrame || !IsCurrent(message.PeerId, frame)) { RejectedFrames++; return; }
                frame.PeerId = message.PeerId; frame.AccountId = account.ToString("N"); frame.Position = frame.Channel == VoiceChannel.Proximity ? participant.Position : Vector3.Zero; AcceptedFrames++;
            }
            catch (Exception error) when (error is IOException || error is InvalidDataException || error is ArgumentException || error is SocketJack.Multiplayer.Chat.SocialException) { RejectedFrames++; return; }
            // User callbacks run outside packet parsing: local game-code exceptions are not malformed traffic.
            FrameAccepted?.Invoke(frame);
            if (!IsCurrent(message.PeerId, frame)) return;
            foreach (ulong recipient in recipients)
            {
                if (recipient == message.PeerId || !server.Peers.Contains(recipient)) continue;
                if (++forwards > options.MaxForwardsPerUpdate) { DroppedFrames++; continue; }
                if (frame.Channel == VoiceChannel.Proximity)
                {
                    if (!server.CanObserve(recipient, frame.ObjectId) || !membership.TryGetParticipant(recipient, out var listener) || listener == null || !VoiceWire.Finite(listener.Position) || Vector3.DistanceSquared(listener.Position, frame.Position) > options.ProximityRange * options.ProximityRange) continue;
                }
                if (!IsCurrent(message.PeerId, frame)) return;
                if (server.SendMessage(recipient, VoiceWire.FrameMessage, VoiceWire.Encode(frame, true, server.GetViewId(recipient, frame.ObjectId)), DeliveryMode.Unreliable, 3)) ForwardedFrames++; else DroppedFrames++;
            }
        }
        private bool IsCurrent(ulong peer, VoiceFrame frame) => !disposed && server.IsRunning && server.Objects.TryGetValue(frame.ObjectId, out var obj) && obj.OwnerId == peer && obj.AuthorityEpoch == frame.AuthorityEpoch && server.GetViewId(peer, frame.ObjectId) == frame.ViewId && frame.ViewId != 0;
        private static bool AcceptSequence(Sender sender, VoiceFrame frame)
        {
            if (sender.Epoch != frame.AuthorityEpoch || sender.View != frame.ViewId) { sender.HasStream = false; sender.Epoch = frame.AuthorityEpoch; sender.View = frame.ViewId; }
            if (!sender.HasStream || (frame.StreamId != sender.Stream && VoiceWire.Newer(frame.StreamId, sender.Stream)))
            { sender.HasStream = true; sender.Stream = frame.StreamId; sender.Highest = frame.Sequence; sender.Mask = 1; return true; }
            if (frame.StreamId != sender.Stream) return false;
            int delta = unchecked((int)(frame.Sequence - sender.Highest));
            if (delta > 0) { sender.Mask = delta >= 64 ? 1 : (sender.Mask << delta) | 1UL; sender.Highest = frame.Sequence; return true; }
            int age = -delta; if (age >= 64 || age < 0) return false; ulong bit = 1UL << age;
            if ((sender.Mask & bit) != 0) return false; sender.Mask |= bit; return true;
        }
        public void Dispose() { if (disposed) return; disposed = true; server.MessageReceived -= OnMessage; server.PeerDisconnected -= OnDisconnected; senders.Clear(); }
    }

    /// <summary>Game-session microphone sender and canonical incoming voice events. Attach jitter buffers at playback.</summary>
    public sealed class NetworkVoiceClient : IDisposable
    {
        private readonly NetworkClient client;
        private readonly VoiceOptions options;
        private readonly OpusVoiceCodec encoder;
        private ulong previousObject, previousView;
        private uint stream, sequence, previousEpoch;
        private VoiceChannel previousChannel;
        private string previousScope, previousTarget;
        private bool disposed;
        public long FramesSent { get; private set; }
        public long DroppedFrames { get; private set; }
        public event Action<VoiceFrame> FrameReceived;
        public NetworkVoiceClient(NetworkClient client, VoiceOptions options = null)
        { this.client = client ?? throw new ArgumentNullException(nameof(client)); this.options = (options ?? new VoiceOptions()).Snapshot(); encoder = new OpusVoiceCodec(this.options); client.MessageReceived += OnMessage; }
        public bool Send(ulong ownedObjectId, float[] monoPcm, VoiceChannel channel = VoiceChannel.Proximity, string scopeId = "", string targetAccountId = "")
        {
            if (disposed || !client.IsConnected || !client.Objects.TryGetValue(ownedObjectId, out var obj) || obj.OwnerId != client.LocalPeerId) return false;
            NormalizeContext(channel, ref scopeId, ref targetAccountId);
            if (previousObject != ownedObjectId || previousView != obj.ViewId || previousEpoch != obj.AuthorityEpoch || previousChannel != channel || previousScope != scopeId || previousTarget != targetAccountId) encoder.Reset();
            return SendEncoded(ownedObjectId, encoder.Encode(monoPcm), channel, scopeId, targetAccountId);
        }
        /// <summary>Begin a fresh codec stream after capture stops/restarts. Call on the session owner thread.</summary>
        public void ResetStream() { previousObject = 0; encoder.Reset(); }
        /// <summary>Send already encoded audio from a background codec pump. Call this on the session owner thread.</summary>
        public bool SendEncoded(ulong ownedObjectId, byte[] opusPacket, VoiceChannel channel = VoiceChannel.Proximity, string scopeId = "", string targetAccountId = "")
        {
            if (disposed || !client.IsConnected || !client.Objects.TryGetValue(ownedObjectId, out var obj) || obj.OwnerId != client.LocalPeerId) return false;
            if (!Enum.IsDefined(typeof(VoiceChannel), channel)) throw new ArgumentOutOfRangeException(nameof(channel));
            NormalizeContext(channel, ref scopeId, ref targetAccountId);
            if (stream == 0 || previousObject != ownedObjectId || previousView != obj.ViewId || previousEpoch != obj.AuthorityEpoch || previousChannel != channel || previousScope != scopeId || previousTarget != targetAccountId)
            {
                stream = unchecked(stream + 1); if (stream == 0) stream = 1; sequence = 0; previousObject = ownedObjectId; previousView = obj.ViewId; previousEpoch = obj.AuthorityEpoch; previousChannel = channel; previousScope = scopeId; previousTarget = targetAccountId;
            }
            var frame = new VoiceFrame { ObjectId = ownedObjectId, AuthorityEpoch = obj.AuthorityEpoch, ViewId = obj.ViewId, StreamId = stream, Sequence = unchecked(++sequence), Channel = channel,
                ScopeId = scopeId, TargetId = targetAccountId, SampleRate = options.SampleRate, EncodedAudio = opusPacket };
            if (!OpusVoiceCodec.IsValidPacket(opusPacket, options.SampleRate, options.MaxEncodedBytes)) throw new ArgumentException("Invalid 20 ms mono Opus packet.");
            if (!client.SendMessage(VoiceWire.InputMessage, VoiceWire.Encode(frame, false), DeliveryMode.Unreliable, 3)) { DroppedFrames++; return false; }
            FramesSent++; return true;
        }
        private static string Normalize(string id)
        { if (string.IsNullOrWhiteSpace(id)) return ""; if (!Guid.TryParse(id, out var value)) throw new ArgumentException("Voice scope/target must be an account/group ID."); return value.ToString("N"); }
        private static void NormalizeContext(VoiceChannel channel, ref string scope, ref string target)
        { scope = channel == VoiceChannel.Party || channel == VoiceChannel.Raid || channel == VoiceChannel.Community ? Normalize(scope) : ""; target = channel == VoiceChannel.Whisper ? Normalize(target) : ""; }
        private void OnMessage(NetworkMessage message)
        {
            if (disposed || message.MessageId != VoiceWire.FrameMessage || message.Delivery != DeliveryMode.Unreliable || message.Channel != 3) return;
            VoiceFrame frame;
            try
            {
                frame = VoiceWire.Decode(message.Payload, true, options);
                if (frame.Channel == VoiceChannel.Proximity && (!client.Objects.TryGetValue(frame.ObjectId, out var obj) || obj.OwnerId != frame.PeerId || obj.AuthorityEpoch != frame.AuthorityEpoch || obj.ViewId != frame.ViewId)) return;
            }
            catch (Exception error) when (error is IOException || error is InvalidDataException || error is ArgumentException) { return; }
            FrameReceived?.Invoke(frame);
        }
        public void Dispose() { if (disposed) return; disposed = true; client.MessageReceived -= OnMessage; }
    }
}
