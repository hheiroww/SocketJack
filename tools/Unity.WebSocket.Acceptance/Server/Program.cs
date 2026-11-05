using System.Net;
using System.Text.Json;
using System.Text;
using System.Security.Cryptography;
using SocketJack.Multiplayer;
using SocketJack.Multiplayer.Transport;
using SocketJack.Multiplayer.Serialization;

if (args.Length < 5 || args.Length > 6) throw new ArgumentException("Supply gamePort, allowedOrigin, readyFile, stopFile, receiptFile and optional relay mode.");
int port = int.Parse(args[0]); string origin = args[1], readyFile = args[2], stopFile = args[3], receiptFile = args[4];
bool useRelay = args.Length == 6 && args[5] == "relay";
using var transport = new WebSocketGameTransport(listenerOptions: new WebSocketListenerOptions { AllowedOrigins = new[] { origin } });
const string memberKey = ":SocketJack.Tests.Browser.BrowserProbeValue:0:SocketJack.Tests.Browser.BrowserProbeValue|F|Blob";
var formatter = new SocketJackBinaryFormatter();
NetworkMemberSchema Schema(Func<object> get, Action<object> set) => new NetworkMemberSchema(formatter, new[] {
    new NetworkMemberBinding(1, memberKey, typeof(byte[]), get, set, NetworkMemberAuthority.Server, maxBytes:8192) });
// Match NetworkManager's registered prefab/serializer compatibility declaration. The probe has
// one named prefab, one byte[] member, no state controller, scenes, or network event components.
ulong fingerprint = Schema(() => Array.Empty<byte>(), _ => { }).Fingerprint;
string declaration = "SocketJack.Unity/second-release/session3\n\n" + formatter.ProtocolId + "\n1:Browser replicated object:::" + fingerprint.ToString("x16") + "\n";
string compatibility = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(declaration))).ToLowerInvariant();
using var server = new NetworkServer(transport, new NetworkSessionOptions { MaxMessageBytes = 9000, SnapshotRate = 5, CompatibilityId = compatibility });
using var members = new NetworkMemberServer(server);
var values = new Dictionary<ulong, byte[]>();
int connected = 0, inputs = 0;
server.PeerConnected += peer =>
{
    connected++; var obj = server.Spawn(1, peer); values[obj.Id] = Enumerable.Range(0, 6000).Select(i => (byte)(i * 13)).ToArray();
    members.RegisterObject(obj.Id, Schema(() => values[obj.Id], value => values[obj.Id] = (byte[])value));
};
server.InputReceived += input => { if (!input.Payload.SequenceEqual(new byte[] { 42 })) throw new InvalidDataException("Browser probe input differs."); inputs++; values[input.ObjectId] = Enumerable.Range(0, 6100).Select(i => (byte)(i * 17)).ToArray(); };
server.Start(new IPEndPoint(IPAddress.Loopback, useRelay ? 0 : port));
await using var relay = useRelay ? new WebSocketRelayService() : null;
await using var relayListener = useRelay ? new WebSocketListener(new WebSocketListenerOptions { AllowedOrigins = new[] { origin } }) : null;
WebSocketRelayHost host = null;
if (useRelay)
{
    // Fixed test-only opaque tickets on a loopback listener; this is not a production account service.
    relay!.AuthorizeTicket = ticket => ticket == "loopback-probe-host" ? new RelayAccess("browser-probe", WebSocketRelayRole.Host, DateTimeOffset.UtcNow.AddMinutes(5)) :
        ticket == "loopback-probe-client" ? new RelayAccess("browser-probe", WebSocketRelayRole.Client, DateTimeOffset.UtcNow.AddMinutes(5)) : null;
    relayListener!.UpgradeAuthorizer = (request, token) => Task.FromResult(request.Path == "/ws/relay");
    relayListener.WebSocketAccepted = relay.HandleWebSocketAsync; relayListener.Start(new IPEndPoint(IPAddress.Loopback, port));
    host = await WebSocketRelayHost.ConnectAsync(transport, new Uri("ws://127.0.0.1:" + port + "/ws/relay"), "loopback-probe-host");
}
File.WriteAllText(readyFile, "ready");
var deadline = DateTime.UtcNow.AddMinutes(5);
while (!File.Exists(stopFile) && DateTime.UtcNow < deadline) { server.Update(.005); members.Update(.005); Thread.Sleep(1); }
server.Stop(); if (host != null) await host.DisposeAsync();
File.WriteAllText(receiptFile, JsonSerializer.Serialize(new { result = connected > 0 && inputs > 0 ? "PASS" : "FAIL", connected, inputs, bytesSent = transport.BytesSent, bytesReceived = transport.BytesReceived, invalidPackets = transport.InvalidPackets, relay = useRelay, scope = "Actual Unity WebGL native browser WebSocket to loopback .NET server" }, new JsonSerializerOptions { WriteIndented = true }));
return connected > 0 && inputs > 0 ? 0 : 1;
