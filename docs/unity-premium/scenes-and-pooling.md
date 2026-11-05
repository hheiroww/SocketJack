# Coordinate scenes, object lifetime and pooling

Use shared network IDs for objects and an explicit scene catalog for level changes. A client loading a level or creating a local GameObject does not make that change authoritative for the other players.

## 1. Keep prefab IDs stable

The `NetworkPrefabRegistry` maps explicit IDs to prefabs and persistent stable keys. Every participant needs the same mapping and compatible prefab schemas.

- Retain the registry asset and its stable keys in version control.
- Do not reuse an old ID for an unrelated prefab while compatible players may still connect.
- The registry requires contiguous IDs starting at 1; reordering entries does not renumber them.
- Configure the registry and manager before starting the listener/client.

`NetworkPlayerSpawner` applies the registry and spawns the registered player for an admitted peer. Configure spawn points with `NetworkSpawnPoint`, including the room appropriate for that spawn. `TryGetPlayer(peerId, out identity)` is the supplied way to find a peer's actual player; do not assume the first arbitrary owned object is always the character.

For game objects, server code uses `NetworkManager.Spawn(prefabId, position, rotation, ownerId, roomId)` and `Despawn(objectId)`. Owned-object cleanup follows peer disconnect. A cached reference to a despawned identity must not be treated as a still-authorized object.

## 2. Bind an object already placed in a scene

A scene door, switch or chest can use `NetworkSceneObject` alongside `NetworkIdentity` and its gameplay components. This binds the saved instance instead of creating another copy from a prefab.

1. Add `NetworkSceneObject` to the placed network root.
2. Save its nonzero Scene Object Id in the scene. Editor validation assigns one when it is unset.
3. Ensure IDs are unique in that scene. Duplicating a GameObject can duplicate its serialized ID; assign a new one to the duplicate.
4. Build the same saved scene into every player/server.

The manager binds scene objects as the listener starts or the coordinated level becomes ready. Their selected members provide current state for late joiners. Keep any visual child separate from the scene root's identity so a door can animate without changing its shared identity.

## 3. Configure a server-directed level change

Add `NetworkSceneCoordinator` beside the manager, spawner and other persistent network services.

1. Fill **Scene Catalog** with the approved scene names in the same order in every build.
2. Add those scenes to the player's Build Settings/Build Profiles scene list.
3. Put the matching level geometry and saved scene-object IDs in those scenes.
4. Choose a Ready Timeout appropriate to your load times. The default is 30 seconds.
5. Leave Spawn Players When Ready enabled if the normal player spawner should create players in the new scene.

Only the server starts the transition. For example, a validated game-over handler can call:

```csharp
using SocketJack.Multiplayer.Components;
using UnityEngine;

public sealed class LevelSelection : MonoBehaviour
{
    public NetworkManager Network;
    public NetworkSceneCoordinator Scenes;

    public void LoadSecondLevel()
    {
        if (Network.IsServer && !Scenes.IsLoading)
            Scenes.LoadScene(1);
    }
}
```

Catalog index 1 must exist in all builds. This API uses a **single active scene** transition. It does not claim additive world streaming. Never accept arbitrary scene paths from a remote player.

The coordinator despawns the previous network objects, loads the selected scene and waits for a matching scene-generation acknowledgement. Peers stay in a hidden loading room until ready. The server then restores their room, binds scene objects and allows player spawning. A late joiner is sent the current catalog scene. `PeerSceneReady` and `SceneLoadFailed` expose results to your loading UI.

Do not place a second always-active manager in every level while the coordinator preserves the original services object. Duplicate managers can create extra listeners, spawners and UI. Keep level content separate from persistent services and make any scene bootstrap explicit.

## 4. Use network lifecycle callbacks

Derive game components from `NetworkBehaviour` when they need shared-object lifecycle:

| Callback | Appropriate work |
| --- | --- |
| `OnNetworkReset()` | Clear transient game state for a fresh or pooled spawn. |
| `OnNetworkSpawn()` | Register per-object handlers once the identity is bound. Initial member values may still be pending. |
| `OnNetworkReady()` | Enable logic that depends on initial state/member baselines. |
| `OnOwnershipChanged(ulong previousOwnerId)` | Re-evaluate local input, camera and ownership-specific caches. |
| `OnNetworkDespawn()` | Unsubscribe per-object events and stop effects/coroutines tied to that network life. |

`IsNetworkReady`, `IsOwner`, `IsServer` and `HasStateAuthority` have separate meanings. A ready observer may display an object without owning its controls; a local owner may predict motion without being the server.

## 5. Enable bounded pooling

Add `NetworkObjectPool` to the manager before starting. It assigns `NetworkManager.ObjectPool` and keeps up to **32 idle instances per prefab**, **256 total** by default. Increase those limits only for a measured need.

Continue to call `Spawn`/`Despawn` normally. Do not manually return a still-spawned instance to the pool. Reuse receives a fresh network ID; identity, initial baselines and prediction are reset. The pool clears supported body velocity and disables idle instances. Your game still resets ammunition, timers, particle effects, subscriptions, AI targets and other local state.

For example, a pooled effect can reset its own countdown:

```csharp
using SocketJack.Multiplayer.Components;

public sealed class ProjectileLifetime : NetworkBehaviour
{
    private float age;

    protected override void OnNetworkReset() => age = 0;

    private void Update()
    {
        if (!IsNetworkReady || !IsServer) return;
        age += UnityEngine.Time.deltaTime;
        if (age >= 3) Network.Despawn(Identity.ObjectId);
    }
}
```

Pooling reduces allocation churn. It is not a way to exceed object/visibility limits or keep an old object's permissions alive. Do not store a pooled identity's previous Object Id as a permanent player/account identifier.

## 6. Verify transition and reuse

1. Run a server and two clients; change a door/health value.
2. Join another client and confirm it sees the current values.
3. Change to the second scene from the server and confirm all ready players spawn exactly once.
4. Join during loading and confirm the new client reaches the same scene without seeing stale objects.
5. Despawn/respawn a pooled object repeatedly. Verify a fresh ID, reset timers, no duplicate callbacks and correct ownership.
6. Disconnect during a transition and verify the remaining peers continue.

Native Unity 6 and 2022 gameplay acceptance covers coordinated scenes, a saved door, health/Animator state, ownership, late join and bounded pool reuse over UDP/TCP. Your scene loading times, references and custom lifecycle code still need their own game test.
