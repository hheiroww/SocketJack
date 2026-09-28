# SocketJack

![SocketJack Icon](https://raw.githubusercontent.com/JackOfFates/SocketJack/master/SocketJack/SocketJackIcon.png)

[![NuGet](https://img.shields.io/nuget/v/SocketJack.svg)](https://www.nuget.org/packages/SocketJack)
[![NuGet Downloads](https://img.shields.io/nuget/dt/SocketJack.svg)](https://www.nuget.org/packages/SocketJack)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](https://github.com/JackOfFates/SocketJack/blob/master/LICENSE)

SocketJack is a batteries-included .NET networking stack for typed object transport, HTTP/WebSocket apps, mutable protocol hosting, streaming, peer routing, and WPF control sharing.

It handles framing, segmentation, serialization, compression, routing, protocol detection, TLS, static file hosting, stream routes, and callback dispatch so application code can focus on intent instead of wire plumbing.

## Packages

| Package | Version | Targets | Role |
|---|---:|---|---|
| [`SocketJack`](https://www.nuget.org/packages/SocketJack) | `2026.11` | `.NET Standard 2.1` | Core TCP, UDP, HTTP, WebSocket, protocol multiplexing, data, file transfer, and streaming utilities. |
| [`SocketJack.WPF`](https://www.nuget.org/packages/SocketJack.WPF) | `2026.7` | `net8.0-windows7.0`, `net10.0-windows7.0` | WPF live capture, remote input, and GUI remoting on top of SocketJack. |

## Feature Map

| Category | Highlights |
|---|---|
| Transport core | Unified `TcpClient`, `TcpServer`, `UdpClient`, `UdpServer`, HTTP, and WebSocket APIs with typed callbacks and consistent lifecycle events. |
| Protocol multiplexing | `MutableTcpServer` auto-detects HTTP, SocketJack, WebSocket, RTMP, TDS/SQL, FTP configuration, and custom protocols on one port. |
| Object messaging | Send serializable objects directly, register typed callbacks, broadcast to peers, and route messages by `Identifier` metadata. |
| HTTP app hosting | Route mapping, typed request bodies, static files, directory hosting, upload streams, chunked stream routes, redirects, caching headers, and `.htaccess` controls. |
| Browser WebSockets | RFC 6455 WebSocket client/server support that shares SocketJack serialization, compression, callbacks, and peer flows. |
| Peer-to-peer | Peer discovery, relay-assisted routing, metadata, host/client roles, broadcasts, and direct peer sends. |
| Compression and throughput | GZip/Deflate compression, configurable buffers, async I/O, automatic segmentation, outbound chunking, and bandwidth throttling. |
| Security | TLS through `SslStream`, X.509 auth, HTTP Basic auth, bearer tokens, host-local checks, and allow/deny rules. |
| Embedded data | JSON-backed data server, SQL admin panel, TDS protocol handler, cache optimization, snapshots, and generated context helpers. |
| File transfer | FTP/SFTP helpers, uploads/downloads, directory hashing, remote clone tracking, and filesystem allowlists. |
| Streaming media | `BroadcastServer` accepts RTMP or HTTP ingest and relays FLV data to browser or VLC viewers. |
| WPF remoting | Share any WPF `FrameworkElement` as a live JPEG stream and route remote mouse, wheel, text, and keyboard input back to WPF. |

## Quick Start

Install the core networking package:

```powershell
dotnet add package SocketJack
```

Or with Package Manager:

```powershell
Install-Package SocketJack
```

Install WPF remoting when you need live WPF capture/input:

```powershell
dotnet add package SocketJack.WPF
```

## TCP

```csharp
using SocketJack.Net;

var server = new TcpServer(port: 12345);
server.Listen();

server.RegisterCallback<ChatMessage>(args =>
{
    Console.WriteLine(args.Object.Text);
    args.Connection.Send(new ChatMessage("received"));
});

var client = new TcpClient();
await client.Connect("127.0.0.1", 12345);
client.Send(new ChatMessage("hello"));

public sealed record ChatMessage(string Text);
```

## HTTP

```csharp
using SocketJack.Net;

var server = new HttpServer(8080);

server.Map("GET", "/health", (connection, request, ct) =>
{
    return new { status = "ok" };
});

server.MapDirectory("/static", @"C:\wwwroot");
server.Listen();
```

## WebSocket

```csharp
using SocketJack.Net;

var server = new WebSocketServer(port: 9000);
server.Listen();

server.RegisterCallback<ChatMessage>(args =>
{
    server.SendBroadcast(new ChatMessage(args.Object.Text));
});

var client = new WebSocketClient();
await client.Connect("127.0.0.1", 9000);
client.Send(new ChatMessage("hello from websocket"));
```

## TypeScript clients

SocketJack can generate a dependency-free TypeScript client from the routes currently mapped on an `HttpServer`. Typed request bodies become TypeScript types, route parameters become required method inputs, and resolvable WebSocket message types from the server whitelist become exported interfaces.

```csharp
public sealed class CreateMessage
{
    public string Text { get; set; }
}

var server = new HttpServer(port: 8080);
server.Map<CreateMessage>("POST", "/rooms/{roomId}/messages",
    (connection, body, request, ct) => new { saved = true, body.Text });

string source = server.GenerateTypeScriptClient(new TypeScriptClientOptions
{
    ClassName = "ChatApi",
    BaseUrl = "https://chat.example.com"
});
File.WriteAllText("socketjack-client.ts", source);
```

The generated class uses the standard `fetch` and `WebSocket` APIs and includes query, header, cancellation, JSON, text, binary, and HTTP-error handling.

## MutableTcpServer

`MutableTcpServer` lets HTTP, SocketJack, WebSocket, RTMP, and custom protocols share one listening port.

```csharp
using SocketJack.Net;

var server = new MutableTcpServer(port: 9000);

server.Http.Map("GET", "/status", (connection, request, ct) => "ok");
server.WebSocketClientConnected += connection => Console.WriteLine($"WebSocket: {connection.ID}");
server.SocketJackClientConnected += connection => Console.WriteLine($"SocketJack: {connection.ID}");

server.Listen();
```

## WPF Remoting

Install `SocketJack.WPF`, then share any WPF element over a SocketJack connection.

```csharp
using SocketJack.WPF;

IDisposable shareHandle = GameCanvas.Share(client, remotePeer, fps: 10);
```

## Documentation

- [Examples](https://github.com/JackOfFates/SocketJack/blob/master/examples.md)
- [SocketJack package](https://www.nuget.org/packages/SocketJack)
- [SocketJack.WPF package](https://www.nuget.org/packages/SocketJack.WPF)
- [GitHub repository](https://github.com/JackOfFates/SocketJack)

## License

SocketJack is open source and licensed under the [MIT License](https://github.com/JackOfFates/SocketJack/blob/master/LICENSE).

<!-- LINECOUNTER-OUTPUT:START -->
<details>
<summary><strong>LineCounter - Output</strong> <code>11,362,228 lines / 304 files</code></summary>

<br>

<strong>Scope:</strong> <code>SocketJack</code><br>
<strong>Source:</strong> <code>GetLineCount.bat</code> rules, non-empty/non-whitespace lines only; build/vendor folders skipped.

| Language | Files | Lines |
|---|---:|---:|
| Text | 123 | 11,232,431 |
| HTML | 11 | 74,994 |
| C# | 121 | 44,652 |
| XML | 3 | 5,193 |
| Markdown | 24 | 2,479 |
| JSON | 16 | 998 |
| MSBuild/XML | 3 | 713 |
| Batch | 1 | 370 |
| PowerShell | 1 | 255 |
| EditorConfig | 1 | 143 |
| **Total** | **304** | **11,362,228** |

</details>
<!-- LINECOUNTER-OUTPUT:END -->


## UDP_Reliable

Reliable indexed UDP is available through `UDP_Reliable.CreateClient` and `CreateServer`. See [usage, limits, and measured results](../docs/UDP_Reliable.md).
