using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using SocketJack.Multiplayer.Components;
using SocketJack.Multiplayer.Transport;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
#endif

namespace SocketJack.Tests.Input
{
    /// <summary>Test-only Play Mode fixture. Uses synthetic device events, never OS keyboard input.</summary>
    public sealed class InputBridgeProbe : MonoBehaviour
    {
        [Serializable] private sealed class Receipt { public string result, error, unityVersion, inputPackage; public bool playing; public string[] checks; }
        private readonly List<string> checks = new List<string>();
        private NetworkManager manager;
        private GameObject prefab3D, prefab2D;
        private bool finished;
        private double deadline;
        private static readonly BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;
        private static string Arg(string name) { var args = Environment.GetCommandLineArgs(); int index = Array.IndexOf(args, name); return index >= 0 && index + 1 < args.Length ? args[index + 1] : ""; }
        private IEnumerator Start()
        {
            deadline = Time.realtimeSinceStartupAsDouble + 30;
            try
            {
                Check(Application.isPlaying, "actual Editor Play Mode");
                foreach (var reference in typeof(NetworkInputBridge).Assembly.GetReferencedAssemblies())
                    Check(reference.Name != "Unity.InputSystem", "component assembly has no Input System reference: " + reference.Name);
                prefab3D = Prefab(false); prefab2D = Prefab(true);
                manager = new GameObject("Input fixture host").AddComponent<NetworkManager>();
                manager.EnableAutomaticPortForwarding = false; manager.Topology = NetworkTopology.HostPeer;
                manager.Port = 0; manager.Transport = TransportType.Udp; manager.RegisterPrefabs(prefab3D, prefab2D); manager.StartHost();
            }
            catch (Exception error) { Finish(error); yield break; }
            while (!manager.IsClient && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
            try
            {
                Check(manager.IsClient, "real loopback host connected");
                var owner3D = manager.Spawn(1, Vector3.zero, Quaternion.identity, manager.LocalPeerId).GetComponent<NetworkInputBridge>();
                var owner2D = manager.Spawn(2, Vector3.zero, Quaternion.identity, manager.LocalPeerId).GetComponent<NetworkInputBridge>();
                var remote = manager.Spawn(1, Vector3.zero, Quaternion.identity).GetComponent<NetworkInputBridge>();
                Check(owner3D.IsOwner && owner3D.IsNetworkReady && owner2D.IsOwner && owner2D.IsNetworkReady, "ready local 3D and 2D owners");
                Check(!remote.IsOwner, "server-owned object is not the local player");
                Check(!owner3D.GetComponent<SocketJackObject>().ReadKeyboard && !owner2D.GetComponent<SocketJackObject2D>().ReadKeyboard, "bridge disables competing body keyboard polling");
#if ENABLE_INPUT_SYSTEM
                VerifyInputSystem(owner3D, owner2D, remote);
#else
                Check(Type.GetType("UnityEngine.InputSystem.Keyboard, Unity.InputSystem", false) == null, "Input System package absent");
                Tick(owner3D); Tick(owner2D); Tick(remote);
                Check(Axes(owner3D) == Vector2.zero && Axes(owner2D) == Vector2.zero, "legacy branch reads a neutral headless keyboard");
                owner3D.UseDefaultKeyboard = false; owner3D.GetComponent<SocketJackObject>().SetInput(1, 0); Tick(owner3D);
                Check(Axes(owner3D) == Vector2.zero, "default keyboard can be disabled without optional package");
#endif
                Finish(null);
            }
            catch (Exception error) { Finish(error); }
        }
        private GameObject Prefab(bool twoD)
        {
            var root = new GameObject(twoD ? "Input prefab 2D" : "Input prefab 3D"); root.SetActive(false);
            root.AddComponent<NetworkIdentity>();
            if (twoD) { root.AddComponent<Rigidbody2D>().bodyType = RigidbodyType2D.Kinematic; root.AddComponent<SocketJackObject2D>(); }
            else { root.AddComponent<Rigidbody>().isKinematic = true; root.AddComponent<SocketJackObject>(); }
            root.AddComponent<NetworkInputBridge>(); return root;
        }
        private void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks.Add(name); }
        private static void Tick(NetworkInputBridge bridge) => typeof(NetworkInputBridge).GetMethod("Update", Hidden).Invoke(bridge, null);
        private static Vector2 Axes(NetworkInputBridge bridge)
        {
            object body = (object)bridge.GetComponent<SocketJackObject>() ?? bridge.GetComponent<SocketJackObject2D>();
            return (Vector2)body.GetType().GetField("localAxes", Hidden).GetValue(body);
        }
        private static bool Jump(NetworkInputBridge bridge)
        {
            object body = (object)bridge.GetComponent<SocketJackObject>() ?? bridge.GetComponent<SocketJackObject2D>();
            return (bool)body.GetType().GetField("localJump", Hidden).GetValue(body);
        }
#if ENABLE_INPUT_SYSTEM
        private void VerifyInputSystem(NetworkInputBridge owner, NetworkInputBridge twoD, NetworkInputBridge remote)
        {
            Check(!PlayerLegacyEnabled(), "Input System-only project setting");
            Application.runInBackground = true;
            InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
            InputSystem.settings.updateMode = InputSettings.UpdateMode.ProcessEventsInDynamicUpdate;
            foreach (var device in InputSystem.devices) if (device is Keyboard && device.native) InputSystem.DisableDevice(device);
            var keyboard = InputSystem.AddDevice<Keyboard>();
            InputActionAsset asset = null; InputActionReference moveReference = null, jumpReference = null;
            try
            {
                InputSystem.QueueStateEvent(keyboard, new KeyboardState()); UpdateInput(); Tick(owner); Tick(twoD);
                InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.W, Key.D, Key.Space)); UpdateInput();
                Tick(owner); Tick(twoD); Tick(remote);
                Check((Axes(owner) - new Vector2(1, 1).normalized).sqrMagnitude < .0001f && Jump(owner),
                    "unset actions supply WASD and Space for 3D; axes=" + Axes(owner) + ", jump=" + Jump(owner) + ", syntheticKeys=" + keyboard.wKey.isPressed + "/" + keyboard.dKey.isPressed + "/" + keyboard.spaceKey.wasPressedThisFrame + ", current=" + ReferenceEquals(Keyboard.current, keyboard) + ", default=" + owner.UseDefaultKeyboard + ", update=" + InputState.currentUpdateType + ", deviceUpdated=" + keyboard.wasUpdatedThisFrame + ", pressedStep=" + typeof(UnityEngine.InputSystem.Controls.ButtonControl).GetField("m_UpdateCountLastPressed", Hidden).GetValue(keyboard.spaceKey) + ", deviceStep=" + typeof(InputDevice).GetField("m_CurrentUpdateStepCount", Hidden).GetValue(keyboard));
                Check((Axes(twoD) - new Vector2(1, 1).normalized).sqrMagnitude < .0001f && Jump(twoD), "unset actions supply WASD and Space for 2D");
                Check(Axes(remote) == Vector2.zero && !Jump(remote), "remote does not read local keyboard");
                owner.GetComponent<SocketJackObject>().ClearInput(); twoD.GetComponent<SocketJackObject2D>().ClearInput();
                InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.LeftArrow, Key.DownArrow)); UpdateInput(); Tick(owner);
                Check((Axes(owner) - new Vector2(-1, -1).normalized).sqrMagnitude < .0001f, "arrow-key defaults");
                asset = ScriptableObject.CreateInstance<InputActionAsset>(); var map = asset.AddActionMap("Gameplay");
                var move = map.AddAction("Move", InputActionType.Value); move.expectedControlType = "Vector2";
                move.AddCompositeBinding("2DVector").With("Up", "<Keyboard>/i").With("Down", "<Keyboard>/k").With("Left", "<Keyboard>/j").With("Right", "<Keyboard>/l");
                var jump = map.AddAction("Jump", InputActionType.Button, "<Keyboard>/enter");
                moveReference = InputActionReference.Create(move); jumpReference = InputActionReference.Create(jump);
                move.Enable(); jump.Enable();
                owner.Move = twoD.Move = remote.Move = moveReference; owner.Jump = twoD.Jump = remote.Jump = jumpReference;
                Tick(owner); Tick(twoD); Tick(remote); // Enable copies before the next synthetic input update.
                owner.GetComponent<SocketJackObject>().ClearInput(); twoD.GetComponent<SocketJackObject2D>().ClearInput();
                InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.I, Key.L, Key.Enter)); UpdateInput(); Tick(owner); Tick(twoD);
                Check((Axes(owner) - new Vector2(1, 1).normalized).sqrMagnitude < .0001f && Jump(owner), "assigned Vector2/button actions read through preserved reflection");
                var first = Copy(owner, "move"); var second = Copy(twoD, "move");
                Check(!ReferenceEquals(first, move) && !ReferenceEquals(first, second) && first.enabled && second.enabled, "each ready owner has its own enabled clone");
                Check(Copy(remote, "move") == null, "remote creates no action copy");
                twoD.enabled = false;
                Check(!second.enabled && first.enabled && move.enabled && jump.enabled, "disabling another owner leaves source and local clone enabled");
                NetworkInputBridge.SuppressGameplayInput = true; Tick(owner);
                Check(!first.enabled && Axes(owner) == Vector2.zero && !Jump(owner), "UI suppression disables owned action copy and clears axes/queued jump");
                NetworkInputBridge.SuppressGameplayInput = false; Tick(owner); Check(first.enabled, "resume re-enables only the local copy");
                manager.TransferOwnership(owner.Identity.ObjectId, 0); Tick(owner);
                Check(!first.enabled && Axes(owner) == Vector2.zero && move.enabled, "ownership loss gates input without disabling shared action");
                manager.TransferOwnership(owner.Identity.ObjectId, manager.LocalPeerId); Tick(owner); Check(first.enabled, "ownership return restores input");
                owner.Move = null; owner.Jump = null; owner.UseDefaultKeyboard = false; Tick(owner);
                Check(Axes(owner) == Vector2.zero && move.enabled, "clearing references disposes owned clones, never source");
            }
            finally
            {
                NetworkInputBridge.SuppressGameplayInput = false; InputSystem.RemoveDevice(keyboard);
                if (moveReference != null) Destroy(moveReference); if (jumpReference != null) Destroy(jumpReference); if (asset != null) Destroy(asset);
            }
        }
        private static InputAction Copy(NetworkInputBridge bridge, string name)
        { var copy = typeof(NetworkInputBridge).GetField(name, Hidden).GetValue(bridge); return copy == null ? null : (InputAction)copy.GetType().GetField("instance", Hidden).GetValue(copy); }
        // Public Update() chooses Editor updates in batchmode. Exercise the actual player-loop
        // Dynamic update explicitly so frame-edge queries observe player state, not editor state.
        private static void UpdateInput() => typeof(InputSystem).GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(InputUpdateType) }, null).Invoke(null, new object[] { InputUpdateType.Dynamic });
        private static bool PlayerLegacyEnabled()
        {
#if ENABLE_LEGACY_INPUT_MANAGER
            return true;
#else
            return false;
#endif
        }
#endif
        private void Finish(Exception error)
        {
            if (finished) return; finished = true;
            try { manager?.Shutdown(); } catch (Exception stop) { error = error ?? stop; }
            var result = new Receipt { result = error == null ? "PASS" : "FAIL", error = error?.ToString() ?? "", unityVersion = Application.unityVersion, playing = Application.isPlaying,
#if ENABLE_INPUT_SYSTEM
                inputPackage = typeof(InputAction).Assembly.GetName().Version.ToString(),
#else
                inputPackage = "absent",
#endif
                checks = checks.ToArray() };
            File.WriteAllText(Arg("-input-receipt"), JsonUtility.ToJson(result, true));
            Debug.Log("SOCKETJACK_INPUT_" + result.result);
#if UNITY_EDITOR
            UnityEditor.EditorApplication.Exit(error == null ? 0 : 1);
#else
            Application.Quit(error == null ? 0 : 1);
#endif
        }
    }
}
