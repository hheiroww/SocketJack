using System;
using System.IO;
using SocketJack.Multiplayer.Serialization;
namespace SocketJack.Multiplayer.Serialization
{
    /// <summary>A closed, schema-declared preset family. A packet identifies a member of the family, never a remote codec/type.</summary>
    public static class NetworkPrecisionFamily
    {
        public const string ProtocolId = "sj.adaptive.precision.v1/full-balanced-compact";
        public static NetworkPrecisionProfile FromFingerprint(byte[] bytes, int offset)
        {
            if (bytes == null || offset < 0 || bytes.Length - offset < 8) throw new InvalidDataException("Missing precision identity.");
            ulong id = 0; for (int i = 0; i < 8; i++) id |= (ulong)bytes[offset + i] << (8 * i);
            foreach (var profile in new[] { NetworkPrecisionProfile.FullPrecision, NetworkPrecisionProfile.Balanced, NetworkPrecisionProfile.Compact })
                if (profile.Fingerprint == id) return profile;
            throw new InvalidDataException("Precision is outside the locally configured preset family.");
        }
        public static NetworkPrecisionProfile ForState(byte[] bytes, bool transform, NetworkPrecisionProfile fixedProfile, bool adaptive)
        {
            if (!adaptive) return fixedProfile;
            if (bytes != null && bytes.Length >= 10 && bytes[0] == 2 && (transform ? bytes[1] == 4 : bytes[1] == 2 || bytes[1] == 3))
                return FromFingerprint(bytes, 2);
            return NetworkPrecisionProfile.FullPrecision;
        }
    }
    public sealed class AdaptiveNumericSerializer : INetworkSerializer
    {
        private readonly Func<NetworkPrecisionProfile> current;
        private readonly NetworkQuantizationSemantic semantic;
        public string ProtocolId => NetworkPrecisionFamily.ProtocolId + "/numeric/" + (int)semantic;
        public AdaptiveNumericSerializer(Func<NetworkPrecisionProfile> current, NetworkQuantizationSemantic semantic = NetworkQuantizationSemantic.Scalar)
        { this.current = current ?? throw new ArgumentNullException(nameof(current)); NetworkPrecisionProfile.Balanced.GetRange(semantic); this.semantic = semantic; }
        public bool CanSerialize(Type type) => new QuantizedNetworkSerializer(NetworkPrecisionProfile.Balanced, semantic).CanSerialize(type);
        public byte[] Serialize(object value, Type declaredType, int maxBytes = 8192)
        {
            var profile = current() ?? throw new InvalidOperationException("Adaptive precision is missing.");
            if (profile.Preset == NetworkPrecisionPreset.Custom) throw new InvalidOperationException("Adaptive mode uses the declared preset family; custom grids use fixed mode.");
            return new QuantizedNetworkSerializer(profile, semantic).Serialize(value, declaredType, maxBytes);
        }
        public object Deserialize(byte[] bytes, Type declaredType, int maxBytes = 8192)
            => new QuantizedNetworkSerializer(NetworkPrecisionFamily.FromFingerprint(bytes, 3), semantic).Deserialize(bytes, declaredType, maxBytes);
    }
}
namespace SocketJack.Multiplayer
{
    public sealed class AdaptiveNetworkDecision
    {
        public NetworkPrecisionPreset Precision { get; internal set; }
        public int SnapshotEvery { get; internal set; }
    }
    /// <summary>Conservative hysteresis: improve after five stable seconds, reduce traffic after two congested seconds.</summary>
    public sealed class AdaptiveNetworkPolicy
    {
        private NetworkPrecisionPreset active = NetworkPrecisionPreset.Balanced, candidate = NetworkPrecisionPreset.Balanced;
        private double since, now;
        public AdaptiveNetworkDecision Update(double deltaSeconds, double? roundTripMilliseconds, long queuedBytes,
            NetworkPrecisionPreset lowestAllowed = NetworkPrecisionPreset.Compact)
        {
            SessionWire.ValidateDelta(deltaSeconds);
            if (lowestAllowed < NetworkPrecisionPreset.FullPrecision || lowestAllowed > NetworkPrecisionPreset.Compact || queuedBytes < 0 ||
                (roundTripMilliseconds.HasValue && (double.IsNaN(roundTripMilliseconds.Value) || double.IsInfinity(roundTripMilliseconds.Value) || roundTripMilliseconds.Value < 0))) throw new ArgumentOutOfRangeException(nameof(queuedBytes));
            now += deltaSeconds;
            var wanted = queuedBytes >= 32768 || roundTripMilliseconds > 150 ? NetworkPrecisionPreset.Compact :
                queuedBytes >= 8192 || !roundTripMilliseconds.HasValue || roundTripMilliseconds > 80 ? NetworkPrecisionPreset.Balanced : NetworkPrecisionPreset.FullPrecision;
            wanted = (NetworkPrecisionPreset)Math.Min((int)wanted, (int)lowestAllowed);
            if (candidate != wanted) { candidate = wanted; since = now; }
            if (candidate != active && now - since >= (candidate > active ? 2 : 5)) active = candidate;
            if (active > lowestAllowed) active = lowestAllowed;
            return new AdaptiveNetworkDecision { Precision = active, SnapshotEvery = active == NetworkPrecisionPreset.Compact ? 2 : 1 };
        }
    }
}
