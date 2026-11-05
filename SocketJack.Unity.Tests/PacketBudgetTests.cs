using System;
using System.Net;
using System.Net.Sockets;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Multiplayer.Transport;

namespace SocketJack.Multiplayer.Tests
{
    [TestClass]
    public class PacketBudgetTests
    {
        [TestMethod]
        public void DefaultBudgetIncludesEveryHeaderAndKeepsFrameBelow999Bytes()
        {
            var budget = PacketBudget.Default;
            Assert.AreEqual(1280, budget.PathMtu);
            Assert.AreEqual(40, budget.IpHeaderBytes);
            Assert.AreEqual(8, budget.UdpHeaderBytes);
            Assert.AreEqual(40, budget.TransportHeaderBytes);
            Assert.AreEqual(49, budget.SessionHeaderBytes);
            Assert.AreEqual(28, budget.FrameHeaderBytes);
            Assert.AreEqual(1200, budget.MaxDatagramBytes);
            Assert.AreEqual(1160, budget.MaxPayloadBytes);
            Assert.AreEqual(910, budget.MaxFramePayloadBytes);
            Assert.AreEqual(833, budget.MaxObjectStateBytes);
            Assert.AreEqual(998, budget.GetPacketBytes(budget.MaxFramePayloadBytes));
            Assert.AreEqual(998, budget.GetObjectPacketBytes(budget.MaxObjectStateBytes));
            Assert.AreEqual(1248, budget.GetPacketBytes(budget.MaxPayloadBytes));
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => budget.GetObjectPacketBytes(834));
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => budget.GetPacketBytes(1161));
        }

        [TestMethod]
        public void IPv4AndAdditionalEncapsulationHaveExactBudgets()
        {
            var ipv4 = new PacketBudget(addressFamily: AddressFamily.InterNetwork);
            Assert.AreEqual(20, ipv4.IpHeaderBytes);
            Assert.AreEqual(930, ipv4.MaxFramePayloadBytes);
            Assert.AreEqual(853, ipv4.MaxObjectStateBytes);
            Assert.AreEqual(998, ipv4.GetObjectPacketBytes(853));
            var tunnel = new PacketBudget(extraOverheadBytes: 64);
            Assert.AreEqual(1168, tunnel.MaxDatagramBytes); // 1280 - 40 - 8 - 64.
            Assert.AreEqual(846, tunnel.MaxFramePayloadBytes);
            Assert.AreEqual(769, tunnel.MaxObjectStateBytes);
            Assert.AreEqual(998, tunnel.GetObjectPacketBytes(769));
            var smallPath = new PacketBudget(pathMtu: 900);
            Assert.AreEqual(852, smallPath.MaxDatagramBytes);
            Assert.AreEqual(812, smallPath.MaxFramePayloadBytes);
            Assert.AreEqual(735, smallPath.MaxObjectStateBytes);
            Assert.AreEqual(900, smallPath.GetPacketBytes(812));
        }

        [TestMethod]
        public void NativeUdpRejectsConfigurationBeyondFullPathBudgetAtExactBoundary()
        {
            using var exact = new UdpGameTransport(new TransportOptions { PathMtu = 1248 });
            Assert.AreEqual(1248, exact.Budget.GetPacketBytes(exact.MaxPayloadSize));
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => new UdpGameTransport(new TransportOptions { PathMtu = 1247 }));
            using var reserved = new UdpGameTransport(new TransportOptions { ExtraPacketOverheadBytes = 32 });
            Assert.AreEqual(1280, reserved.Budget.GetPacketBytes(reserved.MaxPayloadSize));
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => new UdpGameTransport(new TransportOptions { ExtraPacketOverheadBytes = 33 }));
            using var ipv4 = new UdpGameTransport(new TransportOptions { PathMtu = 1228, IpHeaderBytes = 20 });
            Assert.AreEqual(1228, ipv4.Budget.GetPacketBytes(ipv4.MaxPayloadSize));
            Assert.ThrowsException<ArgumentException>(() => ipv4.StartServer(new IPEndPoint(IPAddress.IPv6Loopback, 0)));
            // TCP is a stream: its OS segmentation is not governed by the UDP datagram check.
            using var tcp = new TcpGameTransport(new TransportOptions { MaxPacketSize = 1400 });
        }
    }
}
