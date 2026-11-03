# SocketJack.Unity

![SocketJack.Unity](https://raw.githubusercontent.com/hheiroww/SocketJack/socketjack-unity-v2026.11.02.1/SocketJack.Unity/SocketJackUnityIcon.png)

Portable multiplayer networking for Unity games and dedicated .NET servers. Targets .NET Standard 2.1 with no third-party dependencies and no UnityEngine dependency in the runtime DLL.

The package supplies the networking foundation: explicit UDP/TCP transports, authoritative object spawning and state, player ownership, room-based visibility, fixed simulation ticks, bounded queues and Unity component sources. Game code defines its prefabs, input, simulation rules and replicated state. Adding a package alone cannot make arbitrary single-player game logic multiplayer.

## Install

For a dedicated .NET server or a non-Unity integration:

```sh
dotnet add package SocketJack.Unity --version 2026.11.02.1
```

For Unity 2022.3 or later, use the .NET Standard 2.1 API compatibility level. Open the `.nupkg` as a ZIP archive, then copy:

1. `lib/netstandard2.1/SocketJack.Unity.dll` into `Assets/Plugins/SocketJack.Unity/`.
2. The `.cs` files and `link.xml` from `contentFiles/any/any/SocketJack.Unity/` into `Assets/SocketJack.Unity/`.

The component sources intentionally ship as content, rather than a DLL referencing one particular Unity Editor version. Ordinary .NET consumers do not compile them. Do not copy UnityEditor assemblies or `UnityAdapter.Verify.dll` into a game. The standalone multiplayer DLL can coexist with the base `SocketJack` package; it does not duplicate the base library's types.

## First Unity game

Create a prefab containing `NetworkIdentity`, `NetworkTransform` and, optionally, `ExamplePlayerController` from `SocketJack.Unity.Components`. Add `NetworkManager` to an empty scene object. Register the same prefab list in the same order in every build; prefab IDs start at `1`.

Wire a menu or script to `StartHost()` on one instance and `StartClient()` on another. Both default to UDP and `127.0.0.1:7777`. For LAN/Internet clients set `Address` before starting. The server binds all IPv4 interfaces. Open the selected UDP or TCP port on the server's firewall and router as appropriate.

```csharp
using SocketJack.Unity.Components;
using UnityEngine;

public sealed class GameBootstrap : MonoBehaviour
{
    public NetworkManager network;
    public GameObject playerPrefab;

    void Awake()
    {
        network.RegisterPrefabs(playerPrefab);
        network.PeerConnected += peer =>
            network.Spawn(1, Vector3.zero, Quaternion.identity, ownerId: peer);
    }

    public void Host() => network.StartHost();
    public void Join() => network.StartClient();

    // Example for Unity's legacy Input Manager. With the Input System package,
    // pass its movement action value to SetMoveIntent instead.
    void Update()
    {
        if (!network.IsClient) return;
        foreach (var state in network.Client.Objects.Values)
            if (state.OwnerId == network.LocalPeerId &&
                network.TryGetIdentity(state.Id, out var identity))
                identity.GetComponent<ExamplePlayerController>()?.SetMoveIntent(
                    new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical")));
    }
}
```

`ExamplePlayerController` sends two bounded movement axes. The server checks ownership through the session protocol, clamps direction and applies its configured speed on fixed simulation ticks. It is a small movement example; add game-specific collision, movement validation and input sequencing where needed. `NetworkTransform` replicates server position, rotation and scale and interpolates client views. It does not implement prediction, rollback, lag compensation or physics synchronization. Its 40-byte state format occupies the object's state payload; use a custom component/codec to combine transform and additional game state.

Destroy network objects through `NetworkManager.Despawn` on the server. Call `Shutdown` before restarting or reconfiguring the manager. Shutdown destroys managed instances and disposes both host/client transports. Callbacks execute on the thread pumping `Update`; the Unity manager does so on Unity's main thread.

## Transport contracts

| Type | Behavior | Use |
| --- | --- | --- |
| `Udp` | Single nonblocking server socket; independent channel sequencing, acknowledgments and bounded retransmission for reliable messages | Realtime movement and state |
| `Tcp` | Framed ordered stream; every delivery mode arrives reliably in stream order | Turn-based games, tools, networks allowing TCP only |
| `Custom` | Implement `IGameTransport` and pass it to `NetworkServer`/`NetworkClient` | Platform services, relays or browser bridges |

`DeliveryMode.Unreliable` allows loss/reordering. `UnreliableSequenced` discards stale channel sequence numbers. `ReliableOrdered` retries and delivers in order within that channel. TCP preserves reliable stream semantics even when a less strict mode is requested; it cannot remove TCP head-of-line blocking. There is no silent fallback between transport types.

Default datagrams are at most 1,200 bytes, with protocol headers included. Payloads exceeding `MaxPayloadSize` are rejected; game messages are not fragmented into arbitrarily large transfers. `Send`/`SendMessage` returning `false` means disconnected or backpressured: retry reliable application messages after a later update. Internal reliable lifecycle queues are bounded; a client unable to keep up is disconnected instead of receiving an incomplete object view.

The session reserves channel `0` for reliable lifecycle messages, `1` for state snapshots and `2` for owner inputs. Application messages use channel `3` by default. A custom transport must provide at least these four channels and preserve the delivery contracts.

Both built-in endpoints must use the same `MaxPacketSize` and `Channels` settings; a mismatch is rejected during the transport handshake. They use a game-specific wire protocol and do not interoperate with the base library's `UdpClient`/`UdpServer` or reliable bulk-transfer protocol.

## Authoritative runtime

Use `NetworkServer` and `NetworkClient` directly when you do not need the supplied Unity components:

```csharp
using System.Net;
using SocketJack.Unity;
using SocketJack.Unity.Transport;

using var server = new NetworkServer(GameTransport.Create(TransportType.Udp));
server.PeerConnected += peer => server.Spawn(1, ownerId: peer, state: new byte[] { 100 });
server.InputReceived += input =>
{
    // Validate the player's intent against authoritative game rules.
    // Only this server changes replicated state.
    server.SetState(input.ObjectId, new byte[] { 99 });
};
server.Start(new IPEndPoint(IPAddress.Any, 7777));

// In the game/server loop, supply elapsed monotonic time in seconds:
server.Update(deltaSeconds: 1.0 / 30, maxEvents: 4096);
```

Continue pumping the server in a real loop; the final line above illustrates one iteration. Dispose sessions on shutdown. All transport and session operations belong to one thread. No per-player thread or background simulation is created.

State consists of bounded `byte[]` messages with explicit binary framing. There is no automatic CLR type activation, RPC reflection or runtime code generation. Clients send inputs for owned objects; the server decides what state to apply. Use `SendMessage(ushort messageId, byte[] payload, ...)` for explicitly defined application commands/events.

Late joiners receive reliable object baselines, streamed within the per-update budget. `SetPeerRoom` clears the old room view and streams reliable baselines for the new room; client despawn callbacks remove the old objects. `SetObjectRoom` moves an object's visibility. Move an owning peer to the destination room before moving its object. A disconnected owner's objects are despawned. Rooms are a visibility partition, not an authentication or authorization system: only trusted server code assigns them. The supplied host manager renders the authoritative world once; game-specific local rendering filters are needed if the host's view should hide other rooms.

## Capacity and operating limits

Defaults allow 4,096 connected peers, but this is a capacity limit, not a promised production player count. Thousands of users require room/interest partitioning, a suitable snapshot rate, bandwidth capacity and a dedicated-server process/fleet sized for the actual game workload. Do not broadcast every object to every player. Simulations involving thousands of mutually visible players require additional game-specific interest filtering, aggregation or sharding.

`NetworkSessionOptions` controls tick/snapshot rates, catch-up limits, per-peer queues, inbound message rates and per-update work. Defaults include 30 simulation ticks/s, 10 snapshot rounds/s, 64 state updates per peer per snapshot round, 1,024 state/message bytes, 4,096 objects per room, 100,000 objects per server and 240 inbound messages/peer/s. The reliable session queue is capped at 1 MiB and 1,024 messages per peer; initial baselines stream object IDs/canonical state instead of eagerly filling that queue. State cycles revisit unchanged objects so later snapshots repair packet loss. A room with more visible objects than its snapshot budget takes multiple rounds to refresh. Configure and measure that latency for your game. `TransportOptions` separately bounds packet size, connection data, reliable backlog, timeouts and admitted peers. Raising caps increases memory/network cost; it does not create server capacity.

UDP admission uses an endpoint-bound cookie challenge before allocating peer state. Set `TransportOptions.ConnectionValidator` (or the Unity manager's `ConnectionValidator`) before starting to validate a short-lived game ticket. Both included transports carry plaintext game data. They do not provide encryption or protection from an on-path attacker; use a trusted encrypted tunnel or an audited secure transport implementation when that is required. Never transmit reusable account passwords through `connectionData`. The package includes neither a matchmaking service nor hosted relay/NAT traversal, persistence, account management or multi-process orchestration.

WebGL cannot use raw UDP/TCP sockets. The built-in Unity manager rejects it with a clear exception; provide a browser-specific transport/bridge through the runtime API for browser games. Other platform acceptance, including IL2CPP, mobile and consoles, requires testing the actual player build and the platform's networking requirements. The runtime avoids reflection serialization to make that integration practical.

## Build and verification

```sh
dotnet test SocketJack.Unity.Tests/SocketJack.Unity.Tests.csproj -c Release
dotnet pack SocketJack.Unity/SocketJack.Unity.csproj -c Release -o ./artifacts/unity
pwsh -File tools/Verify-UnityPackage.ps1 -Package ./artifacts/unity/SocketJack.Unity.2026.11.2.1.nupkg
```

From a repository checkout with Unity installed, compile component sources against the real Editor reference assemblies:

```powershell
dotnet build tools/UnityAdapter.Verify/UnityAdapter.Verify.csproj -c Release `
  '-p:UnityManagedPath=C:\Program Files\Unity\Hub\Editor\2022.3.17f1\Editor\Data\Managed'
```

This compile check does not establish player/IL2CPP or live gameplay acceptance. CI tests the managed runtime on Windows and Linux, packs with an explicit content allowlist, and preserves the package with SHA-256 checksums. Publish through `dotnet.yml` using `workflow_dispatch` with `releaseTarget: unity`; `unity.yml` validates only. Publication requires a matching date-code release version and uses the existing NuGet trusted-publishing policy.

Unity's [.NET compatibility documentation](https://docs.unity.com/en-us/engine/6000.0/manual/programming-environment/overview-of-dot-net-in-unity/dotnet-profile-support) and [Web .NET API restrictions](https://docs.unity.com/en-us/engine/6000.5/manual/platform-specific/webgl/develop/web-dotnet-api-support) describe the relevant platform constraints.
