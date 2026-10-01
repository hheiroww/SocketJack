using SocketJack.Extensions;
using SocketJack.Serialization;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace UdpReliable.Bench;

internal static class CodecFocusBench {
    static object sink;
    internal static void Run(string output, int runs, int firstRun = 0) {
        var rows = new List<object>();
        foreach (int size in new[] { 256, 32768, 262144 }) {
            byte[] payload = new byte[size]; new Random(731).NextBytes(payload);
            var source = new Message { Kind = 2, Sequence = 127, Total = size, Data = payload, Hash = "sample" };
            for (int run = firstRun; run < firstRun + runs; run++) foreach (string mode in run % 2 == 0 ? new[] { "binary", "json" } : new[] { "json", "binary" }) {
                var options = Program.Options();
                options.Serializer = mode == "binary" ? new BinarySerializer() : new SocketJack.Serialization.Json.JsonSerializer();
                using var socket = new SocketJack.Net.UdpClient(options); socket.RegisterCallback<Message>(_ => { });
                var codec = options.Serializer;
                var wrapper = new Wrapper(source, socket); byte[] encoded = codec.Serialize(wrapper);
                var decoded = (Message)codec.Deserialize(encoded).Unwrap(socket);
                if (!decoded.Data.AsSpan().SequenceEqual(payload) || decoded.Sequence != source.Sequence) throw new InvalidDataException("Round trip mismatch.");
                var operations = new (string Name, Action Run)[] {
                    ("encode", () => sink = codec.Serialize(wrapper)),
                    ("decode", () => sink = codec.Deserialize(encoded)),
                    ("wrap-encode", () => sink = codec.Serialize(new Wrapper(source, socket))),
                    ("decode-unwrap", () => sink = codec.Deserialize(encoded).Unwrap(socket)),
                };
                foreach (var operation in operations.Skip(run % 4).Concat(operations.Take(run % 4))) {
                    var warmup = Stopwatch.StartNew();
                    do { for (int i = 0; i < 32; i++) operation.Run(); } while (warmup.ElapsedMilliseconds < 200);
                    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                    long allocated = GC.GetAllocatedBytesForCurrentThread(); int g0 = GC.CollectionCount(0), g2 = GC.CollectionCount(2);
                    int count = 0; var watch = Stopwatch.StartNew();
                    do { for (int i = 0; i < 32; i++) operation.Run(); count += 32; } while (watch.ElapsedMilliseconds < 150);
                    watch.Stop();
                    rows.Add(new { Mode = mode, Payload = size, Run = run, Operation = operation.Name, Count = count, EncodedBytes = encoded.Length,
                        Nanoseconds = watch.Elapsed.TotalNanoseconds / count, Allocated = (GC.GetAllocatedBytesForCurrentThread() - allocated) / (double)count,
                        Gen0 = GC.CollectionCount(0) - g0, Gen2 = GC.CollectionCount(2) - g2 });
                }
            }
            Console.WriteLine($"Codec focus finished: {size} bytes");
        }
        byte[] bulk = new byte[4 * 1024 * 1024]; new Random(913).NextBytes(bulk);
        for (int run = firstRun; run < firstRun + runs; run++) {
            var segments = bulk.GetSegments();
            foreach (string operation in new[] { "segment-build", "segment-rebuild" }) {
                Action action = operation == "segment-build" ? () => sink = bulk.GetSegments() : () => {
                    SocketJack.Segment.Cache[segments[0].SID] = segments.Reverse().ToList();
                    sink = SocketJack.Segment.Rebuild(segments[0]);
                };
                action(); GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                long allocated = GC.GetAllocatedBytesForCurrentThread(); int count = 0; var watch = Stopwatch.StartNew();
                do { action(); count++; } while (watch.ElapsedMilliseconds < 150);
                watch.Stop();
                if (operation == "segment-rebuild" && !((byte[])sink).AsSpan().SequenceEqual(bulk)) throw new InvalidDataException("Segment mismatch.");
                rows.Add(new { Mode = "segment", Payload = bulk.Length, Run = run, Operation = operation, Count = count,
                    Nanoseconds = watch.Elapsed.TotalNanoseconds / count, Allocated = (GC.GetAllocatedBytesForCurrentThread() - allocated) / (double)count });
            }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(new { DateUtc = DateTime.UtcNow, Environment.MachineName,
            Runtime = Environment.Version.ToString(), CoreSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(BinarySerializer).Assembly.Location))),
            HarnessSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(Program).Assembly.Location))),
            Method = "Ten alternating codec rounds; phases rotate. At least 200ms warmup then full GC before each phase, at least 150ms timed in batches of 32. SafeMode on with trusted assembly pin; same payload. Includes full wrap+encode and decode+unwrap. Separate 4MiB segment builder/rebuilder.", Measurements = rows }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
