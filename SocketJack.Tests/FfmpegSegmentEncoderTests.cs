using System.Diagnostics;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Media;

namespace SocketJack.Tests;

[TestClass]
public class FfmpegSegmentEncoderTests {
    [TestMethod]
    public void RejectsInvalidRawMediaBeforeLaunchingAnEncoder() {
        var encoder = new FfmpegSegmentEncoder("unused-ffmpeg-executable");
        Assert.ThrowsException<ArgumentException>(() => encoder.EncodeVideoAsync([], 320, 180, 30, 500000, default));
        Assert.ThrowsException<ArgumentException>(() => encoder.EncodeVideoAsync([new byte[15]], 320, 180, 30, 500000, default));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => encoder.EncodeVideoAsync([new byte[4]], 319, 180, 30, 500000, default));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => encoder.EncodeVideoAsync([new byte[4]], 1280, 720, 60, 500000, default));
        Assert.ThrowsException<ArgumentException>(() => encoder.EncodeAudioAsync([0, 0, 0], 24000, 48000, default));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => encoder.EncodeAudioAsync([0, 0], 12345, 48000, default));
    }

    [TestMethod]
    public async Task VideoSegmentContainsOneKeyframeAndPredictiveFrames() {
        var encoder = new FfmpegSegmentEncoder(Executable("SOCKETJACK_TEST_FFMPEG"));
        var frames = MakeFrames(320, 180, 8);
        long started = Stopwatch.GetTimestamp();
        byte[] segment = await encoder.EncodeVideoAsync(frames, 320, 180, 24, 800000, default);
        Console.WriteLine($"Encoded 8 BGRA frames in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:0.0} ms, {segment.Length} bytes.");
        Assert.IsTrue(segment.Length > 0 && segment.Length <= FfmpegSegmentEncoder.MaximumSegmentBytes);
        var boxes = new List<string>();
        for (int offset = 0; offset <= segment.Length - 8;) {
            int size = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(segment.AsSpan(offset, 4));
            Assert.IsTrue(size >= 8 && size <= segment.Length - offset);
            boxes.Add(System.Text.Encoding.ASCII.GetString(segment, offset + 4, 4));
            offset += size;
        }
        CollectionAssert.AreEqual(new[] { "ftyp", "moov", "moof", "mdat" }, boxes.Take(4).ToArray());
        int config = segment.AsSpan().IndexOf("avcC"u8);
        Assert.IsTrue(config >= 0 && config <= segment.Length - 8);
        CollectionAssert.AreEqual(new byte[] { 1, 0x42, 0xc0, 0x1f }, segment[(config + 4)..(config + 8)]);

        using var details = await ProbeAsync(segment, "mp4");
        var stream = details.RootElement.GetProperty("streams").EnumerateArray().Single();
        Assert.AreEqual("h264", stream.GetProperty("codec_name").GetString());
        Assert.AreEqual("Constrained Baseline", stream.GetProperty("profile").GetString());
        Assert.AreEqual(31, stream.GetProperty("level").GetInt32());
        string[] types = details.RootElement.GetProperty("frames").EnumerateArray().Select(frame => frame.GetProperty("pict_type").GetString()).ToArray();
        CollectionAssert.AreEqual(new[] { "I", "P", "P", "P", "P", "P", "P", "P" }, types);
    }

    [TestMethod]
    public async Task AudioSegmentContainsDecodableAacLcFrames() {
        var encoder = new FfmpegSegmentEncoder(Executable("SOCKETJACK_TEST_FFMPEG"));
        const int sampleRate = 24000;
        byte[] samples = new byte[9600];
        for (int i = 0; i < samples.Length / 2; i++)
            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(samples.AsSpan(i * 2, 2), (short)(Math.Sin(2 * Math.PI * 440 * i / sampleRate) * 12000));

        byte[] segment = await encoder.EncodeAudioAsync(samples, sampleRate, 48000, default);
        Assert.IsTrue(segment.Length > 8 && segment.Length <= FfmpegSegmentEncoder.MaximumSegmentBytes);
        Assert.AreEqual(0xff, (int)segment[0]);
        Assert.AreEqual(0xf0, segment[1] & 0xf0);
        using var details = await ProbeAsync(segment, "aac");
        var stream = details.RootElement.GetProperty("streams").EnumerateArray().Single();
        Assert.AreEqual("aac", stream.GetProperty("codec_name").GetString());
        Assert.AreEqual("LC", stream.GetProperty("profile").GetString());
        Assert.AreEqual("24000", stream.GetProperty("sample_rate").GetString());
        Assert.AreEqual(1, stream.GetProperty("channels").GetInt32());
        Assert.IsTrue(details.RootElement.GetProperty("frames").GetArrayLength() > 1);
    }

    [TestMethod]
    public async Task CancellationTerminatesTheRealEncoder() {
        var encoder = new FfmpegSegmentEncoder(Executable("SOCKETJACK_TEST_FFMPEG"));
        using var stop = new CancellationTokenSource();
        var encoding = encoder.EncodeVideoAsync(MakeFrames(640, 360, 20), 640, 360, 30, 14000000, stop.Token);
        stop.Cancel();
        try {
            await encoding.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Fail("The encoder completed after cancellation.");
        } catch (OperationCanceledException error) {
            Assert.AreEqual(stop.Token, error.CancellationToken);
        }
    }

    [TestMethod]
    public async Task RejectsOversizedEncoderOutputWithoutBlockingOnPipes() {
        var encoder = new FfmpegSegmentEncoder(Executable("SOCKETJACK_TEST_FFMPEG"));
        var encoding = encoder.EncodeVideoAsync(MakeFrames(640, 360, 45), 640, 360, 30, 14000000, default);
        var error = await Assert.ThrowsExceptionAsync<InvalidDataException>(() => encoding.WaitAsync(TimeSpan.FromSeconds(10)));
        StringAssert.Contains(error.Message, "512 KiB");
    }

    [TestMethod]
    public async Task ProvidesDiagnosticsWhenEncoderExitsEarly() {
        var encoder = new FfmpegSegmentEncoder(Executable("SOCKETJACK_TEST_FFPROBE"));
        var error = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            encoder.EncodeVideoAsync(MakeFrames(320, 180, 2), 320, 180, 24, 500000, default).WaitAsync(TimeSpan.FromSeconds(5)));
        StringAssert.Contains(error.Message, "FFmpeg exited with code");
        StringAssert.Contains(error.Message, "option");
    }

    private static byte[][] MakeFrames(int width, int height, int count) {
        var frames = new byte[count][];
        var random = new Random(12);
        for (int i = 0; i < frames.Length; i++) {
            frames[i] = new byte[width * height * 4];
            random.NextBytes(frames[i]);
        }
        return frames;
    }

    private static string Executable(string name) {
        string executable = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
            Assert.Inconclusive($"Set {name} to run executable-backed tests.");
        return executable;
    }

    private static async Task<JsonDocument> ProbeAsync(byte[] segment, string inputFormat) {
        var info = new ProcessStartInfo(Executable("SOCKETJACK_TEST_FFPROBE")) {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string argument in new[] { "-v", "error", "-show_streams", "-show_frames", "-of", "json", "-f", inputFormat, "pipe:0" })
            info.ArgumentList.Add(argument);
        using var probe = Process.Start(info);
        var output = probe.StandardOutput.ReadToEndAsync();
        var errors = probe.StandardError.ReadToEndAsync();
        try {
            using (Stream input = probe.StandardInput.BaseStream) await input.WriteAsync(segment);
            await probe.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        } finally {
            if (!probe.HasExited) probe.Kill();
        }
        Assert.AreEqual(0, probe.ExitCode, await errors);
        return JsonDocument.Parse(await output);
    }
}
