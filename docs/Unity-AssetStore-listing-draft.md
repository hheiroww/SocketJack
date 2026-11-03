# SocketJack.Unity — listing draft

Suggested public listing copy for the package. Complete publisher-specific fields and compatibility validation before submission.

## Suggested title

SocketJack.Unity Multiplayer & Physics Demo

## Short description

Add explicit UDP/TCP networking, owned network objects, room visibility and Rigidbody prediction to a Unity game. Includes an editable multiplayer physics demo and a dedicated-server runtime.

## Description

SocketJack.Unity gives your game a practical multiplayer starting point. Register network prefabs, select a transport, start a server/host/client and connect your input and simulation rules to owned network objects. A playable physics example demonstrates local prediction, relayed intent and sparse state correction.

Included features:

- UDP with reliable unordered delivery by default, explicit ordered delivery and sequenced state; TCP support for an ordered stream.
- Automatic bounded framing for larger logical messages, with UDP object frames below 999 bytes under the configured full packet budget.
- Networked object lifecycle, ownership checks, room visibility and streamed late-join baselines.
- Separate Host / Peer and Dedicated Server / Client modes, manager authority settings and per-object server/owner authority.
- Four Inspector movement modes, preserving existing Rigidbody mass/drag/material and Collider settings apart from the selected simulation/collider toggles.
- Automatic UPnP router mapping with status, renewal and release on supported networks, plus manual port forwarding when needed.
- Main-thread Unity components, Inspector settings, portable .NET Standard 2.1 runtime and an actual Unity physics sample.
- Configurable queue, peer, message, tick and snapshot limits; no thread per connected player.

The default 4,096-peer cap is a resource setting. Capacity depends on the game's simulation, visibility and bandwidth; benchmark the actual workload. The included 2,000-peer loopback transport exercise is not a guarantee for 2,000 physics players in one world.

Game-specific integration is required. This package does not provide hosted relay, matchmaking, accounts, universal anti-cheat, full physics rollback or a browser transport. Native UDP/TCP traffic is plaintext. Router mapping, where enabled, depends on network support and does not bypass carrier-grade NAT.

## Technical details

- Initial Editor validation: Unity 2022.3.17f1.
- Runtime target: .NET Standard 2.1; no third-party runtime NuGet dependencies.
- Sample render pipeline: Built-in; no claim of URP/HDRP material acceptance.
- Sample Unity modules: physics, IMGUI and JSON serialization; legacy input API.
- Initial actual-player validation: Windows x64 with Mono. Other backends/platforms require further acceptance.
- Runtime/API documentation, editable component source, license and limitations are included. The portable runtime is a managed DLL; its source is available in the linked public repository.

## AI assistance disclosure draft

OpenAI Codex was used for functional code generation, refactoring, documentation and test development. OpenAI ImageGen was used to adapt the existing SocketJack logo's colors to a monochrome variant while retaining its socket design. The generated code was iteratively refined, compiled and checked with managed tests and actual Unity players. The resulting code and artwork are maintained as part of SocketJack.

## Publisher fields to review

Suggested category: Tools / Network. Suggested tags: multiplayer, networking, UDP, TCP, physics, dedicated server, prediction, rooms.

Pricing: TBD. Unity's [publisher FAQ](https://assetstore.unity.com/publishing/publish-and-sell-assets) currently gives USD 4.99 as the minimum paid price.

Publisher display name, support contact, description, key art, screenshots/video, final compatibility claims and license treatment must match the intended publisher account and submitted artifact. Do not invent account or support information. The included MIT license must remain intact; any Asset Store license presentation should be checked against Unity's submission requirements before upload.

License notice for the description: includes first-party SocketJack code under the MIT license; see `Documentation/LICENSE.txt` and `Documentation/Third-Party Notices.txt`. No third-party runtime library is redistributed in the asset package.
