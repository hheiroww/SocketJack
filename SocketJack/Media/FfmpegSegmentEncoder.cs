using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SocketJack.Media {
    /// <summary>Encodes short, independently decodable media segments with a caller-provided FFmpeg executable.</summary>
    public sealed class FfmpegSegmentEncoder {
        public const int MaximumSegmentBytes = 512 * 1024;
        private const int MaximumVideoInputBytes = 128 * 1024 * 1024;
        private readonly string executable;

        public FfmpegSegmentEncoder(string executable) {
            if (string.IsNullOrWhiteSpace(executable)) throw new ArgumentException("An FFmpeg executable is required.", nameof(executable));
            this.executable = executable;
        }

        /// <summary>Encodes tightly packed BGRA frames as constrained-baseline H.264 level 3.1 in fragmented MP4.
        /// Each segment starts with initialization boxes and a keyframe. Later frames can use inter-frame prediction.</summary>
        public Task<byte[]> EncodeVideoAsync(byte[][] bgraFrames, int width, int height, int framesPerSecond, int bitsPerSecond, CancellationToken token) {
            if (bgraFrames == null) throw new ArgumentNullException(nameof(bgraFrames));
            if (width < 16 || width > 1280 || width % 2 != 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (height < 16 || height > 1280 || height % 2 != 0) throw new ArgumentOutOfRangeException(nameof(height));
            int macroblocks = ((width + 15) / 16) * ((height + 15) / 16);
            if (macroblocks > 3600) throw new ArgumentException("Frame dimensions exceed H.264 level 3.1.", nameof(height));
            if (framesPerSecond < 1 || framesPerSecond > 60 || macroblocks * framesPerSecond > 108000)
                throw new ArgumentOutOfRangeException(nameof(framesPerSecond), "Frame rate exceeds the supported H.264 level 3.1 range.");
            if (bitsPerSecond < 16000 || bitsPerSecond > 14000000) throw new ArgumentOutOfRangeException(nameof(bitsPerSecond));

            int frameBytes = width * height * 4;
            if (bgraFrames.Length == 0 || bgraFrames.Length > 120 || (long)frameBytes * bgraFrames.Length > MaximumVideoInputBytes)
                throw new ArgumentException("Video segment input exceeds the frame or byte limit.", nameof(bgraFrames));
            foreach (byte[] frame in bgraFrames)
                if (frame == null || frame.Length != frameBytes) throw new ArgumentException("Expected tightly packed BGRA frames.", nameof(bgraFrames));

            string rate = Number(bitsPerSecond);
            string keyInterval = Number(bgraFrames.Length);
            return EncodeAsync(bgraFrames, new[] {
                "-f", "rawvideo", "-pixel_format", "bgra", "-video_size", Number(width) + "x" + Number(height),
                "-framerate", Number(framesPerSecond), "-i", "pipe:0", "-an", "-c:v", "libx264",
                "-preset", "ultrafast", "-tune", "zerolatency", "-profile:v", "baseline", "-level:v", "3.1", "-pix_fmt", "yuv420p",
                "-threads:v", "2", "-bf", "0", "-g", keyInterval, "-keyint_min", keyInterval, "-sc_threshold", "0",
                "-b:v", rate, "-maxrate", rate, "-bufsize", Number(bitsPerSecond / 2),
                "-movflags", "+empty_moov+default_base_moof+frag_keyframe", "-f", "mp4", "pipe:1"
            }, token);
        }

        /// <summary>Encodes one segment of mono, signed little-endian 16-bit PCM as AAC-LC with ADTS frame headers.</summary>
        public Task<byte[]> EncodeAudioAsync(byte[] pcm, int sampleRate, int bitsPerSecond, CancellationToken token) {
            if (pcm == null) throw new ArgumentNullException(nameof(pcm));
            if (pcm.Length == 0 || pcm.Length > MaximumSegmentBytes || pcm.Length % 2 != 0)
                throw new ArgumentException("Invalid PCM16 segment length.", nameof(pcm));
            if (!(sampleRate is 8000 or 11025 or 12000 or 16000 or 22050 or 24000 or 32000 or 44100 or 48000))
                throw new ArgumentOutOfRangeException(nameof(sampleRate));
            if (bitsPerSecond < 8000 || bitsPerSecond > 192000) throw new ArgumentOutOfRangeException(nameof(bitsPerSecond));

            return EncodeAsync(new[] { pcm }, new[] {
                "-f", "s16le", "-ar", Number(sampleRate), "-ac", "1", "-i", "pipe:0", "-vn",
                "-c:a", "aac", "-profile:a", "aac_low", "-b:a", Number(bitsPerSecond), "-f", "adts", "pipe:1"
            }, token);
        }

        private async Task<byte[]> EncodeAsync(IReadOnlyList<byte[]> input, string[] arguments, CancellationToken token) {
            token.ThrowIfCancellationRequested();
            var startInfo = new ProcessStartInfo(executable) {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (string option in new[] { "-hide_banner", "-loglevel", "error", "-nostdin" }) startInfo.ArgumentList.Add(option);
            foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);

            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            process.Exited += (_, __) => exited.TrySetResult(process.ExitCode);
            if (!process.Start()) throw new InvalidOperationException("FFmpeg could not start.");
            using var cancellation = token.Register(() => Stop(process));

            Task<byte[]> output = ReadOutputAsync(process, token);
            Task<string> diagnostics = ReadDiagnosticsAsync(process);
            Task written = WriteInputAsync(process, input, token);
            try {
                await Task.WhenAll(written, output, diagnostics, exited.Task).ConfigureAwait(false);
            } catch {
                token.ThrowIfCancellationRequested();
                if (output.IsFaulted && output.Exception.GetBaseException() is InvalidDataException)
                    await output.ConfigureAwait(false);
                if (exited.Task.Result == 0 || !diagnostics.IsCompletedSuccessfully) throw;
            }

            token.ThrowIfCancellationRequested();
            if (exited.Task.Result != 0) {
                string error = diagnostics.Result.Trim();
                throw new InvalidOperationException("FFmpeg exited with code " + Number(exited.Task.Result) +
                    (error.Length == 0 ? "." : ": " + error));
            }
            if (output.Result.Length == 0) throw new InvalidDataException("FFmpeg produced an empty media segment.");
            return output.Result;
        }

        private static async Task WriteInputAsync(Process process, IReadOnlyList<byte[]> blocks, CancellationToken token) {
            try {
                using Stream input = process.StandardInput.BaseStream;
                foreach (byte[] block in blocks) await input.WriteAsync(block.AsMemory(), token).ConfigureAwait(false);
            } catch {
                Stop(process);
                throw;
            }
        }

        private static async Task<byte[]> ReadOutputAsync(Process process, CancellationToken token) {
            try {
                Stream output = process.StandardOutput.BaseStream;
                using var collected = new MemoryStream();
                byte[] buffer = new byte[8192];
                int length;
                while ((length = await output.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0) {
                    if (collected.Length + length > MaximumSegmentBytes) throw new InvalidDataException("FFmpeg output exceeded the 512 KiB segment limit.");
                    collected.Write(buffer, 0, length);
                }
                return collected.ToArray();
            } catch {
                Stop(process);
                throw;
            }
        }

        private static async Task<string> ReadDiagnosticsAsync(Process process) {
            try {
                const int maximumDiagnosticChars = 4096;
                var text = new StringBuilder();
                char[] buffer = new char[1024];
                int length;
                while ((length = await process.StandardError.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                    text.Append(buffer, 0, Math.Min(length, maximumDiagnosticChars - text.Length));
                return text.ToString();
            } catch {
                Stop(process);
                throw;
            }
        }

        private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

        private static void Stop(Process process) {
            try {
                if (!process.HasExited) process.Kill();
            } catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
    }
}
