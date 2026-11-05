using System;
using System.IO;
using SocketJack.Multiplayer.Components;
using SocketJack.Multiplayer.Serialization;
using UnityEngine;

namespace SocketJack.Tests.MemberSync
{
    public sealed class MemberProbeProvider : MonoBehaviour, INetworkSerializerProvider
    {
        public INetworkSerializer CreateSerializer() => new ProbeSerializer();

        public sealed class ProbeSerializer : INetworkSerializer
        {
            private readonly SocketJackBinaryFormatter inner = NetworkManager.CreateDefaultSerializer();
            public int Reads;
            public string ProtocolId => "native-probe.xor.v1/" + inner.ProtocolId;
            public bool CanSerialize(Type type) => inner.CanSerialize(type);
            public byte[] Serialize(object value, Type type, int maxBytes = 8192)
            {
                if (maxBytes < 5) throw new InvalidDataException();
                var bytes = inner.Serialize(value, type, maxBytes - 1);
                var result = new byte[bytes.Length + 1]; result[0] = 0x5a;
                for (int i = 0; i < bytes.Length; i++) result[i + 1] = (byte)(bytes[i] ^ 0xa5);
                return result;
            }
            public object Deserialize(byte[] bytes, Type type, int maxBytes = 8192)
            {
                if (bytes == null || bytes.Length < 5 || bytes.Length > maxBytes || bytes[0] != 0x5a) throw new InvalidDataException();
                var copy = new byte[bytes.Length - 1];
                for (int i = 0; i < copy.Length; i++) copy[i] = (byte)(bytes[i + 1] ^ 0xa5);
                Reads++; return inner.Deserialize(copy, type, maxBytes - 1);
            }
        }
    }
}
