# Synchronize gameplay state, events and precision

Movement is one part of multiplayer. Health, doors, scores, equipment and animation need explicit state or commands. Configure a small schema for each prefab and keep it identical in every build.

## Choose state or an event

| Need | Use |
| --- | --- |
| Current health, open door, equipped item, score | `SocketJackMemberSync` or a supplied gameplay component |
| Client asks to interact or use an ability | `SocketJackNetworkEvent` command, validated by server game code |
| Sound, effect or one-shot animation trigger | Server-approved event |
| Animator float/int/bool and explicit triggers | `SocketJackAnimator` |

State has a current canonical value and a late-join baseline. Transient events are not saved as history for a late joiner. A bool recording “door is open” and an event playing “door opened” serve different purposes.

## 1. Select fields and properties

1. Add `SocketJackMemberSync` to the network prefab.
2. In its Inspector, discover members for the target component.
3. Enable only the rows another player needs.
4. For each row select Target, Member, Authority, Send Interval, Only When Changed and Max Bytes.
5. Save the prefab and use the same schema in each client/server build.

The selector accepts writable instance fields and readable/writable properties, including explicitly selected nonpublic members. It excludes static, readonly, indexer, generated backing and init-only members. Unsupported serialized types are reported instead of traversing an arbitrary object graph. Property getters/setters are your game code and may have side effects; keep synchronized accessors small and predictable.

Programmatic setup uses the same local whitelist. Run it before the object is spawned:

```csharp
using SocketJack.Multiplayer;
using SocketJack.Multiplayer.Components;
using UnityEngine;

public sealed class PlayerStats : MonoBehaviour
{
    public int Health = 100;
    public string Outfit = "default";
}

public sealed class ConfigurePlayerStats : MonoBehaviour
{
    public void Configure(PlayerStats stats, SocketJackMemberSync sync)
    {
        sync.AddMember(stats, nameof(PlayerStats.Health),
            NetworkMemberAuthority.Server, maxBytes: 16);
        sync.AddMember(stats, nameof(PlayerStats.Outfit),
            NetworkMemberAuthority.Owner, maxBytes: 128);
    }
}
```

Call `Configure` exactly once on the unspawned prefab/instance being prepared, rather than adding duplicate rows every time a pooled player becomes active. For ordinary Unity prefabs, the Inspector is the simpler saved configuration.

For IL2CPP, preserve selected game members using an appropriate `link.xml`/Preserve strategy. Editor-saved selector keys support the configured-member AOT path; dynamic discovery of unknown properties is deliberately conservative. `INetworkMemberSource.CreateNetworkBindings(INetworkSerializer)` with explicit delegates is available for code-owned bindings without runtime member discovery.

## 2. Assign authority per value

- **Server**: capture the value at the server and distribute canonical changes. Use it for health, rewards, inventory and trusted game outcomes.
- **Owner**: accept proposals only from the current owning peer for that selected member, then distribute the server's accepted cache. Use it for values you deliberately trust that client to choose.

These permissions are independent of Rigidbody authority. Owner-authoritative cosmetics do not grant permission to change a server-authoritative health row on the same object.

`NetworkManager.MemberValueValidator` can reject additional owner values using `(NetworkObject object, ushort memberId, object value)`. It is a validation hook; normalize/clamp a value in your own canonical game flow when needed. Owner-side prediction is not a promise that every echoed value will overwrite the local owner.

The server retains canonical member values for late join and room re-entry. Baselines are separate from physics state. Use `NetworkBehaviour.OnNetworkReady` when your game needs those initial values; `Awake` and `OnNetworkSpawn` run earlier.

## 3. Respect byte and work bounds

Each schema allows at most **64 enabled members**. Each serialized member is bounded to **8,192 bytes**, including its serializer envelope. The default Inspector row bound is 256 bytes. Choose the smallest sufficient value rather than raising every row to the maximum.

Large logical values split into MTU-budgeted frames only when necessary and rebuild before member decoding. Real UDP/TCP checks cover 6,000-byte baseline values, 6,100-byte owner replacements and late join with a custom serializer. A large member still consumes queue, canonical-cache and CPU budget; asset streaming is a separate problem.

Change-only behavior avoids unchanged sends. Send Interval controls sampling, not permission. Coordinators also bound sends, scans, owner proposals, subscriptions and retained bytes. Set `NetworkManager.MemberSyncOptions` while stopped if your measured game needs different limits. A backed-up peer is not permission to create an unbounded reliable queue.

Schema compatibility includes member IDs/keys/types, authority, byte bounds, selected codec and precision. Scheduling differences do not change accepted wire representation. Configure schemas before spawn; despawn/reconfigure/respawn to change them deliberately.

## 4. Select the serializer

The manager Inspector offers **Binary formatter (SocketJack)** and **Custom provider**. The default is SocketJack's bounded binary codec, not .NET `BinaryFormatter`. It does not accept remote type names or deserialize arbitrary runtime object graphs.

Built-in support covers primitives, enums, strings, byte arrays, Guid, decimal and System.Numerics vector/quaternion values. The Unity default serializer also registers Unity Vector2/3/4, Quaternion, Color and Color32. Register your own explicit codecs for other types, or provide `INetworkSerializerProvider.CreateSerializer()` on a selected component.

`INetworkSerializer` defines `ProtocolId`, `CanSerialize(Type)`, `Serialize(object, Type, int maxBytes)` and `Deserialize(byte[], Type, int maxBytes)`. A custom implementation must return the declared type, enforce bounds and use a stable protocol ID that changes when its wire contract changes. Both peers must configure the same codec.

For `SocketJackBinaryFormatter.Register<T>`, use stable custom IDs at or above 256 and a stable schema ID. The default Unity registrations reserve IDs 256–261. A custom provider returning this formatter should call `NetworkManager.RegisterUnityTypes(formatter)` before protocol ID/schema use if it needs the Unity built-ins. Configuration freezes when ProtocolId is first read; register everything first.

## 5. Quantize only values that tolerate it

Use Full Precision, Balanced or Compact on physics components and selected numeric member rows. A `NetworkPrecisionAsset` can hold a custom profile shared across prefabs. Position, quaternion components, scale, linear velocity, angular velocity and generic scalar have distinct ranges/steps. Select the member's Semantic so a position uses the position grid rather than a generic scalar grid.

| Typical physics payload, excluding headers | Full | Balanced | Compact |
| --- | ---: | ---: | ---: |
| Rigidbody3D | 70 B | 55 B | 47 B |
| Rigidbody2D | 70 B | 44 B | 40 B |

Reduced profiles use a tagged raw-value escape when a value exceeds the chosen grid range, rather than silently clipping. Payload size therefore varies. Quaternion component steps are not Euler-degree guarantees. Angular-velocity profile values use radians/second; the 2D adapter converts Unity's native angular units appropriately.

To allow adaptation, add `SocketJackAdaptiveNetworking` to the manager and `SocketJackAdaptiveObject` to applicable prefabs. Opt individual numeric members into Adaptive Precision when appropriate. The server selects a declared compatible precision family from sustained RTT/backlog pressure with recovery hysteresis. Lowest Allowed Precision limits how coarse that object may become. Fixed profiles remain fixed; simulation and local input keep running normally.

## 6. Add a validated command

On a `SocketJackNetworkEvent`, select a stable Event Id, payload type and Max Bytes. Keep Require Ownership enabled for a player's own actions. Shared world interactions can disable that check while supplying a distance and game-specific validator.

`Send<T>(value)` submits a command. At the server, `OnCommand` or `Received` handles it; use `CommandValidator` to enforce cooldowns and game rules before dispatch. `Broadcast<T>(value, audience)` emits a server event. Both return bool: handle a rejected/not-ready/backpressured send instead of assuming the command was delivered.

The supplied `SocketJackDoor`, `SocketJackHealth`, `SocketJackPickup` and `SocketJackProjectile` demonstrate these patterns. They are starting game behaviors, not a complete combat/inventory system. Review their callbacks and collision rules for your game. Animator triggers are explicit events; float/int/bool parameters use selected state through `SocketJackAnimator`.

Verify nonowner rejection, owner transfer, late join, room hide/re-entry and custom-codec mismatch in your actual game. Continue with [scenes and pooling](scenes-and-pooling.md).
