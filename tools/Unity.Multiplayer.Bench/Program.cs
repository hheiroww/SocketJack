using System.Diagnostics;
using System.Net;
using System.Text.Json;
using SocketJack.Multiplayer.Transport;

int count = args.Length > 0 ? int.Parse(args[0]) : 2000;
int rounds = args.Length > 1 ? int.Parse(args[1]) : 20;
TransportType type = args.Length > 2 ? Enum.Parse<TransportType>(args[2], true) : TransportType.Udp;
DeliveryMode delivery = args.Length > 3 ? Enum.Parse<DeliveryMode>(args[3], true) : DeliveryMode.ReliableUnordered;
if (count < 1 || count > 10000 || rounds < 1 || rounds > 1000) throw new ArgumentOutOfRangeException("Use 1..10000 peers and 1..1000 rounds.");
using var server = GameTransport.Create(type, new TransportOptions { MaxPeers = Math.Max(count, 4096) });
var clients = new List<IGameTransport>();
var peerIds = new HashSet<ulong>();
int connected = 0, serverReceived = 0, echoed = 0, rejected = 0, disconnects = 0;
var latencies = new List<double>(count * rounds);
var updates = new List<double>();
server.EventReceived += e =>
{
    if (e.Type == TransportEventType.Connected) { connected++; peerIds.Add(e.PeerId); }
    else if (e.Type == TransportEventType.Disconnected) disconnects++;
    else if (e.Type == TransportEventType.Data)
    {
        serverReceived++;
        if (!server.Send(e.PeerId, new ArraySegment<byte>(e.Payload), e.Delivery, e.Channel)) rejected++;
    }
};
server.StartServer(new IPEndPoint(IPAddress.Loopback, 0));
var elapsed = Stopwatch.StartNew();
var process = Process.GetCurrentProcess();
TimeSpan cpuBefore = process.TotalProcessorTime;
try
{
    for (int i = 0; i < count; i++)
    {
        var client = GameTransport.Create(type);
        clients.Add(client);
        client.EventReceived += e =>
        {
            if (e.Type == TransportEventType.Disconnected) disconnects++;
            if (e.Type != TransportEventType.Data) return;
            echoed++;
            latencies.Add((Stopwatch.GetTimestamp() - BitConverter.ToInt64(e.Payload, 0)) * 1000.0 / Stopwatch.Frequency);
        };
        client.StartClient(server.LocalEndPoint);
        // Keep handshake bursts bounded rather than measuring the OS UDP overflow limit.
        if ((i + 1) % 50 == 0) { for (int p = 0; p < 5; p++) Pump(); }
    }
    WaitUntil(() => connected == count && clients.All(c => c.LocalPeerId != 0), TimeSpan.FromSeconds(45));
    double connectSeconds = elapsed.Elapsed.TotalSeconds;
    var traffic = Stopwatch.StartNew();
    for (int round = 0; round < rounds; round++)
    {
        byte[] payload = new byte[64]; BitConverter.GetBytes(Stopwatch.GetTimestamp()).CopyTo(payload, 0);
        foreach (var client in clients) if (!client.Send(0, new ArraySegment<byte>(payload), delivery, 1)) rejected++;
        int target = (round + 1) * count;
        WaitUntil(() => echoed >= target, TimeSpan.FromSeconds(15));
    }
    traffic.Stop();
    process.Refresh(); latencies.Sort(); updates.Sort();
    bool passed = connected == count && peerIds.Count == count && serverReceived == count * rounds && echoed == count * rounds && rejected == 0 && disconnects == 0;
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        passed, transport = type.ToString(), delivery = delivery.ToString(), peers = count, rounds, payloadBytes = 64,
        connected, uniquePeers = peerIds.Count, serverReceived, echoed, rejected, disconnects,
        connectSeconds, trafficSeconds = traffic.Elapsed.TotalSeconds,
        roundTripsPerSecond = echoed / traffic.Elapsed.TotalSeconds,
        latencyMs = new { p50 = Percentile(latencies, .5), p95 = Percentile(latencies, .95), max = latencies.LastOrDefault() },
        pumpAllPeersMs = new { p50 = Percentile(updates, .5), p95 = Percentile(updates, .95), max = updates.LastOrDefault() },
        cpuSeconds = (process.TotalProcessorTime - cpuBefore).TotalSeconds,
        peakWorkingSetBytes = process.PeakWorkingSet64,
        runtime = Environment.Version.ToString(), os = Environment.OSVersion.Platform.ToString(),
        scope = "Single-process loopback transport echo; excludes game simulation, Unity rendering, WAN, encryption and cross-host routing."
    }, new JsonSerializerOptions { WriteIndented = true }));
    return passed ? 0 : 1;
}
finally { foreach (var client in clients) client.Dispose(); }

void Pump()
{
    long start = Stopwatch.GetTimestamp();
    server.Poll(Math.Max(4096, count * 4));
    foreach (var client in clients) client.Poll(32);
    updates.Add((Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency);
}
void WaitUntil(Func<bool> done, TimeSpan timeout)
{
    var wait = Stopwatch.StartNew();
    while (!done() && wait.Elapsed < timeout) { Pump(); Thread.Sleep(1); }
    if (!done()) throw new TimeoutException($"Benchmark timed out: {connected}/{count} connected, {echoed} echoed, {disconnects} disconnected.");
}
static double Percentile(List<double> values, double percentile) => values.Count == 0 ? 0 : values[(int)Math.Min(values.Count - 1, Math.Ceiling(values.Count * percentile) - 1)];
