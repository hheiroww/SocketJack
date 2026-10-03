<div align="center">

<img src="https://raw.githubusercontent.com/hheiroww/SocketJack/master/SocketJack/SocketJackIcon.png" alt="SocketJack" width="128" />

# ⚡ SocketJack 2026

**Typed messages · Reliable UDP · HTTP & WebSockets · One-port protocol hosting**

[![NuGet](https://img.shields.io/nuget/v/SocketJack.svg?logo=nuget)](https://www.nuget.org/packages/SocketJack)
[![Downloads](https://img.shields.io/nuget/dt/SocketJack.svg)](https://www.nuget.org/packages/SocketJack)
[![Build & publish](https://github.com/hheiroww/SocketJack/actions/workflows/dotnet.yml/badge.svg)](https://github.com/hheiroww/SocketJack/actions/workflows/dotnet.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](https://github.com/hheiroww/SocketJack/blob/master/LICENSE)

**📦 [Install](#-install) · ✨ [What's new](#-whats-new-in-202615) · 🧭 [Features](#-feature-explorer) · 📚 [Documentation](#-documentation)**

</div>

---

SocketJack is a .NET networking library for sending typed objects, hosting HTTP APIs, connecting browser clients, and routing peers. It supplies framing, serialization, segmentation, compression, callbacks, and connection metadata so you can build the application on top.

| 🔌 Connect | 🌐 Host | 📡 Exchange | 🛠️ Integrate |
|---|---|---|---|
| TCP · UDP · reliable UDP | HTTP · WebSocket · multiple protocols | Typed messages · broadcasts · peer routing | TypeScript generation · SFTP · WPF companion |

## 📦 Install

```powershell
dotnet add package SocketJack --version 2026.15.0
```

| Package | Target | Purpose |
|---|---|---|
| **[SocketJack](https://www.nuget.org/packages/SocketJack/2026.15.0)** | .NET Standard 2.1 | Core networking library; this release is **2026.15.0**. |
| **[SocketJack.WPF](https://www.nuget.org/packages/SocketJack.WPF)** | Windows / WPF | Companion package for live control capture and remote input; versioned separately. |

<details>
<summary><strong>🚀 First connection — typed TCP messages</strong></summary>

Register a callback before connecting. Run the server and client in separate applications, or together while experimenting:

```csharp
using System;
using SocketJack.Net;

// This example deliberately accepts anonymous text, with peer routing disabled.
var options = new NetworkOptions { UsePeerToPeer = false };
options.Authorization.AnonymousMessageTypes.Add(typeof(string));
using var server = new TcpServer(options, 12345);
server.RegisterCallback<string>(e =>
{
    Console.WriteLine(e.Object);
    e.Connection.Send("Received ✓");
});
server.Listen();

using var client = new TcpClient(new NetworkOptions { UsePeerToPeer = false });
client.RegisterCallback<string>(e => Console.WriteLine(e.Object));
if (await client.Connect("127.0.0.1", 12345))
    client.Send("Hello, SocketJack!");
Console.ReadLine();
```

Keep the host application running while connections are in use. Use matching serialization and compression settings on both endpoints.

</details>

## ✨ What's new in 2026.15

This update gives you more control over what a server accepts. You choose which services to open, SQL connections stay on the same machine unless you allow remote access, and approving a local message DLL no longer requires copying its hash by hand.

| Update | What it means for your application |
|---|---|
| 🔀 **Choose your protocols** | A `MutableTcpServer` starts with every protocol disabled. Enable only the services you need, such as HTTP or SocketJack messages. |
| 🗄️ **Control SQL access** | SQL/TDS connections are local-only by default. You can allow your private network or specific remote IP addresses. Database login rules still apply. |
| 🔒 **Easier DLL approval** | Approve your local message assembly and SocketJack calculates its fingerprint. A trusted release hash is still an option when you need stricter control. |
| 📦 **Embedded DLL support** | SafeMode can locate a matching DLL stored as an embedded resource, as well as a normal file on disk. |

**Upgrading an existing server?** Add its required protocols to `EnabledProtocols` before calling `Listen()`. See the [2026.15 setup and upgrade guide](https://github.com/hheiroww/SocketJack/blob/master/docs/UPGRADING-2026.15.md) for examples.

The earlier 2026.14 protections remain: SafeMode is on by default, application messages require authentication unless explicitly allowed anonymously, and administrative actions need separate permission. Reliable UDP, verified file transfers, the custom binary serializer, and SSH.NET 2026.0.0 remain available.

## 🛡️ Authentication and safe type activation

SafeMode is enabled by default. Approve the local DLLs that define your messages. SocketJack checks the client’s message types and DLL fingerprints when it connects, without sending the server’s fingerprints back. A mismatch disconnects the client before application messages are handled.

Authentication is a separate step. A server verifies credentials, then calls `SetAuthenticatedPrincipal` over encrypted TLS. Peer redirects and control messages require explicit authorization callbacks. Administrative DTOs require the `Administrator` role plus an operation policy and cannot be redirected between peers. Remote CLR reflection endpoints reject invocation. Allowlisted constructors, setters, converters, and application handlers remain trusted code.

[Configuration, login flow, limitations and migration](https://github.com/hheiroww/SocketJack/blob/master/docs/SAFEMODE.md)

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

📖 [API, configuration & limits](https://github.com/hheiroww/SocketJack/blob/master/docs/UDP_Reliable.md)

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

Reliable UDP sends large files and streams in small pieces instead of loading the whole file into memory. It tracks each piece, verifies the completed file with SHA-256, and lets the receiver decide whether to accept it.

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

`MutableTcpServer` lets several services share one port and sends each connection to the right handler. The repository includes HTTP, WebSocket, native SocketJack, RTMP, SQL/TDS, and FTP surfaces, with custom protocol handlers for extensions.

Every protocol is disabled by default. Add only the required names to the server's `EnabledProtocols` whitelist before listening:

```csharp
var server = new MutableTcpServer(8080);
server.EnabledProtocols.Add(MutableTcpProtocols.Http);
server.EnabledProtocols.Add(MutableTcpProtocols.WebSocket);
server.Listen();
```

SQL/TDS additionally defaults to loopback-only access. Use `server.SqlOptions.RemoteAccess` to explicitly allow private-network or public clients, or add individual addresses to `AllowedRemoteIpAddresses`. SFTP runs over SSH and needs the separate backend described above; it is not supplied by ordinary TCP protocol detection.

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

Release builds are checked on Linux and Windows before the package is published.

Before packaging, restore audits direct and transitive dependencies. Known vulnerability warnings and audit-feed failures block the core release. Package audits report known advisories at the time of the check; they do not prove the absence of all security defects.

</details>

## 📚 Documentation

| Resource | What you'll find |
|---|---|
| 📘 [Examples](https://github.com/hheiroww/SocketJack/blob/master/examples.md) | Longer transport and utility examples. |
| 📡 [Reliable UDP guide](https://github.com/hheiroww/SocketJack/blob/master/docs/UDP_Reliable.md) | Options, transfer APIs, limits, and acceptance semantics. |
| 📊 [Reliable UDP benchmark results](https://github.com/hheiroww/SocketJack/blob/master/docs/UDP_Reliable-results.md) | TCP comparison, confidence intervals, acceptance limits, and raw measurements. |
| 🧩 [Binary serializer benchmark results](https://github.com/hheiroww/SocketJack/blob/master/docs/encoder-testing/README.md) | Custom SB encoding/decoding, JSON comparison, allocations, wrapping, and reproduction commands. |
| 🆕 [2026.15 upgrade guide](https://github.com/hheiroww/SocketJack/blob/master/docs/UPGRADING-2026.15.md) | Enable services, choose SQL access, and approve message DLLs. |
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
