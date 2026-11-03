using System;
using UnityEngine;

namespace SocketJack.Unity.Components
{
    /// <summary>Optional two-axis movement example. Wire SetMoveIntent from your input system.
    /// Server authority sends owner intent for server simulation; owner authority simulates locally
    /// and replicates canonical transform keyframes through the manager.</summary>
    [RequireComponent(typeof(NetworkIdentity), typeof(NetworkTransform))]
    public sealed class ExamplePlayerController : MonoBehaviour
    {
        [SerializeField] private float speed = 5f;
        private NetworkIdentity identity;
        private Vector2 localIntent, serverIntent;
        private float nextInput, lastInput;

        private void Start()
        {
            identity = GetComponent<NetworkIdentity>();
            if (identity.IsServer && identity.ServerAuthority)
            {
                identity.Manager.InputReceived += OnInput;
                identity.Manager.Server.Ticked += OnTick;
            }
        }

        /// <summary>Call from the game's input system on the owning client. Components are clamped to [-1,1].</summary>
        public void SetMoveIntent(Vector2 intent)
        {
            if (float.IsNaN(intent.x) || float.IsNaN(intent.y) || float.IsInfinity(intent.x) || float.IsInfinity(intent.y)) return;
            localIntent = Vector2.ClampMagnitude(intent, 1f);
        }

        private void Update()
        {
            if (identity == null || !identity.IsOwner || !identity.ServerAuthority || Time.unscaledTime < nextInput) return;
            nextInput = Time.unscaledTime + 1f / 30f;
            var bytes = new byte[2];
            bytes[0] = (byte)Mathf.RoundToInt((localIntent.x + 1f) * 127f);
            bytes[1] = (byte)Mathf.RoundToInt((localIntent.y + 1f) * 127f);
            identity.SendInput(bytes);
        }

        private void OnInput(NetworkInput input)
        {
            if (!identity.HasStateAuthority || !identity.ServerAuthority || input.ObjectId != identity.ObjectId || input.Payload.Length != 2) return;
            serverIntent = Vector2.ClampMagnitude(new Vector2(input.Payload[0] / 127f - 1f, input.Payload[1] / 127f - 1f), 1f);
            lastInput = Time.unscaledTime;
        }

        private void OnTick(uint tick, float delta)
        { SimulateServer(delta); }
        private void SimulateServer(float delta)
        {
            if (!identity.HasStateAuthority || !identity.ServerAuthority || Time.unscaledTime - lastInput > 0.25f) return;
            transform.position += new Vector3(serverIntent.x, 0, serverIntent.y) * Mathf.Max(0, speed) * delta;
        }
        private void FixedUpdate() { SimulateOwner(Time.fixedDeltaTime); }
        private void SimulateOwner(float delta)
        {
            if (identity == null || !identity.HasStateAuthority || identity.ServerAuthority) return;
            transform.position += new Vector3(localIntent.x, 0, localIntent.y) * Mathf.Max(0, speed) * delta;
        }

        private void OnDestroy()
        {
            if (identity == null || identity.Manager == null) return;
            identity.Manager.InputReceived -= OnInput;
            if (identity.Manager.Server != null) identity.Manager.Server.Ticked -= OnTick;
        }
    }
}
