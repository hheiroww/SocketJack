# SocketJack Premium for Unity

Bring multiplayer to the game you already have. **SocketJack Premium** combines the SocketJack networking foundation with a complete Unity workflow: prefab setup, 3D/2D prediction, configurable gameplay synchronization, MMO chat and Communities, accounts, a server-hosted website, relays and voice.

Premium is a **separate Asset Store offering with a US list price of $9.99**. The existing basic asset remains separate. Store availability depends on Unity's publication/review status; a prepared package or public source commit alone is not evidence of an approved public listing.

## Start here

| Your next task | Guide |
| --- | --- |
| Decide between Basic and Premium | [Compare editions](comparison.md) |
| Import the asset and connect two players | [Installation and first connection](installation.md) |
| Convert an existing single-player prefab | [Convert your game](convert-existing-game.md) |
| Configure 3D/2D movement and authority | [Physics and authority](physics-and-authority.md) |
| Select fields/properties and tune precision | [Gameplay synchronization](game-sync.md) |
| Coordinate levels and reuse objects | [Scenes and pooling](scenes-and-pooling.md) |
| Add accounts and the built-in website | [Accounts and hosting](accounts-and-hosting.md) |
| Configure MMO channels and Communities | [Chat and Communities](chat-and-communities.md) |
| Add opt-in voice | [Voice](voice.md) |
| Connect relays and multiple game servers | [Relays and linked servers](relays-and-linked-servers.md) |
| Diagnose a connection or replication problem | [Troubleshooting](troubleshooting.md) |

Follow installation and a two-player local test first. Add accounts/chat next, then voice and Internet hosting. This keeps each step small enough to verify before adding another service.

## What does “make my game multiplayer” mean?

SocketJack supplies the connection and synchronization machinery. Your game supplies its levels, player prefab, input and rules. The setup wizard connects those pieces; it cannot infer every rule in arbitrary single-player code.

| Piece | What it does | Where it belongs |
| --- | --- | --- |
| `NetworkManager` | Runs a host/server or peer/client session and its registered network objects | One scene/session services object |
| `NetworkPrefabRegistry` and `NetworkPlayerSpawner` | Give prefabs stable IDs and create the correct player for an admitted peer | Registry asset and services object |
| `NetworkIdentity` | Tracks the shared object ID, owner, room and baseline readiness | Each networked root prefab |
| `SocketJackObject` / `SocketJackObject2D` | Predict and synchronize 3D/2D Rigidbody movement | The moving prefab |
| `NetworkTransform` | Interpolates simpler transform state | A prefab without a competing physics synchronizer |
| `SocketJackMemberSync` | Replicates explicitly selected gameplay values | A networked prefab with data to share |
| `NetworkLocalPlayer` / `NetworkInputBridge` | Gate local cameras/input and connect input actions | The player prefab |
| Optional Web/Voice components | Supply account UI, chat, website hosting and authorized voice | Services/UI objects |

Adding `SocketJackObject` alone synchronizes a correctly spawned object's motion. A whole game also needs session startup, prefab registration, player spawning and ownership-aware game code. Premium supplies those helpers so you do not have to build that surrounding workflow from scratch.

## Unity and rendering support

**Unity 6.3 LTS is the primary support bracket. Unity 2022.3 LTS is the compatibility bracket.** The checked Editors are 6000.3.27f2 and 2022.3.17f1. Select .NET Standard 2.1 API compatibility.

The networking code is independent of Built-in, HDRP, URP and Custom RP. Use materials compatible with your active renderer and retain their shaders in player builds. The SDK does not replace your rendering settings or install an SRP package for you.

Native game/account tests use real Windows x64 Mono players. Native account/site hosting supports Windows and Linux, using OS SQLite. Actual Unity 2022.3 WebGL/IL2CPP gameplay uses the browser WebSocket bridge. Other platforms/backends and public-network capacity require their own acceptance. See the validation details in the individual guides; a transport echo benchmark does not establish a production physics-player count.

## Choose the features you need

Use the networking core alone, or enable the complete MMO workflow. A game does not need accounts, a website or a microphone just to synchronize a cube. Microphone capture is opt-in. Hosting is operator-owned; no SocketJack-operated account/relay/voice subscription is required or supplied.

The first-party source and NuGet modules remain public. Premium's value is the complete, documented Unity distribution and its integrated workflows; it does not use a runtime paywall or claim exclusive ownership of upstream open-source code.

## Version compatibility

Premium's second-release generation uses session protocol 3 and member protocol 2. Update every server and player together when migrating from the basic release-one distribution. Preserve stable prefab/scene IDs and review your member schemas. Keep one SDK installation per Unity project to avoid duplicate assemblies and scripts.
