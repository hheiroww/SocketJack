using System;
using System.Reflection;
using UnityEngine;

namespace SocketJack.Multiplayer.Components
{
    /// <summary>Owner-only input adapter. Optional Input System actions use a fixed preserved API,
    /// without an assembly dependency. Action copies isolate owners; unassigned actions can use
    /// the active input backend's default keyboard. Project input settings are never changed.</summary>
    [DisallowMultipleComponent, RequireComponent(typeof(NetworkIdentity))]
    public sealed class NetworkInputBridge : NetworkBehaviour
    {
        public static bool SuppressGameplayInput { get; set; }
        [Tooltip("Optional InputActionReference asset for Vector2 movement; its action is cloned for this owner.")]
        public UnityEngine.Object Move;
        [Tooltip("Optional InputActionReference asset for button jump; its action is cloned for this owner.")]
        public UnityEngine.Object Jump;
        [Tooltip("Use WASD / arrow keys and Space for each unassigned action, using the active input backend.")]
        public bool UseDefaultKeyboard = true;
        private SocketJackObject body3D;
        private SocketJackObject2D body2D;
#if ENABLE_INPUT_SYSTEM
        private ActionCopy move, jump;
        private UnityEngine.Object moveSource, jumpSource;
        private bool warned;
        private KeyboardReader keyboard;
#endif
        private void Awake()
        {
            body3D = GetComponent<SocketJackObject>(); body2D = GetComponent<SocketJackObject2D>();
            if (body3D != null) body3D.ReadKeyboard = false; if (body2D != null) body2D.ReadKeyboard = false;
        }
        private void Update()
        {
            if (!IsNetworkReady || !IsOwner || SuppressGameplayInput) { DisableInput(); return; }
            Vector2 axes = Vector2.zero; bool pressed = false;
#if ENABLE_INPUT_SYSTEM
            try
            {
                RefreshAction(Move, ref moveSource, ref move); RefreshAction(Jump, ref jumpSource, ref jump);
                if (UseDefaultKeyboard && (Move == null || Jump == null))
                {
                    if (keyboard == null) keyboard = new KeyboardReader();
                    keyboard.Read(out axes, out pressed);
                }
                if (Move != null) axes = move == null ? Vector2.zero : move.ReadMove();
                if (Jump != null) pressed = jump != null && jump.ReadJump();
            }
            catch (Exception)
            {
                DisableInput();
                if (!warned) { warned = true; Debug.LogWarning("SocketJack input could not read the optional Input System API. Assign InputActionReference assets with Vector2 movement and button jump, and retain the supplied link.xml in player builds.", this); }
                return;
            }
#elif ENABLE_LEGACY_INPUT_MANAGER
            if (UseDefaultKeyboard)
            {
                axes = new Vector2((Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow) ? 1 : 0) - (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow) ? 1 : 0),
                    (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow) ? 1 : 0) - (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow) ? 1 : 0));
                pressed = Input.GetKeyDown(KeyCode.Space);
            }
#endif
            if (!Finite(axes.x) || !Finite(axes.y)) axes = Vector2.zero;
            body3D?.SetInput(axes.x, axes.y, pressed); body2D?.SetInput(axes.x, axes.y, pressed);
        }
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private void DisableInput()
        {
#if ENABLE_INPUT_SYSTEM
            move?.Disable(); jump?.Disable();
#endif
            body3D?.ClearInput(); body2D?.ClearInput();
        }
        private void ReleaseInput()
        {
            DisableInput();
#if ENABLE_INPUT_SYSTEM
            move?.Dispose(); jump?.Dispose(); move = jump = null; moveSource = jumpSource = null;
#endif
        }
        protected override void OnNetworkReset() => ReleaseInput();
        protected override void OnNetworkDespawn() => ReleaseInput();
        protected override void OnOwnershipChanged(ulong previousOwnerId) { if (!IsOwner) DisableInput(); }
        private void OnDisable() => DisableInput();
        private void OnDestroy() => ReleaseInput();

#if ENABLE_INPUT_SYSTEM
        private static void RefreshAction(UnityEngine.Object source, ref UnityEngine.Object previous, ref ActionCopy copy)
        {
            if (source == previous) return;
            copy?.Dispose(); copy = null; previous = source;
            if (source != null) copy = new ActionCopy(source);
        }
        // Only fixed public package APIs are reflected; network data never selects a type or method.
        private sealed class ActionCopy : IDisposable
        {
            private readonly IDisposable instance;
            private readonly Action enable, disable;
            private readonly Func<object> read;
            private readonly Func<bool> pressed;
            private bool enabled;
            internal ActionCopy(UnityEngine.Object reference)
            {
                Type referenceType = Type.GetType("UnityEngine.InputSystem.InputActionReference, Unity.InputSystem", true);
                Type actionType = Type.GetType("UnityEngine.InputSystem.InputAction, Unity.InputSystem", true);
                if (!referenceType.IsInstanceOfType(reference)) throw new ArgumentException("Assign an InputActionReference asset.");
                object original = referenceType.GetProperty("action").GetValue(reference);
                if (original == null) throw new ArgumentException("The action reference has no action.");
                object clone = actionType.GetMethod("Clone", Type.EmptyTypes).Invoke(original, null);
                instance = clone as IDisposable ?? throw new InvalidOperationException("Input action cannot be disposed.");
                try
                {
                    enable = (Action)Delegate.CreateDelegate(typeof(Action), clone, actionType.GetMethod("Enable", Type.EmptyTypes));
                    disable = (Action)Delegate.CreateDelegate(typeof(Action), clone, actionType.GetMethod("Disable", Type.EmptyTypes));
                    read = (Func<object>)Delegate.CreateDelegate(typeof(Func<object>), clone, actionType.GetMethod("ReadValueAsObject", Type.EmptyTypes));
                    pressed = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), clone, actionType.GetMethod("WasPressedThisFrame", Type.EmptyTypes));
                }
                catch { instance.Dispose(); throw; }
            }
            private void Enable() { if (!enabled) { enable(); enabled = true; } }
            internal Vector2 ReadMove()
            {
                Enable(); object value = read();
                if (value == null) return Vector2.zero;
                if (!(value is Vector2)) throw new InvalidOperationException("Movement action must return Vector2.");
                return (Vector2)value;
            }
            internal bool ReadJump() { Enable(); return pressed(); }
            internal void Disable() { if (enabled) { disable(); enabled = false; } }
            public void Dispose() { Disable(); instance.Dispose(); }
        }
        private sealed class KeyboardReader
        {
            private static readonly string[] Keys = { "dKey", "rightArrowKey", "aKey", "leftArrowKey", "wKey", "upArrowKey", "sKey", "downArrowKey", "spaceKey" };
            private readonly Type type;
            private readonly PropertyInfo current;
            private object device;
            private readonly Func<bool>[] down = new Func<bool>[9];
            private Func<bool> jump;
            internal KeyboardReader()
            {
                type = Type.GetType("UnityEngine.InputSystem.Keyboard, Unity.InputSystem", true);
                current = type.GetProperty("current", BindingFlags.Public | BindingFlags.Static);
            }
            internal void Read(out Vector2 axes, out bool pressed)
            {
                object next = current.GetValue(null);
                if (!ReferenceEquals(next, device))
                {
                    device = next;
                    if (next != null)
                    {
                        for (int i = 0; i < Keys.Length; i++)
                        {
                            object control = type.GetProperty(Keys[i]).GetValue(next);
                            down[i] = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), control, control.GetType().GetProperty("isPressed").GetGetMethod());
                            if (i == 8) jump = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), control, control.GetType().GetProperty("wasPressedThisFrame").GetGetMethod());
                        }
                    }
                }
                axes = Vector2.zero; pressed = false;
                if (device == null) return;
                axes = new Vector2((down[0]() || down[1]() ? 1 : 0) - (down[2]() || down[3]() ? 1 : 0),
                    (down[4]() || down[5]() ? 1 : 0) - (down[6]() || down[7]() ? 1 : 0));
                pressed = jump();
            }
        }
#endif
    }
}
