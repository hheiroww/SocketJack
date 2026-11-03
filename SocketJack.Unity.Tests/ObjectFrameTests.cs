using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Unity.Transport;

namespace SocketJack.Unity.Tests
{
    [TestClass]
    public class ObjectFrameTests
    {
        [TestMethod]
        public void SmallObjectUsesOneFrameAndLargeObjectRebuildsOutOfOrderExactlyOnce()
        {
            var native = new ManualTransport(); using var sender = Ready(native);
            var receivedNative = new ManualTransport(); using var receiver = Ready(receivedNative);
            var received = new List<TransportEvent>(); receiver.EventReceived += e => { if (e.Type == TransportEventType.Data) received.Add(e); };
            Assert.IsTrue(sender.Send(1, new ArraySegment<byte>(new byte[40]))); Assert.AreEqual(1, native.Sent.Count);
            Assert.AreEqual(68, native.Sent[0].Payload.Length);
            native.Sent.Clear(); var payload = Bytes(8192);
            Assert.IsTrue(sender.Send(1, new ArraySegment<byte>(payload))); sender.Poll();
            Assert.IsTrue(native.Sent.Count > 1);
            Assert.IsTrue(native.Sent.All(e => e.Payload.Length <= 910 && PacketBudget.Default.GetPacketBytes(e.Payload.Length) <= 998));
            foreach (var packet in native.Sent.AsEnumerable().Reverse()) receivedNative.Incoming.Enqueue(packet);
            receivedNative.Incoming.Enqueue(native.Sent[0]); receiver.Poll();
            Assert.AreEqual(1, received.Count); CollectionAssert.AreEqual(payload, received[0].Payload);
            Assert.AreEqual(DeliveryMode.ReliableUnordered, received[0].Delivery);
        }

        [DataTestMethod]
        [DataRow(DeliveryMode.ReliableUnordered)]
        [DataRow(DeliveryMode.ReliableOrdered)]
        public void LogicalOrderingIsExplicitAcrossMultipartAndSingleFrame(DeliveryMode delivery)
        {
            var native = new ManualTransport(); using var sender = Ready(native);
            var receivedNative = new ManualTransport(); using var receiver = Ready(receivedNative);
            var received = new List<int>(); receiver.EventReceived += e => { if (e.Type == TransportEventType.Data) received.Add(e.Payload.Length); };
            sender.Send(1, new ArraySegment<byte>(Bytes(4096)), delivery); sender.Poll();
            var large = native.Sent.ToArray(); native.Sent.Clear();
            sender.Send(1, new ArraySegment<byte>(Bytes(40)), delivery); sender.Poll();
            receivedNative.Incoming.Enqueue(native.Sent.Single()); receiver.Poll();
            Assert.AreEqual(delivery == DeliveryMode.ReliableOrdered ? 0 : 1, received.Count);
            foreach (var packet in large.AsEnumerable().Reverse()) receivedNative.Incoming.Enqueue(packet); receiver.Poll();
            CollectionAssert.AreEqual(delivery == DeliveryMode.ReliableOrdered ? new[] { 4096, 40 } : new[] { 40, 4096 }, received);
        }

        [TestMethod]
        public void MultipartSequencedStateUsesReliableFragmentsAndDropsOlderCompleteState()
        {
            var native = new ManualTransport(); using var sender = Ready(native);
            var receivedNative = new ManualTransport(); using var receiver = Ready(receivedNative);
            var received = new List<TransportEvent>(); receiver.EventReceived += e => { if (e.Type == TransportEventType.Data) received.Add(e); };
            sender.Send(1, new ArraySegment<byte>(Bytes(4096)), DeliveryMode.UnreliableSequenced, 1); sender.Poll();
            var older = native.Sent.ToArray(); native.Sent.Clear();
            sender.Send(1, new ArraySegment<byte>(Bytes(5000)), DeliveryMode.UnreliableSequenced, 1); sender.Poll();
            Assert.IsTrue(native.Sent.All(e => e.Delivery == DeliveryMode.ReliableUnordered));
            foreach (var packet in native.Sent.Concat(older)) receivedNative.Incoming.Enqueue(packet); receiver.Poll();
            Assert.AreEqual(1, received.Count); Assert.AreEqual(5000, received[0].Payload.Length);
            Assert.AreEqual(DeliveryMode.UnreliableSequenced, received[0].Delivery);
        }

        [TestMethod]
        public void WholeMessageAcceptanceRemainsAtomicUnderNativeBackpressure()
        {
            var native = new ManualTransport { Blocked = true };
            using var sender = Ready(native, new ObjectFrameOptions { MaxQueuedMessagesPerPeer = 1 });
            Assert.IsTrue(sender.Send(1, new ArraySegment<byte>(Bytes(8192))));
            Assert.IsFalse(sender.Send(1, new ArraySegment<byte>(Bytes(100)))); Assert.AreEqual(0, native.Sent.Count);
            native.Blocked = false; sender.Poll(); Assert.AreEqual(10, native.Sent.Count);
            var receivedNative = new ManualTransport(); using var receiver = Ready(receivedNative); int completed = 0;
            receiver.EventReceived += e => { if (e.Type == TransportEventType.Data) { completed++; Assert.AreEqual(8192, e.Payload.Length); } };
            foreach (var packet in native.Sent) receivedNative.Incoming.Enqueue(packet); receiver.Poll(); Assert.AreEqual(1, completed);
        }

        [TestMethod]
        public void OrderedGapCompletionRespectsPollCallbackBudgetAndDisconnectPurgesQueuedData()
        {
            var native = new ManualTransport(); using var sender = Ready(native);
            for (int i = 0; i < 8; i++) sender.Send(1, new ArraySegment<byte>(new byte[] { (byte)i }), DeliveryMode.ReliableOrdered);
            var receivedNative = new ManualTransport(); using var receiver = Ready(receivedNative);
            int data = 0, disconnected = 0;
            receiver.EventReceived += e => { if (e.Type == TransportEventType.Data) data++; if (e.Type == TransportEventType.Disconnected) disconnected++; };
            foreach (var packet in native.Sent.Skip(1)) { receivedNative.Incoming.Enqueue(packet); receiver.Poll(1); Assert.AreEqual(0, data); }
            receivedNative.Incoming.Enqueue(native.Sent[0]); receiver.Poll(1); Assert.AreEqual(1, data);
            receiver.Poll(1); Assert.AreEqual(2, data);
            receiver.Disconnect(1); receiver.Poll(1);
            Assert.AreEqual(2, data); Assert.AreEqual(1, disconnected);
        }

        [TestMethod]
        public void SmallUnorderedMessageOvertakesQueuedMultipartObject()
        {
            var native = new ManualTransport { Blocked = true }; using var sender = Ready(native);
            sender.Send(1, new ArraySegment<byte>(Bytes(16384))); sender.Send(1, new ArraySegment<byte>(Bytes(40)));
            native.Blocked = false; sender.Poll();
            Assert.AreEqual(40 + ObjectFrameTransport.HeaderSize, native.Sent[1].Payload.Length);
            var receivedNative = new ManualTransport(); using var receiver = Ready(receivedNative);
            var received = new List<int>(); receiver.EventReceived += e => { if (e.Type == TransportEventType.Data) received.Add(e.Payload.Length); };
            foreach (var packet in native.Sent) receivedNative.Incoming.Enqueue(packet); receiver.Poll();
            CollectionAssert.AreEqual(new[] { 40, 16384 }, received);
        }

        [TestMethod]
        public void ReassemblyIsPeerScopedTimesOutAndRejectsChangedDuplicates()
        {
            var native = new ManualTransport(); using var sender = Ready(native); sender.Send(1, new ArraySegment<byte>(Bytes(1000))); sender.Poll();
            var first = native.Sent[0]; var last = native.Sent[1];
            var receivedNative = new ManualTransport(); using var receiver = Ready(receivedNative,
                new ObjectFrameOptions { ReassemblyTimeout = TimeSpan.FromMilliseconds(25), MaxConcurrentObjectsPerPeer = 1 });
            receivedNative.Incoming.Enqueue(new TransportEvent(TransportEventType.Connected, 2)); receiver.Poll();
            int completed = 0, disconnected = 0; receiver.EventReceived += e => { if (e.Type == TransportEventType.Data) completed++; if (e.Type == TransportEventType.Disconnected) disconnected++; };
            receivedNative.Incoming.Enqueue(first);
            receivedNative.Incoming.Enqueue(new TransportEvent(TransportEventType.Data, 2, last.Payload, last.Delivery, last.Channel)); receiver.Poll();
            Assert.AreEqual(0, completed); Thread.Sleep(40);
            receivedNative.Incoming.Enqueue(last); receiver.Poll(); Assert.AreEqual(0, completed);
            var changed = (byte[])last.Payload.Clone(); changed[changed.Length - 1] ^= 1;
            receivedNative.Incoming.Enqueue(new TransportEvent(TransportEventType.Data, 1, changed, last.Delivery, last.Channel)); receiver.Poll();
            receiver.Poll(); Assert.AreEqual(1, disconnected); Assert.AreEqual(0, completed);
        }

        [TestMethod]
        public void RealUdpMultipartSurvivesDroppedDataAckDuplicatesAndReordering()
        {
            var options = new TransportOptions { RetransmitInterval = TimeSpan.FromMilliseconds(15), MaxRetransmits = 100 };
            using var server = new ObjectFrameTransport(new UdpGameTransport(options));
            using var client = new ObjectFrameTransport(new UdpGameTransport(options));
            server.StartServer(new IPEndPoint(IPAddress.Loopback, 0)); using var proxy = new DatagramProxy(server.LocalEndPoint);
            client.StartClient(proxy.LocalEndPoint); Pump(() => client.LocalPeerId != 0, server, client, proxy);
            int data = 0, acks = 0; byte[] held = null; int largestPacket = 0;
            proxy.ClientToServer = packet =>
            {
                if (packet[5] != 6) return true;
                largestPacket = Math.Max(largestPacket, packet.Length + 48); data++;
                if (data == 1) return false;
                if (data == 2) { held = packet; return false; }
                if (data == 3) { proxy.SendServer(packet); proxy.SendServer(packet); proxy.SendServer(held); return false; }
                return true;
            };
            proxy.ServerToClient = packet => packet[5] != 7 || ++acks != 1;
            var payload = Bytes(16384); var received = new List<byte[]>();
            server.EventReceived += e => { if (e.Type == TransportEventType.Data) received.Add(e.Payload); };
            Assert.IsTrue(client.Send(0, new ArraySegment<byte>(payload)));
            Pump(() => received.Count == 1 && data >= 20, server, client, proxy);
            var settle = Stopwatch.StartNew(); while (settle.ElapsedMilliseconds < 80) { client.Poll(); proxy.Poll(); server.Poll(); proxy.Poll(); Thread.Sleep(1); }
            Assert.AreEqual(1, received.Count); CollectionAssert.AreEqual(payload, received[0]); Assert.IsTrue(largestPacket <= 998);
        }

        [DataTestMethod]
        [DataRow(TransportType.Udp)]
        [DataRow(TransportType.Tcp)]
        public void SessionsAutomaticallyReplicateLargeStatesThroughSmallFrames(TransportType type)
        {
            var options = new NetworkSessionOptions { MaxStateBytes = 16384, MaxMessageBytes = 16384 };
            using var server = new NetworkServer(GameTransport.Create(type), options);
            using var client = new NetworkClient(GameTransport.Create(type), options);
            server.Start(new IPEndPoint(IPAddress.Loopback, 0)); client.Connect(server.Transport.LocalEndPoint);
            var initial = Bytes(8192); var obj = server.Spawn(1, state: initial);
            var watch = Stopwatch.StartNew();
            while ((!client.Objects.ContainsKey(obj.Id) || !client.Objects[obj.Id].State.SequenceEqual(initial)) && watch.ElapsedMilliseconds < 5000)
            { server.Update(1d / 60, 4096); client.Update(1d / 60, 4096); Thread.Sleep(1); }
            Assert.IsTrue(client.IsConnected); CollectionAssert.AreEqual(initial, client.Objects[obj.Id].State);
            var changed = Bytes(12000); server.SetState(obj.Id, changed); watch.Restart();
            while (!client.Objects[obj.Id].State.SequenceEqual(changed) && watch.ElapsedMilliseconds < 5000)
            { server.Update(1d / 60, 4096); client.Update(1d / 60, 4096); Thread.Sleep(1); }
            CollectionAssert.AreEqual(changed, client.Objects[obj.Id].State);
        }

        private static byte[] Bytes(int count) => Enumerable.Range(0, count).Select(i => (byte)(i % 251)).ToArray();
        private static ObjectFrameTransport Ready(ManualTransport native, ObjectFrameOptions options = null)
        { var transport = new ObjectFrameTransport(native, options); transport.StartServer(new IPEndPoint(IPAddress.Loopback, 0)); native.Incoming.Enqueue(new TransportEvent(TransportEventType.Connected, 1)); transport.Poll(); return transport; }
        private static void Pump(Func<bool> done, IGameTransport server, IGameTransport client, DatagramProxy proxy)
        {
            var timeout = Stopwatch.StartNew();
            while (!done() && timeout.ElapsedMilliseconds < 5000) { client.Poll(); proxy.Poll(); server.Poll(); proxy.Poll(); client.Poll(); Thread.Sleep(1); }
            Assert.IsTrue(done(), "Object transport operation timed out.");
        }
        private sealed class ManualTransport : IGameTransport
        {
            internal readonly Queue<TransportEvent> Incoming = new Queue<TransportEvent>();
            internal readonly List<TransportEvent> Sent = new List<TransportEvent>();
            internal bool Blocked;
            public TransportType Type => TransportType.Custom;
            public bool IsServer { get; private set; }
            public bool IsRunning { get; private set; }
            public ulong LocalPeerId => 1;
            public IPEndPoint LocalEndPoint => new IPEndPoint(IPAddress.Loopback, 0);
            public int MaxPayloadSize => 1160;
            public event Action<TransportEvent> EventReceived;
            public void StartServer(IPEndPoint endpoint) { IsServer = true; IsRunning = true; }
            public void StartClient(IPEndPoint endpoint, byte[] connectionData = null) { IsServer = false; IsRunning = true; }
            public void Poll(int maxEvents = 256) { for (int i = 0; i < maxEvents && Incoming.Count > 0; i++) EventReceived?.Invoke(Incoming.Dequeue()); }
            public bool Send(ulong peerId, ArraySegment<byte> payload, DeliveryMode delivery = DeliveryMode.ReliableUnordered, byte channel = 0)
            { if (Blocked) return false; Sent.Add(new TransportEvent(TransportEventType.Data, peerId, payload.ToArray(), delivery, channel)); return true; }
            public void Disconnect(ulong peerId, string reason = "Disconnected") => Incoming.Enqueue(new TransportEvent(TransportEventType.Disconnected, peerId, reason: reason));
            public void Stop() { IsRunning = false; Incoming.Clear(); }
            public void Dispose() => Stop();
        }
        private sealed class DatagramProxy : IDisposable
        {
            private readonly Socket _front = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            private readonly Socket _back = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            private readonly IPEndPoint _server; private EndPoint _client;
            internal Func<byte[], bool> ClientToServer = _ => true, ServerToClient = _ => true;
            internal IPEndPoint LocalEndPoint => (IPEndPoint)_front.LocalEndPoint;
            internal DatagramProxy(IPEndPoint server)
            { _server = server; _front.Bind(new IPEndPoint(IPAddress.Loopback, 0)); _back.Bind(new IPEndPoint(IPAddress.Loopback, 0)); _front.Blocking = _back.Blocking = false; }
            internal void Poll()
            {
                Drain(_front, (bytes, source) => { _client = source; if (ClientToServer(bytes)) _back.SendTo(bytes, _server); });
                Drain(_back, (bytes, source) => { if (_client != null && ServerToClient(bytes)) _front.SendTo(bytes, _client); });
            }
            internal void SendServer(byte[] bytes) => _back.SendTo(bytes, _server);
            private static void Drain(Socket socket, Action<byte[], EndPoint> receive)
            {
                var buffer = new byte[1500];
                for (int i = 0; i < 256; i++)
                {
                    EndPoint source = new IPEndPoint(IPAddress.Any, 0); int count;
                    try { count = socket.ReceiveFrom(buffer, ref source); }
                    catch (SocketException error) when (error.SocketErrorCode == SocketError.WouldBlock || error.SocketErrorCode == SocketError.ConnectionReset) { break; }
                    var bytes = new byte[count]; Buffer.BlockCopy(buffer, 0, bytes, 0, count); receive(bytes, source);
                }
            }
            public void Dispose() { _front.Dispose(); _back.Dispose(); }
        }
    }
}
