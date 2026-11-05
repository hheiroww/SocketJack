# SocketJack release two: complete multiplayer setup

SocketJack helps turn an existing single-player Unity game into a multiplayer game. Its setup wizard connects your player prefab, spawning, ownership and connection controls. `SocketJackObject` or `SocketJackObject2D` synchronizes movement; selected gameplay fields and properties use `SocketJackMemberSync`. The optional MMO modules add accounts, persistent Communities, colored chat, a server-hosted website, relays and voice.

Your game still defines its rules. Gate local input and cameras by ownership, run trusted health/inventory/combat decisions on the server, and register the same prefabs and schemas in every build. Arbitrary game logic is not automatically converted into authoritative multiplayer logic.

## Supported Unity versions and renderers

Unity **6.3 LTS is the primary target**; Unity **2022.3 LTS is the compatibility bracket**. Use .NET Standard 2.1 API compatibility. Actual Windows x64 Mono player checks use **6000.3.27f2** and **2022.3.17f1**. Networking is independent of **Built-in, HDRP, URP and Custom RP**. Supply materials compatible with your pipeline; no SRP dependency is imposed by the SDK.

Native server hosting is implemented for Windows and Linux. Windows uses the operating system's `winsqlite3.dll`; Linux requires the system `libsqlite3.so.0` package, commonly `libsqlite3-0`. No SQLite native binary or database is included. Browser clients cannot host listeners or use raw UDP/TCP. The browser gameplay bridge uses WebSocket, with separate HTTP/WebSocket account adapters. Other consoles, mobile targets and server operating systems require platform-specific integration and acceptance.

## Choose an installation

Use one installation route per project. Remove duplicate SDK copies through your normal version-control/package workflow before switching routes.

| Route | Contents and installation |
| --- | --- |
| Asset Store | Import `SocketJack.Unity`. Editable components live in `Runtime/Components`; optional account/chat and voice components live in `Optional`. Open `Documentation/QuickStart.md` and this guide. |
| UPM tarball | Package Manager → **Add package from tarball**, choose `com.socketjack.multiplayer-2.0.0.tgz`. Import Physics Arena from the package's Samples tab. The complete distribution includes the optional modules. |
| NuGet | Dedicated .NET applications can reference the modules below normally. For Unity, extract the `lib/netstandard2.1` DLLs and the relevant `contentFiles` sources. Unity does not automatically import NuGet content. Preserve the supplied folder/assembly definitions from the Asset Store/UPM distribution when using all modules. |

The six independently installable NuGet IDs are:

| Package | Purpose |
| --- | --- |
| `SocketJack.Unity` | Dependency-free portable networking, synchronization, social contracts and Unity setup/components. |
| `SocketJack.Unity.Web.Client` | Account, chat and social client; no server/database dependency. |
| `SocketJack.Unity.Web` | Native accounts, SQLite persistence, website/API, linked servers and relay allocation. |
| `SocketJack.Unity.Voice` | Managed Opus, bounded voice routing, Unity capture/playback and controls. |
| `SocketJack.Unity.Voice.Web.Client` | Authenticated account voice client without a server/database dependency. |
| `SocketJack.Unity.Voice.Web` | Native authenticated voice service and linked-server voice routing. |

Install matching release versions. Optional Web modules use the MIT System.Text.Json family; Voice uses Concentus's managed Opus implementation. These are included in the complete Unity distribution with their notices. Do not add NAudio, native Concentus accelerators, SQLitePCLRaw or Microsoft.Data.Sqlite. The base networking package remains free of third-party runtime dependencies.

## Convert a single-player scene

1. Save your player as a prefab. Keep its native collider, Rigidbody settings, materials and child camera. Open **Tools → SocketJack → Setup Multiplayer**.
2. Select the prefab and a prefab registry asset. Choose **Rigidbody3D**, **Rigidbody2D** or **ExistingController**, then Host/Peer or Dedicated Server/Client and UDP/TCP. Review the proposed changes and apply. Setup is undoable; reopening it does not edit the project.
3. Setup adds the scene manager/spawner/session controls and the player's identity, movement and local-player helpers. New movement components use Balanced precision. Existing settings are preserved. Stable registry IDs survive reordering; retain the registry GUID keys in all builds.
4. Use the default WASD/arrow movement and Space jump, or disable **Read Keyboard** and call `SetInput`. For a custom motor, implement `INetworkInputMotor` and assign it to the physics component. ExistingController uses `NetworkTransform`; integrate your controller's ownership and simulation rules explicitly.
5. Set the player's local-only camera/audio/input objects in `NetworkLocalPlayer`. Remote players must not read your local input or enable extra listeners. The Input System bridge clones actions for the local player; the legacy input fallback remains available.
6. Add `SocketJackMemberSync`. Discover supported members, enable only the values you need, and set authority, interval, change-only behavior, byte bound and precision per entry. Use Server for trusted health, currency and inventory. Owner is an explicit trust choice for cosmetic or cooperative data.
7. Bind your menu to `NetworkSessionControls.Host`, `Join`, `StartDedicatedServer` and `Stop`. For an account-required game, use the authenticated sequence below instead of an anonymous Join.
8. Build two matching players. Start a host/server and join with the second player. Verify local controls, remote motion, collisions, late join, disconnect and your game's rule validation before deploying.

The manager handles spawn/despawn, object ownership, baseline readiness and room visibility. Spawn through the manager; adding an identity to an arbitrary scene object does not give it a shared ID. Use `NetworkSceneObject` for a placed object and retain its unique saved scene ID. Use `NetworkSceneCoordinator` with an explicit catalog for server-selected scene changes; clients enter a loading room until the new scene is ready. Assign the same catalog/build scenes in every player.

Use `NetworkBehaviour` callbacks for game lifecycle: reset, spawn, ready, ownership change and despawn. The ready callback follows the initial state/member baseline. `NetworkObjectPool` reuses instances with a fresh network ID; reset your own transient state in `OnNetworkReset`. Health, doors, pickups, projectiles, Animator parameters and explicit Animator trigger events have supplied starting components. Customize their game callbacks rather than trusting client-side damage claims.

## Prediction, precision and bandwidth

Both physics components simulate locally from compact 15-byte intent. The canonical server, or an explicitly authorized owner, sends sparse repair keyframes. Interpolation, capped extrapolation, bounded velocity repair and optional child-visual smoothing reduce visible correction. Physics engines are not deterministic across machines: sparse repair is necessary. This is prediction with authoritative repair, not full collision rollback.

| Physics payload before protocol headers | Full | Balanced | Compact |
| --- | ---: | ---: | ---: |
| Rigidbody3D | 70 bytes | 55 bytes | 47 bytes |
| Rigidbody2D | 70 bytes | 44 bytes | 40 bytes |

Full preserves the existing float layout. Reduced profiles have explicit ranges/steps for position, quaternion components, scale, velocity, angular velocity and scalar values. Angular velocity uses radians/second. A value outside the configured range takes a tagged raw-value escape rather than silently clipping. Custom `NetworkPrecisionAsset` settings and member-specific precision participate in compatibility checks. All participants need the same selected fixed profile or declared adaptive family.

| Grid per component | Balanced range / step | Compact range / step |
| --- | --- | --- |
| Position | −1024…1024 / 1⁄128 game unit | −512…512 / 1⁄16 game unit |
| Quaternion component | −1…1 / 1⁄32768 | −1…1 / 1⁄256 |
| Scale | 0…16 / 1⁄1024 | 0…8 / 1⁄128 |
| Linear velocity | −128…128 / 1⁄128 unit/s | −64…64 / 1⁄16 unit/s |
| Angular velocity | −64…64 / 1⁄256 rad/s | −64…64 / 1⁄32 rad/s |
| Generic scalar | −1024…1024 / 1⁄128 | −1024…1024 / 1⁄16 |

Scalar grid rounding is bounded to half a step or escapes to raw precision. Quaternion normalization means a component grid step is not an Euler-angle error guarantee. Quantization changes transmitted values; local physics still uses native precision.

Adaptive precision is opt-in: add `SocketJackAdaptiveNetworking` to the manager and `SocketJackAdaptiveObject` to applicable prefabs. Selected numeric member bindings can also opt in. The server uses measured peer backlog/RTT or an explicitly supplied game latency measurement, waits for sustained pressure before reducing precision, and waits longer before restoring it. Compact may reduce per-peer snapshot cadence; physics simulation and local input remain unchanged. Fixed-profile objects stay fixed. Choose a minimum precision appropriate for the game; adaptive policy is not congestion control for unrelated bulk traffic.

`SocketJackInterestManager` partitions visibility by room and spatial grid, with XZ/XY/XYZ dimensions, radius, hysteresis and visible-object limits. Mark the actual player as the observer anchor when a peer owns other objects. Movement, members and events share the server's visibility permission. Re-entry sends a fresh baseline/view token. Stale owner input from a prior view or ownership epoch is rejected. Partition large worlds and measure CPU, bandwidth and visible-object counts with representative gameplay. A 2,000-peer transport check does not establish 2,000 mutually visible physics players.

## Transports, relays and packet size

UDP is the default. Reliable unordered delivery acknowledges/retries and filters duplicates without waiting for earlier messages; reliable ordered, unreliable and sequenced delivery are explicit alternatives. TCP is available independently of topology and retains stream ordering. Native WebSocket and the WebGL bridge provide browser-compatible gameplay. All peers in a session must use compatible transport/protocol settings.

Packet budgeting includes IP, UDP, native reliability, object-frame and session headers. The default complete reserved UDP/IP packet is at most **998 bytes**. A lower MTU or extra tunnel reserve reduces payload capacity. Large logical messages alone split into bounded frames and rebuild before delivery. Single-frame values avoid multipart reconstruction. Configure logical-size, queue, reassembly and timeout limits; rejection/backpressure must be handled rather than bypassed.

| Default direct UDP packet | Complete reserved bytes |
| --- | ---: |
| Owner input: 48 IP/UDP + 40 native + 28 object frame + 24 session + 15 intent | 155 |
| Full server physics state: 48 + 40 + 28 + 24 + 70 | 210 |
| Full owner physics proposal: 48 + 40 + 28 + 28 + 70 | 214 |

The datagram relay reserves an additional **48-byte authenticated routing envelope**. Its native datagrams are capped at 902 bytes, so IPv6/UDP plus envelope still totals 998. Use `DatagramRelayGameTransport.ReserveOverhead` and the supplied host/client bridges rather than wrapping an unadjusted transport. The relay requires allocated credentials, endpoint reachability verification, bounded quotas and expiring/revocable sessions. Routing authentication does **not** encrypt game payloads. Direct UDP/TCP are plaintext; use an appropriate secure network or a configured TLS/WSS path where confidentiality is required.

The self-hosted WebSocket relay uses authenticated, short-lived tickets. Allocation, join, renewal and close are supplied by the account API. The operator hosts these services; no SocketJack-managed relay subscription or Internet endpoint is bundled. Public connectivity, certificates, firewall/NAT policy and deployment capacity remain the operator's responsibility.

For direct browser gameplay, add `SocketJackBrowserTransport` beside the manager before connecting and set its `Endpoint`, for example `wss://game.example/ws/game`. A native listener uses `WebSocketGameTransport`; supply its certificate/listener options through `TransportFactory` for TLS. For a WebSocket relay, obtain the account-issued join ticket and assign `RelayTicket` while stopped; credentials travel in the binary join, never in the URL or scene asset. An HTTPS page requires WSS. Set the allowed browser origin on the listening service. Account API/voice sockets are separate from the gameplay socket.

Avoid SDK application message IDs: 60001 physics relay; 60003–60004 members; 60005–60006 typed events; 60008–60009 scenes; 60010–60011 native voice; 60012–60015 latency probes; 60016 adaptive policy. The Physics Arena sample uses 60002 for its own RTT probe. Use distinct IDs for your game's protocol.

## Accounts, website and authenticated game entry

Choose the wizard's optional MMO/accounts/site setup, or add `SocketJackWebHost` to the native listening manager and `SocketJackWebClientBehaviour` plus `SocketJackMMOChatPanel` to the client UI. `SocketJackWebSessionControls` supplies the listener/sign-in/ticketed-join flow for account-required games; bind its actions to your game's menu or use its compact UI. The website starts automatically with the native game listener when the optional host is present. It does not start on ordinary clients. The default URL is `http://127.0.0.1:7778`; loopback HTTP is for local development.

The website has a dense monochrome layout, square controls, SocketJack branding and no external font/CDN dependency. It provides sign-in/registration, chat/social controls and operator functions according to account permissions. Its server-side APIs enforce the same memberships as the game UI. Message text is plain text; structured links use registered local handlers/validators.

For a public server, bind the intended interface and configure a valid PFX certificate. Keep the certificate outside Assets and put its password in the configured environment variable, default `SOCKETJACK_WEB_CERTIFICATE_PASSWORD`. Configure allowed browser origins for your deployed game. Never disable certificate validation to make production clients connect. Account data defaults to `Application.persistentDataPath/SocketJack/<nodeId>`; configure a writable, backed-up directory outside Assets/source/webroot. Windows uses OS SQLite; install the system SQLite runtime on Linux.

Use the one-time bootstrap code returned by `SocketJackWebHost.BootstrapCode` to create the first operator through the website. Keep this code private. Accounts use salted PBKDF2 password hashes, expiring opaque sessions, revocation, role checks and bounded authentication/API rates. Passwords and bearer tokens are not stored in prefabs, PlayerPrefs or URLs by the supplied client. Your operator still needs backup, recovery, retention and abuse-management policies for a real service.

Enable **Require Game Account** on the host to reject anonymous game joins. After `LoginAsync` or `RegisterAsync`, call `SocketJackWebClientBehaviour.JoinGameAsync(manager, serverId)` so the client obtains a short-lived game ticket. The server validates admission bytes and binds the resulting peer to the authenticated account. `TryGetAuthenticatedAccount` exposes the binding. Proximity is based on the trusted player returned by `NetworkPlayerSpawner.TryGetPlayer`, or your explicitly assigned `PresenceObjectResolver`.

For an account-required local host, first call `manager.StartHostListener()`, wait for the website, sign in, then call `web.ConnectLocalHostAsync(manager, serverId)`. This creates the local player through the same ticket path. Calling anonymous `StartHost()` cannot substitute for authentication when accounts are required. Sign-out revokes the web session, game tickets and linked service permissions.

## MMO chat, parties, raids and Communities

The supplied preset uses a lower-left translucent chat panel, tabs, slash commands, readable channel prefixes and editable channel colors. It is inspired by familiar MMO workflows; it contains no World of Warcraft assets, names, fonts or copied UI art.

Channels include Global, Trade, Zone, Looking for Group, Proximity/Say, Yell, Party, Raid, Community, Officers, Whisper, Emote, System and Combat. Select the preset tabs/colors and channel scopes for your game. Party/raid membership and Community roles are server-authorized; typed scope IDs in a client are requests, not permission grants. System/Combat messages must originate from trusted game integration. Registered item/player/location links invoke explicit local callbacks; arbitrary markup does not execute code.

Persistent Communities include membership, role permissions, invitations, ownership/management, bans, moderation and history. The social panel provides the included management flow; use the API adapters to integrate existing party/raid/game systems. Ignore/report controls and operator moderation are included. Review channel history limits and retention for your game. No voice audio is stored in chat history.

To share Global chat and identities across game servers, operate one canonical hub and enroll leaf servers using one-use enrollment tokens. Give each node a stable ID and a separate writable data directory. Set its hub HTTPS origin and provide its enrollment token through the configured environment variable, default `SOCKETJACK_NODE_ENROLLMENT_TOKEN`; do not serialize it into a scene. The hub issues node credentials that can be revoked. Linked leaves forward authorized account/social requests to the fixed hub, publish trusted game presence and install acknowledged game tickets before returning them. Global and Community text/voice can span these enrolled servers; proximity remains constrained by trusted world/room/position.

## Voice

Add `SocketJackVoiceManager` and `SocketJackVoiceControls` to the manager/UI. Microphone capture starts only when explicitly enabled by the player or your game. The controls expose capture, push-to-talk, mute and channel selection. The default PTT key is V; integrate the Input System with `SetPushToTalk(bool)` when using custom input. Use `SocketJackWebVoiceClient` for authenticated account voice and `SocketJackWebVoiceHost` on the native account server. These bridge to the same canonical account/membership permissions, including linked-server Global and Community voice.

The optional codec is managed Opus through Concentus. Native session voice uses bounded unreliable frames; authenticated account voice uses a separate bounded binary WSS socket. There are per-speaker jitter buffers, sequence filtering, packet-loss concealment and finite speaker/queue limits. Old audio is dropped under pressure rather than building a long playback delay. Unknown account membership is denied by default. Trusted-guest mode is an explicit development/cooperative choice, and does not enable account-controlled party/Community membership.

Voice is not end-to-end encrypted: the trusted server relays audio; WSS encrypts the connection to that server. No recording or transcript service is included. Native codec/AudioSource acceptance uses synthetic PCM so it does not record the developer's microphone. Test actual microphone devices, permissions, audio routing and platform policies with your intended hardware. Browser microphone/audio acceptance and Unity 6 WebGL player acceptance require their own platform run; do not infer them from native audio or Unity 2022 WebGL gameplay checks.

## Migration from release one

Release two uses **session wire protocol 3** and member protocol 2. Upgrade every peer, server and relay integration together. Existing sessions cannot mix release-one and release-two players. Rebuild prefab/member manifests after the upgrade. Preserve registry/scene IDs and native Rigidbody settings. New setup defaults do not overwrite an existing component's precision.

The namespace remains `SocketJack.Multiplayer`; Unity components remain `.Components`. Binary formatter (SocketJack) remains the default bounded game-value serializer and is independent of .NET BinaryFormatter. You can replace `INetworkSerializerProvider` or register typed codecs. Custom serializers still need stable protocol IDs, size bounds and game-side authority validation. Lifecycle/movement/session envelopes retain explicit binary formats.

## Release notes

Release two expands the original 3D multiplayer foundation into a complete optional MMO toolkit:

- Undoable existing-prefab setup, stable registration, local-player/input helpers and connection actions.
- Real Rigidbody2D platformer/top-down prediction alongside 3D and custom motor integration.
- Full/Balanced/Compact/custom quantization, per-member precision and opt-in server-selected adaptive policy.
- Ownership epochs, per-view stale-input rejection, readiness callbacks, scene objects/transitions, pooling and gameplay starting components.
- Shared spatial visibility, bounded replication work and measured transport/latency diagnostics.
- Native/browser WebSocket gameplay, self-hosted authenticated WebSocket/datagram relays and complete packet-envelope budgeting.
- Persistent operator-owned accounts, ticketed game admission, automatic native website/API hosting and enrolled multi-server federation.
- MMO preset chat, configurable colors/tabs, structured links, party/raid, persistent Communities and moderation.
- Optional Opus voice with capture/PTT/mute, bounded jitter/PLC and authenticated deployment-wide account voice.
- Separate NuGet modules, complete Unity distribution, offline setup/deployment guidance and third-party notices.

Validation receipts distinguish managed tests, actual native players, actual browser gameplay, TLS service checks and untested public deployment boundaries. A loopback test is not a guarantee of WAN latency, zero correction under every condition or production capacity. Profile and test your full game at its target load.
