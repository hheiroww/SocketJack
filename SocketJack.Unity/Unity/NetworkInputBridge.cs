using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace SocketJack.Multiplayer.Components
{
    /// <summary>Owner-only input adapter. Action copies prevent a remote player's teardown disabling local actions.</summary>
    [DisallowMultipleComponent, RequireComponent(typeof(NetworkIdentity))]
    public sealed class NetworkInputBridge : NetworkBehaviour
    {
        public static bool SuppressGameplayInput { get; set; }
#if ENABLE_INPUT_SYSTEM
        public InputActionReference Move;
        public InputActionReference Jump;
        private InputAction move, jump;
#endif
        private SocketJackObject body3D;
        private SocketJackObject2D body2D;
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
            if (move == null && Move != null) move = Move.action?.Clone();
            if (jump == null && Jump != null) jump = Jump.action?.Clone();
            move?.Enable(); jump?.Enable();
            if (move != null) axes = move.ReadValue<Vector2>(); pressed = jump != null && jump.WasPressedThisFrame();
#elif ENABLE_LEGACY_INPUT_MANAGER
            axes = new Vector2((Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow) ? 1 : 0) - (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow) ? 1 : 0),
                (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow) ? 1 : 0) - (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow) ? 1 : 0));
            pressed = Input.GetKeyDown(KeyCode.Space);
#endif
            body3D?.SetInput(axes.x, axes.y, pressed); body2D?.SetInput(axes.x, axes.y, pressed);
        }
        private void DisableInput()
        {
#if ENABLE_INPUT_SYSTEM
            move?.Disable(); jump?.Disable();
#endif
            body3D?.SetInput(0, 0); body2D?.SetInput(0, 0);
        }
        private void OnDisable() => DisableInput();
        private void OnDestroy()
        {
            DisableInput();
#if ENABLE_INPUT_SYSTEM
            move?.Dispose(); jump?.Dispose();
#endif
        }
    }
}
