using System;
using System.IO;
using SocketJack.Multiplayer.Physics;
using SocketJack.Multiplayer.Transport;
using SocketJack.Multiplayer.Serialization;
using UnityEngine;
using UnityEngine.Serialization;
using NumericsVector3 = System.Numerics.Vector3;
using NumericsQuaternion = System.Numerics.Quaternion;

namespace SocketJack.Multiplayer.Components
{
    public enum ObjectMovementMode
    {
        [InspectorName("Static w/Colliders Enabled")] StaticWithColliders = 0,
        [InspectorName("Static")] Static = 1,
        [InspectorName("Physics Enabled")] PhysicsEnabled = 2,
        [InspectorName("Physics w/Colliders Enabled")] PhysicsWithColliders = 3
    }
    public enum ObjectAuthority { InheritManager = 0, Server = 1, Owner = 2 }
    /// <summary>Opt-in Rigidbody networking. Every peer simulates local physics from compact owner intent.
    /// The server relays validated input and sends sparse binary keyframes to repair divergence.
    /// Do not combine this component with NetworkTransform on the same object.</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkIdentity), typeof(Rigidbody))]
    public sealed class SocketJackObject : MonoBehaviour, INetworkStateController
    {
        internal const ushort InputRelayMessageId = 60001;
        [Header("Object networking")]
        [SerializeField] private ObjectMovementMode movementMode = ObjectMovementMode.PhysicsWithColliders;
        [SerializeField, Tooltip("The server chooses this policy when the prefab is spawned. Respawn after changing it.")]
        private ObjectAuthority authority = ObjectAuthority.InheritManager;
        [Header("Input and local physics")]
        [SerializeField] private bool readKeyboard = true;
        [SerializeField] private bool simulatePhysics = true;
        [SerializeField, Tooltip("Optional INetworkInputMotor replaces the built-in force/jump motor.")] private MonoBehaviour inputMotor = null;
        [SerializeField, Min(1)] private float inputSendRate = 30;
        [SerializeField] private bool sendInputOnChange = true;
        [SerializeField, Range(2, 20)] private float inputKeepAliveRate = 5;
        [SerializeField, Min(0), FormerlySerializedAs("acceleration")] private float movementForce = 25;
        [SerializeField] private ForceMode movementForceMode = ForceMode.Force;
        [SerializeField, Min(0)] private float maxSpeed = 6;
        [SerializeField, Min(0)] private float jumpVelocity = 5;
        [SerializeField, Min(0.1f)] private float inputTimeout = 0.5f;
        [Header("Sparse authority keyframes")]
        [SerializeField, Range(1, 20)] private float keyframeRate = 5;
        [SerializeField] private DeliveryMode inputDelivery = DeliveryMode.ReliableUnordered;
        [Header("Correction and visual smoothing")]
        [SerializeField] private bool reconcile = true;
        [SerializeField, Min(0.02f)] private float correctionTime = 0.15f;
        [SerializeField, Min(0)] private float maxCorrectionSpeed = 3;
        [SerializeField, Min(0)] private float correctionDeadZone = 0.08f;
        [SerializeField, Min(0.1f)] private float snapDistance = 5;
        [SerializeField, Range(0, 0.5f)] private float maxExtrapolation = 0.25f;
        [SerializeField, Range(0, 0.25f)] private float estimatedOneWayLatency = 0.02f;
        [SerializeField, Range(0, 1)] private float ownerCorrectionWeight = 0.35f;
        [SerializeField, Min(0)] private float smoothingSpeed = 15;
        [SerializeField] private Transform visual = null;
        private NetworkIdentity identity;
        private Rigidbody body;
        private PhysicsInput intent;
        private PhysicsSnapshot authoritative;
        private Vector2 localAxes;
        private bool localJump, localJumpPredicted, jumpPending, hasInput, hasSnapshot;
        private uint nextSequence, lastAcceptedSequence, lastRelaySequence, physicsTick;
        private bool acceptedAnySequence, relayedAnySequence;
        private double nextSend, nextKeepAlive, nextKeyframe, lastInputAt, snapshotReceivedAt;
        private Vector2 lastSentAxes;
        private Vector3 visualOrigin;
        private Quaternion visualRotation;
        private Vector3 previousVisualPosition;
        private bool observedVisual;
        private bool movementModeDirty = true;
        [Header("Network precision")]
        [SerializeField] private NetworkPrecisionPreset precisionPreset = NetworkPrecisionPreset.FullPrecision;
        [SerializeField] private NetworkPrecisionAsset precisionAsset = null;
        private NetworkPrecisionProfile precisionProfile;
        public NetworkPrecisionPreset PrecisionPreset { get => precisionPreset; set { if (value == NetworkPrecisionPreset.Custom) throw new ArgumentException("Assign PrecisionProfile for custom settings."); precisionPreset = value; precisionProfile = null; } }
        public NetworkPrecisionProfile PrecisionProfile
        {
            get { var adaptive = GetComponent<SocketJackAdaptiveObject>(); return adaptive != null && adaptive.isActiveAndEnabled ? adaptive.CurrentProfile : precisionProfile ?? (precisionProfile = precisionAsset != null ? precisionAsset.CreateProfile() : NetworkPrecisionProfile.ForPreset(precisionPreset)); }
            set => precisionProfile = value ?? throw new ArgumentNullException(nameof(value));
        }
        private NetworkPrecisionProfile DecodeProfile(byte[] bytes) => NetworkPrecisionFamily.ForState(bytes, false, PrecisionProfile, GetComponent<SocketJackAdaptiveObject>()?.isActiveAndEnabled == true);
#if !ENABLE_LEGACY_INPUT_MANAGER
        private bool keyboardWarningLogged;
#endif
        public long InputsSent { get; private set; }
        public long InputsReceived { get; private set; }
        public long KeyframesReceived { get; private set; }
        public long HardCorrections { get; private set; }
        public float LastPositionError { get; private set; }
        public float PeakPositionError { get; private set; }
        public float PeakNetworkVelocityCorrection { get; private set; }
        public float PeakVisualStep { get; private set; }
        public long OwnerStatesSent { get; private set; }
        public bool IsOwner => identity != null && identity.IsOwner;
        public bool UsesServerAuthority => identity != null && identity.Manager != null ? identity.ServerAuthority :
            ResolveServerAuthority(true);
        public bool HasStateAuthority => identity != null && identity.Manager != null &&
            (UsesServerAuthority || identity.OwnerId == 0 ? identity.IsServer : identity.IsOwner);
        public ObjectMovementMode MovementMode { get => movementMode; set { movementMode = value; movementModeDirty = true; } }
        public ObjectAuthority Authority { get => authority; set => authority = value; }
        public float KeyframeRate { get => keyframeRate; set => keyframeRate = Mathf.Clamp(value, 1, 20); }
        public bool ReadKeyboard { get => readKeyboard; set => readKeyboard = value; }
        public bool UsesInput => true;
        public MonoBehaviour InputMotor { get => inputMotor; set { if (value != null && !(value is INetworkInputMotor)) throw new ArgumentException("Motor must implement INetworkInputMotor."); inputMotor = value; } }
        public Transform Visual
        {
            get => visual;
            set { visual = value; if (visual != null) { visualOrigin = visual.localPosition; visualRotation = visual.localRotation; } }
        }

        private void Awake()
        {
            identity = GetComponent<NetworkIdentity>(); body = GetComponent<Rigidbody>();
            ApplyMovementMode();
            if (visual != null) { visualOrigin = visual.localPosition; visualRotation = visual.localRotation; }
            if (GetComponent<NetworkTransform>() != null)
                Debug.LogError("SocketJackObject and NetworkTransform must not control the same Rigidbody.", this);
        }

        private Vector3 BodyVelocity
        {
            get
            {
#if UNITY_6000_0_OR_NEWER
                return body.linearVelocity;
#else
                return body.velocity;
#endif
            }
            set
            {
#if UNITY_6000_0_OR_NEWER
                body.linearVelocity = value;
#else
                body.velocity = value;
#endif
            }
        }

        /// <summary>Feed a game input action or test harness. Keyboard input can be disabled in the Inspector.</summary>
        public void SetInput(float horizontal, float vertical, bool jump = false)
        {
            if (!Finite(horizontal) || !Finite(vertical)) return;
            localAxes = Vector2.ClampMagnitude(new Vector2(horizontal, vertical), 1);
            localJump |= jump;
        }
        private void Update()
        {
            if (movementModeDirty) ApplyMovementMode();
            if (identity != null && identity.IsOwner && !UsesServerAuthority && CaptureEligible(Time.unscaledTimeAsDouble))
            {
                if (identity.Manager.Client.SendOwnerState(identity.ObjectId, CaptureState())) OwnerStatesSent++;
            }
            if (identity == null || !identity.IsOwner) return;
            if (body.isKinematic && inputMotor == null) return;
            if (readKeyboard)
            {
#if ENABLE_LEGACY_INPUT_MANAGER
                float h = (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow) ? 1 : 0) -
                    (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow) ? 1 : 0);
                float v = (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow) ? 1 : 0) -
                    (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow) ? 1 : 0);
                SetInput(h, v, Input.GetKeyDown(KeyCode.Space));
#else
                readKeyboard = false;
                if (!keyboardWarningLogged)
                {
                    Debug.LogWarning("SocketJack keyboard polling requires the legacy Input Manager or Both. Polling is disabled; call SetInput from your input actions.", this);
                    keyboardWarningLogged = true;
                }
#endif
            }
            // Prediction is immediate; input transmission has its own bounded rate.
            intent.Horizontal = localAxes.x; intent.Vertical = localAxes.y;
            if (localJump && !localJumpPredicted) { jumpPending = true; localJumpPredicted = true; }
            hasInput = true; lastInputAt = Time.unscaledTimeAsDouble;
            double now = Time.unscaledTimeAsDouble;
            if (now < nextSend) return;
            bool changed = (localAxes - lastSentAxes).sqrMagnitude > 1f / (32767f * 32767f);
            if (sendInputOnChange && !changed && !localJump && now < nextKeepAlive) return;
            nextSend = now + 1d / Math.Max(1, inputSendRate);
            var input = new PhysicsInput { Sequence = unchecked(++nextSequence), Tick = physicsTick,
                Horizontal = localAxes.x, Vertical = localAxes.y, Jump = localJump };
            byte[] bytes = PhysicsBinarySerializer.SerializeInput(input);
            if (identity.Manager.Client.SendInput(identity.ObjectId, bytes, inputDelivery))
            {
                InputsSent++; lastSentAxes = localAxes; nextKeepAlive = now + 1d / Math.Max(2, inputKeepAliveRate);
                localJump = false; localJumpPredicted = false;
            }
        }

        public bool ReceiveOwnerInput(byte[] bytes)
        {
            PhysicsInput input;
            try { input = PhysicsBinarySerializer.DeserializeInput(bytes); }
            catch (Exception error) when (error is ArgumentException || error is IOException || error is InvalidDataException) { return false; }
            if (acceptedAnySequence && !PhysicsReconciler.IsNewer(input.Sequence, lastAcceptedSequence)) return false;
            acceptedAnySequence = true; lastAcceptedSequence = input.Sequence;
            ApplyIntent(input); InputsReceived++; return true;
        }
        public void ReceiveRelayedInput(byte[] bytes)
        {
            if (identity == null || identity.IsServer || identity.IsOwner) return;
            PhysicsInput input;
            try { input = PhysicsBinarySerializer.DeserializeInput(bytes); }
            catch (Exception error) when (error is ArgumentException || error is IOException || error is InvalidDataException) { return; }
            if (relayedAnySequence && !PhysicsReconciler.IsNewer(input.Sequence, lastRelaySequence)) return;
            relayedAnySequence = true; lastRelaySequence = input.Sequence;
            ApplyIntent(input); InputsReceived++;
        }
        private void ApplyIntent(PhysicsInput input)
        {
            intent = input; hasInput = true; lastInputAt = Time.unscaledTimeAsDouble;
            if (input.Jump) jumpPending = true;
        }
        private void FixedUpdate()
        {
            physicsTick = unchecked(physicsTick + 1);
            if (identity == null || identity.Manager == null) return;
            bool liveInput = hasInput && Time.unscaledTimeAsDouble - lastInputAt <= Math.Max(inputTimeout, 2f / Math.Max(2, inputKeepAliveRate));
            if (simulatePhysics && (!body.isKinematic || inputMotor != null))
            {
                if (inputMotor is INetworkInputMotor motor)
                {
                    var motorInput = liveInput ? intent : default; motorInput.Jump = liveInput && jumpPending;
                    motor.SimulateInput(motorInput, Time.fixedDeltaTime);
                    body.position = transform.position; body.rotation = transform.rotation;
                }
                else if (liveInput)
                {
                var direction = Vector3.ClampMagnitude(new Vector3(intent.Horizontal, 0, intent.Vertical), 1);
                body.AddForce(direction * Mathf.Max(0, movementForce), movementForceMode);
                var velocity = BodyVelocity;
                var planar = Vector3.ClampMagnitude(new Vector3(velocity.x, 0, velocity.z), Mathf.Max(0, maxSpeed));
                BodyVelocity = new Vector3(planar.x, velocity.y, planar.z);
                if (jumpPending && Grounded()) BodyVelocity = new Vector3(BodyVelocity.x, jumpVelocity, BodyVelocity.z);
                }
            }
            jumpPending = false;
            if (!HasStateAuthority && hasSnapshot && reconcile) Reconcile();
        }
        public bool ResolveServerAuthority(bool managerAuthority) => authority == ObjectAuthority.InheritManager ? managerAuthority : authority == ObjectAuthority.Server;
        private void OnValidate() { movementModeDirty = true; }
        private void ApplyMovementMode()
        {
            if (body == null) body = GetComponent<Rigidbody>();
            if (body == null) return;
            body.isKinematic = movementMode == ObjectMovementMode.Static || movementMode == ObjectMovementMode.StaticWithColliders;
            bool collidersEnabled = movementMode == ObjectMovementMode.StaticWithColliders || movementMode == ObjectMovementMode.PhysicsWithColliders;
            foreach (var collider in GetComponentsInChildren<Collider>(true)) collider.enabled = collidersEnabled;
            movementModeDirty = false;
        }
        private bool Grounded()
        {
            var collider = GetComponent<Collider>();
            float distance = collider == null ? 0.6f : collider.bounds.extents.y + 0.12f;
            return UnityEngine.Physics.Raycast(body.position, Vector3.down, distance, ~0, QueryTriggerInteraction.Ignore);
        }
        public bool CaptureEligible(double now)
        {
            if (now < nextKeyframe) return false;
            nextKeyframe = now + 1d / Math.Max(1, keyframeRate); return true;
        }
        public byte[] CaptureState()
        {
            if (body == null) body = GetComponent<Rigidbody>();
            return QuantizedPhysicsSerializer.SerializeSnapshot(new PhysicsSnapshot { Tick = physicsTick,
                LastInputSequence = UsesServerAuthority ? lastAcceptedSequence : nextSequence, Time = Time.fixedTimeAsDouble,
                Position = ToNumerics(body.position), Rotation = ToNumerics(body.rotation),
                Velocity = ToNumerics(BodyVelocity), AngularVelocity = ToNumerics(body.angularVelocity) }, PrecisionProfile);
        }
        public void ApplyState(byte[] bytes, bool immediate)
        {
            PhysicsSnapshot snapshot;
            try { snapshot = QuantizedPhysicsSerializer.DeserializeSnapshot(bytes, DecodeProfile(bytes)); }
            catch (Exception error) when (error is ArgumentException || error is IOException || error is InvalidDataException) { return; }
            if (hasSnapshot && !PhysicsReconciler.IsNewer(snapshot.Tick, authoritative.Tick)) return;
            authoritative = snapshot; hasSnapshot = true; snapshotReceivedAt = Time.unscaledTimeAsDouble; KeyframesReceived++;
            if (immediate)
            {
                transform.SetPositionAndRotation(ToUnity(snapshot.Position), ToUnity(snapshot.Rotation));
                body.position = ToUnity(snapshot.Position); body.rotation = ToUnity(snapshot.Rotation);
                if (!body.isKinematic) { BodyVelocity = ToUnity(snapshot.Velocity); body.angularVelocity = ToUnity(snapshot.AngularVelocity); }
            }
        }
        private void Reconcile()
        {
            double age = Time.unscaledTimeAsDouble - snapshotReceivedAt + estimatedOneWayLatency;
            if (age > maxExtrapolation + 0.5) return; // Keep simulating locally through a temporary keyframe gap.
            var target = PhysicsReconciler.Extrapolate(authoritative, age, maxExtrapolation);
            var targetPosition = ToUnity(target.Position);
            float error = Vector3.Distance(body.position, targetPosition);
            LastPositionError = error; PeakPositionError = Mathf.Max(PeakPositionError, error);
            if (error > Mathf.Max(0.1f, snapDistance))
            {
                Debug.LogWarning("SOCKETJACK_PHYSICS_HARD_CORRECTION object=" + identity.ObjectId + " old=" + body.position +
                    " target=" + targetPosition + " tick=" + authoritative.Tick + " age=" + age + " time=" + Time.unscaledTimeAsDouble, this);
                Vector3 oldVisualPosition = visual == null ? Vector3.zero : visual.position;
                Quaternion oldVisualRotation = visual == null ? Quaternion.identity : visual.rotation;
                body.position = targetPosition; body.rotation = ToUnity(target.Rotation);
                if (!body.isKinematic) { BodyVelocity = ToUnity(target.Velocity); body.angularVelocity = ToUnity(target.AngularVelocity); }
                if (visual != null) { visual.position = oldVisualPosition; visual.rotation = oldVisualRotation; }
                HardCorrections++; return;
            }
            if (error <= correctionDeadZone) return;
            if (body.isKinematic)
            {
                float factor = PhysicsReconciler.SmoothingFactor(smoothingSpeed, Time.fixedDeltaTime);
                body.MovePosition(Vector3.Lerp(body.position, targetPosition, factor));
                body.MoveRotation(Quaternion.Slerp(body.rotation, ToUnity(target.Rotation), factor));
                return;
            }
            var corrected = PhysicsReconciler.CalculateVelocityCorrection(ToNumerics(body.position), ToNumerics(BodyVelocity),
                target, Mathf.Max(0.02f, correctionTime), Mathf.Max(0, maxCorrectionSpeed));
            float weight = identity.IsOwner ? ownerCorrectionWeight : 1;
            float blend = PhysicsReconciler.SmoothingFactor(smoothingSpeed, Time.fixedDeltaTime) * weight;
            Vector3 correction = ToUnity(corrected) - BodyVelocity;
            Vector3 correctedVelocity = Vector3.Lerp(BodyVelocity, ToUnity(target.Velocity) + correction, blend);
            PeakNetworkVelocityCorrection = Mathf.Max(PeakNetworkVelocityCorrection, (correctedVelocity - BodyVelocity).magnitude);
            BodyVelocity = correctedVelocity;
            body.angularVelocity = Vector3.Lerp(body.angularVelocity, ToUnity(target.AngularVelocity), blend);
            body.MoveRotation(Quaternion.Slerp(body.rotation, ToUnity(target.Rotation), blend));
        }
        public bool ValidateState(byte[] bytes)
        {
            try { QuantizedPhysicsSerializer.DeserializeSnapshot(bytes, DecodeProfile(bytes)); return true; }
            catch (Exception error) when (error is ArgumentException || error is IOException) { return false; }
        }
        public void ResetPrediction()
        {
            intent = default; localAxes = lastSentAxes = Vector2.zero;
            localJump = localJumpPredicted = jumpPending = hasInput = hasSnapshot = false;
            acceptedAnySequence = relayedAnySequence = false;
            nextSequence = lastAcceptedSequence = lastRelaySequence = 0;
            nextSend = nextKeepAlive = nextKeyframe = 0;
            (inputMotor as INetworkInputMotor)?.ResetInput();
        }
        private void LateUpdate()
        {
            if (visual != null)
            {
                float blend = PhysicsReconciler.SmoothingFactor(smoothingSpeed, Time.unscaledDeltaTime);
                visual.localPosition = Vector3.Lerp(visual.localPosition, visualOrigin, blend);
                visual.localRotation = Quaternion.Slerp(visual.localRotation, visualRotation, blend);
            }
            Vector3 position = visual == null ? transform.position : visual.position;
            if (observedVisual) PeakVisualStep = Mathf.Max(PeakVisualStep, Vector3.Distance(position, previousVisualPosition));
            previousVisualPosition = position; observedVisual = true;
        }
        private static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
        private static NumericsVector3 ToNumerics(Vector3 v) => new NumericsVector3(v.x, v.y, v.z);
        private static NumericsQuaternion ToNumerics(Quaternion q) => new NumericsQuaternion(q.x, q.y, q.z, q.w);
        private static Vector3 ToUnity(NumericsVector3 v) => new Vector3(v.X, v.Y, v.Z);
        private static Quaternion ToUnity(NumericsQuaternion q) => new Quaternion(q.X, q.Y, q.Z, q.W);
    }
}
