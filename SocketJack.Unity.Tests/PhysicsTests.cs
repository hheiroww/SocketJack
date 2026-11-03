using System;
using System.IO;
using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Unity.Physics;

namespace SocketJack.Unity.Tests
{
    [TestClass]
    public class PhysicsTests
    {
        [TestMethod]
        public void FixedBinaryMessagesRoundTripWithoutJsonOrTypeMetadata()
        {
            var input = new PhysicsInput { Sequence = uint.MaxValue, Tick = 12, Horizontal = -.5f, Vertical = 1, Jump = true };
            byte[] bytes = PhysicsBinarySerializer.SerializeInput(input);
            Assert.AreEqual(15, bytes.Length);
            var decoded = PhysicsBinarySerializer.DeserializeInput(bytes);
            Assert.AreEqual(input.Sequence, decoded.Sequence); Assert.AreEqual(input.Tick, decoded.Tick);
            Assert.AreEqual(input.Horizontal, decoded.Horizontal, .0001f); Assert.AreEqual(input.Vertical, decoded.Vertical);
            Assert.IsTrue(decoded.Jump);
            var snapshot = Snapshot(); bytes = PhysicsBinarySerializer.SerializeSnapshot(snapshot);
            Assert.AreEqual(70, bytes.Length);
            var state = PhysicsBinarySerializer.DeserializeSnapshot(bytes);
            Assert.AreEqual(snapshot.Position, state.Position); Assert.AreEqual(snapshot.Velocity, state.Velocity);
            Assert.AreEqual(snapshot.AngularVelocity, state.AngularVelocity); Assert.AreEqual(snapshot.Time, state.Time);
            Assert.AreEqual(1, state.Rotation.Length(), .0001f);
        }
        [TestMethod]
        public void InvalidInputAndSnapshotsAreRejectedBeforePhysics()
        {
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => PhysicsBinarySerializer.SerializeInput(new PhysicsInput { Horizontal = float.NaN }));
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => PhysicsBinarySerializer.SerializeInput(new PhysicsInput { Vertical = 2 }));
            Assert.ThrowsException<InvalidDataException>(() => PhysicsBinarySerializer.DeserializeInput(new byte[15]));
            var invalid = Snapshot(); invalid.Position.X = float.PositiveInfinity;
            Assert.ThrowsException<InvalidDataException>(() => PhysicsBinarySerializer.SerializeSnapshot(invalid));
            invalid = Snapshot(); invalid.Rotation = default;
            Assert.ThrowsException<InvalidDataException>(() => PhysicsBinarySerializer.SerializeSnapshot(invalid));
            byte[] packet = PhysicsBinarySerializer.SerializeInput(new PhysicsInput()); packet[14] = 2;
            Assert.ThrowsException<InvalidDataException>(() => PhysicsBinarySerializer.DeserializeInput(packet));
        }
        [TestMethod]
        public void ExtrapolationIsBoundedAndIntegratesRotation()
        {
            var state = Snapshot(); state.AngularVelocity = new Vector3(0, (float)Math.PI, 0);
            var predicted = PhysicsReconciler.Extrapolate(state, 5, .15);
            Assert.AreEqual(1.3f, predicted.Position.X, .0001f);
            Assert.AreEqual(state.Time + .15, predicted.Time, .0001);
            Assert.AreNotEqual(state.Rotation, predicted.Rotation);
            Assert.AreEqual(1, predicted.Rotation.Length(), .0001f);
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => PhysicsReconciler.Extrapolate(state, -1, .15));
        }
        [TestMethod]
        public void CorrectionsAreContinuousAndInterpolationIndependentOfFrameRate()
        {
            var target = Snapshot(); target.Position = new Vector3(1000, 0, 0);
            Vector3 velocity = PhysicsReconciler.CalculateVelocityCorrection(Vector3.Zero, new Vector3(2, 0, 0), target, .1f, 3);
            Assert.AreEqual(5f, velocity.X, .0001f);
            Assert.AreEqual(0f, velocity.Y); Assert.AreEqual(0f, velocity.Z);
            double oneFrame = PhysicsReconciler.SmoothingFactor(15, 1f / 30);
            double halfFrame = PhysicsReconciler.SmoothingFactor(15, 1f / 60);
            Assert.AreEqual(oneFrame, 1 - Math.Pow(1 - halfFrame, 2), .000001);
            Assert.IsTrue(PhysicsReconciler.IsNewer(0, uint.MaxValue));
            Assert.IsFalse(PhysicsReconciler.IsNewer(uint.MaxValue, 0));
        }
        private static PhysicsSnapshot Snapshot() => new PhysicsSnapshot { Tick = 42, LastInputSequence = 12, Time = 8,
            Position = new Vector3(1, 2, 3), Velocity = new Vector3(2, 0, 0), AngularVelocity = Vector3.Zero, Rotation = Quaternion.Identity };
    }
}
