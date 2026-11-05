using System;
using SocketJack.Multiplayer.Serialization;
using UnityEngine;

namespace SocketJack.Multiplayer.Components
{
    /// <summary>Shared precision asset. All participants use identical settings; configure before spawning.</summary>
    [CreateAssetMenu(menuName = "SocketJack/Precision Profile", fileName = "NetworkPrecision")]
    public sealed class NetworkPrecisionAsset : ScriptableObject
    {
        [Serializable]
        public sealed class Range
        {
            public double Minimum = -1024, Maximum = 1024, Step = 1d / 128;
            internal NetworkQuantizationRange Build() => new NetworkQuantizationRange(Minimum, Maximum, Step);
        }
        public NetworkPrecisionPreset Preset = NetworkPrecisionPreset.Balanced;
        public Range Position = new Range();
        public Range Rotation = new Range { Minimum = -1, Maximum = 1, Step = 1d / 32768 };
        public Range Scale = new Range { Minimum = 0, Maximum = 16, Step = 1d / 1024 };
        public Range Velocity = new Range { Minimum = -128, Maximum = 128 };
        public Range AngularVelocity = new Range { Minimum = -64, Maximum = 64, Step = 1d / 256 };
        public Range Scalar = new Range();
        public NetworkPrecisionProfile CreateProfile() => Preset == NetworkPrecisionPreset.Custom
            ? new NetworkPrecisionProfile(Position.Build(), Rotation.Build(), Scale.Build(), Velocity.Build(), AngularVelocity.Build(), Scalar.Build())
            : NetworkPrecisionProfile.ForPreset(Preset);
    }

    /// <summary>Explicit Unity numeric adapter; no Unity object graphs or remote-selected types.</summary>
    public sealed class UnityQuantizedNetworkSerializer : INetworkSerializer
    {
        private readonly QuantizedNetworkSerializer codec;
        public string ProtocolId => "sj.unity.numeric.v1/" + codec.ProtocolId;
        public UnityQuantizedNetworkSerializer(NetworkPrecisionProfile profile, NetworkQuantizationSemantic semantic = NetworkQuantizationSemantic.Scalar)
        { codec = new QuantizedNetworkSerializer(profile, semantic); }
        private static Type WireType(Type type)
        {
            if (type == typeof(Vector2)) return typeof(System.Numerics.Vector2);
            if (type == typeof(Vector3)) return typeof(System.Numerics.Vector3);
            if (type == typeof(Vector4) || type == typeof(Color)) return typeof(System.Numerics.Vector4);
            if (type == typeof(Quaternion)) return typeof(System.Numerics.Quaternion);
            return type;
        }
        public bool CanSerialize(Type type) => codec.CanSerialize(WireType(type));
        public byte[] Serialize(object value, Type declaredType, int maxBytes = 8192)
        {
            object wire = value;
            if (value is Vector2 a) wire = new System.Numerics.Vector2(a.x, a.y);
            else if (value is Vector3 b) wire = new System.Numerics.Vector3(b.x, b.y, b.z);
            else if (value is Vector4 c) wire = new System.Numerics.Vector4(c.x, c.y, c.z, c.w);
            else if (value is Color color) wire = new System.Numerics.Vector4(color.r, color.g, color.b, color.a);
            else if (value is Quaternion rotation) wire = new System.Numerics.Quaternion(rotation.x, rotation.y, rotation.z, rotation.w);
            return codec.Serialize(wire, WireType(declaredType), maxBytes);
        }
        public object Deserialize(byte[] bytes, Type declaredType, int maxBytes = 8192)
        {
            object wire = codec.Deserialize(bytes, WireType(declaredType), maxBytes);
            if (declaredType == typeof(Vector2)) { var v = (System.Numerics.Vector2)wire; return new Vector2(v.X, v.Y); }
            if (declaredType == typeof(Vector3)) { var v = (System.Numerics.Vector3)wire; return new Vector3(v.X, v.Y, v.Z); }
            if (declaredType == typeof(Vector4)) { var v = (System.Numerics.Vector4)wire; return new Vector4(v.X, v.Y, v.Z, v.W); }
            if (declaredType == typeof(Color)) { var v = (System.Numerics.Vector4)wire; return new Color(v.X, v.Y, v.Z, v.W); }
            if (declaredType == typeof(Quaternion)) { var v = (System.Numerics.Quaternion)wire; return new Quaternion(v.X, v.Y, v.Z, v.W).normalized; }
            return wire;
        }
    }
}
