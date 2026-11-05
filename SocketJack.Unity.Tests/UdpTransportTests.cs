using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Multiplayer.Transport;

namespace SocketJack.Multiplayer.Tests
{
    [TestClass]
    public class UdpTransportTests
    {
        private static readonly IPEndPoint Any = new IPEndPoint(IPAddress.Loopback, 0);
        private static TransportOptions Fast() => new TransportOptions
        {
            RetransmitInterval = TimeSpan.FromMilliseconds(15),
            HeartbeatInterval = TimeSpan.FromMilliseconds(100),
            PeerTimeout = TimeSpan.FromSeconds(2),
            ConnectTimeout = TimeSpan.FromSeconds(2),
            MaxRetransmits = 100
        };

        [TestMethod]
        public void UdpRoundTripRetainsPayloadAndIndependentDeliveryChannels()
        {
            using var server = new UdpGameTransport(Fast());
            using var client = new UdpGameTransport(Fast());
            var received = new List<TransportEvent>();
            ulong peer = 0;
            server.EventReceived += e => { if (e.Type == TransportEventType.Connected) peer = e.PeerId; if (e.Type == TransportEventType.Data) received.Add(e); };
            server.StartServer(Any);
            client.StartClient(server.LocalEndPoint);
            PumpUntil(() => peer != 0 && client.LocalPeerId != 0, server, client);
            var source = new byte[] { 99, 11, 22, 88 };
            Assert.IsTrue(client.Send(0, new ArraySegment<byte>(source, 1, 2), DeliveryMode.ReliableOrdered, 1));
            source[1] = 0;
            Assert.IsTrue(client.Send(0, new ArraySegment<byte>(new byte[] { 33 }), DeliveryMode.Unreliable, 2));
            Assert.IsTrue(client.Send(0, new ArraySegment<byte>(new byte[] { 44 }), DeliveryMode.UnreliableSequenced, 3));
            PumpUntil(() => received.Count == 3, server, client);
            var reliable = received.Single(e => e.Channel == 1);
            CollectionAssert.AreEqual(new byte[] { 11, 22 }, reliable.Payload);
            Assert.AreEqual(DeliveryMode.ReliableOrdered, reliable.Delivery);
            Assert.AreEqual(peer, reliable.PeerId);
            byte[] echo = null;
            client.EventReceived += e => { if (e.Type == TransportEventType.Data) { Assert.AreEqual(0UL, e.PeerId); echo = e.Payload; } };
            Assert.IsTrue(server.Send(peer, new ArraySegment<byte>(reliable.Payload)));
            PumpUntil(() => echo != null, server, client);
            CollectionAssert.AreEqual(reliable.Payload, echo);
        }

        [TestMethod]
        public void UdpReliableDeliverySurvivesDroppedDataAckAndReorderingExactlyOnce()
        {
            using var server = new UdpGameTransport(Fast());
            using var client = new UdpGameTransport(Fast());
            server.StartServer(Any);
            using var proxy = new DatagramProxy(server.LocalEndPoint);
            client.StartClient(proxy.LocalEndPoint);
            var values = new List<byte>();
            server.EventReceived += e => { if (e.Type == TransportEventType.Data) values.Add(e.Payload[0]); };
            PumpUntil(() => client.LocalPeerId != 0, server, client, proxy);
            int data = 0, acks = 0;
            byte[] held = null;
            proxy.ClientToServer = packet =>
            {
                if (packet[5] != 6) return true;
                data++;
                if (data == 1) return false; // Original sequence 1 is lost.
                if (data == 2) { held = packet; return false; } // Hold sequence 2.
                if (data == 3 && held != null) { proxy.SendServer(held); held = null; } // Deliver 2, 3, then retry 1.
                return true;
            };
            proxy.ServerToClient = packet => packet[5] != 7 || ++acks != 1;
            for (byte i = 1; i <= 3; i++) Assert.IsTrue(client.Send(0, new ArraySegment<byte>(new[] { i }), DeliveryMode.ReliableOrdered));
            PumpUntil(() => values.Count == 3 && data >= 4, server, client, proxy);
            PumpFor(TimeSpan.FromMilliseconds(120), server, client, proxy);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, values.ToArray());
            Assert.IsTrue(acks >= 3);
        }

        [TestMethod]
        public void UdpSequencedDropsStaleSnapshotsAndChannelsAvoidHeadOfLineBlocking()
        {
            using var server = new UdpGameTransport(Fast());
            using var client = new UdpGameTransport(Fast());
            server.StartServer(Any);
            using var proxy = new DatagramProxy(server.LocalEndPoint);
            client.StartClient(proxy.LocalEndPoint);
            var values = new List<byte>();
            server.EventReceived += e => { if (e.Type == TransportEventType.Data) values.Add(e.Payload[0]); };
            PumpUntil(() => client.LocalPeerId != 0, server, client, proxy);
            byte[] stale = null;
            proxy.ClientToServer = packet =>
            {
                if (packet[5] != 6) return true;
                if (packet[6] == 0) return false; // Stall reliable channel zero.
                if (packet[6] == 2 && stale == null && packet[40] == 10) { stale = packet; return false; }
                if (packet[6] == 2 && packet[40] == 20) { proxy.SendServer(packet); proxy.SendServer(stale); return false; }
                return true;
            };
            Assert.IsTrue(client.Send(0, new ArraySegment<byte>(new byte[] { 1 }), DeliveryMode.ReliableOrdered, 0));
            Assert.IsTrue(client.Send(0, new ArraySegment<byte>(new byte[] { 2 }), DeliveryMode.ReliableOrdered, 1));
            Assert.IsTrue(client.Send(0, new ArraySegment<byte>(new byte[] { 10 }), DeliveryMode.UnreliableSequenced, 2));
            Assert.IsTrue(client.Send(0, new ArraySegment<byte>(new byte[] { 20 }), DeliveryMode.UnreliableSequenced, 2));
            PumpUntil(() => values.Count == 2, server, client, proxy);
            CollectionAssert.AreEquivalent(new byte[] { 2, 20 }, values.ToArray());
        }

        [TestMethod]
        public void UdpAdmissionCapacityAndCookieValidationProtectPeerAllocation()
        {
            var options = Fast();
            int validations = 0;
            options.MaxPeers = 1;
            options.ConnectionValidator = (endpoint, ticket) => { validations++; return ticket.SequenceEqual(new byte[] { 42 }); };
            using var server = new UdpGameTransport(options);
            using var accepted = new UdpGameTransport(Fast());
            using var rejected = new UdpGameTransport(Fast());
            server.StartServer(Any);
            using (var attacker = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            {
                var forged = new byte[67];
                BinaryPrimitives.WriteUInt32LittleEndian(forged, 0x31474a53);
                forged[4] = 1; forged[5] = 3;
                BinaryPrimitives.WriteUInt16LittleEndian(forged.AsSpan(36), 27);
                BinaryPrimitives.WriteInt64LittleEndian(forged.AsSpan(40), DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                BinaryPrimitives.WriteUInt16LittleEndian(forged.AsSpan(64), 1200);
                forged[66] = 4;
                for (int i = 0; i < 20; i++) attacker.SendTo(forged, server.LocalEndPoint);
                attacker.SendTo(new byte[3000], server.LocalEndPoint);
            }
            server.Poll(64);
            Assert.AreEqual(0, validations);
            string reason = null;
            rejected.EventReceived += e => { if (e.Type == TransportEventType.Disconnected) reason = e.Reason; };
            rejected.StartClient(server.LocalEndPoint, new byte[] { 0 });
            PumpUntil(() => reason != null, server, rejected);
            Assert.AreEqual("Connection rejected", reason);
            Assert.AreEqual(1, validations);
            accepted.StartClient(server.LocalEndPoint, new byte[] { 42 });
            PumpUntil(() => accepted.LocalPeerId != 0, server, accepted);
            rejected.Stop();
            reason = null;
            rejected.StartClient(server.LocalEndPoint, new byte[] { 42 });
            PumpUntil(() => reason != null, server, accepted, null, rejected);
            Assert.AreEqual("Server is full", reason);
            Assert.AreEqual(2, validations);
        }

        [TestMethod]
        public void UdpRejectsSpoofedEndpointsAndStaleSessionsAfterRestart()
        {
            using var server = new UdpGameTransport(Fast());
            using var client = new UdpGameTransport(Fast());
            server.StartServer(Any);
            using var proxy = new DatagramProxy(server.LocalEndPoint);
            client.StartClient(proxy.LocalEndPoint);
            int data = 0;
            byte[] captured = null;
            server.EventReceived += e => { if (e.Type == TransportEventType.Data) data++; };
            proxy.ClientToServer = p => { if (p[5] == 6) captured = p; return true; };
            PumpUntil(() => client.LocalPeerId != 0, server, client, proxy);
            Assert.IsTrue(client.Send(0, new ArraySegment<byte>(new byte[] { 5 }), DeliveryMode.Unreliable));
            PumpUntil(() => data == 1, server, client, proxy);
            using (var attacker = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)) attacker.SendTo(captured, server.LocalEndPoint);
            server.Poll();
            Assert.AreEqual(1, data);
            ulong oldId = client.LocalPeerId;
            client.Stop();
            PumpFor(TimeSpan.FromMilliseconds(30), server, null, proxy);
            client.StartClient(proxy.LocalEndPoint);
            PumpUntil(() => client.LocalPeerId != 0, server, client, proxy);
            Assert.AreNotEqual(oldId, client.LocalPeerId);
            proxy.SendServer(captured);
            server.Poll();
            Assert.AreEqual(1, data);
        }

        [TestMethod]
        public void UdpReliableBackpressureRecoversAndPollBoundsCallbacks()
        {
            var options = Fast(); options.MaxPendingReliablePerPeer = 2;
            using var server = new UdpGameTransport(options);
            using var client = new UdpGameTransport(options);
            server.StartServer(Any);
            client.StartClient(server.LocalEndPoint);
            PumpUntil(() => client.LocalPeerId != 0, server, client);
            int received = 0;
            server.EventReceived += e => { if (e.Type == TransportEventType.Data) received++; };
            var payload = new ArraySegment<byte>(new byte[] { 1 });
            Assert.IsTrue(client.Send(0, payload));
            Assert.IsTrue(client.Send(0, payload));
            Assert.IsFalse(client.Send(0, payload));
            server.Poll(1);
            Assert.AreEqual(1, received);
            server.Poll(1);
            Assert.AreEqual(2, received);
            client.Poll();
            Assert.IsTrue(client.Send(0, payload));
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => client.Send(0, new ArraySegment<byte>(new byte[client.MaxPayloadSize + 1])));
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => client.Send(0, payload, channel: 4));
        }

        [TestMethod]
        public void UdpPeerAndConnectTimeoutsAreObservableAndCanRestart()
        {
            var options = Fast();
            options.PeerTimeout = TimeSpan.FromMilliseconds(160);
            options.HeartbeatInterval = TimeSpan.FromMilliseconds(30);
            options.ConnectTimeout = TimeSpan.FromMilliseconds(120);
            using var server = new UdpGameTransport(options);
            using var client = new UdpGameTransport(options);
            server.StartServer(Any);
            client.StartClient(server.LocalEndPoint);
            PumpUntil(() => client.LocalPeerId != 0, server, client);
            string reason = null;
            client.EventReceived += e => { if (e.Type == TransportEventType.Disconnected) reason = e.Reason; };
            PumpUntil(() => reason != null, null, client); // Server never pumps heartbeat/data.
            Assert.AreEqual("Peer timed out", reason);
            Assert.AreEqual(0UL, client.LocalPeerId);
            client.Stop();
            server.Stop();
            using var reserved = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            reserved.Bind(Any);
            client.StartClient((IPEndPoint)reserved.LocalEndPoint);
            reason = null;
            PumpUntil(() => reason != null, null, client);
            Assert.AreEqual("Connection timed out", reason);
        }

        [TestMethod]
        public void UdpRejectsIncompatiblePacketAndChannelConfigurationExplicitly()
        {
            using var server = new UdpGameTransport(Fast());
            var options = Fast(); options.Channels = 2;
            using var client = new UdpGameTransport(options);
            string reason = null;
            client.EventReceived += e => { if (e.Type == TransportEventType.Disconnected) reason = e.Reason; };
            server.StartServer(Any);
            client.StartClient(server.LocalEndPoint);
            PumpUntil(() => reason != null, server, client);
            StringAssert.Contains(reason, "must match");
            Assert.AreEqual(0UL, client.LocalPeerId);
        }

        [TestMethod]
        public void UdpRejectsDifferentEffectiveFrameBudgetBeforeConnecting()
        {
            using var server = new UdpGameTransport(Fast());
            var options = Fast(); options.ExtraPacketOverheadBytes = 32;
            using var client = new UdpGameTransport(options);
            string reason = null;
            int connected = 0;
            client.EventReceived += e => { if (e.Type == TransportEventType.Disconnected) reason = e.Reason; if (e.Type == TransportEventType.Connected) connected++; };
            server.StartServer(Any);
            client.StartClient(server.LocalEndPoint);
            PumpUntil(() => reason != null, server, client);
            StringAssert.Contains(reason, "frame budget must match");
            Assert.AreEqual(0, connected);
        }

        [TestMethod]
        public void UdpSharedReorderBudgetReservesSpaceForMissingPacketAcrossChannels()
        {
            var receiverOptions = Fast(); receiverOptions.MaxPendingReliablePerPeer = 2;
            using var server = new UdpGameTransport(receiverOptions);
            using var client = new UdpGameTransport(Fast());
            server.StartServer(Any);
            using var proxy = new DatagramProxy(server.LocalEndPoint);
            client.StartClient(proxy.LocalEndPoint);
            var values = new List<byte>();
            server.EventReceived += e => { if (e.Type == TransportEventType.Data) values.Add(e.Payload[0]); };
            PumpUntil(() => client.LocalPeerId != 0, server, client, proxy);
            var dropped = new HashSet<byte>();
            proxy.ClientToServer = packet =>
            {
                if (packet[5] == 6 && BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(32)) == 1 && dropped.Add(packet[6])) return false;
                return true;
            };
            Assert.IsTrue(client.Send(0, new ArraySegment<byte>(new byte[] { 1 }), DeliveryMode.ReliableOrdered, 0));
            Assert.IsTrue(client.Send(0, new ArraySegment<byte>(new byte[] { 2 }), DeliveryMode.ReliableOrdered, 0));
            Assert.IsTrue(client.Send(0, new ArraySegment<byte>(new byte[] { 3 }), DeliveryMode.ReliableOrdered, 1));
            Assert.IsTrue(client.Send(0, new ArraySegment<byte>(new byte[] { 4 }), DeliveryMode.ReliableOrdered, 1));
            PumpUntil(() => values.Count == 4, server, client, proxy);
            CollectionAssert.AreEquivalent(new byte[] { 1, 2, 3, 4 }, values.ToArray());
            Assert.IsTrue(values.IndexOf(1) < values.IndexOf(2));
            Assert.IsTrue(values.IndexOf(3) < values.IndexOf(4));
        }

        [TestMethod]
        public void UdpDisconnectInCallbackSuppressesQueuedDataForRemovedPeer()
        {
            using var server = new UdpGameTransport(Fast());
            using var client = new UdpGameTransport(Fast());
            server.StartServer(Any);
            using var proxy = new DatagramProxy(server.LocalEndPoint);
            client.StartClient(proxy.LocalEndPoint);
            PumpUntil(() => client.LocalPeerId != 0, server, client, proxy);
            byte[] held = null;
            proxy.ClientToServer = packet =>
            {
                if (packet[5] != 6) return true;
                if (packet[40] == 1) { held = packet; return false; }
                if (packet[40] == 2) { proxy.SendServer(packet); proxy.SendServer(held); return false; }
                return true;
            };
            int data = 0, disconnected = 0;
            server.EventReceived += e =>
            {
                if (e.Type == TransportEventType.Data) { data++; server.Disconnect(e.PeerId); }
                if (e.Type == TransportEventType.Disconnected) disconnected++;
            };
            Assert.IsTrue(client.Send(0, new ArraySegment<byte>(new byte[] { 1 }), DeliveryMode.ReliableOrdered));
            Assert.IsTrue(client.Send(0, new ArraySegment<byte>(new byte[] { 2 }), DeliveryMode.ReliableOrdered));
            PumpUntil(() => disconnected != 0, server, client, proxy);
            Assert.AreEqual(1, data);
        }

        [TestMethod]
        public void UdpReliableAndSequencedChannelsHandleUintSequenceWrap()
        {
            using var server = new UdpGameTransport(Fast());
            using var client = new UdpGameTransport(Fast());
            server.StartServer(Any);
            using var proxy = new DatagramProxy(server.LocalEndPoint);
            client.StartClient(proxy.LocalEndPoint);
            var values = new List<byte>();
            server.EventReceived += e => { if (e.Type == TransportEventType.Data) values.Add(e.Payload[0]); };
            PumpUntil(() => client.LocalPeerId != 0, server, client, proxy);
            // Seed only the starting sequence to exercise the real wire/ACK/reorder paths at
            // the boundary without transmitting four billion packets to reach it.
            SetChannelField(client, 0, 0, "NextSendReliable", uint.MaxValue - 1);
            SetChannelField(server, client.LocalPeerId, 0, "NextReceiveReliable", uint.MaxValue - 1);
            byte[] held = null, duplicate = null;
            proxy.ClientToServer = packet =>
            {
                if (packet[5] != 6 || packet[6] != 0) return true;
                uint sequence = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(32));
                if (sequence == uint.MaxValue - 1) { held = packet; return false; }
                if (sequence == uint.MaxValue)
                {
                    duplicate = packet; proxy.SendServer(packet); proxy.SendServer(held); return false;
                }
                return true;
            };
            foreach (byte value in new byte[] { 11, 22, 33, 44 })
                Assert.IsTrue(client.Send(0, new ArraySegment<byte>(new[] { value }), DeliveryMode.ReliableOrdered));
            PumpUntil(() => values.Count == 4, server, client, proxy);
            proxy.SendServer(duplicate); // Already delivered sequence MAX is stale after wrapping to 2.
            PumpFor(TimeSpan.FromMilliseconds(40), server, client, proxy);
            CollectionAssert.AreEqual(new byte[] { 11, 22, 33, 44 }, values.ToArray());

            SetChannelField(client, 0, 2, "NextSendSequenced", uint.MaxValue);
            SetChannelField(server, client.LocalPeerId, 2, "LastReceiveSequenced", uint.MaxValue - 1);
            SetChannelField(server, client.LocalPeerId, 2, "HasReceiveSequenced", true);
            byte[] stale = null;
            proxy.ClientToServer = packet =>
            {
                if (packet[5] != 6 || packet[6] != 2) return true;
                uint sequence = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(32));
                if (sequence == uint.MaxValue) { stale = packet; return false; }
                proxy.SendServer(packet); proxy.SendServer(stale); return false;
            };
            Assert.IsTrue(client.Send(0, new ArraySegment<byte>(new byte[] { 55 }), DeliveryMode.UnreliableSequenced, 2));
            Assert.IsTrue(client.Send(0, new ArraySegment<byte>(new byte[] { 66 }), DeliveryMode.UnreliableSequenced, 2));
            PumpUntil(() => values.Count == 5, server, client, proxy);
            PumpFor(TimeSpan.FromMilliseconds(40), server, client, proxy);
            CollectionAssert.AreEqual(new byte[] { 11, 22, 33, 44, 66 }, values.ToArray());
        }

        [TestMethod]
        public void UdpDefaultReliableUnorderedDeliversFirstArrivalWithoutWaitingForLostPacket()
        {
            using var server = new UdpGameTransport(Fast());
            using var client = new UdpGameTransport(Fast());
            server.StartServer(Any);
            using var proxy = new DatagramProxy(server.LocalEndPoint);
            client.StartClient(proxy.LocalEndPoint);
            var received = new List<TransportEvent>();
            server.EventReceived += e => { if (e.Type == TransportEventType.Data) received.Add(e); };
            PumpUntil(() => client.LocalPeerId != 0, server, client, proxy);
            byte[] held = null;
            bool lost = false, lostAck = false;
            proxy.ClientToServer = packet =>
            {
                if (packet[5] != 6) return true;
                Assert.AreEqual((byte)DeliveryMode.ReliableUnordered, packet[7]);
                if (packet[40] == 1 && !lost) { lost = true; return false; }
                if (packet[40] == 2 && held == null) { held = packet; return false; }
                if (packet[40] == 3)
                {
                    proxy.SendServer(packet); proxy.SendServer(packet); // Duplicate delivery must be suppressed.
                    proxy.SendServer(held); return false;
                }
                return true;
            };
            proxy.ServerToClient = packet =>
            {
                if (packet[5] == 7 && !lostAck) { lostAck = true; return false; }
                return true;
            };
            IGameTransport throughInterface = client;
            Assert.IsTrue(throughInterface.Send(0, new ArraySegment<byte>(new byte[] { 1 })));
            Assert.IsTrue(client.Send(0, new ArraySegment<byte>(new byte[] { 2 })));
            Assert.IsTrue(client.Send(0, new ArraySegment<byte>(new byte[] { 3 })));
            PumpUntil(() => received.Count >= 2, server, client, proxy);
            CollectionAssert.AreEqual(new byte[] { 3, 2 }, received.Take(2).Select(e => e.Payload[0]).ToArray());
            PumpUntil(() => received.Count == 3, server, client, proxy);
            PumpFor(TimeSpan.FromMilliseconds(80), server, client, proxy);
            CollectionAssert.AreEqual(new byte[] { 3, 2, 1 }, received.Select(e => e.Payload[0]).ToArray());
            Assert.IsTrue(received.All(e => e.Delivery == DeliveryMode.ReliableUnordered));
            Assert.AreEqual(DeliveryMode.ReliableUnordered, DeliveryMode.Reliable);
        }

        [TestMethod]
        public void UdpReliableModesHaveIndependentSequenceAndAckSpacesOnTheSameChannel()
        {
            using var server = new UdpGameTransport(Fast());
            using var client = new UdpGameTransport(Fast());
            server.StartServer(Any);
            using var proxy = new DatagramProxy(server.LocalEndPoint);
            client.StartClient(proxy.LocalEndPoint);
            var received = new List<byte>();
            server.EventReceived += e => { if (e.Type == TransportEventType.Data) received.Add(e.Payload[0]); };
            PumpUntil(() => client.LocalPeerId != 0, server, client, proxy);
            bool allowOrdered = false;
            proxy.ClientToServer = packet => packet[5] != 6 || packet[7] != (byte)DeliveryMode.ReliableOrdered || allowOrdered;
            Assert.IsTrue(client.Send(0, new ArraySegment<byte>(new byte[] { 10 }), DeliveryMode.ReliableOrdered)); // Ordered seq1 is lost.
            Assert.IsTrue(client.Send(0, new ArraySegment<byte>(new byte[] { 20 }))); // Unordered seq1 must deliver and ACK independently.
            PumpUntil(() => received.Count == 1, server, client, proxy);
            Assert.AreEqual((byte)20, received[0]);
            allowOrdered = true;
            PumpUntil(() => received.Count == 2, server, client, proxy);
            CollectionAssert.AreEqual(new byte[] { 20, 10 }, received.ToArray());
            Assert.IsTrue(client.Send(0, new ArraySegment<byte>(new byte[] { 30 }), DeliveryMode.ReliableOrdered));
            Assert.IsTrue(client.Send(0, new ArraySegment<byte>(new byte[] { 40 })));
            PumpUntil(() => received.Count == 4, server, client, proxy);
            CollectionAssert.AreEquivalent(new byte[] { 10, 20, 30, 40 }, received.ToArray());
        }

        [TestMethod]
        public void UdpReliableUnorderedDeduplicationHandlesWrapAndBoundedMissingPacketWindow()
        {
            var receiveOptions = Fast(); receiveOptions.MaxPendingReliablePerPeer = 2;
            using var server = new UdpGameTransport(receiveOptions);
            using var client = new UdpGameTransport(Fast());
            server.StartServer(Any);
            using var proxy = new DatagramProxy(server.LocalEndPoint);
            client.StartClient(proxy.LocalEndPoint);
            var values = new List<byte>();
            server.EventReceived += e => { if (e.Type == TransportEventType.Data) values.Add(e.Payload[0]); };
            PumpUntil(() => client.LocalPeerId != 0, server, client, proxy);
            SetChannelField(client, 0, 0, "NextSendUnordered", uint.MaxValue);
            SetChannelField(server, client.LocalPeerId, 0, "NextReceiveUnordered", uint.MaxValue);
            bool missing = true;
            byte[] duplicate = null;
            proxy.ClientToServer = packet =>
            {
                if (packet[5] != 6) return true;
                uint sequence = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(32));
                if (sequence == uint.MaxValue && missing) return false;
                if (sequence == 0) duplicate = packet;
                return true;
            };
            Assert.IsTrue(client.Send(0, new ArraySegment<byte>(new byte[] { 1 })));
            Assert.IsTrue(client.Send(0, new ArraySegment<byte>(new byte[] { 2 })));
            Assert.IsTrue(client.Send(0, new ArraySegment<byte>(new byte[] { 3 })));
            PumpUntil(() => values.Count == 1, server, client, proxy);
            Assert.AreEqual((byte)2, values[0]); // Sequence0 is delivered despite missing MAX.
            missing = false;
            PumpUntil(() => values.Count == 3, server, client, proxy);
            proxy.SendServer(duplicate); // Sequence0 remains a duplicate after crossing the wrap.
            PumpFor(TimeSpan.FromMilliseconds(50), server, client, proxy);
            Assert.AreEqual(3, values.Count);
            CollectionAssert.AreEquivalent(new byte[] { 1, 2, 3 }, values.ToArray());
        }

        [TestMethod]
        public void UdpWireVersionOneCannotEstablishVersionTwoSession()
        {
            var options = Fast(); options.ConnectTimeout = TimeSpan.FromMilliseconds(80);
            using var server = new UdpGameTransport(options);
            using var client = new UdpGameTransport(options);
            server.StartServer(Any);
            using var proxy = new DatagramProxy(server.LocalEndPoint);
            int connected = 0;
            string reason = null;
            server.EventReceived += e => { if (e.Type == TransportEventType.Connected) connected++; };
            client.EventReceived += e => { if (e.Type == TransportEventType.Disconnected) reason = e.Reason; };
            proxy.ClientToServer = packet => { packet[4] = 1; return true; };
            client.StartClient(proxy.LocalEndPoint);
            PumpUntil(() => reason != null, server, client, proxy);
            Assert.AreEqual("Connection timed out", reason);
            Assert.AreEqual(0, connected);
        }

        private static void SetChannelField(UdpGameTransport transport, ulong peerId, int channel, string name, object value)
        {
            const BindingFlags fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var peers = (System.Collections.IDictionary)typeof(UdpGameTransport).GetField("_peers", fields).GetValue(transport);
            object peer = peers[peerId];
            var channels = (Array)peer.GetType().GetField("Channels", fields).GetValue(peer);
            object state = channels.GetValue(channel);
            state.GetType().GetField(name, fields).SetValue(state, value);
        }

        private static void PumpUntil(Func<bool> done, UdpGameTransport server, UdpGameTransport client, DatagramProxy proxy = null, UdpGameTransport second = null)
        {
            var watch = Stopwatch.StartNew();
            while (!done() && watch.Elapsed < TimeSpan.FromSeconds(5))
            {
                client?.Poll(); second?.Poll(); proxy?.Poll(); server?.Poll(); proxy?.Poll(); client?.Poll(); second?.Poll();
                Thread.Sleep(1);
            }
            Assert.IsTrue(done(), "UDP condition did not complete within five seconds.");
        }

        private static void PumpFor(TimeSpan time, UdpGameTransport server, UdpGameTransport client, DatagramProxy proxy = null)
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < time) { client?.Poll(); proxy?.Poll(); server?.Poll(); proxy?.Poll(); client?.Poll(); Thread.Sleep(1); }
        }

        private sealed class DatagramProxy : IDisposable
        {
            private readonly Socket _front = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            private readonly Socket _back = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            private readonly IPEndPoint _server;
            private EndPoint _client;
            public Func<byte[], bool> ClientToServer = p => true;
            public Func<byte[], bool> ServerToClient = p => true;
            public IPEndPoint LocalEndPoint => (IPEndPoint)_front.LocalEndPoint;
            public DatagramProxy(IPEndPoint server)
            {
                _server = server;
                _front.Bind(Any); _back.Bind(Any); _front.Blocking = false; _back.Blocking = false;
            }
            public void Poll()
            {
                Drain(_front, (packet, source) => { _client = source; if (ClientToServer(packet)) _back.SendTo(packet, _server); });
                Drain(_back, (packet, source) => { if (_client != null && ServerToClient(packet)) _front.SendTo(packet, _client); });
            }
            public void SendServer(byte[] packet) => _back.SendTo(packet, _server);
            private static void Drain(Socket socket, Action<byte[], EndPoint> forward)
            {
                var buffer = new byte[1500];
                for (int i = 0; i < 256; i++)
                {
                    EndPoint source = new IPEndPoint(IPAddress.Any, 0);
                    int count;
                    try { count = socket.ReceiveFrom(buffer, ref source); }
                    catch (SocketException ex) when (ex.SocketErrorCode == SocketError.WouldBlock || ex.SocketErrorCode == SocketError.ConnectionReset) { break; }
                    var packet = new byte[count]; Buffer.BlockCopy(buffer, 0, packet, 0, count); forward(packet, source);
                }
            }
            public void Dispose() { _front.Dispose(); _back.Dispose(); }
        }
    }
}
