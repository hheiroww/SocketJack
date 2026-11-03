# Unity physics multiplayer sample

A real Unity game with a sliding cube per connected player. WASD or arrow keys apply forces immediately on the owning client; Space jumps. Every client runs local Rigidbody physics from relayed input. The server sends sparse corrections instead of a full transform every render frame. A raised floor and static blocks provide actual collider interactions.

This is a deliberately small sample, not a deterministic physics engine. Physics, prediction and repair settings are on the `SocketJackObject` component. A project's game rules must decide which client inputs are valid.

## Prepare and build

Unity 6 is the primary support target, with Unity 2022.3 LTS retained as the compatibility bracket. Use Windows Standalone support and an activated Editor license. The complete setup and single-player conversion guide (`docs/Unity-AssetStore-QuickStart.md` in source, `Documentation/QuickStart.md` in the Asset Store edition) records the exact verified Editor/player versions and explains required components, ownership, input and camera setup. The sample uses Built-in rendering and legacy keyboard input (Input Manager (Old) or Both); a new-Input-System-only game can supply its own actions through `SetInput` as shown in the guide. From the repository root:

```powershell
pwsh -File tools/Prepare-UnityPhysics.ps1 -StagingDirectory "$env:TEMP/SocketJackPhysics" -Build
```

The script builds the portable library, copies that DLL and the Unity component sources into this project's ignored dependency folders, and invokes Unity's `SocketJack.Samples.Physics.Editor.PhysicsSampleBuild.BuildWindows`. The actual player appears at `<staging>/Player/SocketJackPhysics.exe`. Build logs, checksums/dependencies and the ownership manifest remain in the staging directory. The script performs no cleanup deletion.

To open the project visibly instead:

```powershell
pwsh -File tools/Prepare-UnityPhysics.ps1 -StagingDirectory "$env:TEMP/SocketJackPhysics" -Open
```

In the Editor, use **Tools → SocketJack → Physics sample → Prepare scene**, then enter Play Mode. **Build Windows player** creates a standalone player. The generated demo scene contains an explicit `PhysicsGame` object that constructs the floor, lighting, camera, player prefab and role menu. Importing component sources into another scene does not automatically launch the demo. The source project intentionally excludes generated `Library`, account/cloud settings and copied binary dependencies from Git. The build verifies all four movement modes against a real Rigidbody/Collider and checks that mass, drag, angular drag, gravity, interpolation and friction remain intact.

## Run

Select **Host / Peer**, start one normal player with **Host + play**, and join it from another player with **Join host as peer**. Alternatively, choose **Server / Client** for a dedicated listening server and outbound clients. Topology does not choose UDP/TCP; that is an independent transport setting. Both modes use `127.0.0.1:7777` by default. The locally owned cube is amber. Other cubes are blue. The camera overlooks the shared arena.

The same actual Unity player supports dedicated roles:

```powershell
./SocketJackPhysics.exe -batchmode -nographics -server -no-upnp -port 7777 -logFile server.log
./SocketJackPhysics.exe -client -no-upnp -address 127.0.0.1 -port 7777
./SocketJackPhysics.exe -client -no-upnp -address 127.0.0.1 -port 7777
```

Add `-tcp` to every endpoint to choose TCP instead of the default UDP. A LAN/Internet client can use another address; the server's firewall/router must permit the selected port. Hosted relay and matchmaking are outside this sample.

For host/peer CLI roles use `-host` and `-peer` instead. **Server authority** is enabled by default; `-owner-authority` explicitly selects canonical owner keyframes with server ownership/format validation and relay. Objects inherit the manager policy unless their prefab sets a **Server**/**Owner** authority override. The listening host/server alone performs automatic UPnP mapping by default; `-no-upnp` opts out, as in the local examples above. The role menu exposes the same option and current mapping status. Clients/peers never map a service port.

## Actual-player checks

```powershell
pwsh -File tools/Test-UnityPhysicsPlayers.ps1 `
  -Player "$env:TEMP/SocketJackPhysics/Player/SocketJackPhysics.exe" `
  -OutputDirectory "$env:TEMP/SocketJackPhysics/player-check" -Duration 20
```

This launches an actual headless Unity server and two visible Unity clients. Clients feed an automatic square movement pattern through the same `SetInput` API used by the keyboard controller. Add `-HeadlessClients` for a nonvisual run. No .NET-only substitute is used for physics acceptance.

The harness passes `-no-upnp` to every role and checks that automatic mapping stayed disabled. Add `-OwnerAuthority` to test owner-canonical keyframes and `-Topology HostPeer` to test a host plus peers. Owner-authority checks require observed owner-state transmissions, so a nonfunctional checkbox cannot pass.

Each player writes JSON metrics and its Unity log. The checks require both clients to have observed two objects, submitted owner input, received relayed input and received sparse corrections. They reject hard snaps, mean position error over 0.5 m or peak error over 2 m in this loopback scenario. Metrics also record peak per-frame visual movement and velocity correction magnitude; these are evidence to inspect alongside visible movement, not a universal promise of zero jerk. Obstacles and physical collisions can cause legitimate abrupt velocity changes.

An 8-byte monotonic timestamp echo runs at 2 Hz on sample message ID `60002`, recording measured mean/p95/maximum application RTT; the loopback check requires at least five replies and p95 below 100 ms. These unreliable echo packets measure actual network plus player-loop delay. Immediate owner-side prediction is a separate responsiveness behavior and is not reported as zero network latency.

The command-line `-duration`, `-metrics` and `-autoplay` options are also available directly. Player processes exit themselves after the requested duration; the harness does not force-close a user's game. Logs, outputs and build/cache files remain for review and require explicit cleanup approval.

## Data flow and limits

`SocketJackObject` sends 15-byte versioned binary intent on changes, with a 30 Hz ceiling and 5 Hz keepalive. Input uses reliable unordered delivery by default; sequence numbers discard stale intent. The server applies the input to its own Rigidbody and relays it to same-room observers. All peers continue simulating locally between inputs.

The 70-byte binary keyframe contains position, rotation, linear/angular velocity, simulation tick, server time and last accepted input sequence. Keyframes are sampled at 5 Hz. Client repair extrapolates only within a configured limit, gently adjusts velocity and uses Rigidbody/optional child-visual interpolation. This does not rewind/replay collision worlds or provide full rollback. The component's Inspector exposes these choices. `NetworkTransform` must not be attached to the same body.

These codec sizes exclude session, transport and IP headers. Larger application objects use automatic bounded object frames; ordinary movement input/keyframes fit one frame. The transport's packet budget accounts for all headers and keeps object IP packets below 999 bytes. Thousands of players still require room/interest partitioning, bandwidth sizing and load tests against the actual game.
