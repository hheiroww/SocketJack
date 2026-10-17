using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using SocketJack.Serialization;

const int Size = 32768, Warm = 512, Count = 1536;
var rows = new List<object>();
foreach (string workload in new[] { "repeated", "delta", "random", "runs", "spaced" }) {
    var common = new byte[Size]; new Random(912).NextBytes(common);
    var fixtures = new byte[Warm + Count][];
    for (int i = 0; i < fixtures.Length; i++) {
        var raw = (byte[])common.Clone();
        if (workload == "random") new Random(7301 + i).NextBytes(raw);
        else if (workload == "delta") new Random(7301 + i).NextBytes(raw.AsSpan(Size * 3 / 4));
        else if (workload == "runs") { raw.AsSpan().Fill((byte)'A'); new Random(7301 + i).NextBytes(raw.AsSpan(0, 64)); }
        else if (workload == "spaced") new Random(1912 + i % 32).NextBytes(raw);
        BitConverter.GetBytes(i).CopyTo(raw, 0); fixtures[i] = raw;
    }
    for (int round = 0; round < 3; round++) {
        using var cache = new BinaryCompressionCache();
        var known = new HashSet<ulong>();
        byte[] Transfer(byte[] raw) {
            var frame = cache.Encode(raw, known, out var added);
            foreach (var definition in added) known.Add(definition.Id);
            if (added.Length != 0) cache.RequestProbe();
            return frame;
        }
        for (int i = 0; i < Warm; i++) Transfer(fixtures[i]);
        var encoded = new byte[Count][]; var decoded = new byte[Count][];
        long allocated = GC.GetTotalAllocatedBytes();
        var timer = Stopwatch.StartNew();
        for (int i = 0; i < Count; i++) encoded[i] = Transfer(fixtures[Warm + i]);
        timer.Stop(); double encodeUs = timer.Elapsed.TotalMicroseconds / Count;
        long encoderAllocated = GC.GetTotalAllocatedBytes() - allocated;
        timer.Restart();
        for (int i = 0; i < Count; i++) decoded[i] = cache.Decode(encoded[i], Size, out _);
        timer.Stop(); double decodeUs = timer.Elapsed.TotalMicroseconds / Count;
        for (int i = 0; i < Count; i++) if (!decoded[i].AsSpan().SequenceEqual(fixtures[Warm + i])) throw new Exception("CPU-probe payload mismatch.");
        var planHits = cache.GetType().GetProperty("PlanHits", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(cache);
        rows.Add(new { workload, round, encodeUs, decodeUs, encoderAllocated, encodedBytes = encoded.Sum(frame => (long)frame.Length), cacheBytes = cache.RetainedBytes, dictionaryCount = cache.DictionaryCount, cache.EncodingAttempts, cache.BypassedMessages, planHits });
        Console.Error.WriteLine($"{workload} {round}: encode {encodeUs:F2} us, decode {decodeUs:F2} us");
    }
    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
}
File.WriteAllText(args[0], JsonSerializer.Serialize(new { scope = "Diagnostic codec-only CPU cost; no network, SB1 object formatting or dictionary-update bytes. Seeded byte fixtures, immediate simulated ACK, 512 warm objects then 1536 measured, three fresh-cache rounds. Tiered compilation disabled consistently for this diagnostic only.", runtime = Environment.Version.ToString(), rows }, new JsonSerializerOptions { WriteIndented = true }));
