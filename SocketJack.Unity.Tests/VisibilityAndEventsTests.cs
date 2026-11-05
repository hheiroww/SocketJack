using System;
using System.Diagnostics;
using System.Net;
using System.Numerics;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Multiplayer.Serialization;
using SocketJack.Multiplayer.Transport;
namespace SocketJack.Multiplayer.Tests
{
    [TestClass]
    public sealed class VisibilityAndEventsTests
    {
        [DataTestMethod, DataRow(TransportType.Udp), DataRow(TransportType.Tcp)]
        public void SpatialViewsRevokeMembersAndEventsAndRestoreFreshBaselines(TransportType type)
        {
            var index = new SpatialInterestIndex(new SpatialInterestOptions { Radius = 10, Hysteresis = 2, CellSize = 10 });
            using var server = new NetworkServer(GameTransport.Create(type)) { InterestProvider = index };
            using var a = new NetworkClient(GameTransport.Create(type)); using var b = new NetworkClient(GameTransport.Create(type));
            using var sm = new NetworkMemberServer(server); using var am = new NetworkMemberClient(a); using var bm = new NetworkMemberClient(b);
            using var se = new NetworkEventHub(server); using var ae = new NetworkEventHub(client: a); using var be = new NetworkEventHub(client: b);
            var codec = new SocketJackBinaryFormatter(); int canonical = 10, av = 0, bv = 0, ac = 0, bc = 0;
            NetworkMemberSchema Schema(Func<object> get, Action<object> set) => new NetworkMemberSchema(codec,
                new[] { new NetworkMemberBinding(1, "health", typeof(int), get, set, sendInterval: .016) });
            a.ObjectSpawned += obj => { am.RegisterObject(obj.Id, Schema(() => av, value => av = (int)value)); ae.Register<int>(obj.Id, 1, codec, (_, value) => ac += value); };
            b.ObjectSpawned += obj => { bm.RegisterObject(obj.Id, Schema(() => bv, value => bv = (int)value)); be.Register<int>(obj.Id, 1, codec, (_, value) => bc += value); };
            a.ObjectDespawned += id => ae.Unregister(id, 1); b.ObjectDespawned += id => be.Unregister(id, 1);
            server.Start(new IPEndPoint(IPAddress.Loopback, 0)); a.Connect(server.Transport.LocalEndPoint); b.Connect(server.Transport.LocalEndPoint);
            void Step() { server.Update(.02); sm.Update(.02); se.Update(); a.Update(.02); am.Update(.02); ae.Update(); b.Update(.02); bm.Update(.02); be.Update(); Thread.Sleep(1); }
            Until(() => a.IsConnected && b.IsConnected, Step);
            index.SetPeerPosition(a.LocalPeerId, Vector3.Zero); index.SetPeerPosition(b.LocalPeerId, new Vector3(100, 0, 0));
            var obj = server.Spawn(1); index.SetObjectPosition(obj.Id, 0, Vector3.Zero); sm.RegisterObject(obj.Id, Schema(() => canonical, value => canonical = (int)value));
            se.Register<int>(obj.Id, 1, codec, (_, __) => { });
            Until(() => am.IsReady(obj.Id), Step);
            Assert.AreEqual(10, av); Assert.IsFalse(b.Objects.ContainsKey(obj.Id)); Assert.IsFalse(server.CanObserve(b.LocalPeerId, obj.Id));
            ulong firstView = a.Objects[obj.Id].ViewId;
            Assert.IsTrue(se.Emit(obj.Id, (ushort)1, 7)); Until(() => ac == 7, Step); Assert.AreEqual(0, bc);
            index.SetPeerPosition(a.LocalPeerId, new Vector3(100, 0, 0)); index.SetPeerPosition(b.LocalPeerId, Vector3.Zero); canonical = 20;
            Until(() => !a.Objects.ContainsKey(obj.Id) && bm.IsReady(obj.Id) && bv == 20, Step);
            Assert.IsFalse(server.CanObserve(a.LocalPeerId, obj.Id)); Assert.AreEqual(0, bc, "Transient events are not late-join state.");
            index.SetPeerPosition(a.LocalPeerId, Vector3.Zero);
            Until(() => am.IsReady(obj.Id) && av == 20, Step);
            Assert.AreNotEqual(firstView, a.Objects[obj.Id].ViewId); Assert.AreEqual(7, ac);
        }
        [DataTestMethod, DataRow(TransportType.Udp), DataRow(TransportType.Tcp)]
        public void TypedCommandsCheckOwnershipAndEchoExactlyOnceToObservers(TransportType type)
        {
            using var server = new NetworkServer(GameTransport.Create(type));
            using var a = new NetworkClient(GameTransport.Create(type)); using var b = new NetworkClient(GameTransport.Create(type));
            using var se = new NetworkEventHub(server); using var ae = new NetworkEventHub(client: a); using var be = new NetworkEventHub(client: b);
            var codec = new SocketJackBinaryFormatter(); int commands = 0, ac = 0, bc = 0;
            server.Start(new IPEndPoint(IPAddress.Loopback, 0)); a.Connect(server.Transport.LocalEndPoint); b.Connect(server.Transport.LocalEndPoint);
            void Step() { server.Update(.02); se.Update(); a.Update(.02); ae.Update(); b.Update(.02); be.Update(); Thread.Sleep(1); }
            Until(() => a.IsConnected && b.IsConnected, Step);
            var obj = server.Spawn(1, a.LocalPeerId);
            Until(() => a.Objects.ContainsKey(obj.Id) && b.Objects.ContainsKey(obj.Id), Step);
            se.Register<int>(obj.Id, 1, codec, (context, value) => { Assert.IsTrue(context.IsServerCommand); commands += value; }, validator: (_, value) => value > 0 && value < 10, echoCommands: true);
            ae.Register<int>(obj.Id, 1, codec, (_, value) => ac += value); be.Register<int>(obj.Id, 1, codec, (_, value) => bc += value);
            Assert.IsFalse(be.SendCommand(obj.Id, (ushort)1, 2)); Assert.IsTrue(ae.SendCommand(obj.Id, (ushort)1, 3));
            Until(() => commands == 3 && ac == 3 && bc == 3, Step);
            Assert.IsTrue(ae.SendCommand(obj.Id, (ushort)1, -1)); for (int i = 0; i < 20; i++) Step();
            Assert.AreEqual(3, commands); Assert.AreEqual(3, ac); Assert.AreEqual(3, bc);
        }
        [TestMethod]
        public void SpatialHysteresisAndPlaneAreExplicit()
        {
            var index = new SpatialInterestIndex(new SpatialInterestOptions { Radius = 10, Hysteresis = 2, CellSize = 10, Dimension = NetworkSpatialDimension.XY });
            using var server = new NetworkServer(GameTransport.Create(TransportType.Udp)); server.Start(new IPEndPoint(IPAddress.Loopback, 0));
            var obj = server.Spawn(1); index.SetPeerPosition(1, Vector3.Zero); index.SetObjectPosition(obj.Id, 0, new Vector3(11, 0, 100));
            Assert.IsFalse(index.IsVisible(1, obj, false)); Assert.IsTrue(index.IsVisible(1, obj, true));
            index.SetObjectPosition(obj.Id, 0, new Vector3(13, 0, 100)); Assert.IsFalse(index.IsVisible(1, obj, true));
            Assert.ThrowsException<ArgumentException>(() => new SpatialInterestIndex(new SpatialInterestOptions { Radius = 10000, CellSize = 1 }));
        }
        private static void Until(Func<bool> done, Action step)
        { var clock = Stopwatch.StartNew(); while (!done() && clock.Elapsed < TimeSpan.FromSeconds(6)) step(); Assert.IsTrue(done(), "Visibility/event operation timed out."); }
    }
}
