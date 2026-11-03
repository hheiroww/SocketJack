# SocketJack.Unity quick start

SocketJack.Unity provides a multiplayer runtime, Unity components and a playable physics example. Your game supplies its networked prefabs, player intent, simulation rules and state. Existing single-player scripts require integration with those rules.

## Import and try the example

1. Import the package into Unity 2022.3 with the .NET Standard 2.1 API compatibility level. The example uses the Built-in Render Pipeline, Rigidbody physics and Unity's legacy input API. Its menu uses IMGUI.
2. Open `Assets/SocketJack.Unity/Samples/Physics/Scenes/SocketJackPhysics.unity` and enter Play Mode.
3. Choose **Host + play**. Build a second player or open the example in another Unity project and choose **Join server** at `127.0.0.1:7777`.
4. Use WASD/arrow keys to move and Space to jump. One process can also run as a dedicated server.

All files are under `Assets/SocketJack.Unity`. Importing the package adds assets; it does not replace project settings. The example runs from its demo scene, rather than starting in unrelated scenes.

## Add networking to your game

Add `NetworkManager` to a scene object. Register the same network prefab list in the same order in every build. Add `NetworkIdentity` to those prefabs. Use `SocketJackObject` for the supplied Rigidbody prediction and repair workflow, or `NetworkTransform` for server transform replication; use one synchronization method per object.

Start a server, host or client from your game menu. Spawn network objects on the server through `NetworkManager.Spawn`; assign the connected player's peer ID as owner when appropriate. Game input is sent for owned objects and is checked against ownership on the server. Your simulation must additionally validate movement, collisions, cooldowns and other game rules. Despawn through the networking manager.

Choose **Host / Peer** for a player-hosted session (`StartHost` / `StartPeer`), or **Dedicated Server / Client** for a server process and its clients (`StartServer` / `StartClient`). Topology is independent of UDP/TCP. Selecting dedicated topology enables **Server Authority** by default. A network object's **Authority** can inherit the manager policy or select server/owner authority; the server records that choice when it spawns the object. Owner authority permits that object's owner to submit canonical state, so use it only where your game trusts the owner.

Choose the appropriate **Movement Mode** on `SocketJackObject`:

| Mode | Rigidbody simulation | Colliders |
| --- | --- | --- |
| Static w/Colliders Enabled | Kinematic | Enabled |
| Static | Kinematic | Disabled |
| Physics Enabled | Dynamic | Disabled |
| Physics w/Colliders Enabled | Dynamic | Enabled |

The component preserves existing mass, drag, material, constraints, gravity and interpolation configuration. The mode controls `isKinematic` and collider enabled state. Tune prediction, repair and force settings in the same Inspector. The selected release's API details are in `Documentation/Runtime.md`. Call `Shutdown` before changing manager settings or starting again.

## Transport and Internet setup

UDP is the normal realtime transport. Reliable delivery is unordered unless ordering is requested explicitly. TCP is available for an ordered stream; requesting an unreliable mode over TCP does not remove stream blocking.

Large logical messages are split automatically into bounded frames only when one frame is too small. Included UDP object frames stay below 999 bytes for the configured full IP packet budget. This limit includes reserved headers. It is separate from the configurable logical-message limit.

For LAN or Internet play, use a reachable server address and permit the selected UDP or TCP port through the server firewall. The manager attempts automatic UPnP port mapping when enabled, reports its status and renews/releases the mapping lease. Mapping depends on the router, network and current transport settings; it cannot bypass carrier-grade NAT or a policy that blocks inbound connections. The package does not include a hosted relay, matchmaking service or account service. Check the manager's mapping status and provide manual forwarding, a reachable dedicated server or a separate relay when needed.

The included transports carry plaintext game data. Validate short-lived game tickets using the connection validator when admission is restricted. Do not transmit reusable passwords. Use a separately secured transport or tunnel when confidentiality is required.

## Capacity and platform limits

The default peer cap is 4,096. This is a configured resource limit, not a guaranteed production player count. Partition rooms/interest, size bandwidth and test the actual game workload. The supplied room API avoids broadcasting every object to every peer. A process supporting 2,000 loopback transport peers does not establish that a particular physics world supports 2,000 mutually visible players.

The physics example predicts local movement and applies sparse repairs. Unity physics is not deterministic across peers; prediction is not full rollback or complete collision replay. Input/keyframe sizes, repair settings and measurement boundaries are documented in `Documentation/PhysicsSample.md`.

Native Windows Mono player acceptance and Unity 2022.3 compilation are the initial validation targets. Mobile, consoles, IL2CPP and other player platforms need platform-specific acceptance. WebGL does not expose raw UDP/TCP sockets; it needs a browser transport bridge.

## Contents and support

`Runtime/Plugins` contains the portable managed DLL; `Runtime/Components` contains editable Unity component sources and linker configuration. `Samples/Physics` contains the example scene, material, scripts and explicit build helper. The package uses Unity modules for physics, IMGUI and JSON serialization, with no third-party runtime NuGet dependencies.

Source, issues and release information: [SocketJack on GitHub](https://github.com/hheiroww/SocketJack). License: `Documentation/LICENSE.txt`. Development used AI-assisted code generation and refactoring; the existing SocketJack logo also received an AI-assisted monochrome color adaptation. Functional behavior is validated by the accompanying managed and actual-player checks.
