using System.Net;
using System.Net.Sockets;

namespace SocketJack.UdpReliable.Tests;

// Network-only impairment: the transport under test still uses real sockets.
internal sealed class ImpairmentProxy : IDisposable {
    readonly System.Net.Sockets.UdpClient downstream = new(new IPEndPoint(IPAddress.Loopback, 0));
    readonly System.Net.Sockets.UdpClient upstream = new(new IPEndPoint(IPAddress.Loopback, 0));
    readonly CancellationTokenSource stop = new();
    readonly Random random = new(1701);
    readonly object gate = new();
    readonly IPEndPoint server;
    IPEndPoint client;
    int packets;
    public double Loss { get; set; }
    public bool Disturb { get; set; }
    public bool Blackhole { get; set; }
    public bool HoldFragmented { get; set; }
    public bool DropCompletions { get; set; }
    long heldMessage = -1;
    public int Held { get; private set; }
    public int Dropped { get; private set; }
    public int Duplicated { get; private set; }
    public byte[] LastData { get; private set; }
    public int Port => ((IPEndPoint)downstream.Client.LocalEndPoint).Port;
    public ImpairmentProxy(int port, double loss, bool disturb = true) {
        server = new(IPAddress.Loopback, port); Loss = loss; Disturb = disturb;
        _ = Loop(downstream, upstream, true); _ = Loop(upstream, downstream, false);
    }
    async Task Loop(System.Net.Sockets.UdpClient input, System.Net.Sockets.UdpClient output, bool toServer) {
        try {
            while (!stop.IsCancellationRequested) {
                var packet = await input.ReceiveAsync(stop.Token);
                if (toServer) client = packet.RemoteEndPoint;
                var target = toServer ? server : client;
                if (target == null) continue;
                bool drop, duplicate; int delay;
                lock (gate) {
                    packets++;
                    if (toServer && packet.Buffer.Length > 14 && ((packet.Buffer[3] & 127) == 7 || (packet.Buffer[3] & 127) == 13)) LastData = packet.Buffer;
                    drop = Blackhole || random.NextDouble() < Loss || (Disturb && packets % 211 < 4);
                    if (DropCompletions && packet.Buffer.Length >= 14 && (packet.Buffer[3] & 127) == 9) drop = true;
                    if (HoldFragmented && toServer && packet.Buffer.Length >= 14 && (packet.Buffer[3] & 127) == 7) {
                        long message = ReadId(packet.Buffer);
                        if (heldMessage == -1) heldMessage = message;
                        if (heldMessage == message) { drop = true; Held++; }
                    }
                    duplicate = Disturb && packets % 37 == 0;
                    delay = Disturb && packets % 11 == 0 ? 12 : 0;
                    if (drop) Dropped++;
                    if (duplicate) Duplicated++;
                }
                if (drop) continue;
                _ = Forward(output, target, packet.Buffer, delay, duplicate);
            }
        } catch (OperationCanceledException) { } catch (ObjectDisposedException) { } catch (SocketException) when (stop.IsCancellationRequested) { }
    }
    async Task Forward(System.Net.Sockets.UdpClient socket, IPEndPoint target, byte[] data, int delay, bool duplicate) {
        try {
            if (delay != 0) await Task.Delay(delay, stop.Token);
            await socket.SendAsync(data, target, stop.Token);
            if (duplicate) await socket.SendAsync(data, target, stop.Token);
        } catch (OperationCanceledException) { } catch (ObjectDisposedException) { } catch (SocketException) when (stop.IsCancellationRequested) { }
    }
    static long ReadId(byte[] bytes) {
        long id = 0; int shift = 0, index = 12;
        while (index < bytes.Length) { byte part = bytes[index++]; id |= (long)(part & 127) << shift; if (part < 128) return id; shift += 7; }
        return -1;
    }
    public async Task InjectServer(byte[] data) => await upstream.SendAsync(data, server);
    public void Dispose() { stop.Cancel(); downstream.Dispose(); upstream.Dispose(); }
}
