using System;
using System.Collections.Generic;

namespace SocketJack.Multiplayer.Voice
{
    /// <summary>Bounded per-speaker jitter window. Handles reordering, duplicates, uint wrap and Opus loss
    /// concealment; expired streams output silence. Thread safe for a network/main-thread writer and audio reader.</summary>
    public sealed class VoiceJitterBuffer
    {
        private readonly object gate = new object();
        private readonly Dictionary<uint, byte[]> packets = new Dictionary<uint, byte[]>();
        private readonly VoiceOptions options;
        private OpusVoiceCodec codec;
        private uint expected;
        private bool active;
        private bool needsCodecReset;
        private double nextAt, lastPacketAt;
        public long DuplicateOrLatePackets { get; private set; }
        public long ConcealedFrames { get; private set; }
        public long DecodedFrames { get; private set; }
        public long DecodedPackets { get; private set; }
        public long Resets { get; private set; }
        public int BufferedFrames { get { lock (gate) return packets.Count; } }
        public VoiceJitterBuffer(VoiceOptions options = null) { this.options = (options ?? new VoiceOptions()).Snapshot(); }
        public bool Enqueue(uint sequence, byte[] packet, double now)
        {
            if (double.IsNaN(now) || double.IsInfinity(now) || !OpusVoiceCodec.IsValidPacket(packet, options.SampleRate, options.MaxEncodedBytes)) return false;
            lock (gate)
            {
                if (!active || now - lastPacketAt > options.StreamTimeoutSeconds) Start(sequence, now);
                int distance = unchecked((int)(sequence - expected));
                if (distance < 0 || packets.ContainsKey(sequence)) { DuplicateOrLatePackets++; return false; }
                if (distance >= options.JitterFrames) { Start(sequence, now); Resets++; }
                if (packets.Count >= options.JitterFrames) return false;
                packets.Add(sequence, (byte[])packet.Clone()); lastPacketAt = now; return true;
            }
        }
        public bool TryDecode(float[] pcm, double now)
        {
            if (pcm == null || pcm.Length < options.SamplesPerFrame) throw new ArgumentException("Output needs one full mono frame.");
            lock (gate)
            {
                if (!active || now < nextAt || now - lastPacketAt > options.StreamTimeoutSeconds) { Array.Clear(pcm, 0, pcm.Length); return false; }
                // Do not play a stale backlog after a suspended audio callback.
                if (now - nextAt > .2) { ResetCore(); Array.Clear(pcm, 0, pcm.Length); return false; }
                packets.TryGetValue(expected, out var packet); packets.Remove(expected); expected = unchecked(expected + 1); nextAt += .02;
                if (packet == null) ConcealedFrames++;
                try { if (codec == null) codec = new OpusVoiceCodec(options); else if (needsCodecReset) codec.Reset(); needsCodecReset = false; codec.Decode(packet, pcm); DecodedFrames++; if (packet != null) DecodedPackets++; return true; }
                catch (Exception error) when (error is ArgumentException || error is Concentus.OpusException) { Array.Clear(pcm, 0, pcm.Length); return false; }
            }
        }
        public void Reset() { lock (gate) ResetCore(); }
        private void ResetCore() { active = false; packets.Clear(); needsCodecReset = true; }
        private void Start(uint sequence, double now) { packets.Clear(); needsCodecReset = true; expected = sequence; nextAt = now + options.InitialDelayFrames * .02; lastPacketAt = now; active = true; }
    }
}
