using System;
using System.IO;
using SocketJack.Multiplayer.Physics;
using SocketJack.Multiplayer.Serialization;
using SocketJack.Multiplayer.Transport;
using UnityEngine;
using NumericsVector3 = System.Numerics.Vector3;
using NumericsQuaternion = System.Numerics.Quaternion;

namespace SocketJack.Multiplayer.Components
{
    public enum NetworkMovement2D { Platformer = 0, TopDown = 1 }

    /// <summary>Real Rigidbody2D movement, predicted locally from bounded owner intent and repaired from
    /// canonical keyframes. Platformer uses horizontal movement and grounded jump; TopDown uses both axes.
    /// Configure native gravityScale (normally zero for TopDown), collider shape, friction and constraints.
    /// Physics is not deterministic: this is sparse repair, not collision rollback.</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkIdentity), typeof(Rigidbody2D))]
    public sealed class SocketJackObject2D : MonoBehaviour, INetworkStateController
    {
        private enum PrecisionChoice { FullPrecision = 0, Balanced = 1, Compact = 2 }
        [Header("Object networking")]
        [SerializeField] private ObjectMovementMode movementMode = ObjectMovementMode.PhysicsWithColliders;
        [SerializeField] private ObjectAuthority authority = ObjectAuthority.InheritManager;
        [SerializeField] private PrecisionChoice precision = PrecisionChoice.FullPrecision;
        [SerializeField] private NetworkPrecisionAsset precisionAsset = null;
        [Header("Input and local 2D physics")]
        [SerializeField] private NetworkMovement2D movement = NetworkMovement2D.Platformer;
        [SerializeField] private bool readKeyboard = true;
        [SerializeField] private bool simulatePhysics = true;
        [SerializeField, Tooltip("Optional game motor implementing INetworkInputMotor; replaces the built-in force/jump motor.")]
        private MonoBehaviour inputMotor;
        [SerializeField, Range(1, 120)] private float inputSendRate = 30;
        [SerializeField] private bool sendInputOnChange = true;
        [SerializeField, Range(2, 20)] private float inputKeepAliveRate = 5;
        [SerializeField, Min(0)] private float movementForce = 25;
        [SerializeField] private ForceMode2D movementForceMode = ForceMode2D.Force;
        [SerializeField, Min(0)] private float maxSpeed = 6;
        [SerializeField, Min(0)] private float jumpVelocity = 5;
        [SerializeField, Min(0.1f)] private float inputTimeout = 0.5f;
        [Header("2D ground detection")]
        [SerializeField] private LayerMask groundLayers = ~0;
        [SerializeField, Min(0.001f)] private float groundProbeDistance = 0.08f;
        [SerializeField, Range(0, 1)] private float minimumGroundNormalY = 0.5f;
        [Header("Sparse authority keyframes")]
        [SerializeField, Range(1, 20)] private float keyframeRate = 5;
        [SerializeField] private DeliveryMode inputDelivery = DeliveryMode.ReliableUnordered;
        [Header("Correction and visual smoothing")]
        [SerializeField] private bool reconcile = true;
        [SerializeField, Min(0.02f)] private float correctionTime = 0.15f;
        [SerializeField, Min(0)] private float maxCorrectionSpeed = 3;
        [SerializeField, Min(0)] private float correctionDeadZone = 0.08f;
        [SerializeField, Min(0.1f)] private float snapDistance = 5;
        [SerializeField, Min(0)] private float rotationDeadZone = 1;
        [SerializeField, Min(0)] private float maxAngularCorrectionSpeed = 180;
        [SerializeField, Range(0, 0.5f)] private float maxExtrapolation = 0.25f;
        [SerializeField, Range(0, 0.25f)] private float estimatedOneWayLatency = 0.02f;
        [SerializeField, Range(0, 1)] private float ownerCorrectionWeight = 0.35f;
        [SerializeField, Min(0)] private float smoothingSpeed = 15;
        [SerializeField] private Transform visual;
        private readonly RaycastHit2D[] groundHits = new RaycastHit2D[16];
        private NetworkIdentity identity;
        private Rigidbody2D body;
        private NetworkPrecisionProfile profileOverride;
        private PhysicsInput intent;
        private PhysicsSnapshot authoritative;
        private Vector2 localAxes, lastSentAxes;
        private bool localJump, localJumpPredicted, jumpPending, hasInput, hasSnapshot, movementModeDirty = true;
        private bool acceptedAnySequence, relayedAnySequence, hasPredictedJumpSequence;
        private uint nextSequence, lastAcceptedSequence, lastRelaySequence, physicsTick, predictedJumpSequence;
        private double nextSend, nextKeepAlive, nextKeyframe, lastInputAt, snapshotReceivedAt;
        private Vector3 visualOrigin, previousVisualPosition;
        private Quaternion visualRotation;
        private bool observedVisual;
#if !ENABLE_LEGACY_INPUT_MANAGER
        private bool keyboardWarningLogged;
#endif
        public bool UsesInput => true;
        public bool IsOwner => identity != null && identity.IsOwner;
        public bool UsesServerAuthority => identity != null && identity.Manager != null ? identity.ServerAuthority : ResolveServerAuthority(true);
        public bool HasStateAuthority => identity != null && identity.HasStateAuthority;
        public ObjectMovementMode MovementMode { get => movementMode; set { movementMode = value; movementModeDirty = true; } }
        public ObjectAuthority Authority { get => authority; set => authority = value; }
        public NetworkMovement2D Movement { get => movement; set => movement = value; }
        public bool ReadKeyboard { get => readKeyboard; set => readKeyboard = value; }
        public bool SimulatePhysics { get => simulatePhysics; set => simulatePhysics = value; }
        public float KeyframeRate { get => keyframeRate; set => keyframeRate = Mathf.Clamp(value, 1, 20); }
        public float MovementForce { get => movementForce; set => movementForce = Mathf.Max(0, value); }
        public float MaxSpeed { get => maxSpeed; set => maxSpeed = Mathf.Max(0, value); }
        public float JumpVelocity { get => jumpVelocity; set => jumpVelocity = Mathf.Max(0, value); }
        public MonoBehaviour InputMotor
        {
            get => inputMotor;
            set { if (value != null && !(value is INetworkInputMotor)) throw new ArgumentException("Motor must implement INetworkInputMotor."); inputMotor = value; }
        }
        public NetworkPrecisionPreset PrecisionPreset
        {
            get => (NetworkPrecisionPreset)(int)precision;
            set { if (value == NetworkPrecisionPreset.Custom) throw new ArgumentException("Assign a custom PrecisionProfile before spawning."); RequireUnboundPrecision(); precision = (PrecisionChoice)(int)value; profileOverride = null; }
        }
        /// <summary>Optional immutable custom profile. Assign before the manager binds/spawns this instance.</summary>
        public NetworkPrecisionProfile PrecisionProfile
        {
            get { var adaptive = GetComponent<SocketJackAdaptiveObject>(); return adaptive != null && adaptive.isActiveAndEnabled ? adaptive.CurrentProfile : profileOverride ?? (profileOverride = precisionAsset != null ? precisionAsset.CreateProfile() : NetworkPrecisionProfile.ForPreset((NetworkPrecisionPreset)(int)precision)); }
            set { RequireUnboundPrecision(); profileOverride = value ?? throw new ArgumentNullException(nameof(value)); }
        }
        private NetworkPrecisionProfile DecodeProfile(byte[] bytes) => NetworkPrecisionFamily.ForState(bytes, false, PrecisionProfile, GetComponent<SocketJackAdaptiveObject>()?.isActiveAndEnabled == true);
        public Transform Visual { get => visual; set { visual = value; RememberVisual(); } }
        public long InputsSent { get; private set; }
        public long InputsReceived { get; private set; }
        public long KeyframesReceived { get; private set; }
        public long OwnerStatesSent { get; private set; }
        public long HardCorrections { get; private set; }
        public long JumpsPerformed { get; private set; }
        public float LastPositionError { get; private set; }
        public float PeakPositionError { get; private set; }
        public float PeakNetworkVelocityCorrection { get; private set; }
        public float PeakVisualStep { get; private set; }
        public bool IsGrounded => body != null && Grounded();

        private void Awake()
        {
            EnsureBody(); ApplyMovementMode(); RememberVisual();
            if (inputMotor != null && !(inputMotor is INetworkInputMotor))
                throw new InvalidOperationException("SocketJackObject2D Input Motor must implement INetworkInputMotor.");
        }
        private void EnsureBody()
        { if (identity == null) identity = GetComponent<NetworkIdentity>(); if (body == null) body = GetComponent<Rigidbody2D>(); }
        private void RequireUnboundPrecision()
        { if (identity != null && identity.Manager != null) throw new InvalidOperationException("Configure precision before spawning; peers must use the same profile."); }
        private Vector2 BodyVelocity
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
        public void SetInput(float horizontal, float vertical, bool jump = false)
        { if (!Finite(horizontal) || !Finite(vertical)) return; localAxes = Vector2.ClampMagnitude(new Vector2(horizontal, vertical), 1); localJump |= jump; }
        private void Update()
        {
            EnsureBody(); if (movementModeDirty) ApplyMovementMode();
            if (IsOwner && !UsesServerAuthority && CaptureEligible(Time.unscaledTimeAsDouble))
                if (identity.Manager.Client.SendOwnerState(identity.ObjectId, CaptureState())) OwnerStatesSent++;
            if (!IsOwner || body.bodyType != RigidbodyType2D.Dynamic) return;
            if (readKeyboard)
            {
#if ENABLE_LEGACY_INPUT_MANAGER
                float h = (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow) ? 1 : 0) - (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow) ? 1 : 0);
                float v = (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow) ? 1 : 0) - (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow) ? 1 : 0);
                SetInput(h, v, Input.GetKeyDown(KeyCode.Space));
#else
                readKeyboard = false;
                if (!keyboardWarningLogged) { keyboardWarningLogged = true; Debug.LogWarning("SocketJack2D keyboard polling requires Input Manager (Old) or Both. Feed SetInput from your Input System actions.", this); }
#endif
            }
            intent.Horizontal = localAxes.x; intent.Vertical = localAxes.y;
            if (localJump && !localJumpPredicted) { jumpPending = true; localJumpPredicted = true; }
            hasInput = true; lastInputAt = Time.unscaledTimeAsDouble;
            double now = Time.unscaledTimeAsDouble; if (now < nextSend) return;
            bool changed = (localAxes - lastSentAxes).sqrMagnitude > 1f / (32767f * 32767f);
            if (sendInputOnChange && !changed && !localJump && now < nextKeepAlive) return;
            nextSend = now + 1d / Math.Max(1, inputSendRate);
            uint sequence = unchecked(nextSequence + 1);
            var input = new PhysicsInput { Sequence = sequence, Tick = physicsTick, Horizontal = localAxes.x, Vertical = localAxes.y, Jump = localJump };
            if (identity.Manager.Client.SendInput(identity.ObjectId, PhysicsBinarySerializer.SerializeInput(input), inputDelivery))
            {
                nextSequence = sequence; intent.Sequence = sequence; intent.Tick = physicsTick;
                if (localJump && localJumpPredicted) { hasPredictedJumpSequence = true; predictedJumpSequence = sequence; }
                InputsSent++; lastSentAxes = localAxes; nextKeepAlive = now + 1d / Math.Max(2, inputKeepAliveRate);
                localJump = localJumpPredicted = false;
            }
        }
        public bool ReceiveOwnerInput(byte[] bytes)
        {
            PhysicsInput input;
            try { input = PhysicsBinarySerializer.DeserializeInput(bytes); }
            catch (Exception error) when (error is ArgumentException || error is IOException || error is InvalidDataException) { return false; }
            if (acceptedAnySequence && !PhysicsReconciler.IsNewer(input.Sequence, lastAcceptedSequence)) return false;
            acceptedAnySequence = true; lastAcceptedSequence = input.Sequence;
            bool predictedOnHost = IsOwner && hasPredictedJumpSequence && input.Sequence == predictedJumpSequence;
            ApplyIntent(input, !predictedOnHost); InputsReceived++; return true;
        }
        public void ReceiveRelayedInput(byte[] bytes)
        {
            if (identity == null || identity.IsServer || identity.IsOwner) return;
            PhysicsInput input;
            try { input = PhysicsBinarySerializer.DeserializeInput(bytes); }
            catch (Exception error) when (error is ArgumentException || error is IOException || error is InvalidDataException) { return; }
            if (relayedAnySequence && !PhysicsReconciler.IsNewer(input.Sequence, lastRelaySequence)) return;
            relayedAnySequence = true; lastRelaySequence = input.Sequence; ApplyIntent(input, true); InputsReceived++;
        }
        private void ApplyIntent(PhysicsInput input, bool applyJump)
        { intent = input; hasInput = true; lastInputAt = Time.unscaledTimeAsDouble; if (applyJump && input.Jump) jumpPending = true; }
        private void FixedUpdate()
        {
            physicsTick = unchecked(physicsTick + 1); EnsureBody();
            if (identity == null || identity.Manager == null) return;
            bool fresh = hasInput && Time.unscaledTimeAsDouble - lastInputAt <= Math.Max(inputTimeout, 2f / Math.Max(2, inputKeepAliveRate));
            if (simulatePhysics && body.bodyType == RigidbodyType2D.Dynamic && fresh)
            {
                var step = intent; step.Jump = jumpPending;
                if (inputMotor is INetworkInputMotor motor) motor.SimulateInput(step, Time.fixedDeltaTime);
                else SimulateBuiltIn(step);
            }
            jumpPending = false;
            if (!HasStateAuthority && hasSnapshot && reconcile) Reconcile();
        }
        private void SimulateBuiltIn(PhysicsInput input)
        {
            Vector2 direction = movement == NetworkMovement2D.Platformer ? new Vector2(input.Horizontal, 0) : Vector2.ClampMagnitude(new Vector2(input.Horizontal, input.Vertical), 1);
            body.AddForce(direction * Mathf.Max(0, movementForce), movementForceMode);
            Vector2 velocity = BodyVelocity;
            if (movement == NetworkMovement2D.Platformer)
            {
                velocity.x = Mathf.Clamp(velocity.x, -Mathf.Max(0, maxSpeed), Mathf.Max(0, maxSpeed));
                if (input.Jump && Grounded()) { velocity.y = Mathf.Max(0, jumpVelocity); JumpsPerformed++; }
            }
            else velocity = Vector2.ClampMagnitude(velocity, Mathf.Max(0, maxSpeed));
            BodyVelocity = velocity;
        }
        private bool Grounded()
        {
            var filter = new ContactFilter2D(); filter.SetLayerMask(groundLayers); filter.useTriggers = false;
            int count = body.Cast(Vector2.down, filter, groundHits, Mathf.Max(0.001f, groundProbeDistance));
            for (int i = 0; i < count; i++)
                if (groundHits[i].collider != null && groundHits[i].rigidbody != body && groundHits[i].normal.y >= minimumGroundNormalY) return true;
            return false;
        }
        public bool ResolveServerAuthority(bool managerAuthority) => authority == ObjectAuthority.InheritManager ? managerAuthority : authority == ObjectAuthority.Server;
        private void OnValidate() { movementModeDirty = true; }
        private void ApplyMovementMode()
        {
            EnsureBody(); if (body == null) return;
            body.bodyType = movementMode == ObjectMovementMode.Static || movementMode == ObjectMovementMode.StaticWithColliders ? RigidbodyType2D.Kinematic : RigidbodyType2D.Dynamic;
            bool enabled = movementMode == ObjectMovementMode.StaticWithColliders || movementMode == ObjectMovementMode.PhysicsWithColliders;
            foreach (var collider in GetComponentsInChildren<Collider2D>(true)) collider.enabled = enabled;
            movementModeDirty = false;
        }
        public bool CaptureEligible(double now)
        { if (now < nextKeyframe) return false; nextKeyframe = now + 1d / Math.Max(1, keyframeRate); return true; }
        public byte[] CaptureState()
        {
            EnsureBody(); Vector2 velocity = BodyVelocity;
            var snapshot = new PhysicsSnapshot { Tick = physicsTick, LastInputSequence = UsesServerAuthority ? lastAcceptedSequence : nextSequence,
                Time = Time.fixedTimeAsDouble, Position = new NumericsVector3(body.position.x, body.position.y, transform.position.z),
                Rotation = ToNumerics(Quaternion.Euler(0, 0, body.rotation)), Velocity = new NumericsVector3(velocity.x, velocity.y, 0),
                AngularVelocity = new NumericsVector3(0, 0, body.angularVelocity * Mathf.Deg2Rad) };
            return QuantizedPhysicsSerializer.SerializeSnapshot2D(snapshot, PrecisionProfile);
        }
        public bool ValidateState(byte[] state)
        {
            try { QuantizedPhysicsSerializer.DeserializeSnapshot2D(state, DecodeProfile(state)); return true; }
            catch (Exception error) when (error is ArgumentException || error is IOException || error is InvalidDataException || error is InvalidOperationException) { return false; }
        }
        public void ApplyState(byte[] state, bool immediate)
        {
            PhysicsSnapshot snapshot;
            try { snapshot = QuantizedPhysicsSerializer.DeserializeSnapshot2D(state, DecodeProfile(state)); }
            catch (Exception error) when (error is ArgumentException || error is IOException || error is InvalidDataException || error is InvalidOperationException) { return; }
            if (hasSnapshot && !PhysicsReconciler.IsNewer(snapshot.Tick, authoritative.Tick)) return;
            EnsureBody(); authoritative = snapshot; hasSnapshot = true; snapshotReceivedAt = Time.unscaledTimeAsDouble; KeyframesReceived++;
            if (immediate) Snap(snapshot, false);
        }
        private void Snap(PhysicsSnapshot snapshot, bool preserveVisual)
        {
            Vector3 oldPosition = visual == null ? Vector3.zero : visual.position; Quaternion oldRotation = visual == null ? Quaternion.identity : visual.rotation;
            body.position = new Vector2(snapshot.Position.X, snapshot.Position.Y); body.rotation = Angle(snapshot.Rotation);
            var position = transform.position; position.z = snapshot.Position.Z; transform.position = position;
            if (body.bodyType == RigidbodyType2D.Dynamic) { BodyVelocity = new Vector2(snapshot.Velocity.X, snapshot.Velocity.Y); body.angularVelocity = snapshot.AngularVelocity.Z * Mathf.Rad2Deg; }
            if (preserveVisual && visual != null) { visual.position = oldPosition; visual.rotation = oldRotation; }
        }
        private void Reconcile()
        {
            double age = Time.unscaledTimeAsDouble - snapshotReceivedAt + estimatedOneWayLatency;
            if (age > maxExtrapolation + 0.5) return;
            var target = PhysicsReconciler.Extrapolate(authoritative, age, maxExtrapolation);
            Vector2 targetPosition = new Vector2(target.Position.X, target.Position.Y);
            float error = Vector2.Distance(body.position, targetPosition), angleError = Mathf.DeltaAngle(body.rotation, Angle(target.Rotation));
            LastPositionError = error; PeakPositionError = Mathf.Max(PeakPositionError, error);
            if (error > Mathf.Max(0.1f, snapDistance)) { Snap(target, true); HardCorrections++; return; }
            if (error <= correctionDeadZone && Mathf.Abs(angleError) <= rotationDeadZone) return;
            float weight = IsOwner ? ownerCorrectionWeight : 1;
            float blend = PhysicsReconciler.SmoothingFactor(Mathf.Max(0, smoothingSpeed), Time.fixedDeltaTime) * weight;
            if (body.bodyType != RigidbodyType2D.Dynamic)
            {
                body.MovePosition(Vector2.Lerp(body.position, targetPosition, blend)); body.MoveRotation(Mathf.LerpAngle(body.rotation, Angle(target.Rotation), blend)); return;
            }
            Vector2 currentVelocity = BodyVelocity;
            var corrected = PhysicsReconciler.CalculateVelocityCorrection(new NumericsVector3(body.position.x, body.position.y, target.Position.Z),
                new NumericsVector3(currentVelocity.x, currentVelocity.y, 0), target, Mathf.Max(.02f, correctionTime), Mathf.Max(0, maxCorrectionSpeed));
            Vector2 correction = error <= correctionDeadZone ? Vector2.zero : new Vector2(corrected.X - currentVelocity.x, corrected.Y - currentVelocity.y);
            Vector2 next = Vector2.Lerp(currentVelocity, new Vector2(target.Velocity.X, target.Velocity.Y) + correction, blend);
            PeakNetworkVelocityCorrection = Mathf.Max(PeakNetworkVelocityCorrection, (next - currentVelocity).magnitude); BodyVelocity = next;
            float angularCorrection = Mathf.Abs(angleError) <= rotationDeadZone ? 0 : Mathf.Clamp(angleError / Mathf.Max(.02f, correctionTime), -maxAngularCorrectionSpeed, maxAngularCorrectionSpeed);
            body.angularVelocity = Mathf.Lerp(body.angularVelocity, target.AngularVelocity.Z * Mathf.Rad2Deg + angularCorrection, blend);
        }
        private void RememberVisual() { if (visual != null) { visualOrigin = visual.localPosition; visualRotation = visual.localRotation; } }
        private void LateUpdate()
        {
            if (visual != null)
            {
                float blend = PhysicsReconciler.SmoothingFactor(Mathf.Max(0, smoothingSpeed), Time.unscaledDeltaTime);
                visual.localPosition = Vector3.Lerp(visual.localPosition, visualOrigin, blend); visual.localRotation = Quaternion.Slerp(visual.localRotation, visualRotation, blend);
            }
            Vector3 position = visual == null ? transform.position : visual.position;
            if (observedVisual) PeakVisualStep = Mathf.Max(PeakVisualStep, Vector3.Distance(position, previousVisualPosition));
            previousVisualPosition = position; observedVisual = true;
        }
        public void ResetPrediction()
        {
            intent = default; authoritative = default; localAxes = lastSentAxes = Vector2.zero;
            localJump = localJumpPredicted = jumpPending = hasInput = hasSnapshot = acceptedAnySequence = relayedAnySequence = hasPredictedJumpSequence = false;
            nextSequence = lastAcceptedSequence = lastRelaySequence = predictedJumpSequence = 0;
            nextSend = nextKeepAlive = nextKeyframe = lastInputAt = snapshotReceivedAt = 0;
            if (inputMotor is INetworkInputMotor motor) motor.ResetInput();
        }
        private static float Angle(NumericsQuaternion value) => 2f * Mathf.Atan2(value.Z, value.W) * Mathf.Rad2Deg;
        private static NumericsQuaternion ToNumerics(Quaternion value) => new NumericsQuaternion(value.x, value.y, value.z, value.w);
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
