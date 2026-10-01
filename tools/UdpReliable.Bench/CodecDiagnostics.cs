using SocketJack.Serialization;
using System.Diagnostics;
using System.Text.Json;

namespace UdpReliable.Bench;

// Isolate the raw codec from Unwrap and the preceding phase's garbage collections.
// Supplemental diagnostic, separate from the primary alternating microbenchmark.
internal static class CodecDiagnostics {
    internal static void Run(string output) {
        var rows = new List<object>();
        byte[] payload = new byte[262144]; new Random(731).NextBytes(payload);
        using var schema = new SocketJack.Net.UdpClient(Program.Options());
        schema.RegisterCallback<Message>(_ => { });
        var wrapper = new Wrapper(new Message { Kind = 2, Sequence = 127, Total = payload.Length, Data = payload }, schema);
        var modes = new[] { "binary-before", "binary", "json" };
        for (int run = 0; run < 10; run++) foreach (string mode in modes.Skip(run % 3).Concat(modes.Take(run % 3))) {
            ISerializer codec = mode == "binary" ? new BinarySerializer() : mode == "binary-before" ? new BeforeBinarySerializer() : new SocketJack.Serialization.Json.JsonSerializer();
            byte[] encoded = codec.Serialize(wrapper);
            foreach (string operation in new[] { "encode", "decode" }) {
                object sink = null;
                for (int i = 0; i < 200; i++) sink = operation == "encode" ? codec.Serialize(wrapper) : codec.Deserialize(encoded);
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                long allocated = GC.GetAllocatedBytesForCurrentThread();
                int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
                var watch = Stopwatch.StartNew();
                for (int i = 0; i < 2000; i++) sink = operation == "encode" ? codec.Serialize(wrapper) : codec.Deserialize(encoded);
                watch.Stop();
                rows.Add(new { Run = run, Mode = mode, Operation = operation, Nanoseconds = watch.Elapsed.TotalNanoseconds / 2000,
                    AllocatedBytes = (GC.GetAllocatedBytesForCurrentThread() - allocated) / 2000.0,
                    Gen0 = GC.CollectionCount(0) - g0, Gen1 = GC.CollectionCount(1) - g1, Gen2 = GC.CollectionCount(2) - g2 });
                GC.KeepAlive(sink);
            }
        }
        File.WriteAllText(output, JsonSerializer.Serialize(new { DateUtc = DateTime.UtcNow, Method = "256 KiB raw codec only; 200 warmups, full GC before each phase, 2000 operations, ten rotating rounds; in-phase GC remains timed.", Measurements = rows }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
