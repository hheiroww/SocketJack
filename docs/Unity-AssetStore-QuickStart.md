# SocketJack.Unity setup and single-player conversion guide

**Unity 6 is the primary support bracket, with Unity 2022.3 LTS retained as the compatibility bracket.** The following exact configurations have passed real Editor and player checks:

| Support bracket | Verified Editor | Verified checks |
| --- | --- | --- |
| Primary: Unity 6.3 LTS | **6000.3.27f2** | Component/sample and both guide examples compile; authority checks, Windows x64 Mono build and a server/two-client physics run pass. |
| Compatibility: Unity 2022.3 LTS | **2022.3.17f1** | Component/sample and both guide examples compile; authority checks, Windows x64 Mono build and a server/two-client physics run pass. |
| Unity 6 rendering smoke | **6000.3.27f2**, URP/HDRP **17.3.0** | Actual GPU camera renders of the sample material factory in Built-in, URP, HDRP and a minimal custom SRP with its own material; no missing-shader pixels. |
| Configurable member sync | **6000.3.27f2** and **2022.3.17f1** | Six Editor Play Mode and six native-player cases per Editor: UDP/TCP, default/custom serializers, field/property authority, serialized prefab bindings, host mode and late joins. |

Both physics player checks use Built-in rendering assets and headless loopback processes for 20 seconds of client movement; neither client required a hard correction. The separate rendering smoke uses Direct3D 11 and checks shader selection, visible geometry and the material override; it is not a complete SRP player-build or custom-renderer test matrix. These checks do not establish Internet behavior or production capacity. Validate other Editor patches, platform/backends, shader stripping and your own render-pipeline configuration with your actual game.

SocketJack.Unity supplies connections, replicated objects, ownership and state delivery. Your game supplies its levels, player prefabs, input and game rules. This guide converts a small single-player Rigidbody game into a host-and-peer game, then explains where custom integration is needed.

## 1. Import and configure your project

Choose the distribution format for your project:

- **Asset Store `.unitypackage`:** import it into Unity. It adds the library, component sources, demo scene and offline documentation under `Assets/SocketJack.Unity`, without replacing project settings or launching the demo in unrelated scenes.
- **NuGet `.nupkg`:** includes the portable DLL, Unity component source content, `README.md` and this guide as root `QuickStart.md`. It does **not** contain the demo scene or automatically import assets into Unity. Open it as a ZIP archive and copy `lib/netstandard2.1/SocketJack.Unity.dll` into `Assets/Plugins/SocketJack.Unity/`; copy the `.cs` files and `link.xml` from `contentFiles/any/any/SocketJack.Unity/` into `Assets/SocketJack.Unity/`. The NuGet `README.md` gives the full manual-copy instructions. Get the demo separately from the Asset Store package or the repository's [Examples/UnityPhysics project](https://github.com/hheiroww/SocketJack/tree/codex/unity-multiplayer/Examples/UnityPhysics).
- **Standalone .NET server/application:** use `dotnet add package SocketJack.Unity`. This installs a .NET dependency; it is not a Unity asset-import command.

In Unity, select **.NET Standard 2.1** under **Edit > Project Settings > Player > Other Settings > Configuration > API Compatibility Level**. See Unity's [.NET compatibility documentation](https://docs.unity3d.com/6000.0/Documentation/Manual/dotnet-profile-support.html) for the setting. This guide's `Samples/Physics` and `Documentation/...` paths refer to the Asset Store layout; the runtime documentation is the NuGet `README.md` and the sample documentation is in the repository's example README.

The supplied physics sample uses 3D Rigidbody physics, IMGUI for its menu and Unity's legacy `UnityEngine.Input` API. For its keyboard controls, set **Active Input Handling** to **Input Manager (Old)** or **Both**, including in Unity 6. A project using only **Input System Package (New)** should disable **Read Keyboard** on its own network prefab and feed `SocketJackObject.SetInput` from its existing actions, as shown below. The package does not install the Input System or change input settings for you. Unity documents these choices in the [Input System installation guide](https://docs.unity3d.com/Packages/com.unity.inputsystem@1.11/manual/Installation.html).

The networking runtime works independently of **Built-in, HDRP, URP and Custom RP**. The sample detects Built-in/URP/HDRP and chooses a compatible shader without requiring those SRP packages in every project. Its `PhysicsGame` Inspector exposes **Sample Material**: assign a material for your active pipeline to override its generated materials. A custom render pipeline should provide its own compatible material there. The template is cloned rather than modifying your source asset.

Include the chosen material/shader in player builds. The explicit sample build helper retains its selected material through a referenced resource; when building another scene or using another build process, retain your template on the scene's `PhysicsGame` or through your own shader inclusion setup. A pink material indicates shader compatibility or stripping, so verify the active pipeline, template and build inclusion. The package does not replace your project's render pipeline or install URP/HDRP for you. Unity documents [how the active pipeline is selected](https://docs.unity.com/en-us/engine/6000.0/manual/render-pipelines/choose-a-render-pipeline/srp-setting-render-pipeline-asset).

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
| `SocketJackMemberSync` | A network prefab with selected gameplay state | Opt-in field/property replication, independently configured for each member; can accompany either movement component or be used on an otherwise stationary object. |

Use **one movement synchronization component per object**. Do not combine `SocketJackObject` and `NetworkTransform` on the same prefab. `SocketJackMemberSync` can accompany either one because it uses separate messages for selected gameplay values. `NetworkTransform` does not supply a Rigidbody motor or automatically decide how your existing controller should simulate.

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

### Turn on field and property syncing

Add **SocketJackMemberSync** to the same network prefab as its `NetworkIdentity`. It can accompany `SocketJackObject` or `NetworkTransform`; member values use separate messages and do not overwrite a movement snapshot. Use it on an otherwise stationary network object as well.

For example, keep your existing `PlayerStats` component with its `Health` property and `DisplayName` field. In the member-sync Inspector, choose that component and select its eligible members. Newly discovered entries are disabled until you opt in. Configure `Health` as **Server** authority and `DisplayName` as **Owner** authority. Spawn the prefab through the manager as before. The server's health changes then replicate to observers; only the owning client can propose a display-name change. This policy is independent of whether movement uses server or owner authority.

Each entry has its own settings:

| Setting | Meaning |
| --- | --- |
| Enabled | Opt this member into the prefab's synchronization schema. |
| Target and Member | Select a component on this object's hierarchy and a specific field or readable/writable property. |
| Authority | **Server** accepts changes from the server; **Owner** accepts proposals from the object's owning client, with the server checking identity, room and configured permission. An object without a client owner remains server controlled. |
| Send Interval | Normal interval between successful captures/sends. Budgets or backpressure can delay delivery; pending retries may re-read the getter. Tune a cosmetic value differently from a frequently changing value. |
| Only When Changed | Avoid sending identical values at every capture. Replication represents current state, so several changes can coalesce into the latest value. |
| Max Bytes | Bound the serialized value. A larger logical value can span multiple UDP frames; it must still fit the configured member/session limits. |

The server maintains canonical values and supplies a baseline when a peer first sees the object or returns to its room. Owners wait for their baseline before proposing changes. Per-member revisions reject stale updates, and a new view token prevents delayed packets from a previous room/view from applying to the current instance. Backpressure retries the latest pending state within bounded work limits.

Keep the same prefab/member schema and serializer configuration on every participant. Configure entries before starting the session; restart/respawn after schema changes. A client's Inspector cannot grant permission that is absent from the server's prefab. Client authority is a deliberate trust decision: validate gameplay commands on the server when they affect damage, inventory, currency or progression.

For additional owner-value rules, assign the manager's `MemberValueValidator` before starting. It receives the authoritative object, the configured member ID (the Inspector entry number) and the decoded value. Check allowed ranges, string lengths and game permissions there; returning `false` rejects the write and disconnects the sender. A selected property setter may also enforce local data invariants, but state replication does not replace a validated command protocol for purchases, damage or inventory transactions.

The selector supports eligible instance fields and properties with both a getter and a setter. Static fields, constants, readonly fields, init-only properties, indexers and compiler-generated backing fields are excluded. Values must be supported by the selected serializer. A reference to a GameObject, Component, texture or another arbitrary object graph is not automatically transferred. Represent such references with game/network IDs and resolve them through your game logic. Do not sync a Rigidbody position a second time through member syncing when a movement component already controls it.

Property getters/setters execute on Unity's main thread and may have game-specific side effects. Prefer simple data properties; use explicit commands/events for one-shot effects. For IL2CPP or aggressive managed stripping, preserve the selected component members with your game's `link.xml` or preservation attributes; the SDK cannot preserve arbitrary user assemblies in advance.

### Choose a serializer

The manager defaults to **Binary formatter (SocketJack)**: `SocketJack.Multiplayer.Serialization.SocketJackBinaryFormatter`. This is SocketJack's bounded, typed game-state format; it does not use .NET's `System.Runtime.Serialization.Formatters.Binary.BinaryFormatter`, transmitted CLR type names or automatic object activation.

Use the manager's **Custom provider** selection and assign a component implementing `INetworkSerializerProvider`, or set `network.Serializer` to your `INetworkSerializer` before starting. A provider returns an `INetworkSerializer` with a stable `ProtocolId`, `CanSerialize(Type)`, `Serialize` and `Deserialize`. Implementations must honor the supplied byte limit and return the requested local schema type. Give different wire-affecting settings different protocol IDs. A mismatch is rejected rather than guessed.

The default formatter supports booleans, integer types, float/double/decimal, char/string, enums, `Guid`, `byte[]`, date/time values, `TimeSpan`, System.Numerics vectors/quaternions and nullable supported value types. The Unity default adds explicit codecs for Unity's Vector2/3/4, Quaternion, Color and Color32. Unknown classes, other arrays, collections and nested object graphs need an explicitly registered bounded codec or a custom serializer.

For a custom type, register a bounded writer/reader with `SocketJackBinaryFormatter.Register<T>` using a unique ID and a stable schema ID. IDs below 256 are built-in; Unity reserves IDs 256–261, so use IDs of **1024 or higher** for your game. The manager registers its Unity codecs automatically only for the default formatter. If your provider/override creates a `SocketJackBinaryFormatter`, call `NetworkManager.RegisterUnityTypes(formatter)` yourself when you need those Unity types. Finish registering before the first `ProtocolId` read: that freezes the registry. Register the same codecs in every build. Do not use runtime type discovery from received data.

Serializer choice applies to member values. Session framing, lifecycle messages and the compact physics/transform codecs retain their specified binary protocol, preserving packet budgeting and interoperability checks. Match release, prefab schema and serializer settings across the server and clients.

Each object supports up to 64 enabled members. A member defaults to a 256-byte serialized limit and can be configured up to 8,192 bytes, including its formatter header. This is a logical value limit, separate from the full UDP/IP frame budget; larger accepted values use automatic bounded reconstruction. The server's canonical cache and subscriptions have separate resource limits. Client member requests/proposals share a 120-message/s budget with a burst of 32 to leave room for physics input under the session's admission limits.

For custom game commands or state that does not fit member syncing, define an explicit protocol with the runtime's bounded binary state and `SendMessage` APIs. Keep durable state on the server and provide it to late joiners; an ephemeral event alone is not a saved-state baseline. Application message IDs `60001` (physics relay) and `60003`/`60004` (member request/update) are reserved. The demo uses `60002` for round-trip measurements. Do not overwrite a `SocketJackObject` physics payload with unrelated health bytes.

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

Select **Transport > Udp** or **Tcp** on the scene's `NetworkManager` before starting. Code can set `network.Transport = SocketJack.Multiplayer.Transport.TransportType.Tcp`; call `Shutdown` first when reconfiguring an existing session. Set the same transport and port on every participant. Topology (host/peer or dedicated server/client) is a separate choice; TCP works with both.

UDP is the default realtime transport. Reliable delivery is unordered unless ordering is requested explicitly. TCP supplies a reliable ordered stream; requesting an unreliable mode over TCP does not remove stream blocking. SocketJack never silently changes from UDP to TCP. Large logical messages are split automatically into bounded frames only when one frame is too small. Included UDP object frames stay below 999 bytes for the configured full IP packet budget, including reserved headers; logical-message limits are configured separately.

Permit the selected UDP or TCP port through the host/server firewall. When enabled, the manager attempts automatic UPnP port mapping and reports/renews/releases its lease. Only the host/server requests a listening-port mapping; peers/clients initiate outbound connections. Mapping depends on the router and network and cannot bypass carrier-grade NAT or an inbound-blocking policy. There is no bundled hosted relay, matchmaking or account service. Provide manual forwarding, a reachable dedicated server or a separate relay when necessary.

The included transports carry plaintext game data. For restricted admission, validate short-lived game tickets with `ConnectionValidator`; do not send reusable passwords. Use a separately secured transport or tunnel where confidentiality is required.

The default peer cap of 4,096 is a configured resource limit, not a guaranteed production player count. Partition rooms/interest, budget bandwidth and test the actual simulation. `SetPeerRoom` and `SetObjectRoom` on the server control room visibility; they do not load scenes or authorize player actions. The host renders its authoritative world once, so game-specific local rendering filters are needed to hide other rooms from the host player. A 2,000-peer loopback transport check does not establish that a physics world supports 2,000 mutually visible players.

The member/serializer checks include a 6,000-byte baseline, a 6,100-byte owner update and an observer/late join receiving the canonical replacement through real UDP and TCP. Reusable acceptance tools live under `tools/Unity.MemberSync.Acceptance` and `tools/Test-UnityRenderPipelines.ps1` in the source repository; their test components and generated outputs are excluded from the product packages.

The tested Unity 6 and Unity 2022.3 configurations are recorded at the beginning of this guide. Mobile, consoles, IL2CPP and other player targets require platform-specific acceptance. Web builds lack raw UDP/TCP sockets and need a browser transport bridge.

## Contents and support

In the Asset Store layout, `Runtime/Plugins` contains the portable managed DLL; `Runtime/Components` contains editable Unity component sources and linker configuration. `Samples/Physics` contains the example scene, material, scripts and explicit build helper. NuGet contents and their manual Unity installation paths are listed in section 1. The Unity integration uses modules for physics, IMGUI and JSON serialization, with no third-party runtime NuGet dependencies.

Source, issues and release information: [SocketJack on GitHub](https://github.com/hheiroww/SocketJack). License: `Documentation/LICENSE.txt`; dependency notices: `Documentation/Third-Party Notices.txt`. OpenAI Codex assisted code, documentation and tests; OpenAI ImageGen assisted the existing SocketJack logo's monochrome color adaptation. The code was iteratively refined and validated by managed and actual-player checks.
