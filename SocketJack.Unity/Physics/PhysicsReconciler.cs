using System;
using System.Numerics;

namespace SocketJack.Unity.Physics
{
    /// <summary>Pure correction math shared by Unity and headless tests. Unity/PhysX is not
    /// deterministic across machines; sparse authoritative corrections remain necessary.</summary>
    public static class PhysicsReconciler
    {
        public static bool IsNewer(uint sequence, uint previous) => unchecked((int)(sequence - previous)) > 0;

        /// <summary>Predicts with constant linear/angular velocity, capped to avoid unbounded
        /// drift after packet loss. This does not predict collisions or replace local physics.</summary>
        public static PhysicsSnapshot Extrapolate(PhysicsSnapshot value, double elapsedSeconds, double maxExtrapolation)
        {
            PhysicsBinarySerializer.Validate(value);
            if (double.IsNaN(elapsedSeconds) || double.IsInfinity(elapsedSeconds) || elapsedSeconds < 0 ||
                double.IsNaN(maxExtrapolation) || double.IsInfinity(maxExtrapolation) || maxExtrapolation < 0 || maxExtrapolation > 2)
                throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
            float dt = (float)Math.Min(elapsedSeconds, maxExtrapolation);
            value.Position += value.Velocity * dt;
            float speed = value.AngularVelocity.Length();
            if (speed > 0.00001f)
                value.Rotation = Quaternion.Normalize(Quaternion.Multiply(
                    Quaternion.CreateFromAxisAngle(value.AngularVelocity / speed, speed * dt), value.Rotation));
            value.Time += dt;
            return value;
        }

        /// <summary>Preserves current simulated velocity and adds a bounded correction toward
        /// the predicted position. Apply on each physics step instead of snapping position.</summary>
        public static Vector3 CalculateVelocityCorrection(Vector3 currentPosition, Vector3 currentVelocity,
            PhysicsSnapshot target, float correctionTime, float maxCorrectionSpeed)
        {
            PhysicsBinarySerializer.Validate(target);
            if (!Finite(currentPosition) || !Finite(currentVelocity) || !Finite(correctionTime) || correctionTime <= 0 ||
                !Finite(maxCorrectionSpeed) || maxCorrectionSpeed < 0) throw new ArgumentOutOfRangeException(nameof(correctionTime));
            Vector3 correction = (target.Position - currentPosition) / correctionTime;
            float length = correction.Length();
            if (length > maxCorrectionSpeed && length > 0) correction *= maxCorrectionSpeed / length;
            return currentVelocity + correction;
        }

        /// <summary>Frame-rate-independent exponential visual interpolation factor.</summary>
        public static float SmoothingFactor(float speed, float deltaSeconds)
        {
            if (!Finite(speed) || !Finite(deltaSeconds) || speed < 0 || deltaSeconds < 0) throw new ArgumentOutOfRangeException(nameof(speed));
            return (float)(1 - Math.Exp(-speed * deltaSeconds));
        }
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool Finite(Vector3 value) => Finite(value.X) && Finite(value.Y) && Finite(value.Z);
    }
}
