using BinaryCompression.Bench;
using SocketJack.Net;
using SocketJack.Serialization;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Client = SocketJack.Net.TcpClient;
using Server = SocketJack.Net.TcpServer;

namespace BinaryCompression.FanoutBench;

public sealed class Payload { public int Number { get; set; } public byte[] Data { get; set; } }
public sealed class Receipt { public int Number { get; set; } }

internal sealed record Measurement(
    int Round, bool Enabled, int Clients, int ActiveClients, int MessageCount, long PayloadBytes,
    double ElapsedMs, double SetupMs, double SettleMs,
    long TotalUpBytes, long TotalDownBytes, long TotalBytes,
    long ActiveUpBytes, long ActiveDownBytes, long IdleTotalBytes,
    long SetupUpBytes, long SetupDownBytes, long SetupActiveUpBytes, long SetupActiveDownBytes, long SetupIdleTotalBytes,
    long DataUpBytes, long DataDownBytes, long DataActiveUpBytes, long DataActiveDownBytes, long DataIdleTotalBytes,
    int ServerDictionaryCount, int ActiveDictionaryCount, int[] IdleDictionaryCounts, long[] IdleDataBytes);

internal static class Program
{
    private const int ClientCount = 4, Messages = 100, PayloadLength = 32 * 1024;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 0)
                throw new ArgumentException("Use <results.json> [paired-rounds=5].");
            string output = Path.GetFullPath(args[0]);
            int rounds = args.Length > 1 ? int.Parse(args[1]) : 5;
            ArgumentOutOfRangeException.ThrowIfLessThan(rounds, 1);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            var started = DateTimeOffset.UtcNow;
            byte[] data = new byte[PayloadLength];
            new Random(912).NextBytes(data);
            var measurements = new List<Measurement>();
            string corePath = typeof(BinarySerializer).Assembly.Location;
            string coreHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(corePath)));

            for (int round = 1; round <= rounds; round++)
            {
                foreach (bool enabled in round % 2 == 1 ? new[] { false, true } : new[] { true, false })
                {
                    Measurement row = await Trial(round, enabled, data);
                    measurements.Add(row);
                    if (coreHash != Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(corePath))))
                        throw new IOException("The core DLL changed during the benchmark.");
                    await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new
                    {
                        startedUtc = started,
                        updatedUtc = DateTimeOffset.UtcNow,
                        Environment.MachineName,
                        runtime = Environment.Version.ToString(),
                        os = Environment.OSVersion.ToString(),
                        corePath,
                        coreSha256 = coreHash,
                        harnessSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(Program).Assembly.Location))),
                        rounds,
                        scope = "Supplementary TCP fanout byte accounting in one process. One active uploading client and three idle clients use four independent transparent counting proxies. Timing is auxiliary, not a process-isolated speed measurement.",
                        workload = "100 stop-and-wait uploads of the same seeded random 32 KiB Data value, with Number changing on each object. Each server receive verifies all bytes and replies only to the active sender.",
                        counters = "Successfully forwarded TCP stream bytes, both directions exactly once, summed across all four links; includes SafeMode handshake, all dictionary definitions and acknowledgements, application receipts and any other socket control traffic. Excludes operating-system TCP/IP headers and shutdown. Total fields include setup; Data fields subtract setup and include cold learning plus trailing control traffic after 160 ms quiet.",
                        settings = "All sockets use fresh NetworkOptions and BinarySerializer instances with ShareCompressionDictionary=true, explicitly testing shared propagation. SafeMode on; BinarySerializer.EnableCompression alone varies. Outer compression, old pattern cache, P2P, chunking, FPS throttling and bandwidth limits off. Fresh caches each trial with disposal and full GC between trials.",
                        measurements
                    }, Json));
                    Console.WriteLine($"{round}/{rounds} {(enabled ? "on" : "off")}: all links {row.TotalBytes:N0} bytes, data {row.DataUpBytes + row.DataDownBytes:N0}, idle data/control {row.DataIdleTotalBytes:N0}, idle dictionaries [{string.Join(", ", row.IdleDictionaryCounts)}], uploads {row.ElapsedMs:F2} ms");
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                    await Task.Delay(50);
                }
            }
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static NetworkOptions Options(bool enabled)
    {
        var options = new NetworkOptions
        {
            Serializer = new BinarySerializer { EnableCompression = enabled, ShareCompressionDictionary = true },
            BindAddress = IPAddress.Loopback,
            SafeMode = true,
            UseCompression = false,
            EnablePatternCache = false,
            UsePeerToPeer = false,
            Chunking = false,
            Fps = 0,
            Logging = false,
            AutoReconnect = false,
            ConnectionTimeout = TimeSpan.FromSeconds(8),
            MaximumUploadMbps = 0,
            MaximumDownloadMbps = 0
        };
        options.Whitelist.Add(typeof(Payload));
        options.Whitelist.Add(typeof(Receipt));
        options.Authorization.AnonymousMessageTypes.Add(typeof(Payload));
        options.Authorization.AnonymousMessageTypes.Add(typeof(Receipt));
        return options;
    }

    private static async Task<Measurement> Trial(int round, bool enabled, byte[] expected)
    {
        var errors = new ConcurrentQueue<Exception>();
        var proxies = new List<TrafficProxy>();
        var clients = new List<Client>();
        using var server = new Server(Options(enabled), 0);
        server.OnError += e => errors.Enqueue(e.Exception);
        var seen = new int[Messages];
        var acknowledgements = Enumerable.Range(0, Messages)
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        server.RegisterCallback<Payload>(e =>
        {
            int number = e.Object.Number;
            if ((uint)number >= Messages || e.Object.Data == null || !expected.AsSpan().SequenceEqual(e.Object.Data))
            {
                errors.Enqueue(new InvalidDataException("The received upload differs from its seeded input."));
                return;
            }
            if (Interlocked.Exchange(ref seen[number], 1) != 0)
            {
                errors.Enqueue(new InvalidDataException("An upload was delivered more than once."));
                return;
            }
            server.Send(e.Connection, new Receipt { Number = number });
        });

        try
        {
            var setupWatch = Stopwatch.StartNew();
            if (!server.Listen())
                throw new IOException("The fanout server could not listen.");
            int port = ((IPEndPoint)server.Socket.LocalEndPoint!).Port;
            for (int index = 0; index < ClientCount; index++)
            {
                int clientIndex = index;
                var proxy = new TrafficProxy(TrafficProxy.Transport.Tcp, port);
                proxies.Add(proxy);
                var client = new Client(Options(enabled));
                clients.Add(client);
                client.OnError += e => errors.Enqueue(e.Exception);
                client.RegisterCallback<Receipt>(e =>
                {
                    if (clientIndex != 0 || (uint)e.Object.Number >= Messages || !acknowledgements[e.Object.Number].TrySetResult())
                        errors.Enqueue(new InvalidDataException("An invalid, duplicate, or idle-client receipt was delivered."));
                });
                if (!await client.Connect("127.0.0.1", proxy.Port))
                    throw new IOException($"Client {index} did not connect through its counting proxy.");
                if (client.Connection.Compatibility?.BinaryCompression != enabled)
                    throw new InvalidDataException("Compression negotiation differs from the requested setting.");
            }
            setupWatch.Stop();
            await WaitUntil(() => server.Clients.Count == ClientCount, "Four clients did not finish handshaking.");
            await Settle(proxies, errors);
            var setup = proxies.Select(proxy => proxy.Snapshot()).ToArray();

            var elapsed = Stopwatch.StartNew();
            for (int number = 0; number < Messages; number++)
            {
                clients[0].Send(new Payload { Number = number, Data = expected });
                await acknowledgements[number].Task.WaitAsync(TimeSpan.FromSeconds(20));
                ThrowIfFailed(proxies, errors);
            }
            elapsed.Stop();
            double settleMs = await Settle(proxies, errors);
            var after = proxies.Select(proxy => proxy.Snapshot()).ToArray();
            if (seen.Any(value => value != 1))
                throw new InvalidDataException("Not every upload was received exactly once.");
            int[] idleCounts = clients.Skip(1).Select(client => ((BinarySerializer)client.Options.Serializer).CompressionDictionaryCount).ToArray();
            if (idleCounts.Any(count => enabled ? count <= 0 : count != 0))
                throw new InvalidDataException("Idle-client dictionary propagation does not match the enabled setting.");
            if (!clients.All(client => client.Connected) || server.Clients.Count != ClientCount)
                throw new IOException("A fanout client disconnected during byte measurement.");

            long totalUp = after.Sum(item => item.up), totalDown = after.Sum(item => item.down);
            long setupUp = setup.Sum(item => item.up), setupDown = setup.Sum(item => item.down);
            long idleTotal = after.Skip(1).Sum(item => item.up + item.down);
            long setupIdleTotal = setup.Skip(1).Sum(item => item.up + item.down);
            return new Measurement(round, enabled, ClientCount, 1, Messages, (long)Messages * expected.Length,
                elapsed.Elapsed.TotalMilliseconds, setupWatch.Elapsed.TotalMilliseconds, settleMs,
                totalUp, totalDown, totalUp + totalDown, after[0].up, after[0].down, idleTotal,
                setupUp, setupDown, setup[0].up, setup[0].down, setupIdleTotal,
                totalUp - setupUp, totalDown - setupDown, after[0].up - setup[0].up, after[0].down - setup[0].down, idleTotal - setupIdleTotal,
                ((BinarySerializer)server.Options.Serializer).CompressionDictionaryCount,
                ((BinarySerializer)clients[0].Options.Serializer).CompressionDictionaryCount,
                idleCounts,
                Enumerable.Range(1, ClientCount - 1).Select(i => after[i].up + after[i].down - setup[i].up - setup[i].down).ToArray());
        }
        finally
        {
            foreach (var client in clients) client.Dispose();
            server.Dispose();
            foreach (var proxy in proxies) await proxy.DisposeAsync();
        }
    }

    private static async Task<double> Settle(IReadOnlyList<TrafficProxy> proxies, ConcurrentQueue<Exception> errors)
    {
        var elapsed = Stopwatch.StartNew();
        var quiet = Stopwatch.StartNew();
        var previous = proxies.Select(proxy => proxy.Snapshot()).ToArray();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(10))
        {
            await Task.Delay(20);
            ThrowIfFailed(proxies, errors);
            var current = proxies.Select(proxy => proxy.Snapshot()).ToArray();
            if (current.Where((value, i) => value.up != previous[i].up || value.down != previous[i].down).Any())
                quiet.Restart();
            else if (quiet.ElapsedMilliseconds >= 160)
                return elapsed.Elapsed.TotalMilliseconds;
            previous = current;
        }
        throw new TimeoutException("Fanout dictionary/control traffic did not become quiet within ten seconds.");
    }

    private static async Task WaitUntil(Func<bool> condition, string error)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException(error);
            await Task.Delay(10);
        }
    }

    private static void ThrowIfFailed(IEnumerable<TrafficProxy> proxies, ConcurrentQueue<Exception> errors)
    {
        if (errors.TryPeek(out var error))
            throw new IOException("SocketJack reported a transport error.", error);
        foreach (var proxy in proxies)
            if (proxy.Failure is Exception failure)
                throw new IOException("A counting proxy failed.", failure);
    }
}
