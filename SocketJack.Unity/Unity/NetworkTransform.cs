using System;
using System.IO;
using UnityEngine;

namespace SocketJack.Multiplayer.Components
{
    /// <summary>Canonical server- or owner-authoritative position, rotation and local scale.
    /// The authority controls its transform directly; observing clients/server interpolate received state.</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkIdentity))]
    public sealed class NetworkTransform : MonoBehaviour
    {
        [SerializeField] private float interpolationSpeed = 15f;
        [SerializeField] private float teleportDistance = 20f;
        private Vector3 targetPosition, targetScale;
        private Quaternion targetRotation;
        private bool hasTarget;
        private NetworkIdentity identity;

        private void Awake() => identity = GetComponent<NetworkIdentity>();
        public byte[] CaptureState()
        {
            using (var stream = new MemoryStream(40))
            using (var writer = new BinaryWriter(stream))
            {
                var p = transform.position; var r = transform.rotation; var s = transform.localScale;
                writer.Write(p.x); writer.Write(p.y); writer.Write(p.z);
                writer.Write(r.x); writer.Write(r.y); writer.Write(r.z); writer.Write(r.w);
                writer.Write(s.x); writer.Write(s.y); writer.Write(s.z);
                return stream.ToArray();
            }
        }
        public void ApplyState(byte[] state, bool immediate)
        {
            if (state == null || state.Length != 40) return;
            using (var stream = new MemoryStream(state, false))
            using (var reader = new BinaryReader(stream))
            {
                var p = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                var r = new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                var s = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                if (!Finite(p.x) || !Finite(p.y) || !Finite(p.z) || !Finite(r.x) || !Finite(r.y) || !Finite(r.z) || !Finite(r.w) ||
                    !Finite(s.x) || !Finite(s.y) || !Finite(s.z) || r.x * r.x + r.y * r.y + r.z * r.z + r.w * r.w < 0.0001f) return;
                targetPosition = p; targetRotation = r.normalized; targetScale = s; hasTarget = true;
                if (immediate || Vector3.Distance(transform.position, p) > teleportDistance) Snap();
            }
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
