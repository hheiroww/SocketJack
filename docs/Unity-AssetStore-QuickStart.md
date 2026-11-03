# SocketJack.Unity setup and single-player conversion guide

**Primary support target: Unity 6.3 LTS, with a Unity 2022.3 LTS compatibility bracket.** Unity 6 validation is pending; the target is not a claim of completed testing. The verified configuration for this release is Unity **2022.3.17f1**, including Editor import/compilation and actual Windows Mono players. Other Editor versions, render pipelines, platforms and scripting backends require their own acceptance checks.

SocketJack.Unity supplies connections, replicated objects, ownership and state delivery. Your game supplies its levels, player prefabs, input and game rules. This guide converts a small single-player Rigidbody game into a host-and-peer game, then explains where custom integration is needed.

## 1. Import and configure your project

Choose the distribution format for your project:

- **Asset Store `.unitypackage`:** import it into Unity. It adds the library, component sources, demo scene and offline documentation under `Assets/SocketJack.Unity`, without replacing project settings or launching the demo in unrelated scenes.
- **NuGet `.nupkg`:** includes the portable DLL, Unity component source content, `README.md` and this guide as root `QuickStart.md`. It does **not** contain the demo scene or automatically import assets into Unity. Open it as a ZIP archive and copy `lib/netstandard2.1/SocketJack.Unity.dll` into `Assets/Plugins/SocketJack.Unity/`; copy the `.cs` files and `link.xml` from `contentFiles/any/any/SocketJack.Unity/` into `Assets/SocketJack.Unity/`. The NuGet `README.md` gives the full manual-copy instructions. Get the demo separately from the Asset Store package or the repository's [Examples/UnityPhysics project](https://github.com/hheiroww/SocketJack/tree/codex/unity-multiplayer/Examples/UnityPhysics).
- **Standalone .NET server/application:** use `dotnet add package SocketJack.Unity`. This installs a .NET dependency; it is not a Unity asset-import command.

In Unity, select **.NET Standard 2.1** under **Edit > Project Settings > Player > Other Settings > Configuration > API Compatibility Level**. See Unity's [.NET compatibility documentation](https://docs.unity3d.com/6000.0/Documentation/Manual/dotnet-profile-support.html) for the setting. This guide's `Samples/Physics` and `Documentation/...` paths refer to the Asset Store layout; the runtime documentation is the NuGet `README.md` and the sample documentation is in the repository's example README.

The supplied physics sample uses 3D Rigidbody physics, IMGUI for its menu, Unity's legacy `UnityEngine.Input` API and Built-in Render Pipeline materials. For its keyboard controls, set **Active Input Handling** to **Input Manager (Old)** or **Both**, including in Unity 6. A project using only **Input System Package (New)** should disable **Read Keyboard** on its own network prefab and feed `SocketJackObject.SetInput` from its existing actions, as shown below. The package does not install the Input System or change input settings for you. Unity documents these choices in the [Input System installation guide](https://docs.unity3d.com/Packages/com.unity.inputsystem@1.11/manual/Installation.html).

The networking runtime does not depend on a render pipeline. The sample's Standard material does: in a URP or HDRP project, supply compatible sample materials and adapt its material creation before judging the visuals. A pink Built-in material is a shader compatibility issue, not evidence that peers failed to connect. See Unity's [URP material conversion guide](https://docs.unity3d.com/6000.0/Documentation/Manual/urp/upgrading-your-shaders.html). URP/HDRP sample visuals are not part of the verified matrix above.

## 2. Try the included game first

1. Open `Assets/SocketJack.Unity/Samples/Physics/Scenes/SocketJackPhysics.unity` and enter Play Mode.
2. Select **Host / Peer** on both instances. On the first, choose **Host + play**.
3. Build a second player, or use a separate Unity project containing the same package. On that instance choose **Join host as peer**, using `127.0.0.1:7777` when both run on the same computer.
4. Move with WASD/arrow keys and jump with Space. Each instance should control its own player and see the other player's movement.

For a separate server process, select **Server / Client** instead: choose **Dedicated server** on the server and **Join server as client** on each client. Keep the topology selection consistent. A host also runs a local player client; a dedicated server does not.

The demo constructs its level and players from the explicit `PhysicsGame` scene object. Its scripts and build instructions are documented in `Documentation/PhysicsSample.md`.

## 3. Understand the components

There is one session manager and a small set of components on each replicated prefab:

| Component | Where it belongs | What it does |
| --- | --- | --- |
| `NetworkManager` | One scene object | Starts a host/server or peer/client, pumps networking and instantiates/despawns registered prefabs. |
| `NetworkIdentity` | Each network object | Holds its network ID, owner, room and authority policy. |
| `SocketJackObject` | A Rigidbody object using the supplied motor | Sends owner movement intent, simulates local physics and applies sparse authoritative repairs. Adding it automatically adds required `NetworkIdentity` and `Rigidbody` components if absent. |
| `NetworkTransform` | An object using your own movement logic | Replicates position, rotation and local scale; observers interpolate received transforms. Adding it automatically adds its required `NetworkIdentity` if absent. |

Use **one synchronization component per object**. Do not combine `SocketJackObject` and `NetworkTransform` on the same prefab. `NetworkTransform` does not supply a Rigidbody motor or automatically decide how your existing controller should simulate.

Component scripts use `SocketJack.Multiplayer.Components`; the portable runtime uses `SocketJack.Multiplayer`. The product, NuGet package and DLL remain named `SocketJack.Unity`.

## 4. Convert a single-player Rigidbody player

Suppose your game already has a floor, a player mesh, a Rigidbody, a collider and a script that reads movement input and applies forces.

1. Keep the existing level, player mesh, collider and Rigidbody. Add `SocketJackObject` to the player; Unity supplies `NetworkIdentity` if needed.
2. Select **Physics w/Colliders Enabled** as its Movement Mode. Leave Authority at **Inherit Manager** for this example.
3. Disable the old script's movement-force/jump implementation. `SocketJackObject` supplies its own force/jump motor; running both would apply movement twice. Keep unrelated gameplay or visuals in separate scripts.
4. Tune movement force, Force Mode, maximum speed, jump velocity and correction settings to suit your game. The component does not translate an arbitrary controller's behavior automatically.
5. For the first test, leave **Read Keyboard** enabled to use the built-in keys. For your own input, use the bridge in section 6.
6. Save the player as a prefab. Remove or disable the old scene player and the single-player code that instantiates it. The server will spawn network players instead.
7. Keep a shared scene camera for the first test, or guard each player's camera and audio listener by ownership as shown below.

Choose the Rigidbody mode deliberately:

| Movement Mode | Rigidbody simulation | Child colliders |
| --- | --- | --- |
| Static w/Colliders Enabled | Kinematic | Enabled |
| Static | Kinematic | Disabled |
| Physics Enabled | Dynamic | Disabled |
| Physics w/Colliders Enabled | Dynamic | Enabled |

The mode controls `isKinematic` and collider enabled state, including colliders on child objects. Native mass, drag/damping, angular drag/damping, material/friction, gravity, constraints, collision detection and interpolation settings remain yours. A mode that disables colliders also disables the collisions your controller may rely on. Enable Rigidbody interpolation in the Rigidbody Inspector when desired. An optional child renderer transform can be assigned as **Visual** for correction smoothing while the Rigidbody and collider remain on the root.

For an existing vehicle, CharacterController, animation-driven motor or custom Rigidbody controller that you must preserve, integrate its input and authoritative simulation explicitly instead of stacking the supplied motor on top. `NetworkTransform` and the runtime input/state APIs provide the foundation; they do not convert that controller's rules for you.

## 5. Register, spawn, host and join

Create an empty scene GameObject named `Multiplayer`. Add the following script as `GameMultiplayer.cs`; its required component adds `NetworkManager`. Assign your player prefab in the Inspector. Put the same script, prefab and level in every build.

```csharp
using SocketJack.Multiplayer.Components;
using UnityEngine;

namespace MyGame
{
    [RequireComponent(typeof(NetworkManager))]
    public sealed class GameMultiplayer : MonoBehaviour
    {
        [SerializeField] private GameObject playerPrefab;
        private NetworkManager network;
        private int playerNumber;

        private void Awake()
        {
            network = GetComponent<NetworkManager>();
            network.Topology = NetworkTopology.HostPeer;
            network.ServerAuthority = true;
            network.RegisterPrefabs(playerPrefab);
            network.PeerConnected += SpawnPlayer;
        }

        private void SpawnPlayer(ulong peerId)
        {
            var position = new Vector3((playerNumber++ % 8) * 2f, 1f, 0f);
            network.Spawn(1, position, Quaternion.identity, ownerId: peerId);
        }

        public void Host() => network.StartHost();

        public void Join(string ipAddress)
        {
            network.Address = ipAddress;
            network.StartPeer();
        }

        public void Stop() => network.Shutdown();

        private void OnDestroy()
        {
            if (network != null)
                network.PeerConnected -= SpawnPlayer;
        }
    }
}
```

Connect your menu's Host button to `Host()` and its Join action to `Join(ipAddress)`. Use `127.0.0.1` for a second process on the same machine; use the host's reachable IP for another machine. The current manager parses an IP address, not a hostname. Both use port `7777` and UDP by default; configure matching settings before starting.

`RegisterPrefabs` assigns **one-based** IDs: the first prefab is `1`, the second is `2`, and so on. Register the same list in the same order in every build. `Spawn` runs only on the server/host, creates the local object and causes clients to instantiate their corresponding prefab. Do not also call `Instantiate(playerPrefab)` independently on joining clients. The owner ID identifies the player allowed to send input for that object. Late joiners receive existing object baselines, and disconnected owners' network objects are despawned.

Only the listener raises `PeerConnected`, so the bootstrap spawns once per accepted player, including the host's local client. **Host / Peer** still has one central listening host; it is not a mesh of direct connections between every player.

For dedicated topology, set `network.Topology = NetworkTopology.DedicatedServerClient`, retain the same registration and spawn handler, and use `network.StartServer()` on the server or `network.StartClient()` on clients. Select the matching topology before those calls. The full spawn signature is `Spawn(uint prefabId, Vector3 position, Quaternion rotation, ulong ownerId = 0, int roomId = 0)`; an owner ID of `0` creates a server-owned object. Remove network objects with `network.Despawn(objectId)`. Call `Shutdown()` before changing manager settings or starting a new session.

## 6. Connect your input and local camera

Only the owning player's instance should read local gameplay input, enable its player camera/audio listener or accept local UI commands. Do not disable a controller permanently in `Awake` based on ownership: its identity may not be bound yet. Check `NetworkIdentity.IsOwner` after binding, such as in `Update`.

The optional audio-listener example below requires Unity's built-in **Audio** module, which ordinary game templates include. If your project deliberately removes unused built-in modules, enable Audio before compiling that example or remove its audio-listener field and assignments.

For a project using the Input System, add this optional `PlayerNetworkInput.cs` bridge to the player prefab:

1. In your existing Input Actions asset, create **Move**, with Action Type **Value** and Control Type **Vector2**. Bind a WASD 2D Vector composite and/or gamepad left stick.
2. Create **Jump**, with Action Type **Button**, and bind Space and/or the gamepad's jump button.
3. Assign those action references to **Move Input** and **Jump Input** on the bridge. Assign the prefab's camera and audio listener if it has them.
4. The bridge disables **Read Keyboard**, reads actions only for the local owner and calls `SetInput`. It creates its own action copies so removing a remote player cannot disable another player's actions. A `PlayerInput` component is not required for this small example.

The Input System code is guarded by `ENABLE_INPUT_SYSTEM`; it adds no hard Input System dependency to the networking library. When that package is not enabled, the separate `#else` fallback uses legacy **Horizontal**, **Vertical** and **Jump** bindings and requires **Input Manager (Old)** or **Both**. The shortest legacy setup remains leaving `SocketJackObject.ReadKeyboard` enabled without this bridge.

```csharp
using SocketJack.Multiplayer.Components;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace MyGame
{
    [RequireComponent(typeof(SocketJackObject))]
    public sealed class PlayerNetworkInput : MonoBehaviour
    {
        [SerializeField] private Camera playerCamera;
        [SerializeField] private AudioListener playerAudio;
#if ENABLE_INPUT_SYSTEM
        [SerializeField] private InputActionReference moveInput;
        [SerializeField] private InputActionReference jumpInput;
        private InputAction moveAction;
        private InputAction jumpAction;
#endif
        private NetworkIdentity identity;
        private SocketJackObject movement;

        private void Awake()
        {
            identity = GetComponent<NetworkIdentity>();
            movement = GetComponent<SocketJackObject>();
            movement.ReadKeyboard = false;
            if (playerCamera != null) playerCamera.enabled = false;
            if (playerAudio != null) playerAudio.enabled = false;
        }

        private void Update()
        {
            bool localPlayer = identity.IsOwner;
            if (playerCamera != null) playerCamera.enabled = localPlayer;
            if (playerAudio != null) playerAudio.enabled = localPlayer;
            if (!localPlayer)
            {
                DisableActions();
                return;
            }

#if ENABLE_INPUT_SYSTEM
            if (moveAction == null && moveInput != null)
                moveAction = moveInput.action?.Clone();
            if (jumpAction == null && jumpInput != null)
                jumpAction = jumpInput.action?.Clone();
            moveAction?.Enable();
            jumpAction?.Enable();
            Vector2 move = moveAction == null
                ? Vector2.zero : moveAction.ReadValue<Vector2>();
            movement.SetInput(move.x, move.y,
                jumpAction != null && jumpAction.WasPressedThisFrame());
#else
            // Requires Input Manager (Old) or Both.
            movement.SetInput(Input.GetAxisRaw("Horizontal"),
                Input.GetAxisRaw("Vertical"), Input.GetButtonDown("Jump"));
#endif
        }

        private void DisableActions()
        {
#if ENABLE_INPUT_SYSTEM
            moveAction?.Disable();
            jumpAction?.Disable();
#endif
        }

        private void OnDisable()
        {
            DisableActions();
            if (movement != null) movement.SetInput(0f, 0f);
            if (playerCamera != null) playerCamera.enabled = false;
            if (playerAudio != null) playerAudio.enabled = false;
        }

        private void OnDestroy()
        {
#if ENABLE_INPUT_SYSTEM
            moveAction?.Dispose();
            jumpAction?.Dispose();
#endif
        }
    }
}
```

If your existing controller already owns its input actions, retain that action lifecycle instead of adding a second reader. From its owner-only input path, pass the move vector and jump press to `SocketJackObject.SetInput(horizontal, vertical, jump)`. Continue sending zero movement when input is released. Replace its old movement-force calls with this intent submission; do not retain both motors.

Guard camera-look, interaction, inventory UI and other local scripts with the same ownership rule. A shared level camera needs no per-player toggle. Avoid keeping both a shared audio listener and an enabled local-player listener.

## 7. Choose authority and replicate game rules

With **Server Authority** enabled, owner input predicts movement locally, the server simulates canonical state and sparse keyframes repair divergence on other instances. The server checks input ownership and bounded message formats. Your game must still validate movement rules, permissions, damage, cooldowns and other commands.

An object's Authority can **Inherit Manager**, select **Server**, or select **Owner**. The server records that policy when spawning; respawn after changing it. Owner authority permits the object's owner to submit canonical state. The server checks ownership and format and can apply `OwnerStateValidator`, but a valid format alone cannot prove a movement or combat claim is honest. Server-owned objects retain server authority. Use server authority where the game must enforce trusted rules.

The supplied adapters synchronize their documented physics or transform state. They do not automatically synchronize health, inventory, animation parameters, quests, score, NPC decisions or every C# field. Define an explicit game protocol using the runtime's bounded binary state and `SendMessage` APIs. Keep authoritative durable state on the server and provide it to late joiners; an ephemeral custom event alone is not a saved-state baseline. Do not overwrite a `SocketJackObject` physics payload with unrelated health bytes. Application message ID `60001` is reserved by its physics input relay. See `Documentation/Runtime.md` for custom messages, state limits, delivery modes and rooms.

## 8. Keep the level and simulation consistent

Start all peers in the same game level with compatible builds, prefab registration and static geometry. Keep physics timestep, gravity, collision layers and relevant prefab settings consistent. The sample uses a shared static level; only network-spawned objects are instantiated through the session.

SocketJack does not copy a scene, terrain or asset bundle from the host and does not automatically call `SceneManager.LoadScene` on peers. Your game must coordinate level selection, scene readiness and scene transitions before spawning into the new level. Decide whether the manager survives a transition and how its session/objects are shut down or migrated. Room visibility is not automatic scene loading or a substitute for game-level readiness.

## 9. Verify your converted game

1. Run the same scene/build in two processes. Host on the first and join as a peer on the second. Check for import, script and missing-prefab errors.
2. Confirm each sees two players and controls only its own. Check one active local camera/listener per player process and no duplicate original scene player.
3. Move, stop, jump and collide with the shared level. Verify remote movement, stopping and repairs; tune Rigidbody and networking settings against your actual motor.
4. Join a third instance after players have moved. It should receive the existing objects' current baselines. Disconnect one owner and confirm its network player disappears elsewhere.
5. Repeat with a dedicated server plus clients, then across two LAN machines. Test delay/loss, reconnects, rejected commands and scene transitions before Internet deployment.

The supplied physics example predicts local movement and applies sparse repairs. Unity physics is not deterministic across peers; this is not full rollback or complete collision replay. Inspect behavior and measured errors with your actual geometry and player workload. Input/keyframe sizes, repair settings and measurement boundaries are in `Documentation/PhysicsSample.md`.

## 10. Transport, Internet and capacity

UDP is the normal realtime transport. Reliable delivery is unordered unless ordering is requested explicitly. TCP is available for an ordered stream; requesting an unreliable mode over TCP does not remove stream blocking. Large logical messages are split automatically into bounded frames only when one frame is too small. Included UDP object frames stay below 999 bytes for the configured full IP packet budget, including reserved headers; logical-message limits are configured separately.

Permit the selected UDP or TCP port through the host/server firewall. When enabled, the manager attempts automatic UPnP port mapping and reports/renews/releases its lease. Only the host/server requests a listening-port mapping; peers/clients initiate outbound connections. Mapping depends on the router and network and cannot bypass carrier-grade NAT or an inbound-blocking policy. There is no bundled hosted relay, matchmaking or account service. Provide manual forwarding, a reachable dedicated server or a separate relay when necessary.

The included transports carry plaintext game data. For restricted admission, validate short-lived game tickets with `ConnectionValidator`; do not send reusable passwords. Use a separately secured transport or tunnel where confidentiality is required.

The default peer cap of 4,096 is a configured resource limit, not a guaranteed production player count. Partition rooms/interest, budget bandwidth and test the actual simulation. `SetPeerRoom` and `SetObjectRoom` on the server control room visibility; they do not load scenes or authorize player actions. The host renders its authoritative world once, so game-specific local rendering filters are needed to hide other rooms from the host player. A 2,000-peer loopback transport check does not establish that a physics world supports 2,000 mutually visible players.

Unity 6 acceptance remains pending as stated at the beginning of this guide. Mobile, consoles, IL2CPP and other player targets also require platform-specific acceptance. Web builds lack raw UDP/TCP sockets and need a browser transport bridge.

## Contents and support

In the Asset Store layout, `Runtime/Plugins` contains the portable managed DLL; `Runtime/Components` contains editable Unity component sources and linker configuration. `Samples/Physics` contains the example scene, material, scripts and explicit build helper. NuGet contents and their manual Unity installation paths are listed in section 1. The Unity integration uses modules for physics, IMGUI and JSON serialization, with no third-party runtime NuGet dependencies.

Source, issues and release information: [SocketJack on GitHub](https://github.com/hheiroww/SocketJack). License: `Documentation/LICENSE.txt`; dependency notices: `Documentation/Third-Party Notices.txt`. OpenAI Codex assisted code, documentation and tests; OpenAI ImageGen assisted the existing SocketJack logo's monochrome color adaptation. The code was iteratively refined and validated by managed and actual-player checks.
