using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Multiplayer.Physics;
using SocketJack.Multiplayer.Serialization;

namespace SocketJack.Multiplayer.Tests
{
    [TestClass]
    public sealed class QuantizationTests
    {
        [TestMethod]
        public void PresetsHaveExplicitIndependentBoundsAndStableConfigurationIdentity()
        {
            var balanced = NetworkPrecisionProfile.Balanced; var compact = NetworkPrecisionProfile.Compact;
            Assert.AreEqual(-1024, balanced.Position.Minimum); Assert.AreEqual(1024, balanced.Position.Maximum);
            Assert.AreEqual(1d / 128, balanced.Position.Step); Assert.AreEqual(1d / 32768, balanced.Rotation.Step);
            Assert.AreEqual(1d / 1024, balanced.Scale.Step); Assert.AreEqual(1d / 128, balanced.Velocity.Step);
            Assert.AreEqual(1d / 256, balanced.AngularVelocity.Step);
            Assert.AreEqual(1d / 16, compact.Position.Step); Assert.AreEqual(1d / 256, compact.Rotation.Step);
            Assert.AreEqual(balanced.Fingerprint, balanced.With().Fingerprint, "Preset labels do not alter the wire grid.");
            Assert.AreNotEqual(balanced.Fingerprint, compact.Fingerprint);
            Assert.AreEqual("sj.precision.v1.a9bdefde45c66292", NetworkPrecisionProfile.FullPrecision.ProtocolId);
            Assert.AreEqual("sj.precision.v1.5afb319f6bbb5678", balanced.ProtocolId);
            Assert.AreEqual("sj.precision.v1.9b1841c6fd416ff7", compact.ProtocolId);
            Assert.AreNotEqual(balanced.Fingerprint, NetworkPrecisionProfile.FullPrecision.Fingerprint);
            Assert.AreNotEqual(balanced.Fingerprint, balanced.With(angularVelocity: new NetworkQuantizationRange(-64, 64, .5)).Fingerprint);
            var zero = balanced.With(scale: new NetworkQuantizationRange(-0d, 16, 1d / 1024));
            Assert.AreEqual(balanced.Fingerprint, zero.Fingerprint, "Negative zero must have the same signature.");
            var culture = CultureInfo.CurrentCulture;
            try { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR"); Assert.AreEqual(balanced.ProtocolId, balanced.With().ProtocolId); }
            finally { CultureInfo.CurrentCulture = culture; }
            Assert.AreSame(compact, NetworkPrecisionProfile.ForPreset(NetworkPrecisionPreset.Compact));
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => NetworkPrecisionProfile.ForPreset(NetworkPrecisionPreset.Custom));
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => new NetworkQuantizationRange(0, 1, 0));
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => new NetworkQuantizationRange(double.NaN, 1, .1));
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => new NetworkQuantizationRange(-double.MaxValue, double.MaxValue, 1));
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => new NetworkQuantizationRange(0, 1, 1e-30));
        }

        [DataTestMethod]
        [DataRow(NetworkPrecisionPreset.Balanced)]
        [DataRow(NetworkPrecisionPreset.Compact)]
        public void StatelessThreeDimensionalPhysicsHasBoundedErrorAndSmallerWireSize(NetworkPrecisionPreset preset)
        {
            var profile = NetworkPrecisionProfile.ForPreset(preset); var random = new Random(1729);
            for (int i = 0; i < 200; i++)
            {
                var source = Snapshot(); source.Tick = (uint)(uint.MaxValue - i); source.LastInputSequence = (uint)i;
                source.Position = new Vector3((float)random.NextDouble() * 100 - 50, (float)random.NextDouble() * 100 - 50, (float)random.NextDouble() * 100 - 50);
                source.Rotation = Quaternion.CreateFromYawPitchRoll((float)random.NextDouble() * 5, (float)random.NextDouble() * 4, (float)random.NextDouble() * 3);
                source.Velocity = new Vector3(1.123f, -2.987f, .0013f); source.AngularVelocity = new Vector3(.1007f, -.5001f, .991f);
                byte[] bytes = QuantizedPhysicsSerializer.SerializeSnapshot(source, profile);
                var result = QuantizedPhysicsSerializer.DeserializeSnapshot(bytes, profile);
                Assert.IsTrue(bytes.Length < PhysicsBinarySerializer.SnapshotBytes);
                Assert.AreEqual(preset == NetworkPrecisionPreset.Balanced ? 55 : 47, bytes.Length);
                Assert.AreEqual(source.Tick, result.Tick); Assert.AreEqual(source.LastInputSequence, result.LastInputSequence); Assert.AreEqual(source.Time, result.Time);
                VectorError(source.Position, result.Position, profile.Position.Step / 2);
                VectorError(source.Velocity, result.Velocity, profile.Velocity.Step / 2);
                VectorError(source.AngularVelocity, result.AngularVelocity, profile.AngularVelocity.Step / 2);
                Assert.IsTrue(Alignment(source.Rotation, result.Rotation) > 1 - 8 * profile.Rotation.Step * profile.Rotation.Step,
                    "Normalized quaternion error exceeded the component quantization bound.");
                CollectionAssert.AreEqual(bytes, QuantizedPhysicsSerializer.SerializeSnapshot(source, profile), "Encoding must not depend on a previous baseline.");
            }
        }

        [TestMethod]
        public void TwoDimensionalPhysicsPreservesDepthAndPlanarityAndFullPrecisionLegacyBytes()
        {
            var source = Snapshot(); source.Position = new Vector3(1.234f, -3.211f, -2.5f);
            source.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .741f); source.Velocity = new Vector3(2.123f, -1.411f, 0); source.AngularVelocity = new Vector3(0, 0, .523f);
            CollectionAssert.AreEqual(PhysicsBinarySerializer.SerializeSnapshot(source), QuantizedPhysicsSerializer.SerializeSnapshot(source, NetworkPrecisionProfile.FullPrecision));
            CollectionAssert.AreEqual(PhysicsBinarySerializer.SerializeSnapshot(source), QuantizedPhysicsSerializer.SerializeSnapshot2D(source, NetworkPrecisionProfile.FullPrecision));
            Assert.AreEqual(70, PhysicsBinarySerializer.SerializeSnapshot(source).Length);
            Assert.AreEqual(15, PhysicsBinarySerializer.SerializeInput(new PhysicsInput { Horizontal = .5f, Vertical = -.5f }).Length);
            foreach (var profile in new[] { NetworkPrecisionProfile.Balanced, NetworkPrecisionProfile.Compact })
            {
                byte[] bytes = QuantizedPhysicsSerializer.SerializeSnapshot2D(source, profile);
                Assert.AreEqual(profile.Preset == NetworkPrecisionPreset.Balanced ? 44 : 40, bytes.Length);
                Assert.IsTrue(bytes.Length < QuantizedPhysicsSerializer.SerializeSnapshot(source, profile).Length);
                var result = QuantizedPhysicsSerializer.DeserializeSnapshot2D(bytes, profile);
                VectorError(source.Position, result.Position, profile.Position.Step / 2);
                VectorError(source.Velocity, result.Velocity, profile.Velocity.Step / 2);
                VectorError(source.AngularVelocity, result.AngularVelocity, profile.AngularVelocity.Step / 2);
                Assert.AreEqual(0, result.Rotation.X); Assert.AreEqual(0, result.Rotation.Y); Assert.AreEqual(0, result.Velocity.Z);
                Assert.ThrowsException<InvalidDataException>(() => QuantizedPhysicsSerializer.DeserializeSnapshot(bytes, profile));
            }
            source.Velocity.Z = 1;
            Assert.ThrowsException<InvalidDataException>(() => QuantizedPhysicsSerializer.SerializeSnapshot2D(source, NetworkPrecisionProfile.Balanced));
        }

        [TestMethod]
        public void OutOfRangeGroupsEscapeWithoutClampingOtherGroupsOrLosingFullPrecision()
        {
            var profile = NetworkPrecisionProfile.Compact;
            var source = Snapshot(); source.Position = new Vector3(99999.125f, -88888.75f, .001234f);
            source.Velocity = new Vector3(10000.25f, .12345f, -.23456f); source.AngularVelocity = new Vector3(777.125f, .00123f, -.98765f);
            var bytes = QuantizedPhysicsSerializer.SerializeSnapshot(source, profile);
            var result = QuantizedPhysicsSerializer.DeserializeSnapshot(bytes, profile);
            Assert.AreEqual(source.Position, result.Position); Assert.AreEqual(source.Velocity, result.Velocity); Assert.AreEqual(source.AngularVelocity, result.AngularVelocity);
            Assert.IsTrue(bytes.Length <= QuantizedPhysicsSerializer.MaximumSnapshotBytes);
            source.Position.X = float.NaN;
            Assert.ThrowsException<InvalidDataException>(() => QuantizedPhysicsSerializer.SerializeSnapshot(source, profile));
        }

        [TestMethod]
        public void MemberNumericCodecsSupportExplicitTypesSemanticsNullableAndRawEscapes()
        {
            var profile = NetworkPrecisionProfile.Compact;
            foreach (var entry in new (Type type, object value)[] { (typeof(float), 12.345f), (typeof(double), 12.3456789), (typeof(Vector2), new Vector2(1.11f, 2.22f)), (typeof(Vector3), new Vector3(1.11f, 2.22f, 3.33f)), (typeof(Vector4), new Vector4(1.11f, 2.22f, 3.33f, 4.44f)), (typeof(Quaternion), new Quaternion(.1f, .2f, .3f, .9f)) })
            {
                var codec = new QuantizedNetworkSerializer(profile);
                Assert.IsTrue(codec.CanSerialize(entry.type)); byte[] bytes = codec.Serialize(entry.value, entry.type, 64);
                Assert.IsTrue(entry.type.IsInstanceOfType(codec.Deserialize(bytes, entry.type, bytes.Length)));
                Assert.ThrowsException<InvalidDataException>(() => codec.Deserialize(bytes, entry.type, bytes.Length - 1));
            }
            var position = new QuantizedNetworkSerializer(profile, NetworkQuantizationSemantic.Position);
            var velocity = new QuantizedNetworkSerializer(profile, NetworkQuantizationSemantic.Velocity);
            var scale = new QuantizedNetworkSerializer(profile, NetworkQuantizationSemantic.Scale);
            Assert.AreNotEqual(position.ProtocolId, velocity.ProtocolId);
            Assert.ThrowsException<InvalidDataException>(() => velocity.Deserialize(position.Serialize(Vector3.One, typeof(Vector3)), typeof(Vector3)));
            var nullable = new QuantizedNetworkSerializer(profile);
            Assert.IsNull(nullable.Deserialize(nullable.Serialize(null, typeof(float?)), typeof(float?)));
            Assert.AreEqual(2f, nullable.Deserialize(nullable.Serialize(2f, typeof(float?)), typeof(float?)));
            var huge = new Vector3(100000.125f, .1234567f, -.222222f);
            Assert.AreEqual(huge, position.Deserialize(position.Serialize(huge, typeof(Vector3)), typeof(Vector3)));
            Assert.AreEqual(-1.2345678f, scale.Deserialize(scale.Serialize(-1.2345678f, typeof(float)), typeof(float)), "Negative scale must escape, never clamp to zero.");
            Assert.IsFalse(position.CanSerialize(typeof(string))); Assert.IsFalse(position.CanSerialize(typeof(object)));
            Assert.ThrowsException<NotSupportedException>(() => position.Serialize("text", typeof(string)));
            Assert.ThrowsException<InvalidDataException>(() => position.Serialize(float.PositiveInfinity, typeof(float)));
            Assert.ThrowsException<InvalidDataException>(() => position.Serialize(double.NaN, typeof(double)));
        }

        [TestMethod]
        public void DecodersRejectTruncationWrongProfileVersionTypeUnusedCodesPaddingAndNonfiniteRaw()
        {
            var profile = NetworkPrecisionProfile.Compact; byte[] bytes = QuantizedPhysicsSerializer.SerializeSnapshot(Snapshot(), profile);
            for (int i = 0; i < bytes.Length; i++) Assert.ThrowsException<InvalidDataException>(() => QuantizedPhysicsSerializer.DeserializeSnapshot(bytes.Take(i).ToArray(), profile));
            Assert.ThrowsException<InvalidDataException>(() => QuantizedPhysicsSerializer.DeserializeSnapshot(bytes, NetworkPrecisionProfile.Balanced));
            Assert.ThrowsException<InvalidDataException>(() => QuantizedPhysicsSerializer.DeserializeSnapshot(bytes.Concat(new byte[] { 0 }).ToArray(), profile));
            bytes[0] = 99; Assert.ThrowsException<InvalidDataException>(() => QuantizedPhysicsSerializer.DeserializeSnapshot(bytes, profile));
            var small = profile.With(scalar: new NetworkQuantizationRange(0, 2, 1));
            var codec = new QuantizedNetworkSerializer(small); byte[] number = codec.Serialize(1f, typeof(float));
            byte[] invalidCode = (byte[])number.Clone(); invalidCode[11] = 6; // raw=0, 2-bit grid code3 is unused.
            Assert.ThrowsException<InvalidDataException>(() => codec.Deserialize(invalidCode, typeof(float)));
            byte[] padding = (byte[])number.Clone(); padding[padding.Length - 1] |= 0x80;
            Assert.ThrowsException<InvalidDataException>(() => codec.Deserialize(padding, typeof(float)));
            Assert.ThrowsException<InvalidDataException>(() => codec.Deserialize(number, typeof(Vector2)));
            var full = new QuantizedNetworkSerializer(NetworkPrecisionProfile.FullPrecision); byte[] nonfinite = full.Serialize(1f, typeof(float));
            // First body bit is raw tag1; next32 bits contain the IEEE value. Replace with +infinity.
            for (int bit = 0; bit < 32; bit++) { int at = 89 + bit; int mask = 1 << (at & 7); nonfinite[at >> 3] = (byte)((nonfinite[at >> 3] & ~mask) | (((0x7f800000u >> bit) & 1) != 0 ? mask : 0)); }
            Assert.ThrowsException<InvalidDataException>(() => full.Deserialize(nonfinite, typeof(float)));
        }

        [TestMethod]
        public void CustomUnevenGridBoundariesStayWithinHalfStepAndPayloadLimits()
        {
            var profile = NetworkPrecisionProfile.Balanced.With(scalar: new NetworkQuantizationRange(-1.3, 2.2, .3));
            var codec = new QuantizedNetworkSerializer(profile);
            foreach (double value in new[] { -1.3, -1.299, -.151, 0, .151, 1.999, 2.199, 2.2 })
            {
                byte[] bytes = codec.Serialize(value, typeof(double)); double result = (double)codec.Deserialize(bytes, typeof(double));
                Assert.IsTrue(Math.Abs(value - result) <= .15);
                CollectionAssert.AreEqual(bytes, codec.Serialize(value, typeof(double), bytes.Length));
                Assert.ThrowsException<InvalidDataException>(() => codec.Serialize(value, typeof(double), bytes.Length - 1));
            }
        }
        private static PhysicsSnapshot Snapshot() => new PhysicsSnapshot { Tick = 19, LastInputSequence = 17, Time = 12.345,
            Position = new Vector3(1.234f, -2.345f, 3.456f), Rotation = Quaternion.CreateFromYawPitchRoll(.2f, .3f, .4f), Velocity = new Vector3(.1f, .2f, .3f), AngularVelocity = new Vector3(.01f, .02f, .03f) };
        private static void VectorError(Vector3 expected, Vector3 actual, double maximum)
        {
            Assert.IsTrue(Math.Abs((double)expected.X - actual.X) <= maximum);
            Assert.IsTrue(Math.Abs((double)expected.Y - actual.Y) <= maximum);
            Assert.IsTrue(Math.Abs((double)expected.Z - actual.Z) <= maximum);
        }
        private static double Alignment(Quaternion first, Quaternion second)
        {
            double dot = (double)first.X * second.X + (double)first.Y * second.Y + (double)first.Z * second.Z + (double)first.W * second.W;
            double firstNorm = (double)first.X * first.X + (double)first.Y * first.Y + (double)first.Z * first.Z + (double)first.W * first.W;
            double secondNorm = (double)second.X * second.X + (double)second.Y * second.Y + (double)second.Z * second.Z + (double)second.W * second.W;
            return Math.Abs(dot) / Math.Sqrt(firstNorm * secondNorm);
        }
    }
}
