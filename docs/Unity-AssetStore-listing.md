# SocketJack Premium — complete multiplayer listing

Category: Tools / Network. New separate product: SocketJack Premium. Price: USD 9.99. Store version: 2.0. Runtime/NuGet family candidate: 2026.11.4.0. Keep the existing Basic asset separate and available. Premium is the complete expansion.

## Summary

Easily turn your Unity game into multiplayer. Sync 3D/2D physics and gameplay, then add MMO chat, accounts, Communities, voice and your own server-hosted website. UDP, TCP and WebSocket.

## Description

### Easily turn your Unity game into a multiplayer game with SocketJack Premium

Bring other players into the game you are already building. SocketJack connects your existing player prefab to networking, spawning, ownership and synchronized gameplay. Start with the setup wizard, keep your game's own levels and controls, and choose the multiplayer features you need.

Use the same foundation for a physics arena, cooperative adventure, platformer, shared sandbox or persistent online world. The complete Premium distribution includes optional MMO chat, accounts, Communities, voice, a server-hosted website and linked-server services alongside its 3D/2D networking tools.

### Connect an existing game in a few steps

### Why choose Premium over the basic asset?

The basic asset provides the original UDP/TCP, 3D physics and selected-member synchronization foundation. Premium adds the surrounding production workflow: an undoable prefab setup wizard, real 2D synchronization, precision/spatial/lifecycle tools, browser WebSocket integration, account-backed MMO chat and Communities, the hosted website/API, linked servers, authenticated self-hosted relays and Opus voice. It includes the updated core, so you do not need a second Basic copy installed. Detailed task guides, examples and actual-UI screenshots help you integrate and verify each stage.

First-party source and NuGet modules remain public. Premium supplies the complete Unity distribution and integrated workflow; it does not add a runtime paywall or require a SocketJack-operated service subscription.

### Start with your existing prefab

1. Open Tools → SocketJack → Setup Multiplayer and select your player prefab.
2. Choose Rigidbody3D, Rigidbody2D or an existing controller, a host/peer or dedicated-server topology, and UDP/TCP transport. Review and apply the undoable setup.
3. Use the supplied WASD/arrow movement and Space jump, or connect your own input/motor. Select the gameplay fields and properties you want to synchronize, then build and join.

The wizard supplies stable prefab registration, player spawning, connection actions and local-player helpers. Editable components and an offline conversion guide explain how each piece fits into a real single-player project. Your game defines its rules: integrate ownership checks and authoritative combat, inventory and other trusted decisions rather than assuming arbitrary single-player code automatically becomes multiplayer-safe.

### Responsive local physics with smooth shared motion

SocketJackObject synchronizes 3D Rigidbody movement; SocketJackObject2D supplies real Rigidbody2D platformer and top-down movement. Players simulate locally from compact binary intent for immediate response. Sparse canonical keyframes repair divergence, with interpolation, capped extrapolation, bounded velocity correction and optional child-visual smoothing.

Keep native mass, damping/drag, gravity, constraints, collision and material settings. Choose server authority or explicit owner authority per object. Connect a custom INetworkInputMotor, or use NetworkTransform for simpler state replication. Unity physics is not deterministic across machines: this is predictive local simulation with authoritative repair, rather than full collision rollback.

### Synchronize the values your game needs

SocketJackMemberSync lets you opt into individual fields and readable/writable properties. Each entry has authority, interval, change-only, serialized byte-limit and precision controls. Keep health server controlled and cosmetic data owner controlled on the same object. The server enforces its schema; late joiners receive current baselines.

Binary formatter (SocketJack) is the default bounded typed value serializer, independent of .NET BinaryFormatter. Register explicit codecs or supply your own serializer provider. Matching builds, prefab/member schemas and serializer settings are required. Arbitrary Unity assets and object graphs are not transferred automatically.

### Precision and visibility that you can tune

Choose Full, Balanced, Compact or custom quantization for movement and selected numeric members. Typical 3D physics payloads are 70/55/47 bytes; 2D payloads are 70/44/40 bytes before protocol headers. Values outside a configured range use a raw-value escape instead of silent clipping. New wizard-created movement uses Balanced while existing settings are preserved.

Opt-in adaptive precision lets the server respond to sustained measured backlog/latency and restore precision after stability returns. Spatial interest adds room/grid visibility, radius, hysteresis and per-peer limits. All replication streams share visibility permission, and re-entry receives a fresh baseline. Ownership epochs and view tokens reject stale proposals after ownership or visibility changes.

### A complete optional MMO communication preset

Add a familiar lower-left translucent chat panel with tabs, slash commands, channel prefixes and configurable colors. Presets cover MMO, cooperative and arena workflows. Global, Trade, Zone, Looking for Group, Proximity/Say, Yell, Party, Raid, Community, Officers, Whisper, Emote, System and trusted Combat feeds are available.

Persistent Communities include memberships, roles, invitations, ownership, bans and moderation. Party/raid APIs and the social panel connect group membership to chat and voice permissions. Ignore/report controls and registered item/player/location links are included. Channel access is enforced by the server; entering a scope ID does not grant membership.

The design follows familiar MMO conventions without redistributing World of Warcraft artwork, fonts or other game assets.

### Your accounts, your website, your server

The optional native Web host starts a monochrome, square-edged website/API when the game listener starts. Players can create accounts, sign in, chat and manage social features. Operators get bootstrap setup, role-restricted administration, moderation and diagnostics.

Accounts use salted PBKDF2 hashes, expiring opaque sessions, revocation and bounded request rates. Short-lived game tickets bind network peers to authenticated accounts. Trusted player positions supply proximity routing. Data lives outside Assets/webroot in an operator-owned SQLite database, using the operating system's SQLite runtime.

Enroll multiple game servers with a canonical hub to share accounts, Communities and deployment-wide Global chat. Enrollment credentials are revocable; linked nodes publish trusted presence and install acknowledged game tickets. The operator provides hosting, backups, certificates and network configuration. No SocketJack-operated subscription or universal matchmaking service is required or included.

### Optional Opus voice

Enable voice capture explicitly, choose an authorized Proximity, Party, Raid, Community, Whisper or Global channel and use push-to-talk or your game's controls. Managed Opus encoding, per-speaker jitter buffers, sequence filtering, packet-loss concealment, mute controls and bounded queues are included.

Native gameplay voice uses unreliable frames. Account-backed voice uses a separate authenticated binary WSS connection and can span enrolled servers for authorized deployment channels. Old audio is dropped under pressure instead of building a long delay. No recording/transcription service is included. The trusted server can relay the audio; this is not end-to-end encryption.

### UDP, TCP, WebSocket and self-hosted relays

UDP uses reliable unordered delivery by default, with explicit ordered, unreliable and sequenced choices. TCP is available as an ordered stream. Native WS/WSS and the supplied WebGL bridge support browser-compatible gameplay.

Packet budgeting reserves IP, transport, reliability, object-frame and session headers. Complete reserved UDP/IP frames remain at most 998 bytes under the configured budget. Larger logical values alone split into smaller frames and rebuild with bounded memory/time limits. The datagram relay reserves its additional authenticated envelope in that same budget.

Self-hosted authenticated WebSocket and UDP relays include allocated credentials, bounded quotas and expiration/revocation. Direct UDP/TCP are plaintext; the UDP envelope authenticates routing without encrypting payloads. Use configured TLS/WSS or an appropriate secure network where confidentiality is needed. Router mapping cannot bypass carrier-grade NAT or firewall policy.

### Gameplay and lifecycle helpers

Use stable prefab IDs, spawn points, local camera/audio/input gates, baseline-ready callbacks, ownership transfer, placed scene objects, trusted scene catalogs/transitions and optional pooling. Starting components cover server-owned health, doors, pickups, projectiles and selected Animator parameters/triggers. State and one-shot events have separate lifecycles so late joins do not replay old trigger effects.

The package includes the sliding-cube Physics Arena and complete native 3D/2D account-backed samples with chat/voice controls. Offline guides cover single-player conversion, the module graph, deployment, authority, custom serializers and release-one migration.

### Unity compatibility and dependencies

Unity 6.3 LTS is the primary support target; Unity 2022.3 LTS is the compatibility bracket. Tested Editors: 6000.3.27f2 and 2022.3.17f1. Use .NET Standard 2.1 API compatibility. Networking supports Built-in, HDRP, URP and Custom RP independently of the renderer; assign and retain compatible sample materials/shaders for your pipeline.

Actual native Windows x64 Mono checks cover UDP/TCP, 3D/2D physics, authority, lifecycle, scenes, pooling, precision and audio DSP. Actual Unity 2022.3 WebGL/IL2CPP gameplay checks cover direct and relayed browser WebSocket paths and large-value reconstruction. These do not establish Unity 6 WebGL, mobile/console, physical microphone, public-WAN or production-capacity acceptance. See the included validation notes for the precise checked boundaries.

Native account/site hosting supports Windows and Linux. Windows uses OS winsqlite3; Linux requires libsqlite3.so.0. Public HTTP/WebSocket services require valid TLS certificates/origin configuration. Browser clients cannot host listeners or open raw UDP/TCP sockets. A 2,000-peer transport check is not a guarantee of 2,000 mutually visible physics players; measure and partition your actual workload.

The base runtime has no third-party NuGet dependencies. Optional Web modules use the MIT System.Text.Json family; optional Voice uses managed Concentus/Opus under its included permissive license. See Third-Party Notices and the included upstream notices. Unity engine, SRP, publisher tools, NAudio and native SQLite binaries are not redistributed. Source: https://github.com/hheiroww/SocketJack

## Release notes

Release two expands SocketJack into a complete optional multiplayer toolkit:

• Added an undoable existing-prefab setup wizard, stable registration, automatic player spawning, local-player/input helpers and menu actions.
• Added real Rigidbody2D platformer/top-down prediction, alongside 3D prediction and custom motor integration.
• Added Full/Balanced/Compact/custom quantization, per-member precision and opt-in server-selected adaptive policy.
• Added ownership epochs, per-view stale-input rejection, readiness callbacks, placed scene objects, trusted scene transitions, pooling and gameplay helper components.
• Added shared spatial interest, bounded replication work and measured transport/latency diagnostics.
• Added native/browser WebSocket gameplay plus authenticated self-hosted WebSocket and UDP relays with complete packet-envelope budgeting.
• Added operator-owned accounts, SQLite persistence, ticketed game admission, automatic native website/API hosting and enrolled multi-server federation.
• Added MMO/cooperative/arena chat presets, colored channels, structured links, party/raid, persistent Communities and moderation.
• Added optional managed Opus voice with explicit capture/PTT/mute, bounded jitter/PLC and authenticated linked-server account voice.
• Added separate NuGet modules, complete Unity distribution, native 3D/2D examples and detailed offline setup/deployment guidance.

Migration: release two uses session protocol 3 and member protocol 2. Upgrade every server and player together; release-one sessions cannot mix with release two. Preserve stable prefab/scene IDs, review schemas and rebuild all players. Binary formatter (SocketJack) remains the default; UDP and TCP remain available.

## AI-assistance disclosure

OpenAI Codex assisted source, documentation and tests. OpenAI ImageGen assisted the existing SocketJack logo's monochrome color adaptation. The package was refined with managed, Editor, native-player and browser checks; validation boundaries are documented.

## Submission review settings

Create a separate SocketJack Premium product at USD 9.99; preserve the existing Basic product and its listing. Upload through Unity 6 so the primary package records Unity 6000.3.27f2; attach the separately authored 2022.3 archive for that compatibility bracket. Confirm Built-in, HDRP, URP and Custom RP compatibility flags. Use the existing SocketJack logo in the Unity monochrome scheme. Do not claim production scaling, Internet WSS or untested platform/audio acceptance from loopback receipts. Run the official uploader validator against the exact complete archive before submission.
