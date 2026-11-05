# Diagnose and verify your multiplayer game

Test the smallest failing layer first: compilation, connection, identity/spawning, movement, gameplay state, then optional accounts/chat/voice. A successful website sign-in does not establish a game connection, and a game connection does not prove audio playback.

## Compilation and installation

| Symptom | Check |
| --- | --- |
| Duplicate SocketJack or JSON types | Keep one SDK installation. Do not mix an Asset Store copy with another UPM copy or manually added dependency DLLs. |
| Unity type/API mismatch | Use the editable components for the matching release and a supported Unity bracket; do not pair new scripts with an old runtime DLL. |
| Input System action unavailable | Install/configure Unity's Input System package before assigning its InputActionReference assets to the bridge. The SDK's base assembly does not require that package. |
| Keyboard input exception/no movement in an Input System-only project | Disable the physics/voice Read Keyboard settings and use `NetworkInputBridge`/`SetInput` and `SetPushToTalk`. The default keyboard path needs Old or Both. |
| Pink/missing sample materials | Select materials/shaders compatible with Built-in, URP, HDRP or Custom RP and retain them in the build. Transport configuration cannot fix a shader mismatch. |
| WebGL server/database reference error | Preserve the distribution's client/native-host assembly split. Browser players use the Web client assemblies, not the SQLite/listener backend. |

Select .NET Standard 2.1 API compatibility. Keep server and clients on the same release: release two uses session protocol 3 and member protocol 2. Compatibility checks also cover prefab schemas, controller/precision settings and serializer IDs.

## The second player cannot connect

1. Confirm the listener actually started. Host/Peer uses `StartHost` and `StartPeer`; Dedicated Server/Client uses `StartServer` and `StartClient`.
2. Confirm the address and port were configured while stopped. The joining player must use the listener's address.
3. Try two native players on the same computer, then another computer on the same network, then the public path. This separates game configuration from firewall/router behavior.
4. Confirm UDP, TCP or WebSocket matches at both ends. One transport's port being open does not establish another transport's reachability.
5. For account-required admission, sign in and use `JoinGameAsync`; verify the host accepted the short-lived game ticket. Anonymous join methods cannot bypass Require Game Account.
6. For WebSocket, check the endpoint path, browser origin policy and certificate. HTTPS pages require WSS. Do not disable certificate validation to hide the failure.
7. If joining through a relay, verify allocation/join/renewal and the configured relay transport, not only the account socket.

`NetworkManager.PortMappingStatus`, `IsPortMapped` and `ExternalEndpoint` describe automatic router mapping. An accepted UPnP mapping is useful evidence, not proof of public reachability. Carrier-grade NAT, a second router, firewall policy or an unreachable address can still prevent direct inbound connections. Use the configured relay path when direct hosting is unsuitable.

Account API, game, relay and voice listeners are separate services/routes. See [accounts and hosting](accounts-and-hosting.md) and [relays and linked servers](relays-and-linked-servers.md).

## Connected, but no player appears

- Check the registry, explicit prefab IDs and stable keys match in every build.
- Check `NetworkPlayerSpawner` has the correct registry/player ID and Spawn On Connect is enabled for the normal automatic flow.
- If using `NetworkSceneCoordinator`, check the scene catalog/build scenes and readiness acknowledgement. A peer intentionally stays hidden while loading.
- Check room assignment and visibility radius. An object in another room or outside interest is not sent to that peer.
- Spawn through `NetworkManager.Spawn` at the server. A local `Instantiate` or a local identity component alone does not create a shared object.
- For placed scene objects, save unique matching Scene Object Id values; do not give two copies the same ID.

Use `NetworkManager.SpawnedObjects`, `TryGetIdentity` and `NetworkPlayerSpawner.TryGetPlayer` to inspect canonical objects rather than guessing from scene names. A rendered player before `IsNetworkReady` may still be awaiting its selected-member baseline.

## Both players move from one keyboard, or cameras conflict

Gate input/camera/HUD with `NetworkLocalPlayer` or `IsNetworkReady && IsOwner`. Assign only local presentation/input targets. Keep remote identity/movement/member components enabled. Remove the original single-player scene instance and disable a competing movement script.

When using `SetInput`, send zero input after releasing movement and a single jump edge. The custom motor must use the received intent; it must not read the server computer's keyboard. Use the same collision geometry and appropriate native physics configuration in every build.

## Movement jitters or snaps repeatedly

Inspect the physics component's `LastPositionError`, `PeakPositionError`, `HardCorrections`, `PeakNetworkVelocityCorrection` and `PeakVisualStep`.

1. Remove a second transform/velocity writer on the same body.
2. Check authority, movement mode, native mass/gravity/timestep and collision geometry.
3. Verify the selected precision profile/family matches.
4. Check input/keyframe counters increase at the expected endpoints.
5. Measure queue growth, loss/retries and application RTT before changing smoothing.
6. Tune bounded correction/visual smoothing against the observed error. Do not hide a wrong canonical body by smoothing it indefinitely.

Some correction is expected: native Unity physics is not deterministic across computers. The supplied motors predict and repair; they do not roll back and replay the entire collision world.

## A selected field is stale or rejects the connection

| Check | Why it matters |
| --- | --- |
| Enabled row, target and exact member | Only opt-in local schema entries are synchronized. |
| Authority | A nonowner cannot publish Owner rows; clients cannot publish Server rows. |
| Matching codec/schema/precision | Mismatched wire representations are rejected rather than guessed. |
| Max Bytes | The encoded value, including codec envelope, must fit the row's bound; absolute member cap is 8,192 bytes. |
| `OnNetworkReady` | Initial members may not be available in `Awake` or `OnNetworkSpawn`. |
| Property side effects | A getter/setter that changes scenes, destroys the object or throws can invalidate that binding. |
| IL2CPP preservation | Selected nonpublic members need an appropriate preservation strategy; explicit delegate bindings avoid discovery. |

Use lasting member state for late join. An old effect/trigger event is not a state baseline. Check `MemberServer.BindingError` or `MemberClient.BindingError` when integrating a custom UI/logger; avoid logging account tokens or whole serialized personal values.

## Measure latency and backpressure

`NetworkManager.Latency` measures the application message path with local elapsed time. It includes scheduling/queuing; it is not a remote client's unverified RTT claim. A missing RTT means unknown, not zero.

For a small in-game diagnostics button, this example prints bounded summaries on Unity's main thread:

```csharp
using SocketJack.Multiplayer.Components;
using SocketJack.Multiplayer.Transport;
using UnityEngine;

public sealed class ConnectionSummary : MonoBehaviour
{
    public NetworkManager Network;

    public void PrintSummary()
    {
        var latency = Network.Latency?.GetLocalSample();
        if (latency != null)
            Debug.Log($"Game RTT={latency.RttMilliseconds} ms, p95={latency.P95Milliseconds} ms");

        var transport = Network.IsServer ? Network.Server.Transport : Network.Client?.Transport;
        var metrics = TransportMetrics.Source(transport)?.GetTransportMetrics(16);
        if (metrics != null)
            Debug.Log($"Peers={metrics.ConnectedPeers}, queued={metrics.QueuedBytes}, rejected={metrics.SendsRejected}");
    }
}
```

Optional transport metrics expose native protocol bytes, retries, invalid packets, rejected sends and per-peer queue/RTT details. These byte counters exclude IP/TCP/UDP/TLS and WebSocket framing; use `PacketBudget` when calculating complete UDP packet reservations. Session queues and native transport queues are separate bounds.

`SendMessage`/typed event methods return false when they cannot accept the operation. Handle that outcome: retry a still-relevant state later, report a failed action, or apply the game's disconnect policy. Do not bypass limits by accumulating an unbounded application list. Voice deliberately drops stale audio instead of retrying it forever.

For WebSockets, compare game RTT with account request latency and voice state separately. A successful HTTP response or WebSocket upgrade alone does not prove that login, game join or audio has completed.

## Scale by controlling visibility and work

Add `SocketJackInterestManager` for room/spatial visibility. Choose XZ for a typical 3D ground plane, XY for 2D, or XYZ for a volume. Set radius, cell size, hysteresis and Max Visible Objects according to your game. Mark the intended player with `SocketJackInterestObject.ObserverAnchor` when a peer owns several objects.

Room isolation, visible-object caps, member intervals, change-only sends, adaptive precision and bounded queues work together. An Always Visible object still needs a deliberate use case; do not mark an entire world always visible and expect spatial partitioning to reduce its traffic.

Measure server update time, allocations, fan-out, bandwidth, queue growth and reconnection behavior with representative gameplay. The 2,000-peer transport/logical-room checks are useful foundation evidence; they are not a promise of 2,000 mutually visible colliding characters, chat recipients or simultaneous voice speakers on a particular host.

## Voice packets arrive but nothing is heard

1. Check explicit capture permission and PTT state, plus the selected channel/scope/target.
2. Check canonical membership and trusted game presence. Native routing denies unknown accounts by default.
3. Inspect decoded `PlayedFrames` and nonzero `PlaybackSamples`, not only received packets.
4. Check mute, Unity AudioListener/output, volume and OS device routing.
5. In a browser, add both voice adapters, resume audio from a user gesture, and verify secure-origin microphone permission. Editor microphone behavior does not test WebGL.
6. Disconnect/sign out and confirm buffered audio is revoked. Reconnect and confirm a fresh stream plays.

See the [voice guide](voice.md) for the exact component layout and tested software/hardware boundary.

## A practical release check

Run separate built players on the intended target. Verify host/join, independent input, collisions, late join, disconnect, ownership transfer, visibility re-entry, scene readiness, selected state, command denial and pooled reuse. Add real account/chat/voice checks for the enabled modules. Test the actual public TLS/relay/network path and microphone devices before making target or capacity claims.

Keep a compact record of the SDK/game version, Unity version/backend, transport, object/peer counts and observed result. Exclude passwords, bearer tokens, bootstrap codes and personal chat/audio from shared diagnostic logs.
