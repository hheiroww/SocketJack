# SocketJack Examples

These examples are kept out of the main README so the overview stays quick to scan. The snippets cover the core transport APIs, HTTP hosting, WebSockets, mutable protocol routing, WPF sharing, file transfer, embedded data..

## Install

```powershell
Install-Package SocketJack
Install-Package SocketJack.WPF
```

```powershell
dotnet add package SocketJack
dotnet add package SocketJack.WPF
```

## TCP Server and Client

```cs
using SocketJack.Net;

public sealed record CustomMessage(string Message);

var server = new TcpServer(port: 12345);
server.Listen();

var client = new TcpClient();
await client.Connect("127.0.0.1", 12345);

client.Send(new CustomMessage("Hello!"));

server.RegisterCallback<CustomMessage>((args) =>
{
    Console.WriteLine($"Received: {args.Object.Message}");
    args.Connection.Send(new CustomMessage("10-4"));
});
```

## TCP Default Options

Set global defaults before creating any client or server instance.

```cs
using SocketJack.Net;
using SocketJack.Compression;

NetworkOptions.DefaultOptions.UsePeerToPeer = true;
NetworkOptions.DefaultOptions.UseCompression = true;
NetworkOptions.DefaultOptions.CompressionAlgorithm = new GZip2Compression();
```

## TCP Metadata

```cs
server.ClientConnected += e =>
{
    e.Connection.SetMetaData("room", "Lobby1");
    e.Connection.SetMetaData("role", "host");
};
```

## UDP Server and Client

```cs
using SocketJack.Net;

public sealed record CustomMessage(string Message);

var server = new UdpServer(port: 12345);
server.Listen();

var client = new UdpClient();
await client.Connect("127.0.0.1", 12345);

client.Send(new CustomMessage("Hello via UDP!"));

server.RegisterCallback<CustomMessage>((args) =>
{
    Console.WriteLine($"Received: {args.Object.Message}");
});
```

## UDP Peer-to-Peer and Broadcast

```cs
client.Send(remotePeer, new CustomMessage("P2P over UDP"));
client.SendPeerBroadcast(new CustomMessage("Hello everyone!"));

server.SendBroadcast(new CustomMessage("Server announcement"));
server.Send(clientIdentifier, new CustomMessage("Direct message"));
```

## UDP Options

```cs
using SocketJack.Net;

var options = new NetworkOptions
{
    MaxDatagramSize = 1400,
    ClientTimeoutSeconds = 60,
    UdpReceiveBufferSize = 131072,
    EnableBroadcast = true,
    ClientTimeoutCheckIntervalMs = 10000
};

var server = new UdpServer(options, port: 12345);
var client = new UdpClient(options);
```

## HTTP Server

```cs
using SocketJack.Net;

var server = new HttpServer(port: 8080);
server.Listen();

server.IndexPageHtml = "<html><body><h1>Welcome!</h1></body></html>";

server.Map("GET", "/hello", (connection, request, ct) =>
{
    return "Hello, World!";
});

server.Map("POST", "/echo", (connection, request, ct) =>
{
    return new EchoResponse(request.Body);
});

server.RemoveRoute("GET", "/hello");

server.OnHttpRequest += (connection, ref HttpContext context, CancellationToken ct) =>
{
    context.Response.Body = "Custom response";
    context.Response.ContentType = "text/plain";
    context.StatusCode = "200 OK";
};
```

## HTTP Typed Routes

```cs
server.Map<LoginRequest>("POST", "/login", (connection, body, request, ct) =>
{
    return new LoginResponse(body.Username);
});
```

## HTTP Static File Serving

```cs
server.MapDirectory("/static", @"C:\wwwroot");
server.AllowDirectoryListing = true;
server.RemoveDirectoryMapping("/static");
```

## HTTP Map One File

```cs
server.MapFile("/js/app.js", @"C:\Pages\app.js");
```

## HTTP .htaccess Security

```cs
server.MapDirectory("/secure", @"C:\data", htaccess =>
{
    htaccess
        .DenyDirectoryListing()
        .AllowFrom("192.168.1.0/24")
        .DenyFiles("*.log", "*.bak")
        .RequireBasicAuth("Admin Area", "admin:secret")
        .AddHeader("X-Frame-Options", "DENY");
});
```

## HTTP Streaming Routes

```cs
server.MapStream("GET", "/events", async (connection, request, chunkedStream, ct) =>
{
    for (int i = 0; i < 10; i++)
    {
        chunkedStream.WriteLine("event: " + i);
        await Task.Delay(1000, ct);
    }
});

server.MapUploadStream("POST", "/upload", (connection, request, uploadStream, ct) =>
{
    byte[] chunk;

    while ((chunk = uploadStream.ReadAsync(ct).GetAwaiter().GetResult()) != null)
    {
        Console.WriteLine("Received " + chunk.Length + " bytes.");
    }
});
```

## HTTP RTMP Ingest

```cs
server.MapRtmpPublish("live", async (connection, app, streamKey, uploadStream, ct) =>
{
    byte[] chunk;

    while ((chunk = await uploadStream.ReadAsync(ct)) != null)
    {
        Console.WriteLine("Received RTMP chunk " + chunk.Length + " bytes.");
    }
});
```

## HTTP Client

```cs
using SocketJack.Net;
using System.Text;

using var client = new HttpClient();

HttpResponse response = await client.GetAsync("http://localhost:8080/hello");
Console.WriteLine(response.Body);

byte[] body = Encoding.UTF8.GetBytes("{\"message\":\"hi\"}");
HttpResponse postResp = await client.PostAsync(
    "http://localhost:8080/echo",
    "application/json",
    body);

HttpResponse resp = await client.SendAsync(
    "PUT",
    "https://example.com/api/resource",
    new Dictionary<string, string> { ["Authorization"] = "Bearer token" },
    body);

using var fileStream = File.Create("download.bin");
await client.GetAsync("http://example.com/largefile", responseStream: fileStream);
```

## HTTP Client Options

```cs
var client = new HttpClient();
client.Timeout = TimeSpan.FromSeconds(60);
client.MaxRedirects = 10;
client.DefaultHeaders["Accept"] = "application/json";
```

## BroadcastServer Live Streaming

`BroadcastServer` turns any `HttpServer` into a live video relay. Point OBS or another RTMP encoder at the server and viewers can watch in a browser or VLC.

![OBS streaming to SocketJack HttpServer via BroadcastServer](https://raw.githubusercontent.com/JackOfFates/SocketJack/master/SocketJack/httpStream.PNG)

```cs
using SocketJack.Net;

var server = new HttpServer(port: 8080);

var broadcast = new BroadcastServer(server);
broadcast.Register();

server.Listen();

Console.WriteLine("Stream Key: " + broadcast.StreamKey);
Console.WriteLine("OBS Server: rtmp://localhost:8080/live");
Console.WriteLine("Browser:    http://localhost:8080/stream");
Console.WriteLine("VLC:        http://localhost:8080/stream/data");

while (true)
{
    var stats = broadcast.UpdateStats();

    if (stats.Active)
    {
        Console.WriteLine(
            stats.BitrateKbps.ToString("N0") + " kbps | " +
            stats.VideoFrames + " video | " +
            stats.AudioFrames + " audio | " +
            BroadcastServer.FormatBytes(stats.TotalBytes));
    }

    Thread.Sleep(1000);
}
```

## WebSocket Server and Client

```cs
using SocketJack.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;

var server = new WebSocketServer(port: 9000);
server.Listen();

server.SslCertificate = new X509Certificate2("cert.pfx", "password");
server.Options.UseSsl = true;
server.Listen();

var client = new WebSocketClient();
await client.Connect("127.0.0.1", 9000);

await client.ConnectAsync(new Uri("ws://127.0.0.1:9000/path"));
```

## WebSocket Send, Receive, and Broadcast

```cs
client.Send(new CustomMessage("Hello via WebSocket!"));

server.RegisterCallback<CustomMessage>((args) =>
{
    Console.WriteLine($"Received: {args.Object.Message}");
});

server.Send(clientConnection, new CustomMessage("Reply"));
server.SendBroadcast(new CustomMessage("Announcement"));
```

## WebSocket Peer-to-Peer

```cs
var options = new NetworkOptions
{
    UsePeerToPeer = true
};

var client = new WebSocketClient(options);
await client.Connect("127.0.0.1", 9000);

client.Send(remotePeer, new CustomMessage("P2P over WebSocket"));
client.SendBroadcast(new CustomMessage("Hello everyone!"));
```

## WebSocket Events

```cs
server.ClientConnected += e => Console.WriteLine($"Client connected: {e.Connection.Identity.ID}");
server.ClientDisconnected += e => Console.WriteLine($"Client disconnected: {e.Connection.Identity.ID}");
server.OnReceive += (ref e) => Console.WriteLine($"Received: {e.Obj}");

client.OnConnected += e => Console.WriteLine("Connected!");
client.OnDisconnected += e => Console.WriteLine("Disconnected.");
client.PeerConnected += (sender, peer) => Console.WriteLine($"Peer joined: {peer.ID}");
client.PeerDisconnected += (sender, peer) => Console.WriteLine($"Peer left: {peer.ID}");
```

## MutableTcpServer Multi-Protocol Hosting

`MutableTcpServer` extends `HttpServer` and auto-detects the protocol for each incoming connection. HTTP, SocketJack, WebSocket, RTMP, TDS/SQL, and custom protocols can share one listening port.

```cs
using SocketJack.Net;
using SocketJack.Net.WebSockets;

var server = new MutableTcpServer(port: 9000);

server.Http.Map("GET", "/api/status", (connection, request, ct) =>
{
    return "{ \"status\": \"ok\" }";
});

server.Http.MapDirectory("/www", @"C:\wwwroot");
server.Http.MapFile("/js/app.js", @"C:\Pages\app.js");

server.SocketJackClientConnected += connection =>
{
    Console.WriteLine($"SocketJack client connected: {connection.ID}");
};

server.WebSocketClientConnected += connection =>
{
    Console.WriteLine($"WebSocket client connected: {connection.ID}");
};

server.RegisterCallback<CustomMessage>((args) =>
{
    Console.WriteLine($"Received: {args.Object.Message}");
});

server.Listen();

var tcpClient = new TcpClient();
await tcpClient.Connect("127.0.0.1", 9000);
tcpClient.Send(new CustomMessage("Hello!"));

var wsClient = new WebSocketClient();
await wsClient.ConnectAsync(new Uri("ws://127.0.0.1:9000"));
wsClient.Send(new CustomMessage("Hello from browser!"));
```

## MutableTcpServer Custom Protocol Handler

```cs
using SocketJack.Net;

public sealed class MyProtocolHandler : IProtocolHandler
{
    public string Name => "MyProtocol";

    public bool CanHandle(byte[] data)
    {
        return data.Length >= 4 && data[0] == 0xAB;
    }

    public void ProcessReceive(
        MutableTcpServer server,
        NetworkConnection connection,
        ref IReceivedEventArgs e)
    {
        Console.WriteLine("Custom protocol data received.");
    }

    public void OnDisconnected(MutableTcpServer server, NetworkConnection connection)
    {
        Console.WriteLine("Custom protocol connection closed.");
    }
}

server.RegisterProtocol(new MyProtocolHandler());
```

## Reverse TCP Forwarding

```cs
using SocketJack.Net;

var relay = new ReverseTcpRelayServer(
    id: "model-relay",
    name: "Remote model relay",
    publicPort: 11434,
    agentPort: 11437);

relay.Log += (sender, e) => Console.WriteLine(e.Message);
relay.Start();

var agent = new ReverseTcpRelayClient(
    instanceId: "workstation-1",
    name: "LM Studio workstation",
    masterHost: "relay.example.com",
    agentPort: 11437,
    localHost: "127.0.0.1",
    localPort: 1234,
    connectionPoolSize: 4);

agent.Start();
```

## Directory Hash Manifest

```cs
using SocketJack.Net;

DirectoryHashManifest manifest = DirectoryHashMetadata.BuildManifest(@"C:\data");

foreach (DirectoryHashFile file in manifest.Files)
{
    Console.WriteLine($"{file.Path}: {file.Hash}");
}
```

## FTP Server

```cs
using SocketJack.Net;

var users = new FtpUserStore();
users.Add(new FtpUser
{
    UserName = "demo",
    Password = "secret",
    RootPath = @"C:\ftp-root",
    CanRead = true,
    CanWrite = true
});

var options = new FtpServerOptions
{
    Authenticator = users,
    AllowAnonymous = false
};

var server = new FtpServer(2121, options);
server.Listen();
```

## FTP Client

```cs
using SocketJack.Net;

using var client = new FtpClient();
await client.ConnectAsync("localhost", 2121);
await client.LoginAsync("demo", "secret");
await client.UploadFileAsync(@"C:\local\report.txt", "/report.txt");
IReadOnlyList<FtpListItem> files = await client.ListDirectoryAsync("/");
```

## SFTP Client

```cs
using SocketJack.Net;

var options = new SftpClientOptions
{
    Host = "example.com",
    Port = 22,
    UserName = "demo",
    Password = "secret"
};

using var client = new SftpClient();
await client.ConnectAsync(options);
await client.UploadFileAsync(@"C:\local\report.txt", "/remote/report.txt");
```

## Data Server

```cs
using SocketJack.Net.Database;

var database = new Database("AppData");
var table = new Table("Messages");
table.Columns.Add(new Column("Id", typeof(string)));
table.Columns.Add(new Column("Text", typeof(string)));
table.Rows.Add(new object[] { Guid.NewGuid().ToString("N"), "Hello database" });
database.Tables.TryAdd("Messages", table);

var server = new DataServer(port: 8494);
server.Databases.TryAdd("AppData", database);
server.Listen();
```

## SQL Admin Panel

```cs
using SocketJack.Net.Database;

var http = new MutableTcpServer(port: 8494);
var tds = new TdsProtocolHandler();
var dataServer = tds.Server;
dataServer.Databases.TryAdd("AppData", database);
dataServer.Username = "sa";
dataServer.Password = "YourStrong!Passw0rd";

http.RegisterProtocol(tds);
http.Http.Map("GET", "/", (connection, request, ct) => "<h1>SocketJack data server</h1>");
http.Listen();
```

## WPF Share a Control

These examples require the `SocketJack.WPF` package.

```cs
using SocketJack.Net;
using SocketJack.Net.P2P;
using SocketJack.WPF;

IDisposable shareHandle = myCanvas.Share(client, peer, fps: 10);

shareHandle.Dispose();
```

## WPF View a Shared Control

```cs
using System.Windows.Controls;
using SocketJack.Net;
using SocketJack.WPF;

var viewer = client.ViewShare(sharedImage, sharerPeer);

viewer.Dispose();
```

## WPF Full Sharing Flow

```xml
<Image x:Name="SharedImage" Stretch="Uniform" />
```

```cs
Identifier remotePeer = client.Peers.FirstNotMe();
IDisposable shareHandle = GameCanvas.Share(client, remotePeer, fps: 10);
```

```cs
Identifier remotePeer = client.Peers.FirstNotMe();
var viewer = client.ViewShare(SharedImage, remotePeer);
```
