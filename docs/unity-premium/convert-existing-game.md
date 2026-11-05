# Convert an existing single-player game

Start with one player prefab and one level. SocketJack can register, spawn and synchronize that player; your code still decides what the player is allowed to do. Expand to the rest of the game after this small conversion works in two separate players.

## 1. Separate the responsibilities

| Existing code | Multiplayer treatment |
| --- | --- |
| Reads local keyboard/gamepad | Run only for the ready owning player; send intent through the chosen controller. |
| Moves a Rigidbody | Use `SocketJackObject`/`SocketJackObject2D`, or integrate its custom motor interface. Avoid two scripts applying the same movement. |
| Moves a CharacterController/custom motor | Use the ExistingController path and explicitly define who simulates it; a transform synchronizer alone is not prediction. |
| Camera, listener, HUD | Local-only presentation, gated by ownership. |
| Health, inventory, currency | Server-authoritative selected members or explicit server game logic. |
| Fire/interact request | A typed command, followed by server validation and canonical game changes. |
| Door state, score, cosmetic choice | Selected state with an explicit authority and late-join baseline. |
| Explosion sound, animation trigger | A transient event; keep lasting state separately. |

Do not start by synchronizing every field. Choose the values another player actually needs and decide who may write each one.

## 2. Choose one movement controller

For a Rigidbody player, the easiest route is to let the supplied physics component apply movement and adapt your input reader to it. Preserve your visible model and collider. Disable the movement part of the old controller so it does not also set velocity/position.

The default component reads WASD/arrows and Space with the legacy Input Manager. For Input System-only games, add `NetworkInputBridge` and assign Move/Jump actions. If your existing input system already produces normalized axes, use a small adapter:

```csharp
using SocketJack.Multiplayer.Components;

public sealed class ExistingInputAdapter : NetworkBehaviour
{
    private SocketJackObject body3D;
    private SocketJackObject2D body2D;

    private void Awake()
    {
        body3D = GetComponent<SocketJackObject>();
        body2D = GetComponent<SocketJackObject2D>();
        if (body3D != null) body3D.ReadKeyboard = false;
        if (body2D != null) body2D.ReadKeyboard = false;
    }

    // Call from your existing input code on Unity's main thread.
    public void SetMove(float x, float y, bool jump)
    {
        if (!IsNetworkReady || !IsOwner) return;
        body3D?.SetInput(x, y, jump);
        body2D?.SetInput(x, y, jump);
    }

    private void OnDisable()
    {
        body3D?.SetInput(0, 0);
        body2D?.SetInput(0, 0);
    }
}
```

Attach it to the same root as the selected physics component. Call `SetMove` every input update, including zero input when movement stops. Pass a jump edge once; do not hold the jump flag for every frame. This adapter requires no particular input package.

For a bespoke motor that must run on both prediction and canonical simulation, implement `INetworkInputMotor.SimulateInput(PhysicsInput input, float deltaSeconds)` and `ResetInput()`, then assign that component to the physics synchronizer's **Input Motor**. Keep its simulation driven by the supplied intent; reading the server machine's keyboard inside the motor breaks remote movement. A motor using external state needs its own synchronization/validation strategy.

## 3. Make cameras and input local

Use `NetworkLocalPlayer` to assign the camera, AudioListener, player HUD and input reader. It enables them only for the ready owner and updates them after ownership transfer.

If you gate behavior in code, derive from `NetworkBehaviour` and check `IsNetworkReady && IsOwner` before reading input. `HasStateAuthority` answers a different question: who may publish this object's motion. A server-authoritative player is still locally owned for camera/input purposes.

Keep networking components enabled on remote copies. Disabling a whole remote player can also stop its state application, colliders, members and lifecycle callbacks.

## 4. Spawn through the registry

Use the wizard's `NetworkPlayerSpawner` for normal admitted-player spawning. It knows the peer ID and scene readiness. Remove the single-player script that unconditionally instantiates a local player in every process.

For another object, call the manager on the server:

```csharp
using SocketJack.Multiplayer.Components;
using UnityEngine;

public sealed class ServerObjectFactory : MonoBehaviour
{
    public NetworkManager Network;

    public NetworkIdentity SpawnFor(ulong peerId, Vector3 position)
    {
        if (!Network.IsServer) return null;
        return Network.Spawn(2, position, Quaternion.identity, peerId, 0);
    }

    public void Remove(NetworkIdentity item)
    {
        if (Network.IsServer && item != null) Network.Despawn(item.ObjectId);
    }
}
```

Register that prefab as ID 2 in every build first. Pass owner 0 for a server-owned object. The manager distributes the canonical object ID; independently instantiating the same prefab on clients does not create matching network identities.

## 5. Convert game rules deliberately

For a health value, add `SocketJackMemberSync`, select the health component/field and choose **Server** authority. Change it only after server-side damage validation. `SocketJackHealth` is a supplied starting component with `Damage`, `Heal` and change/death callbacks.

An owner-authoritative appearance selection can share the same object as server-authoritative health. Member authority is independent of the object's physics authority. Owner authority trusts the owning client for that selected value; use `NetworkManager.MemberValueValidator` when the game needs additional constraints.

For an interaction, add `SocketJackNetworkEvent`, choose the payload type and a stable event ID, and set ownership/distance rules. Its `OnCommand` runs at the server. Validate cooldown, range, target and game state there, then change canonical members or emit an approved event. A client's “hit” message is a request, not proof of damage.

Use member state for anything a late joiner needs. An old door-open event will not replay for that player; the current door-open boolean will.

## 6. Convert the rest in small passes

1. Confirm independent input/cameras and remote motion.
2. Add one server-owned value and verify late join.
3. Add one interaction and verify a nonowner cannot invoke an owner-only command.
4. Add death/respawn and disconnect cleanup.
5. Add scene changes and pooling only after the basic lifecycle works.
6. Add accounts/chat/voice and Internet routing as separate tested steps.

Use the same level collision geometry, prefab registry, selected-member schema, precision configuration and scene catalog in every participating build. Client rendering can differ, but a missing floor or differently configured motor produces persistent physics corrections.

Continue with [physics and authority](physics-and-authority.md), [game state](game-sync.md) and [scenes and pooling](scenes-and-pooling.md).
