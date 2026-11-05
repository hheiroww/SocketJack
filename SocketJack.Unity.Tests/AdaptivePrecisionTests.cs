using System;
using System.IO;
using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Multiplayer.Physics;
using SocketJack.Multiplayer.Serialization;
namespace SocketJack.Multiplayer.Tests
{
    [TestClass]
    public sealed class AdaptivePrecisionTests
    {
        [TestMethod]
        public void AdaptiveNumericSchemaStaysStableAcrossPrecisionChanges()
        {
            var selected = NetworkPrecisionProfile.Balanced;
            var codec = new AdaptiveNumericSerializer(() => selected, NetworkQuantizationSemantic.Position);
            string protocol = codec.ProtocolId;
            var value = new Vector3(1.123f, 2.234f, 3.345f);
            foreach (var profile in new[] { NetworkPrecisionProfile.FullPrecision, NetworkPrecisionProfile.Balanced, NetworkPrecisionProfile.Compact })
            {
                selected = profile;
                var bytes = codec.Serialize(value, typeof(Vector3)); var decoded = (Vector3)codec.Deserialize(bytes, typeof(Vector3));
                Assert.AreEqual(protocol, codec.ProtocolId); Assert.IsTrue(Vector3.Distance(value, decoded) <= Math.Sqrt(3) * profile.Position.Step / 2 + .00001);
            }
            var bad = codec.Serialize(value, typeof(Vector3)); bad[3] ^= 0x55;
            Assert.ThrowsException<InvalidDataException>(() => codec.Deserialize(bad, typeof(Vector3)));
        }
        [TestMethod]
        public void AdaptivePolicyHasHysteresisAndHonorsConfiguredPrecisionFloor()
        {
            var policy = new AdaptiveNetworkPolicy();
            Assert.AreEqual(NetworkPrecisionPreset.Balanced, policy.Update(1, 200, 0).Precision);
            Assert.AreEqual(NetworkPrecisionPreset.Balanced, policy.Update(1, 200, 0).Precision);
            Assert.AreEqual(NetworkPrecisionPreset.Compact, policy.Update(1, 200, 0).Precision);
            Assert.AreEqual(NetworkPrecisionPreset.Compact, policy.Update(1, 20, 0).Precision);
            Assert.AreEqual(NetworkPrecisionPreset.Compact, policy.Update(4, 20, 0).Precision);
            Assert.AreEqual(NetworkPrecisionPreset.FullPrecision, policy.Update(1, 20, 0).Precision);
            Assert.AreEqual(NetworkPrecisionPreset.FullPrecision, policy.Update(3, 1000, 100000, NetworkPrecisionPreset.FullPrecision).Precision);
        }
        [TestMethod]
        public void TransformPresetsAreStatelessBoundedAndRetainFullPrecisionEscape()
        {
            var value = new TransformSnapshot { Position = new Vector3(1.234f, 2.345f, 3.456f), Rotation = Quaternion.Identity, Scale = Vector3.One };
            Assert.AreEqual(40, QuantizedTransformSerializer.Serialize(value, NetworkPrecisionProfile.FullPrecision).Length);
            foreach (var profile in new[] { NetworkPrecisionProfile.Balanced, NetworkPrecisionProfile.Compact })
            {
                var bytes = QuantizedTransformSerializer.Serialize(value, profile); Assert.IsTrue(bytes.Length < 40);
                Assert.AreEqual(profile.Fingerprint, NetworkPrecisionFamily.ForState(bytes, true, NetworkPrecisionProfile.FullPrecision, true).Fingerprint);
                var decoded = QuantizedTransformSerializer.Deserialize(bytes, profile); Assert.IsTrue(Vector3.Distance(value.Position, decoded.Position) <= profile.Position.Step);
                value.Position = new Vector3(100000, -100000, 40000); bytes = QuantizedTransformSerializer.Serialize(value, profile);
                decoded = QuantizedTransformSerializer.Deserialize(bytes, profile); Assert.AreEqual(value.Position, decoded.Position);
                Assert.ThrowsException<InvalidDataException>(() => QuantizedTransformSerializer.Deserialize(bytes, profile == NetworkPrecisionProfile.Balanced ? NetworkPrecisionProfile.Compact : NetworkPrecisionProfile.Balanced));
                value.Position = new Vector3(1.234f, 2.345f, 3.456f);
            }
        }
    }
}
