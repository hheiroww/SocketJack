namespace SocketJack.Multiplayer.Components
{
    /// <summary>One movement-state writer on a network prefab. Implementations may adapt an existing
    /// motor; they must use a bounded explicit codec and must not trust types selected by remote data.
    /// NetworkManager owns replication, room visibility and input relay.</summary>
    public interface INetworkStateController
    {
        /// <summary>Whether this controller accepts and relays the owning player's input messages.</summary>
        bool UsesInput { get; }
        /// <summary>Capture the current canonical/predicted state using this prefab's configured codec.</summary>
        byte[] CaptureState();
        /// <summary>Validate a received owner-state payload before authoritative game code applies it.</summary>
        bool ValidateState(byte[] state);
        /// <summary>Apply a spawn baseline immediately or retain a correction target for physics/interpolation.</summary>
        void ApplyState(byte[] state, bool immediate);
        /// <summary>Bound state capture frequency using a monotonic clock in seconds.</summary>
        bool CaptureEligible(double now);
        /// <summary>Resolve the server-assigned object authority when this prefab is spawned.</summary>
        bool ResolveServerAuthority(bool managerAuthority);
        /// <summary>Validate and accept a fresh owner input. False prevents invalid/stale input relay.</summary>
        bool ReceiveOwnerInput(byte[] bytes);
        /// <summary>Apply validated server-relayed input to a nonowning observer.</summary>
        void ReceiveRelayedInput(byte[] bytes);
        /// <summary>Revoke buffered input, sequences and correction history after an ownership epoch changes.</summary>
        void ResetPrediction();
    }

    /// <summary>Optional local motor adapter for the shared two-axis/jump intent. The controller handles
    /// transport, ownership, stale-input timeout and state repairs; the game motor owns its movement rules.
    /// Configure one motor, and do not apply the built-in forces in addition to it.</summary>
    public interface INetworkInputMotor
    {
        void SimulateInput(SocketJack.Multiplayer.Physics.PhysicsInput input, float deltaSeconds);
        void ResetInput();
    }
}
