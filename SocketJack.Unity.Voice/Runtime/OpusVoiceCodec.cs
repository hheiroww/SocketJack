using System;
using Concentus.Enums;
using Concentus.Structs;

namespace SocketJack.Multiplayer.Voice
{
    /// <summary>Managed Opus only: no native codec loader. One codec per transmitting/receiving stream.
    /// Frames are exactly 20 ms of mono PCM. Call a codec from one thread or protect it externally.</summary>
    public sealed class OpusVoiceCodec
    {
        private readonly OpusEncoder encoder;
        private readonly OpusDecoder decoder;
        private readonly VoiceOptions options;
        private readonly byte[] encoded;
        private readonly short[] input, output;
        public int SampleRate => options.SampleRate;
        public int SamplesPerFrame => options.SamplesPerFrame;
        public OpusVoiceCodec(VoiceOptions options = null)
        {
            this.options = (options ?? new VoiceOptions()).Snapshot(); encoded = new byte[this.options.MaxEncodedBytes];
            // The managed 2.2.2 24 kHz path fails the PCM-energy regression. Opus packets do not
            // encode a sample rate; use its verified 48 kHz path and explicitly convert 24 kHz PCM.
            int codecRate = SampleRate == 24000 ? 48000 : SampleRate;
            input = new short[codecRate / 50]; output = new short[codecRate / 50];
            // Deliberately select the managed implementation: the optional package ships no native loader/binaries.
#pragma warning disable CS0618
            encoder = new OpusEncoder(codecRate, 1, OpusApplication.OPUS_APPLICATION_VOIP) { Bitrate = this.options.Bitrate, Complexity = 3, UseVBR = true, UseDTX = false, UseInbandFEC = false };
            decoder = new OpusDecoder(codecRate, 1);
#pragma warning restore CS0618
        }
        public byte[] Encode(float[] pcm)
        {
            if (pcm == null || pcm.Length != SamplesPerFrame) throw new ArgumentException("Provide exactly 20 ms mono PCM.");
            for (int i = 0; i < pcm.Length; i++) if (float.IsNaN(pcm[i]) || float.IsInfinity(pcm[i]) || pcm[i] < -1 || pcm[i] > 1) throw new ArgumentException("PCM must contain finite samples in [-1,1].");
            for (int i = 0; i < input.Length; i++)
            {
                int source = SampleRate == 24000 ? i / 2 : i;
                float sample = pcm[source]; if (SampleRate == 24000 && (i & 1) != 0) sample = (sample + pcm[Math.Min(source + 1, pcm.Length - 1)]) * .5f;
                input[i] = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, Math.Round(sample * 32767)));
            }
            int count = encoder.Encode(input.AsSpan(), input.Length, encoded.AsSpan(), encoded.Length);
            var result = new byte[count]; Buffer.BlockCopy(encoded, 0, result, 0, count); return result;
        }
        public int Decode(byte[] packet, float[] pcm)
        {
            if (pcm == null || pcm.Length < SamplesPerFrame || (packet != null && !IsValidPacket(packet, SampleRate, options.MaxEncodedBytes))) throw new ArgumentException("Invalid bounded 20 ms Opus packet or output buffer.");
            int count = decoder.Decode(packet == null ? ReadOnlySpan<byte>.Empty : packet.AsSpan(), output.AsSpan(), output.Length, false);
            int samples = SampleRate == 24000 ? count / 2 : count;
            for (int i = 0; i < samples; i++) pcm[i] = SampleRate == 24000 ? (output[i * 2] + output[i * 2 + 1]) / 65536f : output[i] / 32768f;
            return samples;
        }
        public void Reset() { encoder.ResetState(); decoder.ResetState(); }
        public static bool IsValidPacket(byte[] packet, int sampleRate, int maxBytes = 400)
        {
            if (packet == null || packet.Length < 1 || packet.Length > maxBytes || (sampleRate != 8000 && sampleRate != 12000 && sampleRate != 16000 && sampleRate != 24000 && sampleRate != 48000)) return false;
            try { return OpusPacketInfo.GetNumEncodedChannels(packet.AsSpan()) == 1 && OpusPacketInfo.GetNumSamples(packet.AsSpan(), sampleRate) == sampleRate / 50; }
            catch (Exception error) when (error is ArgumentException || error is IndexOutOfRangeException) { return false; }
        }
    }
}
