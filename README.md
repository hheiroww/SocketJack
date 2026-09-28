# SocketJack 2026

![SocketJack Icon](https://raw.githubusercontent.com/JackOfFates/SocketJack/master/SocketJack/SocketJackIcon.png)

[![NuGet](https://img.shields.io/nuget/v/SocketJack.svg)](https://www.nuget.org/packages/SocketJack)
[![NuGet Downloads](https://img.shields.io/nuget/dt/SocketJack.svg)](https://www.nuget.org/packages/SocketJack)
[![SocketJack.WPF](https://img.shields.io/nuget/v/SocketJack.WPF.svg?label=SocketJack.WPF)](https://www.nuget.org/packages/SocketJack.WPF)
[![GitHub Packages](https://img.shields.io/badge/GitHub%20Packages-published-24292f?logo=github)](https://github.com/JackOfFates/SocketJack/packages)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

SocketJack 2026 is a batteries-included .NET networking platform for typed object transport, TCP, UDP, WebSockets, HTTP apps, protocol multiplexing, SQL-backed management, peer-to-peer metadata, and remote WPF control.

It is built for projects that need real network behavior without rebuilding wire plumbing from scratch. Framing, segmentation, serialization, compression, routing, protocol detection, TLS, static file hosting, streaming routes, typed callbacks, peer identity, and P2P forwarding are already part of the platform.

| Start here | Link |
|---|---|
| Install the NuGet packages | [#Install](#install) |
| See the 2026 package line | [#Versions](#versions) |
| Explore networking features | [#Networking](#networking) |
| Share or control WPF windows | [#SocketJack.WPF](#socketjackwpf) |

<a id="versions"></a>
## #Versions

SocketJack 2026 is the current platform line. The core packages use year-based versions, while compatibility packages retain their existing package numbering.

| Surface | Version | Target / format | Notes |
|---|---:|---|---|
| [`SocketJack`](https://www.nuget.org/packages/SocketJack) | `2026.11` | `.NET Standard 2.1` | Core networking, protocol hosting, P2P, SQL/data, streaming, HTTP, and WebSockets. |
| [`SocketJack.WPF`](https://www.nuget.org/packages/SocketJack.WPF) | `2026.7` | `net8.0-windows7.0`, `net10.0-windows7.0` | WPF capture, remote input, and GUI remoting. |
| `SocketJack.Unity` | `1.1.0.1` | `.NET Standard 2.1` | Legacy Unity-facing package surface. |
| `SocketJack.WebSocketServer` | `1.1.0.1` | `.NET Standard 2.1` | Legacy WebSocket server package surface. |

<a id="install"></a>
<details open>
<summary><strong>#Install</strong> - package commands and first choice</summary>

Install the core networking package:

```powershell
dotnet add package SocketJack
```

Or with Package Manager:

```powershell
Install-Package SocketJack
```

Install the WPF companion package when you want live WPF capture, remote input, or peer-controlled desktop windows:

```powershell
dotnet add package SocketJack.WPF
```

Use `SocketJack` for servers, clients, protocol hosting, HTTP/WebSocket apps, SQL/data, streaming, and peer routing. Add `SocketJack.WPF` when the network should see or control a WPF interface.

</details>

<a id="networking"></a>
<details>
<summary><strong>#Networking</strong> - TCP, UDP, WebSockets, standard protocols, and the SocketJack protocol</summary>

SocketJack gives one consistent API family for local-network tools, multiplayer games, admin panels, browser apps, agent hosts, stream relays, database management, and peer-routed software.

### #Tcp

`TcpServer` and `TcpClient` provide typed object messaging over TCP. You register callbacks by message type and let SocketJack handle framing, segmentation, connection identity, serialization, compression, and broadcast routing.

```csharp
using SocketJack.Net;

var server = new TcpServer(port: 12345);
server.RegisterCallback<ChatMessage>(args =>
{
    Console.WriteLine(args.Object.Text);
    args.Connection.Send(new ChatMessage("received"));
});
server.Listen();

var client = new TcpClient();
await client.Connect("127.0.0.1", 12345);
client.Send(new ChatMessage("hello"));

public sealed record ChatMessage(string Text);
```

### #Udp

`UdpServer` and `UdpClient` use the same typed callback style for datagram workflows. This is useful for discovery, presence, lightweight state, games, telemetry, local-network devices, and low-overhead service coordination.

`UDP_Reliable` adds indexed fragmentation, acknowledgments, selective retries, ordered delivery, and verified stream/file transfers. See the [API, limits, and measured results](docs/UDP_Reliable.md).

### #WebSockets

SocketJack includes browser-compatible WebSocket clients and servers. WebSocket connections can use the same serialization, compression, callbacks, peer metadata, and P2P routing as native SocketJack connections.

```csharp
using SocketJack.Net;

var server = new WebSocketServer(port: 9000);
server.RegisterCallback<ChatMessage>(args =>
{
    server.SendBroadcast(new ChatMessage(args.Object.Text));
});
server.Listen();
```

### #Standard Protocols Built In

SocketJack can host normal internet and application protocols directly:

| Protocol / surface | What it is used for |
|---|---|
| HTTP | Routes, APIs, typed request bodies, static files, directories, redirects, uploads, and stream responses. |
| WebSockets | Browser clients, dashboards, chat, realtime admin, and app clients. |
| RTMP streaming | Live media ingest and relay workflows. |
| TDS / SQL | SQL-style clients and database management surfaces. |
| FTP / SFTP helpers | File transport, session artifacts, and remote file movement. |
| TLS / auth helpers | `SslStream`, certificates, Basic auth, bearer tokens, local-host checks, and allow/deny rules. |

`MutableTcpServer` can detect and route multiple protocols on one listening port, so a single server can host HTTP, WebSocket, SocketJack protocol traffic, RTMP, SQL/TDS, and custom handlers.

### #SocketJack Protocol Built In

The native SocketJack protocol is the object-transport layer:

- Send CLR objects directly with `client.Send(new MyMessage(...))`.
- Register type-safe callbacks with `RegisterCallback<T>()`.
- Broadcast to connected clients or route to a specific peer.
- Use peer redirects for P2P-style delivery through a coordinating server.
- Carry identity and metadata with each connection.
- Let SocketJack handle wrappers, type information, chunking, compression, and reassembly.

</details>

<a id="serialization"></a>
<details>
<summary><strong>#Serialization</strong> - built-in System.Text.Json with type-safe callbacks</summary>

SocketJack uses `System.Text.Json` by default and keeps application code strongly typed.

| Feature | Benefit |
|---|---|
| Built-in JSON serializer | No serializer setup required for normal object messaging. |
| Type-safe callbacks | Receive `T` directly instead of manually decoding bytes. |
| Wrapper metadata | Type information and peer-routing metadata travel with each message. |
| Pluggable surface | Advanced users can replace the serializer through `NetworkOptions.Serializer`. |

```csharp
server.RegisterCallback<OrderSubmitted>(args =>
{
    OrderSubmitted order = args.Object;
    Console.WriteLine(order.OrderId);
});

client.Send(new OrderSubmitted("SO-1001", total: 42.50m));

public sealed record OrderSubmitted(string OrderId, decimal Total);
```

</details>

<a id="database--mvc"></a>
<details>
<summary><strong>#Database / MVC</strong> - SQL, web admin, HTTP routing, and peer metadata</summary>

SocketJack includes enough web, data, and routing infrastructure to build admin panels, dashboards, internal tools, workstation state, and agent-facing management surfaces without adding a separate web stack first.

### #SQL Database

- Built-in data-server primitives for application records, snapshots, generated context, and cached query paths.
- SQL/TDS protocol handling for SQL-style access paths.
- Good fit for local-first tools, embedded admin systems, workstation state, sessions, permissions, and hosted metadata.

### #Web SQL Admin / Management

- HTTP-hosted SQL admin and management surfaces can run directly on a SocketJack server.
- Route mapping supports JSON APIs, typed request bodies, static pages, uploads, and stream responses.
- The same host can serve an admin UI, a browser app, a database API, and native SocketJack peers.

```csharp
using SocketJack.Net;

var server = new HttpServer(port: 8080);

server.Map("GET", "/health", (connection, request, ct) => new { status = "ok" });
server.MapDirectory("/static", @"C:\wwwroot");

server.Listen();
```

### #P2P Metadata Sharing

SocketJack peers can share metadata with the network. It works like network cookies for clients and servers:

- Announce model, hardware, role, room, user, app, price, uptime, or service state.
- Update peer metadata while connections are alive.
- Route messages based on peer identity and metadata.
- Build server browsers, room lists, workstation directories, or capability registries.

</details>

<a id="socketjackwpf"></a>
<details>
<summary><strong>#SocketJack.WPF</strong> - share, control, and manipulate remote WPF windows</summary>

`SocketJack.WPF` makes WPF windows and controls remotely shareable over SocketJack.

| Capability | What it means |
|---|---|
| Live control sharing | Share any WPF `FrameworkElement` as a live image stream. |
| Remote viewing | View remote WPF content from another WPF client or a browser-backed admin surface. |
| Remote input | Send mouse, wheel, text, and keyboard input back to the shared control. |
| P2P discovery | Use peer identity and metadata so remote windows can be found, shared, controlled, and manipulated through peer flows. |
| Browser administration | Supports browser-backed viewing and remote administration for WPF applications. |

```csharp
using SocketJack.WPF;

IDisposable shareHandle = GameCanvas.Share(client, remotePeer, fps: 10);
IDisposable viewerHandle = client.ViewShare(SharedImage, sharerPeer);
```

</details>

<a id="repository-guide"></a>
<details>
<summary><strong>#Repository Guide</strong> - where the major pieces live</summary>

| Path | Purpose |
|---|---|
| `SocketJack/` | Core `SocketJack` package, transports, HTTP/WebSocket stack, mutable protocol server, SQL/data, streaming, FTP/SFTP, and resources. |
| `SocketJack.Windows/` | `SocketJack.WPF` package and WPF capture/input integration. |
| `SocketJack.Tests/` | Core networking and protocol tests. |
| `SocketJack.WpfBasicGame/` | Minimal WPF sample built against `SocketJack.WPF`. |
| `SocketJack.WebSocketServer/` | WebSocket server package/project surface. |
| `SocketJack.Unity/` | Unity-compatible package surface. |
| `SocketJack-MagicMasterList/` | Public server-list and SocketJack.com-facing host project. |
| `examples.md` | Longer runnable examples across SocketJack transports and utilities. |

</details>

<a id="documentation"></a>
<details>
<summary><strong>#Documentation</strong> - examples, packages, and companion docs</summary>

- [Examples](examples.md)
- [SocketJack package](https://www.nuget.org/packages/SocketJack)
- [SocketJack.WPF package](https://www.nuget.org/packages/SocketJack.WPF)
- [GitHub repository](https://github.com/JackOfFates/SocketJack)

</details>

## License

SocketJack is open source and licensed under the [MIT License](LICENSE).
