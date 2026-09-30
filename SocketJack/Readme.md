<div align="center">

<img src="https://raw.githubusercontent.com/hheiroww/SocketJack/master/SocketJack/SocketJackIcon.png" alt="SocketJack" width="128" />

# ⚡ SocketJack 2026

**Typed messages · Reliable UDP · HTTP & WebSockets · One-port protocol hosting**

[![NuGet](https://img.shields.io/nuget/v/SocketJack.svg?logo=nuget)](https://www.nuget.org/packages/SocketJack)
[![Downloads](https://img.shields.io/nuget/dt/SocketJack.svg)](https://www.nuget.org/packages/SocketJack)
[![Build & publish](https://github.com/hheiroww/SocketJack/actions/workflows/dotnet.yml/badge.svg)](https://github.com/hheiroww/SocketJack/actions/workflows/dotnet.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](https://github.com/hheiroww/SocketJack/blob/master/LICENSE)

**📦 [Install](#-install) · ✨ [What's new](#-whats-new-in-202613) · 🧭 [Features](#-feature-explorer) · 📚 [Documentation](#-documentation)**

</div>

---

SocketJack is a .NET networking library for sending typed objects, hosting HTTP APIs, connecting browser clients, and routing peers. It supplies framing, serialization, segmentation, compression, callbacks, and connection metadata so you can build the application on top.

| 🔌 Connect | 🌐 Host | 📡 Exchange | 🛠️ Integrate |
|---|---|---|---|
| TCP · UDP · reliable UDP | HTTP · WebSocket · multiple protocols | Typed messages · broadcasts · peer routing | TypeScript generation · SFTP · WPF companion |

## 📦 Install

```powershell
dotnet add package SocketJack --version 2026.13.0
```

| Package | Target | Purpose |
|---|---|---|
| **[SocketJack](https://www.nuget.org/packages/SocketJack/2026.13.0)** | .NET Standard 2.1 | Core networking library; this release is **2026.13.0**. |
| **[SocketJack.WPF](https://www.nuget.org/packages/SocketJack.WPF)** | Windows / WPF | Companion package for live control capture and remote input; versioned separately. |

<details>
<summary><strong>🚀 First connection — typed TCP messages</strong></summary>

Register a callback before connecting. Run the server and client in separate applications, or together while experimenting:

```csharp
using System;
using SocketJack.Net;

var server = new TcpServer(port: 12345);
server.RegisterCallback<ChatMessage>(e =>
{
    Console.WriteLine(e.Object.Text);
    e.Connection.Send(new ChatMessage { Text = "Received ✓" });
});
server.Listen();

var client = new TcpClient();
client.RegisterCallback<ChatMessage>(e => Console.WriteLine(e.Object.Text));
if (await client.Connect("127.0.0.1", 12345))
    client.Send(new ChatMessage { Text = "Hello, SocketJack!" });

public sealed class ChatMessage
{
    public string Text { get; set; }
}
```

Keep the host application running while connections are in use. Use matching serialization and compression settings on both endpoints.

</details>

## ✨ What's new in 2026.13

This release refreshes the documentation and publishing checks for the recent networking updates. Expand each feature below for behavior, configuration, and limits.

| Feature | What changed |
|---|---|
| 📡 **Reliable UDP** | Opt-in reliable sessions, indexed fragments, selective retransmission, and independent or ordered delivery. |
| 🧩 **Binary serialization** | Custom `SB` format, compact integers, direct byte arrays, and type whitelist validation. |
| 📂 **Verified transfers** | Chunked stream/file delivery with 64-bit offsets and SHA-256 verification. |
| 🔐 **SSH.NET 2026.0.0** | Updated dependency powering SocketJack's SFTP client integration. |
| 🟦 **TypeScript generation** | Generate a client from mapped HTTP routes and emit whitelisted WebSocket message types. |
| ✅ **Trusted Publishing** | GitHub identity obtains a short-lived NuGet credential; no stored publish API key is required. |

## 🧭 Feature explorer

<details open>
<summary><strong>📡 Reliable UDP — fragments, acknowledgments, retries & ordering</strong></summary>

`UDP_Reliable` adds a reliable session layer to SocketJack UDP. Ordinary UDP remains the default, and existing applications opt in explicitly.

- **✓ Indexed reassembly:** fragments are placed at their indexed positions; duplicate packets are suppressed.
- **↻ Selective retries:** missing data is retransmitted, with bounded queues and receiver backpressure.
- **⇄ Independent delivery:** the default allows complete messages to proceed independently; callbacks must be thread-safe.
- **≡ Ordered delivery:** set `Ordering = true` when callbacks must follow admission order.
- **📊 Statistics:** inspect wire bytes, retries, queued payload, and smoothed RTT through `ReliableStatistics`.

```csharp
using SocketJack.Net;
using SocketJack.Serialization;

var options = new NetworkOptions
{
    Serializer = new BinarySerializer(),
    UdpReliable = new UdpReliableOptions
    {
        Ordering = false,
        DatagramSize = 1200,
        BufferSize = 32768
    }
};
var client = UDP_Reliable.CreateClient(options);
```

Configure a matching reliable server with its own options instance. `SendAsync` waits for remote transport acceptance, not successful application processing or durable storage; use an explicit application reply for that guarantee.

**Limits:** reliability does not provide encryption. The recorded loopback benchmarks did **not** meet the goal of outperforming TCP on both throughput and latency. Choose this transport for its delivery behavior and measure your own workload.

📖 [API, configuration & limits](https://github.com/hheiroww/SocketJack/blob/master/docs/UDP_Reliable.md) · 📊 [Measured results](https://github.com/hheiroww/SocketJack/blob/master/docs/UDP_Reliable-results.md)

</details>

<details>
<summary><strong>🧩 Serialization — JSON by default, compact binary when selected</strong></summary>

`System.Text.Json` remains the default. Select `SocketJack.Serialization.BinarySerializer` through `NetworkOptions.Serializer` on both peers to use SocketJack's versioned **SB** wire format.

| Encoding feature | Behavior |
|---|---|
| Compact integers | Variable-length integer encoding reduces metadata size. |
| Byte arrays | Encoded directly instead of converting payload bytes into JSON text. |
| Uncommon types | JSON fallback retains support beyond the specialized binary cases. |
| Type validation | Decoded wrappers continue through SocketJack's type whitelist checks. |

This is a custom serializer, not `BinaryFormatter`. Register message callbacks or whitelist message types before sending, including nested types where required. Serializer and compression choices must agree between endpoints.

</details>

<details>
<summary><strong>📂 Stream & file transfer — bounded chunks and verified completion</strong></summary>

Reliable UDP supports stream/file transfers without materializing an entire file as one message. Transfer frames use bounded chunks, **64-bit byte offsets**, SHA-256 verification, and explicit destination acceptance.

The per-I/O buffer cap is **32 KiB**, not a total-file size limit. Whole-object messages still have separate memory budgets. Configure delivery timeouts and destination handling for your workload; transport acceptance alone does not establish successful storage.

📖 [Transfer APIs and receiver requirements](https://github.com/hheiroww/SocketJack/blob/master/docs/UDP_Reliable.md)

</details>

<details>
<summary><strong>🔐 SSH.NET 2026.0.0 — SFTP client integration</strong></summary>

SocketJack now references **SSH.NET 2026.0.0**. `SocketJack.Net.SftpClient` wraps SSH.NET for remote file operations:

- **🔑 Authentication:** password and private-key options, including encrypted-key passphrases.
- **📁 Directory operations:** list, create, change, rename, and delete.
- **⇅ File operations:** upload/download with progress callbacks; open read/write streams.
- **⚙️ Metadata:** inspect and update SFTP file attributes.
- **⏱️ Configuration:** host, port, username, and connection timeout through `SftpClientOptions`.

The convenience async methods wrap synchronous work with `Task.Run`; they are not a separate native asynchronous transport. SFTP **server** hosting requires an `ISftpServerBackend` implementation; SocketJack does not bundle an SSH server.

📖 [SFTP implementation](https://github.com/hheiroww/SocketJack/blob/master/SocketJack/Net/FtpSftp.cs) · 📦 [SSH.NET package](https://www.nuget.org/packages/SSH.NET/2026.0.0)

</details>

<details>
<summary><strong>🟦 TypeScript — generate clients from mapped HTTP routes</strong></summary>

`HttpServer.GenerateTypeScriptClient()` emits a dependency-free TypeScript client from the server's currently mapped routes. Typed request bodies become TypeScript aliases, path variables become required inputs, and resolvable whitelisted message classes can be emitted as interfaces for WebSocket use.

```csharp
using System.IO;
using SocketJack.Net;

var server = new HttpServer(port: 8080);
server.Map("GET", "/health", (connection, request, ct) => new { status = "ok" });

File.WriteAllText("SocketJackClient.ts", server.GenerateTypeScriptClient(
    new TypeScriptClientOptions
    {
        ClassName = "ApiClient",
        BaseUrl = "http://localhost:8080",
        IncludeWebSocketTypes = true
    }));
```

Generate after registering routes. Review generated contracts whenever the server API changes.

</details>

<details>
<summary><strong>🔌 TCP & ordinary UDP — typed callbacks, replies and broadcasts</strong></summary>

`TcpClient` / `TcpServer` provide framed object messaging over TCP. `UdpClient` / `UdpServer` provide the typed callback model for datagrams. Use `RegisterCallback<T>()` to receive a message, reply through its connection, or broadcast through the server.

TCP suits ordered streams and general application traffic. Ordinary UDP suits discovery and lightweight updates where applications tolerate loss. Select reliable UDP explicitly when you need its session delivery semantics.

</details>

<details>
<summary><strong>🌐 HTTP & WebSockets — APIs, static files and realtime clients</strong></summary>

`HttpServer` maps methods and paths to handlers, supports typed request bodies, and serves files, directories, redirects, uploads, and streaming responses. WebSocket clients and servers connect browser applications and native peers to typed callbacks and peer metadata.

```csharp
var server = new SocketJack.Net.HttpServer(port: 8080);
server.Map("GET", "/health", (connection, request, ct) => new { status = "ok" });
server.MapDirectory("/static", @"C:\wwwroot");
server.Listen();
```

</details>

<details>
<summary><strong>🔀 Protocol multiplexing — multiple protocols on one listener</strong></summary>

`MutableTcpServer` detects and dispatches supported protocol traffic on one listening port. The repository includes HTTP, WebSocket, native SocketJack, RTMP, SQL/TDS, and FTP surfaces, with custom protocol handlers for extensions.

Enable the handlers required by your application. SFTP runs over SSH and needs the separate backend described above; it is not supplied by ordinary TCP protocol detection.

</details>

<details>
<summary><strong>🤝 Peer routing & metadata — discovery and coordinated delivery</strong></summary>

Associate identity and metadata with peers, update that metadata during a connection, and route messages through a coordinating server. This supports room lists, capability registries, service discovery, and peer-directed application traffic. Server-mediated redirects are distinct from a direct peer connection.

</details>

<details>
<summary><strong>🗄️ Data & streaming — SQL surfaces, management routes and RTMP</strong></summary>

The repository includes data-server primitives, SQL/TDS handling, HTTP management routes, and RTMP ingest/relay support. These components can support embedded administration, application records, and media workflows alongside native SocketJack messaging.

See the examples and component source for configuration and supported protocol behavior.

</details>

<details>
<summary><strong>🖥️ WPF companion — live controls and remote input</strong></summary>

The separately versioned `SocketJack.WPF` package shares `FrameworkElement` content as a live image stream and forwards mouse, wheel, text, and keyboard input.

```csharp
using SocketJack.WPF;

IDisposable shareHandle = GameCanvas.Share(client, remotePeer, fps: 10);
IDisposable viewerHandle = client.ViewShare(SharedImage, sharerPeer);
```

This fragment assumes an existing WPF application, connection, and peer references. Dispose the handles to stop sharing/viewing. The current core publishing workflow does not republish the WPF companion.

</details>

<details>
<summary><strong>✅ Publishing & dependency checks — short-lived credentials</strong></summary>

The release workflow builds on Linux and Windows, packs SocketJack, and publishes to NuGet and GitHub Packages. NuGet Trusted Publishing exchanges GitHub's workflow identity for a temporary credential using `NuGet/login`.

Before packaging, restore audits direct and transitive dependencies. Known vulnerability warnings and audit-feed failures block the core release. Package audits report known advisories at the time of the check; they do not prove the absence of all security defects.

</details>

## 📚 Documentation

| Resource | What you'll find |
|---|---|
| 📘 [Examples](https://github.com/hheiroww/SocketJack/blob/master/examples.md) | Longer transport and utility examples. |
| 📡 [Reliable UDP guide](https://github.com/hheiroww/SocketJack/blob/master/docs/UDP_Reliable.md) | Options, transfer APIs, limits, and acceptance semantics. |
| 📊 [Reliable UDP results](https://github.com/hheiroww/SocketJack/blob/master/docs/UDP_Reliable-results.md) | Recorded correctness and performance evidence. |
| 🔎 [Release audit](https://github.com/hheiroww/SocketJack/blob/master/docs/RELEASE-2026.13.md) | Dependency audit scope and release validation. |
| 🧪 [GitHub Actions](https://github.com/hheiroww/SocketJack/actions) | Build and publishing results. |
| 📦 [NuGet](https://www.nuget.org/packages/SocketJack) | Published versions and dependencies. |

<details>
<summary><strong>🗺️ Repository map</strong></summary>

| Directory | Role |
|---|---|
| `SocketJack/` | Core networking and package README. |
| `SocketJack.Tests/` | Protocol and HTTP integration tests. |
| `SocketJack.UdpReliable.Tests/` | Reliable UDP correctness and impairment tests. |
| `SocketJack.Windows/` | WPF companion. |
| `SocketJack.WpfBasicGame/` | WPF example application. |
| `docs/` | Protocol guides, benchmarks, and release notes. |

</details>

---

**⚖️ License:** [MIT](https://github.com/hheiroww/SocketJack/blob/master/LICENSE)
