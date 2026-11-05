using UnityEngine;
using SocketJack.Multiplayer.Physics;
namespace SocketJack.Multiplayer.Components
{
    /// <summary>CharacterController example adapter. Replaces the built-in force motor; game projects can supply their own INetworkInputMotor.</summary>
    [RequireComponent(typeof(CharacterController), typeof(SocketJackObject)), DisallowMultipleComponent]
    public sealed class SocketJackCharacterMotor : MonoBehaviour, INetworkInputMotor
    {
        [Min(0)] public float Speed = 6;
        public float Gravity = -9.81f;
        [Min(0)] public float JumpSpeed = 5;
        private CharacterController controller;
        private float vertical;
        private void Awake()
        {
            controller = GetComponent<CharacterController>();
            var network = GetComponent<SocketJackObject>(); network.MovementMode = ObjectMovementMode.StaticWithColliders; network.InputMotor = this;
        }
        public void SimulateInput(PhysicsInput input, float deltaSeconds)
        {
            if (!controller.enabled) return;
            if (controller.isGrounded && vertical < 0) vertical = -1;
            if (input.Jump && controller.isGrounded) vertical = JumpSpeed;
            vertical += Gravity * deltaSeconds;
            var direction = Vector3.ClampMagnitude(new Vector3(input.Horizontal, 0, input.Vertical), 1);
            controller.Move((direction * Mathf.Max(0, Speed) + Vector3.up * vertical) * deltaSeconds);
        }
        public void ResetInput() { vertical = 0; }
    }
}
