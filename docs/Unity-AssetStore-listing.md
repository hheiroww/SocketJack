# SocketJack

Category: Tools / Network. Paid price: USD 4.99. Release version: 2026.11.3.

## Summary

Turn your Unity game into a multiplayer game. Add networked objects, responsive local physics and smooth movement with reliable UDP, configurable authority and a playable demo.

## Description

### Turn your Unity game into a multiplayer game

SocketJack makes it easy to bring multiplayer to the game you are already building. Add SocketJackObject to your networked prefabs, connect them to NetworkManager, and start sharing a living, moving world between players. Build cooperative adventures, physics arenas, racing prototypes, shared sandboxes or your own game rules on the same networking foundation.

### Get connected in three steps

1. Add NetworkManager and register the prefabs your game will spawn. Choose player-hosted Host / Peer or Dedicated Server / Client, then select UDP or TCP.
2. Add SocketJackObject to objects that need synchronized 3D Rigidbody physics. It adds the required NetworkIdentity and Rigidbody. Use NetworkTransform when you want simpler transform replication.
3. Spawn objects through the manager, assign their owners, and use the included WASD/arrow-key controls or connect your own controller through SetInput and the session APIs.

The included sliding-cube game demonstrates the complete flow: host or join, move with physics, jump, collide with obstacles and see other players in the same world. Editable scripts and an offline quick start make the example a starting point for your own game.

### Responsive physics, smooth shared movement

Player movement runs locally for immediate response. Compact binary inputs are relayed to the other participants so they can simulate movement too. Sparse authoritative keyframes repair divergence, while interpolation, capped extrapolation, velocity correction and optional child-visual smoothing keep remote motion continuous.

Keep your native Rigidbody and Collider settings: mass, drag, angular drag, gravity, constraints, interpolation and material friction remain under your control. Choose static or dynamic behavior, with colliders enabled or disabled, and tune movement force, speed, input rate, repair settings and smoothing in the Inspector.

### Choose how your game runs

Use a player-hosted session for cooperative play or a dedicated server for a persistent world. Topology and transport are independent. Server Authority is enabled by default; the server chooses each object's authority when it spawns. Explicit owner authority is available for games that trust the owning player. Your game can add movement, collision, cooldown and other rule validation.

### Networking that handles the details

UDP uses reliable unordered delivery by default, with explicit ordered, unreliable and sequenced options. TCP provides an ordered stream. Packet budgeting reserves space for IP, transport and object headers; UDP object frames stay below 999 bytes under the configured budget. Larger logical messages are split automatically and rebuilt with bounded memory and time limits.

Object ownership, spawning, despawning, room visibility and streamed late-join baselines are included. Bounded queues, configurable work budgets and a manually pumped runtime avoid allocating a thread for every player. Automatic UPnP mapping attempts to open the host/server's chosen service port on supported routers and reports its status.

### Grow with your game

Partition players and objects into rooms, control how much each player can see, and tune tick, snapshot and queue limits for your workload. The default 4,096-peer cap is configurable. A 2,000-peer loopback transport test has passed; production capacity depends on simulation, visibility, bandwidth and hosting. Large shared worlds need measured interest management and, where appropriate, multiple server processes.

### What you get

- Portable .NET Standard 2.1 runtime with no third-party runtime NuGet dependencies.
- Editable NetworkManager, NetworkIdentity, SocketJackObject, NetworkTransform and example controller scripts.
- A playable 3D physics demo with host, peer, server and client roles.
- Inspector configuration, binary codecs, offline documentation and source links.

### Integration and platform notes

Your game supplies its prefabs, player intent and gameplay rules. The supplied physics component targets 3D Rigidbody physics; custom controllers, 2D physics and game-specific state use the runtime APIs and your own integration. PhysX is not deterministic: prediction uses sparse repair rather than full rollback or complete collision replay.

Initial Editor/player validation is Unity 2022.3.17f1, Built-in Render Pipeline, Windows x64 Mono. The sample uses the legacy input API and IMGUI. IL2CPP, mobile, console and other platform acceptance require testing your actual build. WebGL requires a separate browser transport bridge.

Included UDP/TCP traffic is plaintext. No hosted relay, matchmaking, accounts, persistence service or universal anti-cheat is included. UPnP depends on the router and cannot bypass carrier-grade NAT or firewall policy. A reachable server, manual forwarding or a separate relay may be needed for Internet play.

Source and issue tracking: https://github.com/hheiroww/SocketJack

## Technical details

### Runtime and workflow

- Unity 2022.3+, .NET Standard 2.1 API compatibility; initial validation on 2022.3.17f1.
- Windows x64 Mono actual-player validation; portable runtime tests on Windows and Linux.
- No third-party runtime NuGet dependencies. Managed SocketJack.Unity.dll; code namespace SocketJack.Multiplayer.
- Built-in Render Pipeline demo; physics, IMGUI and JSON serialization modules; legacy input API.

### Network and physics

- UDP / TCP; independent Host / Peer and Dedicated Server / Client topology.
- Reliable unordered UDP by default; explicit reliable ordered, unreliable and sequenced delivery.
- Complete UDP/IP header budgeting; object frames capped at 998 bytes under the configured budget; automatic bounded reconstruction of larger messages.
- 15-byte binary input and 70-byte physics snapshots before protocol headers; input on change with keepalive; sparse repair keyframes.
- Local prediction, interpolation, capped extrapolation, velocity repair and optional visual smoothing.
- Server-assigned ownership and authority, room visibility, late-join baselines, bounded queues and per-update work limits.
- Automatic finite UPnP leases with conflict/ownership checks, renewal and release on supported gateways.

### Validation and limits

92 managed runtime tests plus 26 focused reconstruction/security tests; actual Unity server/two-client physics acceptance and fresh-package import compilation. A 2,000-peer loopback transport exercise passed; this is not a guarantee for 2,000 physics players in one world. Real-router/WAN and other player backends/platforms remain unverified.

Documentation and source: https://github.com/hheiroww/SocketJack

Included offline quick start and runtime/physics guides. MIT license included.

## AI disclosure

OpenAI Codex assisted with generating and refactoring networking and component code, tests, the physics sample and documentation. OpenAI image generation adapted the existing SocketJack logo to the included monochrome color scheme. The code and artwork were iterated and refined; functional code was compiled and checked with managed tests, real Unity server/client players and clean-package import checks. No AI service or API key is required to use the package at runtime.

## Release notes

### 2026.11.3 — Initial Asset Store release

#### Multiplayer foundation

- Added a portable .NET Standard 2.1 runtime, owned network objects, authoritative spawning/despawning, room visibility and streamed late-join baselines.
- Added independent Host / Peer and Dedicated Server / Client topology with explicit UDP / TCP selection.
- Server Authority is enabled by default. Per-object server/owner authority is assigned by the server and checked alongside ownership, room, sequence and message limits. Games can add their own state validators.

#### Client-simulated physics

- Added SocketJackObject with immediate local input, relayed movement intent and sparse authoritative repair.
- Added 15-byte binary input and 70-byte physics snapshots before network headers, input-on-change delivery and heartbeat keepalive.
- Added interpolation, bounded extrapolation, velocity correction, configurable repair limits and optional child-visual smoothing.
- Added default WASD/arrow-key sliding movement and Space to jump, plus SetInput for custom controllers.
- Added four Inspector movement modes: Static w/Colliders Enabled; Static; Physics Enabled; Physics w/Colliders Enabled.
- Preserved native Rigidbody mass, drag, angular drag, gravity, constraints, interpolation and collider material/friction. Movement uses ForceMode.Force by default.

#### Transport and object delivery

- Reliable UDP is unordered by default. Ordered, unreliable and sequenced modes remain explicit choices; TCP supplies an ordered stream.
- Added complete IP/UDP, reliability, session and object-frame header budgeting. Included UDP object frames are capped at 998 bytes under the configured budget.
- Larger logical messages are automatically split and reconstructed with bounded queues, object counts, memory and timeouts. Small messages use a single frame.
- Added admission checks, retry/acknowledgment handling, heartbeat/timeouts and configurable peer, queue and per-update limits.

#### Connection setup

- Added asynchronous UPnP mapping for the listening host/server only, with status reporting, finite leases, conflict/ownership checks, renewal and release.
- Peer/client roles use outbound connections and do not request service-port mappings. Automated gameplay checks disable UPnP.

#### Unity package and demo

- Included editable networking components, the sliding-cube physics scene, an explicit player-build helper, offline quick-start/runtime guides and the monochrome SocketJack logo.
- Added Tools/SocketJack editor menus and project-specific sample namespaces.
- Code APIs now use SocketJack.Multiplayer and SocketJack.Multiplayer.Components. NuGet package ID and managed assembly name remain SocketJack.Unity. Earlier integrations should update their using directives. Wire/session framing remains explicit; use matching releases for peers and servers.

#### Verification

- 92 managed runtime tests and 26 focused core reconstruction/security tests pass on Windows and Linux.
- Actual Unity 2022.3 Windows Mono server/two-client physics runs passed with zero hard corrections. Fresh exported-package import and component/sample compilation are checked separately.
- A 2,000-peer loopback UDP transport exercise passed; it does not establish 2,000 physics players in one world.

#### Known integration boundaries

Your game supplies prefabs, intent, simulation and rule validation. Built-in physics targets 3D Rigidbody; custom controllers and 2D physics need their own integration. Prediction is not full rollback. Included transports are plaintext. Hosted relay, matchmaking, accounts and persistence are separate services. Real-router/WAN, IL2CPP, mobile and console acceptance remain unverified; WebGL needs a browser bridge.
