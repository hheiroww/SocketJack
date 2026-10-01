using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text.Json;

namespace UdpReliable.Bench;

// Opt-in diagnostic evidence only. Keep disabled for speed acceptance measurements.
internal sealed class Diagnostics : IDisposable {
    readonly ConcurrentDictionary<string, long> counts = new();
    readonly ConcurrentDictionary<string, string> stacks = new();
    readonly Stopwatch elapsed = Stopwatch.StartNew();
    readonly string directory;
    readonly string[] arguments;
    Diagnostics(string directory, string[] arguments) {
        this.directory = directory; this.arguments = arguments;
        AppDomain.CurrentDomain.FirstChanceException += Exception;
    }
    internal static Diagnostics Start(string[] arguments) {
        string directory = Environment.GetEnvironmentVariable("SJ_DIAGNOSTICS");
        return string.IsNullOrEmpty(directory) || arguments.Length == 0 || arguments[0] == "run" ? null : new Diagnostics(directory, arguments);
    }
    void Exception(object sender, FirstChanceExceptionEventArgs args) {
        string type = args.Exception.GetType().FullName;
        counts.AddOrUpdate(type, 1, (_, count) => count + 1);
        if (!stacks.ContainsKey(type)) stacks.TryAdd(type, args.Exception.StackTrace);
    }
    public void Dispose() {
        AppDomain.CurrentDomain.FirstChanceException -= Exception;
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, $"{arguments[1]}-{arguments[0]}-{Environment.ProcessId}.json"),
            JsonSerializer.Serialize(new { Arguments = arguments, ElapsedSeconds = elapsed.Elapsed.TotalSeconds,
                Exceptions = counts, FirstStacks = stacks, Gen0 = GC.CollectionCount(0), Gen1 = GC.CollectionCount(1), Gen2 = GC.CollectionCount(2) },
                new JsonSerializerOptions { WriteIndented = true }));
    }
}
