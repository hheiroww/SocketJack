using SocketJack.Serialization;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace UdpReliable.Bench;

internal static class SerializerBench {
    internal static void Run(string output) {
        var rows = new List<object>();
        foreach (int size in new[] { 256, 32768, 262144 }) {
            byte[] payload = new byte[size]; new Random(731).NextBytes(payload);
            var source = new Message { Kind = 2, Sequence = 127, Total = size, Data = payload };
            int iterations = size == 256 ? 200 : size == 32768 ? 100 : 50;
            var modes = new[] { "binary-before", "binary", "json" };
            for (int run = 0; run < 10; run++) foreach (string mode in modes.Skip(run % 3).Concat(modes.Take(run % 3))) {
                var options = Program.Options();
                options.Serializer = mode == "binary" ? new BinarySerializer() : mode == "binary-before" ? new BeforeBinarySerializer() : new SocketJack.Serialization.Json.JsonSerializer();
                using var schema = new SocketJack.Net.UdpClient(options); schema.RegisterCallback<Message>(_ => { });
                var serializer = options.Serializer;
                var wrapper = new Wrapper(source, schema); byte[] encoded = serializer.Serialize(wrapper);
                for (int warm = 0; warm < 20; warm++) serializer.Deserialize(serializer.Serialize(wrapper)).Unwrap(schema);
                long allocation = GC.GetAllocatedBytesForCurrentThread(); var timer = Stopwatch.StartNew();
                for (int i = 0; i < iterations; i++) encoded = serializer.Serialize(wrapper);
                timer.Stop(); double encodeNs = timer.Elapsed.TotalNanoseconds / iterations;
                long encodeAllocation = (GC.GetAllocatedBytesForCurrentThread() - allocation) / iterations;
                allocation = GC.GetAllocatedBytesForCurrentThread(); timer.Restart();
                for (int i = 0; i < iterations; i++) serializer.Deserialize(encoded);
                timer.Stop(); double decodeNs = timer.Elapsed.TotalNanoseconds / iterations;
                long decodeAllocation = (GC.GetAllocatedBytesForCurrentThread() - allocation) / iterations;
                allocation = GC.GetAllocatedBytesForCurrentThread(); timer.Restart();
                Message decoded = null;
                for (int i = 0; i < iterations; i++) decoded = (Message)serializer.Deserialize(encoded).Unwrap(schema);
                timer.Stop();
                if (decoded.Sequence != source.Sequence || !decoded.Data.AsSpan().SequenceEqual(payload)) throw new InvalidDataException("Serializer round trip failed.");
                rows.Add(new { Mode = mode, Run = run, PayloadBytes = size, EncodedBytes = encoded.Length, Iterations = iterations,
                    EncodeNanoseconds = encodeNs, DecodeAndUnwrapNanoseconds = timer.Elapsed.TotalNanoseconds / iterations,
                    DecodeNanoseconds = decodeNs, DecodeOnlyAllocatedBytes = decodeAllocation,
                    EncodeAllocatedBytes = encodeAllocation, DecodeAllocatedBytes = (GC.GetAllocatedBytesForCurrentThread() - allocation) / iterations });
            }
            Console.WriteLine($"Serializer comparison finished: {size} bytes");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(new { DateUtc = DateTime.UtcNow, Environment.MachineName,
            Runtime = Environment.Version.ToString(), CoreSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(BinarySerializer).Assembly.Location))),
            Method = "Ten rotating runs; prebuilt SocketJack Wrapper encode; decode includes Unwrap and SafeMode checks; 20 warmups per case; same typed payload. binary-before is the saved pre-optimization stream-based codec.", Measurements = rows }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
