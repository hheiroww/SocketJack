using System;
using System.IO;
using SocketJack.Multiplayer.Physics;
using SocketJack.Multiplayer.Serialization;
using UnityEngine;

namespace SocketJack.Multiplayer.Components
{
    /// <summary>Canonical server- or owner-authoritative position, rotation and local scale.
    /// The authority controls its transform directly; observing clients/server interpolate received state.</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkIdentity))]
    public sealed class NetworkTransform : MonoBehaviour, INetworkStateController
    {
        [SerializeField] private float interpolationSpeed = 15f;
        [SerializeField] private float teleportDistance = 20f;
        private Vector3 targetPosition, targetScale;
        private Quaternion targetRotation;
        private bool hasTarget;
        private NetworkIdentity identity;
        [SerializeField] private NetworkPrecisionPreset precisionPreset = NetworkPrecisionPreset.FullPrecision;
        [SerializeField] private NetworkPrecisionAsset precisionAsset = null;
        private NetworkPrecisionProfile profile;
        public NetworkPrecisionPreset PrecisionPreset { get => precisionPreset; set { if (value == NetworkPrecisionPreset.Custom) throw new ArgumentException("Assign PrecisionProfile or a profile asset."); precisionPreset = value; profile = null; } }
        public NetworkPrecisionProfile PrecisionProfile { get => profile ?? (profile = precisionAsset != null ? precisionAsset.CreateProfile() : NetworkPrecisionProfile.ForPreset(precisionPreset)); set => profile = value ?? throw new ArgumentNullException(nameof(value)); }
        public bool UsesInput => false;
        public bool CaptureEligible(double now) => true;
        public bool ResolveServerAuthority(bool managerAuthority) => managerAuthority;
        public bool ReceiveOwnerInput(byte[] bytes) => false;
        public void ReceiveRelayedInput(byte[] bytes) { }
        public void ResetPrediction() { hasTarget = false; }
        public bool ValidateState(byte[] state)
        { try { QuantizedTransformSerializer.Deserialize(state, PrecisionProfile); return true; } catch (Exception error) when (error is IOException || error is ArgumentException) { return false; } }

        private void Awake() => identity = GetComponent<NetworkIdentity>();
        public byte[] CaptureState()
        {
            var p = transform.position; var r = transform.rotation; var s = transform.localScale;
            return QuantizedTransformSerializer.Serialize(new TransformSnapshot {
                Position = new System.Numerics.Vector3(p.x, p.y, p.z), Rotation = new System.Numerics.Quaternion(r.x, r.y, r.z, r.w),
                Scale = new System.Numerics.Vector3(s.x, s.y, s.z) }, PrecisionProfile);
        }
        public void ApplyState(byte[] state, bool immediate)
        {
            try
            {
                var snapshot = QuantizedTransformSerializer.Deserialize(state, PrecisionProfile);
                var p = snapshot.Position; var r = snapshot.Rotation; var s = snapshot.Scale;
                targetPosition = new Vector3(p.X, p.Y, p.Z); targetRotation = new Quaternion(r.X, r.Y, r.Z, r.W); targetScale = new Vector3(s.X, s.Y, s.Z); hasTarget = true;
                if (immediate || Vector3.Distance(transform.position, targetPosition) > teleportDistance) Snap();
            }
            catch (Exception error) when (error is IOException || error is ArgumentException) { }
        }
        internal static bool IsValidState(byte[] state)
        {
            if (state == null || state.Length != 40) return false;
            using (var stream = new MemoryStream(state, false))
            using (var reader = new BinaryReader(stream))
            {
                float q = 0;
                for (int i = 0; i < 10; i++)
                { float value = reader.ReadSingle(); if (!Finite(value)) return false; if (i >= 3 && i <= 6) q += value * value; }
                return Finite(q) && q >= 0.0001f;
            }
        }
        private void Update()
        { Interpolate(Time.unscaledDeltaTime); }
        private void Interpolate(float deltaSeconds)
        {
            if (!hasTarget || (identity != null && identity.HasStateAuthority)) return;
            float blend = 1f - Mathf.Exp(-Mathf.Max(0, interpolationSpeed) * Mathf.Max(0, deltaSeconds));
            transform.position = Vector3.Lerp(transform.position, targetPosition, blend);
            transform.rotation = Quaternion.Slerp(transform.rotation, targetRotation, blend);
            transform.localScale = Vector3.Lerp(transform.localScale, targetScale, blend);
        }
        private void Snap() { transform.position = targetPosition; transform.rotation = targetRotation; transform.localScale = targetScale; }
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
