using SocketJack.Net;
using SocketJack.Serialization;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace BinaryCompression.Bench;

public sealed class Payload { public int Sequence { get; set; } public byte[] Data { get; set; } }
public sealed class Receipt { public int Sequence { get; set; } }
internal sealed record Measurement(string Protocol, string Workload, string Phase, bool Enabled, int Round,
    double ElapsedMs, long PayloadBytes, int Messages, double RttP50Ms, double RttP95Ms, double RttP99Ms,
    long UpBytes, long DownBytes, double SetupMs, long SetupUpBytes, long SetupDownBytes,
    double CpuMs, int DictionaryCount, long CacheBytes, double SettleMs, long PriorUpBytes, long PriorDownBytes,
    long AllocatedBytes, int CandidateCount, long EncodingAttempts, long BypassedMessages, int ProbeInterval,
    int PlanCount, long PlanHits, long LearningPasses);

internal static class Program {
    const int ColdMessages = 64, WarmMessages = 128, LatencyMessages = 64, Window = 8;
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    static readonly string[] Workloads = { "repeated-1k", "repeated-32k", "delta-32k", "random-32k", "runs-32k" };
    internal sealed record Phase(string Name, int Count, int Window);
    static Phase[] Phases(string workload) => workload.StartsWith("sustained-", StringComparison.Ordinal) ? new[] {
        new Phase("cold", ColdMessages, Window), new Phase("warm", 2048, Window), new Phase("latency", 256, 1)
    } : workload == "cold-first-32k" ? new[] {
        new Phase("first", 1, 1), new Phase("cold", 63, Window),
        new Phase("warm", WarmMessages, Window), new Phase("latency", LatencyMessages, 1)
    } : workload == "adaptive-32k" ? new[] {
        new Phase("random", 64, Window), new Phase("repeat", 64, Window), new Phase("churn", 64, Window),
        new Phase("return", 64, Window), new Phase("runs", 64, Window), new Phase("latency", 64, 1)
    } : workload == "spaced-32k" ? new[] {
        new Phase("cold", ColdMessages, Window), new Phase("warm", WarmMessages, Window),
        new Phase("trained", 64, Window), new Phase("latency", LatencyMessages, 1)
    } : new[] { new Phase("cold", ColdMessages, Window), new Phase("warm", WarmMessages, Window), new Phase("latency", LatencyMessages, 1) };

    static async Task<int> Main(string[] args) {
        try {
            if (args.Length == 0) throw new ArgumentException("Use run <results.json> [rounds=10] [protocols=tcp,reliable-udp] [workloads=all].");
            if (args[0] == "server") await Server(args[1], int.Parse(args[2]), bool.Parse(args[3]), args[4]);
            else if (args[0] == "client") Console.WriteLine(JsonSerializer.Serialize(await Client(args[1], int.Parse(args[2]), bool.Parse(args[3]), args[4], int.Parse(args[5])), Json));
            else if (args[0] == "run") await Run(Path.GetFullPath(args[1]), args.Length > 2 ? int.Parse(args[2]) : 10,
                args.Length > 3 ? args[3].Split(',') : new[] { "tcp", "reliable-udp" }, args.Length > 4 ? args[4].Split(',') : Workloads);
            else throw new ArgumentException("Unknown command.");
            return 0;
        } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    static NetworkOptions Options(bool enabled) {
        var options = new NetworkOptions {
            Serializer = new BinarySerializer { EnableCompression = enabled, ShareCompressionDictionary = false }, SafeMode = true,
            UseCompression = false, EnablePatternCache = false, UsePeerToPeer = false,
            Chunking = false, Fps = 0, Logging = false, AutoReconnect = false,
            ConnectionTimeout = TimeSpan.FromSeconds(8), MaximumUploadMbps = 0, MaximumDownloadMbps = 0,
            UdpReliable = new UdpReliableOptions { Ordering = false, DeliveryTimeout = TimeSpan.FromSeconds(30) }
        };
        options.Whitelist.Add(typeof(Payload)); options.Whitelist.Add(typeof(Receipt));
        options.Authorization.AnonymousMessageTypes.Add(typeof(Payload)); options.Authorization.AnonymousMessageTypes.Add(typeof(Receipt));
        return options;
    }

    static byte[][] Fixtures(string workload) {
        string scenario = workload.StartsWith("sustained-", StringComparison.Ordinal) ? workload.Substring("sustained-".Length) : workload;
        int size = workload == "repeated-1k" ? 1024 : 32768;
        byte[] common = new byte[size]; new Random(912).NextBytes(common);
        var values = new byte[Phases(workload).Sum(phase => phase.Count)][];
        for (int i = 0; i < values.Length; i++) {
            if (scenario.StartsWith("repeated-")) values[i] = common;
            else if (workload == "adaptive-32k") {
                // Change stream behavior without reconnecting or clearing the adaptive cache.
                // The original data returns after 64 unrelated objects, well beyond ten.
                int stage = i / 64;
                if (stage == 1 || stage == 3) values[i] = common;
                else if (stage < 4) { values[i] = new byte[size]; new Random(7301 + i).NextBytes(values[i]); }
                else {
                    values[i] = Enumerable.Repeat((byte)'A', size).ToArray();
                    new Random(7301 + i).NextBytes(values[i].AsSpan(0, 64));
                }
            }
            else if (workload == "spaced-32k") {
                // Each high-entropy object returns only after 31 different objects.
                // A strict ten-message candidate history cannot learn these repetitions.
                values[i] = new byte[size]; new Random(1912 + i % 32).NextBytes(values[i]);
            }
            else if (scenario == "random-32k") { values[i] = new byte[size]; new Random(7301 + i).NextBytes(values[i]); }
            else if (workload == "marginal-32k") {
                values[i] = new byte[size]; new Random(7301 + i).NextBytes(values[i]);
                // A stable high-entropy region around the 4/9 scout position should not
                // justify repeatedly scanning and encoding the other 98.4% random bytes.
                int offset = size * 4 / 9 - 256;
                Buffer.BlockCopy(common, offset, values[i], offset, 512);
            }
            else if (scenario == "delta-blocks-32k") {
                values[i] = (byte[])common.Clone();
                var changing = new Random(7301 + i);
                for (int offset = 0; offset < size; offset += 4096) changing.NextBytes(values[i].AsSpan(offset + 3072, 1024));
            }
            else if (scenario == "delta-32k" || workload == "cold-first-32k") {
                values[i] = (byte[])common.Clone();
                byte[] changing = new byte[size / 4]; new Random(7301 + i).NextBytes(changing);
                Buffer.BlockCopy(changing, 0, values[i], size - changing.Length, changing.Length);
            } else if (workload == "runs-32k") {
                values[i] = Enumerable.Repeat((byte)'A', size).ToArray();
                new Random(7301 + i).NextBytes(values[i].AsSpan(0, 64));
            } else throw new ArgumentException("Unknown workload: " + workload);
        }
        return values;
    }

    static int Port(string protocol) {
        if (protocol == "reliable-udp") { using var socket = new System.Net.Sockets.UdpClient(0); return ((IPEndPoint)socket.Client.LocalEndPoint).Port; }
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    static async Task Server(string protocol, int port, bool enabled, string workload) {
        var fixtures = Fixtures(workload);
        using var link = new Link(protocol, Options(enabled), true, port);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new int[fixtures.Length];
        link.Socket.RegisterCallback<Payload>(e => {
            var message = e.Object;
            if (message.Sequence == -1) { done.TrySetResult(); return; }
            if (message.Sequence == -2) { _ = DrainAndReply(e.Connection); return; }
            if ((uint)message.Sequence >= fixtures.Length || message.Data == null || !message.Data.AsSpan().SequenceEqual(fixtures[message.Sequence]))
                throw new InvalidDataException("Received payload differs from the seeded input.");
            if (Interlocked.Exchange(ref seen[message.Sequence], 1) != 0) throw new InvalidDataException("Duplicate application delivery.");
            link.Reply(e.Connection, new Receipt { Sequence = message.Sequence });
        });
        if (!link.Listen()) throw new IOException("Server could not bind its loopback port.");
        Console.WriteLine("READY"); Console.Out.Flush();
        await done.Task.WaitAsync(TimeSpan.FromMinutes(5));
        await Task.Delay(200);
        link.ThrowIfFailed();
        if (seen.Any(value => value != 1)) throw new InvalidDataException("Not every requested message reached the server.");

        async Task DrainAndReply(NetworkConnection connection) {
            try {
                var limit = Stopwatch.StartNew(); var quiet = Stopwatch.StartNew();
                while (quiet.ElapsedMilliseconds < 80) {
                    if (limit.ElapsedMilliseconds > 10000) throw new TimeoutException("Server reliable controls did not drain.");
                    if (link.QueuedBytes != 0) quiet.Restart();
                    await Task.Delay(5);
                }
                link.Reply(connection, new Receipt { Sequence = -2 });
            } catch (Exception error) { done.TrySetException(error); }
        }
    }

    static async Task<Measurement[]> Client(string protocol, int port, bool enabled, string workload, int round) {
        var fixtures = Fixtures(workload); // Fixture creation is outside every timer.
        await using var proxy = new TrafficProxy(protocol == "reliable-udp" ? TrafficProxy.Transport.Udp : TrafficProxy.Transport.Tcp, port);
        using var link = new Link(protocol, Options(enabled), false);
        var arrivals = new TaskCompletionSource<long>[fixtures.Length];
        TaskCompletionSource<long> drained = null;
        for (int i = 0; i < arrivals.Length; i++) arrivals[i] = new(TaskCreationOptions.RunContinuationsAsynchronously);
        link.Socket.RegisterCallback<Receipt>(e => {
            if (e.Object.Sequence == -2) { drained?.TrySetResult(Stopwatch.GetTimestamp()); return; }
            if ((uint)e.Object.Sequence >= arrivals.Length || !arrivals[e.Object.Sequence].TrySetResult(Stopwatch.GetTimestamp()))
                throw new InvalidDataException("Invalid or duplicate application receipt.");
        });
        var connecting = Stopwatch.StartNew();
        if (!await link.Connect(proxy.Port)) throw new IOException("Client failed to connect through the counting proxy.");
        if (link.Socket.Connection.Compatibility?.BinaryCompression != enabled) throw new InvalidDataException("Compression negotiation does not match the requested setting.");
        connecting.Stop();
        await Settle(proxy, link);
        var setup = proxy.Snapshot();
        var measurements = new List<Measurement>();
        int sequence = 0;
        foreach (var phase in Phases(workload)) {
            var before = proxy.Snapshot();
            using var process = Process.GetCurrentProcess(); double cpu = process.TotalProcessorTime.TotalMilliseconds;
            long allocatedBefore = GC.GetTotalAllocatedBytes(false);
            var elapsed = Stopwatch.StartNew();
            var latencies = new List<double>(phase.Count);
            int first = sequence;
            for (int group = 0; group < phase.Count; group += phase.Window) {
                var pending = new List<Task<(long ticks, int sequence, long start)>>(phase.Window);
                for (int j = group; j < Math.Min(group + phase.Window, phase.Count); j++) {
                    int number = sequence++;
                    long start = Stopwatch.GetTimestamp();
                    pending.Add(Deliver(number, start));
                }
                foreach (var receipt in await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(45)))
                    latencies.Add((receipt.ticks - receipt.start) * 1000.0 / Stopwatch.Frequency);
            }
            elapsed.Stop();
            double cpuMs = process.TotalProcessorTime.TotalMilliseconds - cpu;
            long allocatedBytes = GC.GetTotalAllocatedBytes(false) - allocatedBefore;
            var drainWatch = Stopwatch.StartNew();
            if (protocol == "reliable-udp") {
                drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
                await link.Send(new Payload { Sequence = -2, Data = Array.Empty<byte>() });
                await drained.Task.WaitAsync(TimeSpan.FromSeconds(15));
            }
            await Settle(proxy, link);
            double settling = drainWatch.Elapsed.TotalMilliseconds;
            var after = proxy.Snapshot();
            latencies.Sort();
            var serializer = (BinarySerializer)link.Socket.Options.Serializer;
            measurements.Add(new(protocol, workload, phase.Name, enabled, round,
                elapsed.Elapsed.TotalMilliseconds, Enumerable.Range(first, phase.Count).Sum(i => (long)fixtures[i].Length), phase.Count,
                Percentile(latencies, .5), Percentile(latencies, .95), Percentile(latencies, .99),
                after.up - before.up, after.down - before.down, connecting.Elapsed.TotalMilliseconds, setup.up, setup.down,
                cpuMs, serializer.CompressionDictionaryCount, serializer.CompressionCacheBytes, settling,
                before.up - setup.up, before.down - setup.down, allocatedBytes,
                serializer.CompressionCandidateCount, serializer.CompressionEncodingAttempts,
                serializer.CompressionBypassedMessages, serializer.CompressionProbeInterval,
                serializer.CompressionPlanCount, serializer.CompressionPlanHits, serializer.CompressionLearningPasses));
            link.ThrowIfFailed();
            if (proxy.Failure != null) throw new IOException("The counting proxy failed.", proxy.Failure);
        }
        await link.Send(new Payload { Sequence = -1, Data = Array.Empty<byte>() });
        await Task.Delay(100);
        return measurements.ToArray();

        async Task<(long ticks, int sequence, long start)> Deliver(int number, long start) {
            // Both completion conditions are required: typed delivery receipt and successful send.
            Task sending = link.Send(new Payload { Sequence = number, Data = fixtures[number] });
            long ticks = await arrivals[number].Task.WaitAsync(TimeSpan.FromSeconds(40));
            await sending;
            return (ticks, number, start);
        }
    }

    static double Percentile(List<double> ordered, double fraction) => ordered[Math.Clamp((int)Math.Ceiling(ordered.Count * fraction) - 1, 0, ordered.Count - 1)];

    static async Task<double> Settle(TrafficProxy proxy, Link link) {
        var watch = Stopwatch.StartNew(); var quiet = Stopwatch.StartNew();
        var previous = proxy.Snapshot();
        while (watch.Elapsed < TimeSpan.FromSeconds(10)) {
            await Task.Delay(20);
            var current = proxy.Snapshot();
            if (current.up != previous.up || current.down != previous.down || link.QueuedBytes != 0) quiet.Restart();
            else if (quiet.ElapsedMilliseconds >= 160) { link.ThrowIfFailed(); return watch.Elapsed.TotalMilliseconds; }
            previous = current;
        }
        throw new TimeoutException("The transport did not drain dictionary/control traffic within ten seconds.");
    }

    static Process Child(params string[] args) {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        foreach (string argument in args) start.ArgumentList.Add(argument);
        return Process.Start(start);
    }

    static async Task Run(string output, int rounds, string[] protocols, string[] workloads) {
        Directory.CreateDirectory(Path.GetDirectoryName(output));
        var measurements = new List<Measurement>();
        string corePath = typeof(BinarySerializer).Assembly.Location;
        string coreHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(corePath)));
        var started = DateTimeOffset.UtcNow;
        for (int round = 0; round < rounds; round++) {
            var cases = (from protocol in protocols from workload in workloads select (protocol, workload)).ToArray();
            foreach (var item in cases.Skip(round % cases.Length).Concat(cases.Take(round % cases.Length))) {
                foreach (bool enabled in round % 2 == 0 ? new[] { false, true } : new[] { true, false }) {
                    int port = Port(item.protocol);
                    using var server = Child("server", item.protocol, port.ToString(), enabled.ToString(), item.workload);
                    var serverErrors = server.StandardError.ReadToEndAsync();
                    try {
                        if (await server.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)) != "READY")
                            throw new IOException("Server startup failed: " + await serverErrors);
                        using var client = Child("client", item.protocol, port.ToString(), enabled.ToString(), item.workload, round.ToString());
                        Task<string> clientOutput = client.StandardOutput.ReadToEndAsync(), clientErrors = client.StandardError.ReadToEndAsync();
                        await client.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(4));
                        if (client.ExitCode != 0) throw new IOException("Client failed: " + await clientErrors + " stdout=" + await clientOutput);
                        var rows = JsonSerializer.Deserialize<Measurement[]>(await clientOutput, Json);
                        await server.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                        if (server.ExitCode != 0) throw new IOException("Server failed: " + await serverErrors);
                        if (coreHash != Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(corePath)))) throw new IOException("The benchmark DLL changed during the run.");
                        measurements.AddRange(rows);
                        var metadata = new {
                            startedUtc = started, updatedUtc = DateTimeOffset.UtcNow, Environment.MachineName,
                            os = Environment.OSVersion.ToString(), runtime = Environment.Version.ToString(), Environment.ProcessorCount,
                            processor = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"),
                            physicalMemoryBytes = new BinarySerializer().CompressionMemoryLimit * 16,
                            corePath, coreSha256 = coreHash, harnessSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(Program).Assembly.Location))),
                            rounds, coldMessages = ColdMessages, warmMessages = WarmMessages, latencyMessages = LatencyMessages, bulkWindow = Window,
                            phasePlans = workloads.ToDictionary(workload => workload, workload => Phases(workload)),
                            sustained = "Workloads prefixed sustained- use the same seeded delta, distributed-delta or random fixtures with cold 64, warm 2048 and stop-and-wait latency 256. See phasePlans for authoritative per-workload counts.",
                            network = "Separate server/client processes on clean IPv4 loopback, transparent counting proxy in client process; no artificial bandwidth/latency/loss.",
                            counters = "Successfully forwarded TCP stream bytes / UDP datagram bytes, both directions exactly once; includes handshake, dictionary updates/ACKs, application receipts and reliable transport controls/retries; excludes IP/transport/link headers and final harness shutdown message.",
                            phases = "See per-workload phasePlans. Standard workloads use cold 64, warm 128, stop-and-wait latency 64. cold-first-32k uses the 75%-stable delta fixture with first object 1 (window 1), remaining cold 63, warm 128, latency 64; no throwaway object warms compression. delta-blocks-32k changes the last 1 KiB of each 4 KiB block (25% total). adaptive-32k uses fresh random 64, repeated 64, fresh random churn 64, return of original 64, byte runs 64, stop-and-wait byte-runs latency 64; one cache persists across phases. spaced-32k repeats a cycle of 32 independently seeded random objects across cold 64, warm 128 (which may still include dictionary growth), trained bulk 64, and stop-and-wait latency 64. marginal-32k uses fresh random objects with 512 stable high-entropy bytes around the 4/9 scout position (1.6% stable) and standard phases. Bulk RTT uses window 8. Byte snapshots include trailing controls after 160 ms quiet and a reliable-UDP server queue-drain barrier (included in bytes); settling is excluded from throughput timers. Setup timing is Connect only; setup byte counts include its quiet drain.",
                            settings = "SafeMode/compatibility negotiation on; per-client dictionaries (ShareCompressionDictionary=false) on both endpoints; only BinarySerializer.EnableCompression changes. Outer compression, old pattern cache, P2P, batching, FPS and bandwidth caps off. Reliable UDP default Adaptive, independent delivery, 1200-byte datagrams. Shared dictionary propagation is measured separately by FanoutBench.",
                            cpu = "cpuMs is client-process CPU, including counting proxy, not combined endpoint CPU. allocatedBytes is approximate process-wide managed allocation during the timed phase, including the proxy; counters are read outside the stopwatch and exclude the later drain.",
                            measurements
                        };
                        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(metadata, Json));
                        var warm = rows.FirstOrDefault(row => row.Phase == "warm") ?? rows.Single(row => row.Phase == "return");
                        var last = rows[^1];
                        long completeBytes = last.SetupUpBytes + last.SetupDownBytes + last.PriorUpBytes + last.PriorDownBytes + last.UpBytes + last.DownBytes;
                        Console.WriteLine($"{round + 1}/{rounds} {item.protocol} {item.workload} {(enabled ? "on" : "off")}: {warm.Phase} {warm.PayloadBytes / 1048576.0 / (warm.ElapsedMs / 1000):F2} MiB/s, loaded p95 {warm.RttP95Ms:F3} ms, complete bytes {completeBytes:N0}");
                    } finally {
                        // Only the process created by this harness is eligible for cleanup.
                        if (!server.HasExited) server.Kill(entireProcessTree: true);
                    }
                }
            }
        }
    }

    sealed class Link : IDisposable {
        readonly string protocol;
        readonly bool server;
        readonly ConcurrentQueue<Exception> errors = new();
        internal ISocket Socket { get; }
        internal long QueuedBytes => Socket switch {
            SocketJack.Net.UdpClient udp => udp.ReliableStatistics?.QueuedBytes ?? 0,
            SocketJack.Net.UdpServer udp => udp.Clients.Values.Sum(client => client.ReliableStatistics?.QueuedBytes ?? 0),
            _ => 0
        };
        internal Link(string protocol, NetworkOptions options, bool server, int port = 0) {
            this.protocol = protocol; this.server = server;
            Socket = protocol switch {
                "tcp" when server => new SocketJack.Net.TcpServer(options, port),
                "tcp" => new SocketJack.Net.TcpClient(options),
                "reliable-udp" when server => UDP_Reliable.CreateServer(port, options),
                "reliable-udp" => UDP_Reliable.CreateClient(options),
                _ => throw new ArgumentException("Unknown protocol: " + protocol)
            };
            if (Socket is NetworkBase network) network.OnError += e => errors.Enqueue(e.Exception);
        }
        internal bool Listen() => Socket switch { SocketJack.Net.TcpServer tcp => tcp.Listen(), SocketJack.Net.UdpServer udp => udp.Listen(), _ => false };
        internal Task<bool> Connect(int port) => Socket switch { SocketJack.Net.TcpClient tcp => tcp.Connect("127.0.0.1", port), SocketJack.Net.UdpClient udp => udp.Connect("127.0.0.1", port), _ => Task.FromResult(false) };
        internal Task Send(Payload value) {
            if (Socket is SocketJack.Net.TcpClient tcp) { tcp.Send(value); return Task.CompletedTask; }
            return ((SocketJack.Net.UdpClient)Socket).SendAsync(value);
        }
        internal void Reply(NetworkConnection connection, Receipt receipt) {
            if (Socket is SocketJack.Net.TcpServer tcp) tcp.Send(connection, receipt);
            else connection.Send(receipt);
        }
        internal void ThrowIfFailed() { if (errors.TryPeek(out var failure)) throw new IOException("SocketJack reported a transport error.", failure); }
        public void Dispose() => Socket.Dispose();
    }
}
