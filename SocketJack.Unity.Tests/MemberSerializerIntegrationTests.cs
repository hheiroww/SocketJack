using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Multiplayer.Serialization;
using SocketJack.Multiplayer.Transport;

namespace SocketJack.Multiplayer.Tests
{
    [TestClass]
    public class MemberSerializerIntegrationTests
    {
        [DataTestMethod]
        [DataRow(TransportType.Udp)]
        [DataRow(TransportType.Tcp)]
        public void CustomLargeMemberFormatterTraversesNativeTransportAndObservers(TransportType transport)
        {
            var sessionOptions = new NetworkSessionOptions { MaxMessageBytes = 40 + NetworkMemberSyncOptions.AbsoluteMaxMemberBytes };
            using var server = new NetworkServer(GameTransport.Create(transport), sessionOptions);
            using var owner = new NetworkClient(GameTransport.Create(transport), sessionOptions);
            using var observer = new NetworkClient(GameTransport.Create(transport), sessionOptions);
            using var late = new NetworkClient(GameTransport.Create(transport), sessionOptions);
            using var serverMembers = new NetworkMemberServer(server);
            using var ownerMembers = new NetworkMemberClient(owner);
            using var observerMembers = new NetworkMemberClient(observer);
            using var lateMembers = new NetworkMemberClient(late);
            byte[] serverValue = Enumerable.Range(0, 6000).Select(i => (byte)(i * 17)).ToArray();
            byte[] ownerValue = Array.Empty<byte>(), observerValue = Array.Empty<byte>(), lateValue = Array.Empty<byte>();
            var serverCodec = new XorBytesSerializer(); var ownerCodec = new XorBytesSerializer(); var observerCodec = new XorBytesSerializer(); var lateCodec = new XorBytesSerializer();
            NetworkMemberSchema Schema(INetworkSerializer codec, Func<object> get, Action<object> set) => new NetworkMemberSchema(codec,
                new[] { new NetworkMemberBinding(1, "game.large-payload.v1", typeof(byte[]), get, set, NetworkMemberAuthority.Owner, .016, true, 8192) });
            void PumpMembers(Func<bool> done)
            {
                var watch = Stopwatch.StartNew();
                while (!done() && watch.Elapsed < TimeSpan.FromSeconds(8))
                {
                    server.Update(.01); owner.Update(.01); observer.Update(.01); late.Update(.01);
                    serverMembers.Update(.01); ownerMembers.Update(.01); observerMembers.Update(.01); lateMembers.Update(.01); Thread.Sleep(1);
                }
                Assert.IsTrue(done(), transport + " custom large member condition timed out.");
            }
            server.Start(new IPEndPoint(IPAddress.Loopback, 0));
            owner.Connect(server.Transport.LocalEndPoint); observer.Connect(server.Transport.LocalEndPoint);
            PumpMembers(() => owner.IsConnected && observer.IsConnected);
            var obj = server.Spawn(1, owner.LocalPeerId);
            serverMembers.RegisterObject(obj.Id, Schema(serverCodec, () => serverValue, value => serverValue = (byte[])value));
            PumpMembers(() => owner.Objects.ContainsKey(obj.Id) && observer.Objects.ContainsKey(obj.Id));
            ownerMembers.RegisterObject(obj.Id, Schema(ownerCodec, () => ownerValue, value => ownerValue = (byte[])value));
            observerMembers.RegisterObject(obj.Id, Schema(observerCodec, () => observerValue, value => observerValue = (byte[])value));
            PumpMembers(() => ownerMembers.IsReady(obj.Id) && observerMembers.IsReady(obj.Id));
            CollectionAssert.AreEqual(serverValue, ownerValue); CollectionAssert.AreEqual(serverValue, observerValue);
            byte[] proposed = Enumerable.Range(0, 6100).Select(i => (byte)(255 - i * 13)).ToArray(); ownerValue = proposed;
            PumpMembers(() => serverValue.SequenceEqual(proposed) && observerValue.SequenceEqual(proposed));
            late.Connect(server.Transport.LocalEndPoint);
            PumpMembers(() => late.Objects.ContainsKey(obj.Id));
            lateMembers.RegisterObject(obj.Id, Schema(lateCodec, () => lateValue, value => lateValue = (byte[])value));
            PumpMembers(() => lateMembers.IsReady(obj.Id));
            CollectionAssert.AreEqual(proposed, lateValue, "Late join must receive the current custom-serialized canonical member value.");
            Assert.IsTrue(serverCodec.Reads > 0 && ownerCodec.Reads > 0 && observerCodec.Reads > 0 && lateCodec.Reads > 0,
                "Each receiving endpoint must use the selected custom formatter.");
            Assert.AreEqual(transport, server.Transport.Type);
        }

        private sealed class XorBytesSerializer : INetworkSerializer
        {
            public int Reads;
            public string ProtocolId => "test.xor-bytes.v1";
            public bool CanSerialize(Type declaredType) => declaredType == typeof(byte[]);
            public byte[] Serialize(object value, Type declaredType, int maxBytes = 8192)
            {
                if (!CanSerialize(declaredType)) throw new NotSupportedException();
                var input = (byte[])value;
                if (input == null || input.Length + 1 > maxBytes) throw new InvalidDataException();
                var output = new byte[input.Length + 1]; output[0] = 0xa5;
                for (int i = 0; i < input.Length; i++) output[i + 1] = (byte)(input[i] ^ 0x5a);
                return output;
            }
            public object Deserialize(byte[] bytes, Type declaredType, int maxBytes = 8192)
            {
                if (!CanSerialize(declaredType)) throw new NotSupportedException();
                if (bytes == null || bytes.Length < 1 || bytes.Length > maxBytes || bytes[0] != 0xa5) throw new InvalidDataException();
                var output = new byte[bytes.Length - 1];
                for (int i = 0; i < output.Length; i++) output[i] = (byte)(bytes[i + 1] ^ 0x5a);
                Reads++; return output;
            }
        }
    }
}
