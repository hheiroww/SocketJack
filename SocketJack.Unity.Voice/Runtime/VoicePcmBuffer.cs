using System;

namespace SocketJack.Multiplayer.Voice
{
    /// <summary>Fixed-size mono PCM ring with interleaved output resampling. Decoder writer and audio reader
    /// may run on different threads. Overflow drops oldest audio; underflow yields silence.</summary>
    public sealed class VoicePcmBuffer
    {
        private readonly object gate = new object();
        private readonly float[] samples;
        private readonly int sampleRate;
        private int read, count;
        private double phase;
        public long DroppedSamples { get; private set; }
        public int BufferedSamples { get { lock (gate) return count; } }
        public VoicePcmBuffer(int sampleRate, int capacityFrames = 10)
        {
            if (sampleRate < 8000 || sampleRate > 48000 || capacityFrames < 2 || capacityFrames > 50) throw new ArgumentOutOfRangeException();
            this.sampleRate = sampleRate; samples = new float[sampleRate / 50 * capacityFrames];
        }
        public void Write(float[] frame)
        {
            if (frame == null || frame.Length != sampleRate / 50) throw new ArgumentException("Write one 20 ms mono frame.");
            lock (gate)
            {
                int remove = Math.Max(0, count + frame.Length - samples.Length); read = (read + remove) % samples.Length; count -= remove; DroppedSamples += remove;
                for (int i = 0; i < frame.Length; i++) samples[(read + count + i) % samples.Length] = frame[i]; count += frame.Length;
            }
        }
        public void Read(float[] output, int channels, int outputSampleRate)
        {
            if (output == null || channels < 1 || channels > 8 || output.Length % channels != 0 || outputSampleRate < 8000 || outputSampleRate > 192000) throw new ArgumentException("Invalid interleaved audio output.");
            lock (gate)
            {
                double step = (double)sampleRate / outputSampleRate;
                for (int i = 0; i < output.Length; i += channels)
                {
                    float value = 0;
                    if (count >= 2)
                    {
                        value = samples[read] + (samples[(read + 1) % samples.Length] - samples[read]) * (float)phase;
                        phase += step; int consume = Math.Min(count, (int)phase); read = (read + consume) % samples.Length; count -= consume; phase -= consume;
                    }
                    else phase = 0;
                    for (int channel = 0; channel < channels; channel++) output[i + channel] = value;
                }
            }
        }
        public void Clear() { lock (gate) { count = read = 0; phase = 0; } }
    }
}
