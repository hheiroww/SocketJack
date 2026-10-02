using SocketJack.Net;
using SocketJack.Serialization;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;

namespace UdpReliable.Bench;

public sealed class Message {
    public int Kind { get; set; }
    public int Sequence { get; set; }
    public long Total { get; set; }
    public byte[] Data { get; set; }
    public double Milliseconds { get; set; }
    public double CpuMilliseconds { get; set; }
    public long Allocated { get; set; }
    public long Memory { get; set; }
    public string Hash { get; set; }
}
public sealed record Measurement(string Mode, int Run, long Bytes, double MiBPerSecond, double P50Milliseconds,
    double P95Milliseconds, double P99Milliseconds, double SenderCpuMilliseconds, double ReceiverCpuMilliseconds,
    long SenderAllocated, long ReceiverAllocated, long SenderPeakWorkingSet, long ReceiverPeakWorkingSet,
    long? Retransmissions, long? WireBytesSent, long? WireBytesReceived, string Sha256,
    long? BulkWireBytesSent, long? BulkWireBytesReceived);

internal static class Program {
        static readonly string[] Modes = (Environment.GetEnvironmentVariable("SJ_BENCH_MODES") ?? "tcp,raw-tcp,adaptive,fast-lan,fast-lan-32k").Split(',');
    static async Task<int> Main(string[] args) {
        using var diagnostics = Diagnostics.Start(args);
        try {
            switch (args[0]) {
                case "server": await Server(args[1], int.Parse(args[2])); break;
                case "client":
                    Console.WriteLine(JsonSerializer.Serialize(await Client(args[1], args[2], int.Parse(args[3]), int.Parse(args[4]), int.Parse(args[5])))); break;
                case "run": await Run(args[1], args.Length > 2 ? int.Parse(args[2]) : 10, args.Length > 3 ? int.Parse(args[3]) : 256); break;
                case "serializers": SerializerBench.Run(args[1]); break;
                case "codec-diagnostics": CodecDiagnostics.Run(args[1]); break;
                case "codec-focus": CodecFocusBench.Run(args[1], args.Length > 2 ? int.Parse(args[2]) : 10, args.Length > 3 ? int.Parse(args[3]) : 0); break;
                default: throw new ArgumentException("Use run <results.json> [runs=10] [MiB=256], server <mode> <port>, or client <mode> <host> <port> <run> <MiB>.");
            }
            return 0;
        } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    internal static NetworkOptions Options() {
        var options = new NetworkOptions { Serializer = Environment.GetEnvironmentVariable("SJ_SERIALIZER") == "json" ? new SocketJack.Serialization.Json.JsonSerializer() : new BinarySerializer(), UsePeerToPeer = false, UseCompression = false, EnablePatternCache = false,
        Chunking = false, Fps = 0, MaximumUploadMbps = 0, MaximumDownloadMbps = 0,
        ConnectionTimeout = TimeSpan.FromSeconds(8), UdpReliable = new() { DeliveryTimeout = TimeSpan.FromSeconds(30) } };
        var assembly = typeof(Message).Assembly;
        options.VerifiedAssemblies.Add(assembly);
        options.Authorization.AnonymousMessageTypes.Add(typeof(Message));
        return options;
    }
    static async Task Server(string mode, int port) {
        using var link = new Link(mode);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var watch = new Stopwatch(); using var process = Process.GetCurrentProcess();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long bytes = 0, allocated = 0, total = 0; double cpu = 0; int count = 0;
        var indexes = new HashSet<int>(); var pendingHash = new SortedDictionary<int, byte[]>(); int nextHash = 0; object gate = new();
        link.Received = message => {
            lock (gate) {
                if (message.Kind == 1) {
                    bytes = 0; count = 0; total = message.Total; indexes.Clear(); pendingHash.Clear(); nextHash = 0; hash.GetHashAndReset();
                    cpu = process.TotalProcessorTime.TotalMilliseconds; allocated = GC.GetTotalAllocatedBytes(true);
                    watch.Restart(); _ = link.Send(new Message { Kind = 1 });
                } else if (message.Kind == 2) {
                    if (message.Sequence < 0 || message.Sequence >= total / (256 * 1024) || message.Data.Length != 256 * 1024 || !indexes.Add(message.Sequence))
                        throw new InvalidDataException("Invalid or duplicated application delivery.");
                    pendingHash.Add(message.Sequence, message.Data);
                    while (pendingHash.Remove(nextHash, out var part)) { hash.AppendData(part); nextHash++; }
                    if (pendingHash.Count > 8) throw new InvalidDataException("Application window exceeded.");
                    bytes += message.Data.Length; count++;
                    if (bytes > total) throw new InvalidDataException("Too many bytes.");
                    if (bytes == total) {
                        string digest = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(); watch.Stop();
                        _ = link.Send(new Message { Kind = 3, Total = bytes, Milliseconds = watch.Elapsed.TotalMilliseconds,
                            CpuMilliseconds = process.TotalProcessorTime.TotalMilliseconds - cpu, Allocated = GC.GetTotalAllocatedBytes(true) - allocated,
                            Memory = process.PeakWorkingSet64, Hash = digest, Sequence = count });
                    } else if (count % 8 == 0) _ = link.Send(new Message { Kind = 4, Sequence = count });
                } else if (message.Kind == 5) _ = link.Send(message);
                else if (message.Kind == 9) done.TrySetResult();
            }
        };
        await link.Listen(port); Console.WriteLine("READY"); Console.Out.Flush();
        await done.Task.WaitAsync(TimeSpan.FromMinutes(20));
    }
    static async Task<Measurement> Client(string mode, string host, int port, int run, int mib) {
        using var link = new Link(mode); var messages = Channel.CreateUnbounded<Message>();
        link.Received = m => messages.Writer.TryWrite(m);
        await link.Connect(host, port);
        async Task<Message> Next(int kind) {
            var reply = await messages.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(60));
            if (reply.Kind != kind) throw new InvalidDataException($"Expected reply {kind}, got {reply.Kind}.");
            return reply;
        }
        static byte[] Payload(int sequence) { var result = new byte[256 * 1024]; new Random(912 + sequence).NextBytes(result); return result; }
        // Prepare distinct incompressible input before warm-up/timing. Data generation must not
        // become a common throughput ceiling that hides the difference between transports.
        var payloads = Enumerable.Range(0, Math.Max(8, mib) * 4).Select(Payload).ToArray();
        async Task<Message> Bulk(int megabytes) {
            await link.Send(new Message { Kind = 1, Total = megabytes * 1024L * 1024 }); await Next(1);
            int chunks = megabytes * 4;
            for (int i = 0; i < chunks; i += 8) {
                var pending = new List<Task>();
                for (int j = i; j < Math.Min(i + 8, chunks); j++) pending.Add(link.Send(new Message { Kind = 2, Sequence = j, Data = payloads[j] }));
                await Task.WhenAll(pending);
                var response = await Next(i + 8 >= chunks ? 3 : 4);
                if (response.Sequence != Math.Min(i + 8, chunks)) throw new InvalidDataException("Incomplete or duplicated bulk sequence.");
                if (response.Kind == 3) return response;
            }
            throw new InvalidOperationException();
        }
        await Bulk(8); // Untimed warm-up of serialization, socket paths, and JIT.
        byte[] small = new byte[256]; new Random(313).NextBytes(small);
        for (int i = 0; i < 100; i++) { await link.Send(new Message { Kind = 5, Sequence = i, Data = small }); await Next(5); }
        using var process = Process.GetCurrentProcess(); double cpu = process.TotalProcessorTime.TotalMilliseconds;
        long allocation = GC.GetTotalAllocatedBytes(true), retries = link.Stats?.Retransmissions ?? 0;
        long wireSent = link.Stats?.WireBytesSent ?? 0, wireReceived = link.Stats?.WireBytesReceived ?? 0;
        var bulk = await Bulk(mib);
        long? bulkWireSent = link.Stats == null ? null : link.Stats.WireBytesSent - wireSent;
        long? bulkWireReceived = link.Stats == null ? null : link.Stats.WireBytesReceived - wireReceived;
        using var expected = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (int i = 0; i < mib * 4; i++) expected.AppendData(payloads[i]);
        if (bulk.Hash != Convert.ToHexString(expected.GetHashAndReset()).ToLowerInvariant()) throw new InvalidDataException("Bulk SHA-256 mismatch.");
        var latency = new double[1000];
        for (int i = 0; i < latency.Length; i++) {
            var watch = Stopwatch.StartNew();
            var sending = link.Send(new Message { Kind = 5, Sequence = i, Data = small }); var reply = await Next(5); watch.Stop();
            await sending;
            if (reply.Sequence != i || !reply.Data.AsSpan().SequenceEqual(small)) throw new InvalidDataException("Latency echo mismatch.");
            latency[i] = watch.Elapsed.TotalMilliseconds;
        }
        Array.Sort(latency);
        var result = new Measurement(mode, run, bulk.Total, bulk.Total / 1048576.0 / (bulk.Milliseconds / 1000),
            latency[499], latency[949], latency[989], process.TotalProcessorTime.TotalMilliseconds - cpu, bulk.CpuMilliseconds,
            GC.GetTotalAllocatedBytes(true) - allocation, bulk.Allocated, process.PeakWorkingSet64, bulk.Memory,
            link.Stats == null ? null : link.Stats.Retransmissions - retries, link.Stats == null ? null : link.Stats.WireBytesSent - wireSent,
            link.Stats == null ? null : link.Stats.WireBytesReceived - wireReceived, bulk.Hash, bulkWireSent, bulkWireReceived);
        await link.Send(new Message { Kind = 9 }); return result;
    }
    static Process Child(params string[] arguments) {
        var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add(typeof(Program).Assembly.Location);
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        return Process.Start(info);
    }
    static async Task Run(string output, int runs, int mib) {
        var measurements = new List<Measurement>();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)));
        for (int run = 0; run < runs; run++) foreach (string mode in Modes.Skip(run % Modes.Length).Concat(Modes.Take(run % Modes.Length))) {
            int port;
            if (mode.Contains("tcp")) {
                using var allocation = new TcpListener(IPAddress.Loopback, 0); allocation.Start(); port = ((IPEndPoint)allocation.LocalEndpoint).Port;
            } else {
                using var allocation = new System.Net.Sockets.UdpClient(0); port = ((IPEndPoint)allocation.Client.LocalEndPoint).Port;
            }
            using var server = Child("server", mode, port.ToString()); var serverErrors = server.StandardError.ReadToEndAsync();
            try {
                if (await server.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)) != "READY") throw new IOException(await serverErrors);
                using var client = Child("client", mode, "127.0.0.1", port.ToString(), run.ToString(), mib.ToString());
                var errors = client.StandardError.ReadToEndAsync(); string json = await client.StandardOutput.ReadToEndAsync();
                await client.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(20));
                if (client.ExitCode != 0) throw new IOException(await errors);
                var result = JsonSerializer.Deserialize<Measurement>(json); measurements.Add(result);
                await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { DateUtc = DateTime.UtcNow, Environment.MachineName,
                    OS = Environment.OSVersion.ToString(), Runtime = Environment.Version.ToString(), Environment.ProcessorCount,
                    Serializer = Environment.GetEnvironmentVariable("SJ_SERIALIZER") == "json" ? "SocketJack System.Text.Json" : "SocketJack.Serialization.BinarySerializer v1", SafeMode = true, Ordering = false, TransportBufferBytes = 32768,
                    CoreSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(NetworkOptions).Assembly.Location))),
                    Payload = "Distinct seeded random 256 KiB objects prepared before warm-up/timing; 8 outstanding; receiver hashes in sequence with at most 8-object reorder storage; 256-byte latency echoes. Sender peak memory includes 256 MiB input fixtures.",
                    Network = "Two processes on IPv4 loopback; clean path; no shared-network impairment", Measurements = measurements }, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"run={run + 1}/{runs} {mode}: {result.MiBPerSecond:F2} MiB/s; p95={result.P95Milliseconds:F4} ms; retransmissions={result.Retransmissions}");
                await server.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                if (server.ExitCode != 0) throw new IOException(await serverErrors);
            } finally { if (!server.HasExited) server.Kill(true); }
        }
    }
    sealed class Link : IDisposable {
        readonly string mode; readonly NetworkOptions options = Options();
        SocketJack.Net.UdpClient udpClient; SocketJack.Net.UdpServer udpServer;
        SocketJack.Net.TcpClient tcpClient; SocketJack.Net.TcpServer tcpServer;
        NetworkConnection tcpPeer; UdpConnection udpPeer;
        System.Net.Sockets.TcpClient raw; TcpListener listener; NetworkStream stream;
        readonly SocketJack.Net.UdpClient schema;
        readonly SemaphoreSlim writes = new(1, 1);
        bool disposed;
        internal Action<Message> Received;
        internal UdpReliableStatistics Stats => udpClient?.ReliableStatistics;
        internal Link(string mode) {
            this.mode = mode;
            options.UdpReliable.Profile = mode.StartsWith("fast-lan") ? UdpReliableProfile.FastLan : UdpReliableProfile.Adaptive;
            if (mode == "fast-lan-32k") options.UdpReliable.DatagramSize = 32768;
            schema = new SocketJack.Net.UdpClient(Options()); schema.RegisterCallback<Message>(_ => { });
        }
        internal async Task Listen(int port) {
            if (mode == "raw-tcp") {
                listener = new TcpListener(IPAddress.Any, port); listener.Start(); _ = AcceptRaw();
            } else if (mode == "tcp") {
                tcpServer = new SocketJack.Net.TcpServer(options, port); tcpServer.OnError += Error;
                tcpServer.RegisterCallback<Message>(e => { tcpPeer = e.Connection; Received(e.Object); });
                if (!tcpServer.Listen()) throw new IOException("TCP listen failed.");
            } else {
                udpServer = UDP_Reliable.CreateServer(port, options); udpServer.OnError += Error;
                udpServer.RegisterCallback<Message>(e => { udpPeer = udpServer.Clients.Values.Single(c => c.ID == e.Connection.ID); Received(e.Object); });
                if (!udpServer.Listen()) throw new IOException("UDP listen failed.");
            }
            await Task.CompletedTask;
        }
        async Task AcceptRaw() { raw = await listener.AcceptTcpClientAsync(); raw.NoDelay = true; stream = raw.GetStream(); await ReadRaw(); }
        internal async Task Connect(string host, int port) {
            if (mode == "raw-tcp") { raw = new() { NoDelay = true }; await raw.ConnectAsync(host, port); stream = raw.GetStream(); _ = ReadRaw(); }
            else if (mode == "tcp") {
                tcpClient = new SocketJack.Net.TcpClient(options); tcpClient.OnError += Error; tcpClient.RegisterCallback<Message>(e => Received(e.Object));
                if (!await tcpClient.Connect(host, port)) throw new IOException("TCP connect failed.");
            } else {
                udpClient = UDP_Reliable.CreateClient(options); udpClient.OnError += Error; udpClient.RegisterCallback<Message>(e => Received(e.Object));
                if (!await udpClient.Connect(host, port)) throw new IOException("UDP connect failed.");
            }
        }
        internal Task Send(Message message) {
            if (mode == "raw-tcp") return SendRaw(message);
            if (udpClient != null) return udpClient.SendAsync(message);
            if (udpServer != null) return udpServer.SendToAsync(udpPeer, message);
            if (tcpClient != null) tcpClient.Send(message); else tcpPeer.Send(message);
            return Task.CompletedTask;
        }
        async Task SendRaw(Message message) {
            byte[] data = options.Serializer.Serialize(new Wrapper(message, schema));
            await writes.WaitAsync();
            try {
                // One scatter/gather send for the length prefix and payload; handle partial writes.
                var buffers = new List<ArraySegment<byte>> { new(BitConverter.GetBytes(data.Length)), new(data) };
                while (buffers.Count != 0) {
                    int sent = await raw.Client.SendAsync(buffers, SocketFlags.None);
                    if (sent == 0) throw new IOException("TCP send closed.");
                    while (sent != 0) {
                        var first = buffers[0];
                        if (sent >= first.Count) { sent -= first.Count; buffers.RemoveAt(0); }
                        else { buffers[0] = new ArraySegment<byte>(first.Array, first.Offset + sent, first.Count - sent); sent = 0; }
                    }
                }
            }
            finally { writes.Release(); }
        }
        async Task ReadRaw() {
            try {
                var header = new byte[4];
                while (!disposed) {
                    await stream.ReadExactlyAsync(header); int length = BitConverter.ToInt32(header);
                    if (length < 0 || length > options.MaximumBufferSize) throw new InvalidDataException();
                    var bytes = new byte[length]; await stream.ReadExactlyAsync(bytes);
                    Received((Message)options.Serializer.Deserialize(bytes).Unwrap(schema));
                }
            } catch (EndOfStreamException) { } catch (Exception ex) { if (!disposed) Console.Error.WriteLine(ex); }
        }
        void Error(SocketJack.Net.ErrorEventArgs e) { if (!disposed && e.Exception is not ObjectDisposedException) Console.Error.WriteLine(e.Exception); }
        public void Dispose() { disposed = true; udpClient?.Dispose(); udpServer?.Dispose(); tcpClient?.Dispose(); tcpServer?.Dispose(); raw?.Dispose(); listener?.Stop(); schema.Dispose(); }
    }
}
