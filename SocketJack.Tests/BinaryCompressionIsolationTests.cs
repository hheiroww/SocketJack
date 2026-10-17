using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Net;
using SocketJack.Serialization;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security;
using System.Text;
using System.Text.Json;

namespace SocketJack.Tests;

[TestClass]
public sealed class BinaryCompressionIsolationTests {
    public sealed class Message {
        public int Number { get; set; }
        public byte[] Data { get; set; }
    }

    static NetworkOptions Options(bool shared = true) {
        var options = new NetworkOptions {
            Serializer = new BinarySerializer { ShareCompressionDictionary = shared },
            UsePeerToPeer = false, UseCompression = false, EnablePatternCache = false,
            Logging = false, AutoReconnect = false
        };
        options.Whitelist.Add(typeof(Message));
        options.Authorization.AnonymousMessageTypes.Add(typeof(Message));
        return options;
    }

    static byte[] RandomBytes(int length, int seed) {
        var bytes = new byte[length]; new Random(seed).NextBytes(bytes); return bytes;
    }

    static BinaryCompressionDefinition Definition(int seed) {
        byte[] data = RandomBytes(1024, seed);
        return new BinaryCompressionDefinition(BinaryCompressionCodec.Hash(data), data);
    }

    static NetworkConnection Negotiate(ISocket owner, NetworkOptions peer) {
        var connection = new NetworkConnection(owner, null);
        SafeModeHandshake.Validate(SafeModeHandshake.Create(peer), owner.Options, connection);
        return connection;
    }

    static WireContracts.Manifest ReplyManifest(NetworkOptions options, NetworkConnection connection) {
        byte[] reply = SafeModeHandshake.Reply(options, connection);
        Assert.IsTrue(SafeModeHandshake.IsCompatibility(reply));
        return JsonSerializer.Deserialize<WireContracts.Manifest>(reply.AsSpan(5));
    }

    static int Port() {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    static async Task Until(Func<bool> condition, string message) {
        var elapsed = Stopwatch.StartNew();
        while (!condition() && elapsed.Elapsed < TimeSpan.FromSeconds(8)) await Task.Delay(20);
        Assert.IsTrue(condition(), message);
    }

    [DataTestMethod]
    [DataRow(true, true, false)]
    [DataRow(true, false, true)]
    [DataRow(false, true, true)]
    [DataRow(false, false, true)]
    public void EitherPeerCanRequireAConnectionPrivateDictionary(bool localShared, bool peerShared, bool expectedPrivate) {
        var local = WireContracts.Create(Options(localShared));
        var peer = WireContracts.Create(Options(peerShared));
        Assert.IsTrue(local.BinaryCompressionIsolation && peer.BinaryCompressionIsolation);
        Assert.AreEqual(!localShared, local.BinaryCompressionPerClient);
        Assert.AreEqual(!peerShared, peer.BinaryCompressionPerClient);
        var compatibility = WireContracts.Compare(local, peer);
        Assert.IsTrue(compatibility.BinaryCompression);
        Assert.AreEqual(expectedPrivate, compatibility.BinaryCompressionPerClient);
        Assert.AreEqual(expectedPrivate, WireContracts.Compare(peer, local).BinaryCompressionPerClient);
        Assert.IsTrue(new BinarySerializer().ShareCompressionDictionary, "Existing shared dictionary fanout remains the default.");
    }

    [TestMethod]
    public void PrivateCompressionRejectsLegacyPeersWithoutIsolationSupport() {
        var privateManifest = WireContracts.Create(Options(false));
        var legacy = WireContracts.Create(Options());
        legacy.BinaryCompressionIsolation = false;
        Assert.ThrowsException<SecurityException>(() => WireContracts.Compare(privateManifest, legacy));
        Assert.ThrowsException<SecurityException>(() => WireContracts.Compare(legacy, privateManifest));
        Assert.IsTrue(WireContracts.Compare(WireContracts.Create(Options()), legacy).BinaryCompression,
            "Legacy shared compression remains usable when neither peer requests isolation.");
        legacy.BinaryCompression = false;
        Assert.IsFalse(WireContracts.Compare(privateManifest, legacy).BinaryCompression,
            "An uncompressed peer needs no shared dictionary and can remain compatible.");
    }

    [TestMethod]
    public void PrivateCachesHaveIndependentStateAndReleaseMemoryOnDisconnect() {
        using var owner = new SocketJack.Net.TcpClient(Options(false));
        var serializer = (BinarySerializer)owner.Options.Serializer;
        var first = Negotiate(owner, Options());
        var second = Negotiate(owner, Options());
        bool firstDisposed = false, secondDisposed = false;
        try {
            var firstCache = serializer.GetCompressionCache(owner, first);
            var secondCache = serializer.GetCompressionCache(owner, second);
            Assert.AreNotSame(firstCache, secondCache);
            var definition = Definition(12001);
            Assert.IsTrue(firstCache.Import(definition));
            Assert.IsFalse(secondCache.TryGet(definition.Id, out _));
            long siblingBytes = secondCache.RetainedBytes;
            Assert.IsTrue(siblingBytes > 0);
            Assert.AreEqual(firstCache.RetainedBytes + siblingBytes, serializer.CompressionCacheBytes);

            serializer.ShareCompressionDictionary = true;
            Assert.AreSame(firstCache, serializer.GetCompressionCache(owner, first), "Changing configuration after negotiation must not switch an established cache scope.");
            Assert.AreSame(secondCache, serializer.GetCompressionCache(owner, second));
            first.Dispose(); firstDisposed = true;
            Assert.AreEqual(0L, firstCache.RetainedBytes);
            Assert.AreEqual(siblingBytes, secondCache.RetainedBytes, "Disconnecting one private client must preserve its sibling's state.");
            second.Dispose(); secondDisposed = true;
            Assert.AreEqual(0L, secondCache.RetainedBytes);
            Assert.AreEqual(0L, serializer.CompressionCacheBytes);
        } finally {
            if (!firstDisposed) first.Dispose();
            if (!secondDisposed) second.Dispose();
        }
    }

    [TestMethod]
    public void SharedConnectionsRetainTheOriginalSocketRepositoryAndFrozenScope() {
        var owner = new SocketJack.Net.TcpClient(Options());
        var serializer = (BinarySerializer)owner.Options.Serializer;
        var first = Negotiate(owner, Options());
        var second = Negotiate(owner, Options());
        bool firstDisposed = false, secondDisposed = false;
        try {
            var cache = serializer.GetCompressionCache(owner, first);
            Assert.AreSame(cache, serializer.GetCompressionCache(owner, second));
            Assert.AreSame(cache, serializer.GetCompressionCache(owner));
            var definition = Definition(12002);
            Assert.IsTrue(cache.Import(definition));
            serializer.ShareCompressionDictionary = false;
            Assert.AreSame(cache, serializer.GetCompressionCache(owner, first));
            first.Dispose(); firstDisposed = true;
            second.Dispose(); secondDisposed = true;
            Assert.IsTrue(cache.TryGet(definition.Id, out _), "Individual shared-client disconnects must not discard the socket repository.");
            owner.Dispose();
            Assert.AreEqual(0L, cache.RetainedBytes);
        } finally {
            if (!firstDisposed) first.Dispose();
            if (!secondDisposed) second.Dispose();
            owner.Dispose();
        }
    }

    [TestMethod]
    public void HandshakeSnapshotsAndImportsRespectTheNegotiatedPrivateScope() {
        using var owner = new SocketJack.Net.TcpClient(Options());
        var serializer = (BinarySerializer)owner.Options.Serializer;
        var sharedDefinition = Definition(12003);
        Assert.IsTrue(serializer.GetCompressionCache(owner).Import(sharedDefinition));
        using var privateConnection = Negotiate(owner, Options(false));
        using var sharedConnection = Negotiate(owner, Options());
        var privateCache = serializer.GetCompressionCache(owner, privateConnection);
        Assert.IsNull(ReplyManifest(owner.Options, privateConnection).BinaryDictionary,
            "A new private connection must not receive the socket's existing shared handshake dictionary.");
        var sharedSnapshot = ReplyManifest(owner.Options, sharedConnection).BinaryDictionary;
        Assert.IsNotNull(sharedSnapshot);
        Assert.IsTrue(BinaryCompressionCodec.ReadUpdate(sharedSnapshot).Any(d => d.Id == sharedDefinition.Id));

        var privateDefinition = Definition(12004);
        BinaryNetworkCompression.ImportSnapshot(owner, BinaryCompressionCodec.Update(new[] { privateDefinition }), privateConnection);
        Assert.IsTrue(privateCache.TryGet(privateDefinition.Id, out _));
        Assert.IsFalse(serializer.GetCompressionCache(owner).TryGet(privateDefinition.Id, out _));
        var privateSnapshot = ReplyManifest(owner.Options, privateConnection).BinaryDictionary;
        Assert.IsNotNull(privateSnapshot);
        var definitions = BinaryCompressionCodec.ReadUpdate(privateSnapshot);
        Assert.IsTrue(definitions.Any(d => d.Id == privateDefinition.Id));
        Assert.IsFalse(definitions.Any(d => d.Id == sharedDefinition.Id));
    }

    [TestMethod]
    public void TemporaryUdpHandshakeDefinitionsTransferToThePersistentPrivateConnection() {
        using var owner = new SocketJack.Net.TcpClient(Options(false));
        var serializer = (BinarySerializer)owner.Options.Serializer;
        using var temporary = Negotiate(owner, Options());
        using var persistent = Negotiate(owner, Options());
        var temporaryCache = serializer.GetCompressionCache(owner, temporary);
        var persistentCache = serializer.GetCompressionCache(owner, persistent);
        var fromHello = Definition(12007);
        var alreadyPresent = Definition(12008);
        Assert.IsTrue(temporaryCache.Import(fromHello));
        Assert.IsTrue(persistentCache.Import(alreadyPresent));
        serializer.TransferCompressionCache(temporary, persistent);
        Assert.AreEqual(0L, temporaryCache.RetainedBytes, "The temporary handshake repository must release its accounted storage.");
        Assert.AreEqual(0, temporaryCache.DictionaryCount);
        Assert.IsTrue(persistentCache.TryGet(fromHello.Id, out var imported));
        CollectionAssert.AreEqual(fromHello.Data, imported);
        Assert.IsTrue(persistentCache.TryGet(alreadyPresent.Id, out _), "A repeated hello must preserve definitions already retained by the admitted peer.");
        serializer.ReleaseCompressionCache(temporary);
        Assert.IsTrue(persistentCache.TryGet(fromHello.Id, out _), "Handshake cleanup must not dispose its successor's repository.");
        serializer.ReleaseCompressionCache(persistent);
        Assert.AreEqual(0L, persistentCache.RetainedBytes);
        Assert.AreEqual(0L, serializer.CompressionCacheBytes);
    }

    [TestMethod]
    public void HandshakeCacheTransfersRejectDifferentOwnersAndSharedDestinations() {
        using var owner = new SocketJack.Net.TcpClient(Options());
        using var otherOwner = new SocketJack.Net.TcpClient(Options());
        var serializer = (BinarySerializer)owner.Options.Serializer;
        using var temporary = Negotiate(owner, Options(false));
        using var foreign = Negotiate(otherOwner, Options(false));
        using var shared = Negotiate(owner, Options());
        var source = serializer.GetCompressionCache(owner, temporary);
        var definition = Definition(12009);
        Assert.IsTrue(source.Import(definition));
        Assert.ThrowsException<ArgumentException>(() => serializer.TransferCompressionCache(temporary, foreign));
        Assert.IsTrue(source.TryGet(definition.Id, out _), "A foreign owner must not consume or import the original handshake cache.");
        Assert.ThrowsException<InvalidOperationException>(() => serializer.TransferCompressionCache(temporary, shared));
        Assert.IsFalse(serializer.GetCompressionCache(owner, shared).TryGet(definition.Id, out _), "A private handshake must never populate a shared destination.");
        serializer.ReleaseCompressionCache(temporary);
        Assert.AreEqual(0L, source.RetainedBytes);
    }

    [DataTestMethod]
    [DataRow("udp")]
    [DataRow("reliable-independent")]
    [DataRow("websocket")]
    public async Task PrivateBinaryTransfersRoundTripAcrossUdpAndWebSocket(string transport) {
        int port = Port();
        var serverOptions = Options();
        var clientOptions = Options(false);
        if (transport == "reliable-independent") {
            serverOptions.UdpMode = clientOptions.UdpMode = UdpMode.UDP_Reliable;
            serverOptions.UdpReliable.Ordering = clientOptions.UdpReliable.Ordering = false;
        }

        async Task Exchange(ISocket server, ISocket client, Func<bool> listen, Func<Task<bool>> connect) {
            var echoed = new ConcurrentDictionary<int, byte[]>();
            var errors = new ConcurrentQueue<string>();
            if (server is NetworkBase serverNetwork) serverNetwork.OnError += e => errors.Enqueue("server: " + e.Exception);
            if (client is NetworkBase clientNetwork) clientNetwork.OnError += e => errors.Enqueue("client: " + e.Exception);
            server.RegisterCallback<Message>(e => server.Send(e.Connection, e.Object));
            client.RegisterCallback<Message>(e => echoed[e.Object.Number] = e.Object.Data);
            Assert.IsTrue(listen());
            Assert.IsTrue(await connect(), string.Join(Environment.NewLine, errors));
            Assert.IsTrue(client.Connection.Compatibility.BinaryCompressionPerClient);
            byte[] data = RandomBytes(12288, 12010);
            for (int i = 0; i < 5; i++) {
                client.Send(client.Connection, new Message { Number = i, Data = data });
                int expected = i;
                await Until(() => echoed.ContainsKey(expected), $"Private {transport} object {expected} did not round trip.");
                CollectionAssert.AreEqual(data, echoed[expected]);
            }
            var serverSerializer = (BinarySerializer)server.Options.Serializer;
            var clientSerializer = (BinarySerializer)client.Options.Serializer;
            NetworkConnection serverConnection = server is SocketJack.Net.UdpServer udp
                ? udp.CompressionConnections.Single()
                : ((SocketJack.Net.WebSockets.WebSocketServer)server).Clients.Values.Single();
            Assert.IsTrue(serverConnection.Compatibility.BinaryCompressionPerClient);
            Assert.IsTrue(serverSerializer.GetCompressionCache(server, serverConnection).DictionaryCount > 0);
            Assert.IsTrue(clientSerializer.GetCompressionCache(client, client.Connection).DictionaryCount > 0);
            Assert.AreEqual(0, serverSerializer.GetCompressionCache(server).DictionaryCount, "Private transport definitions must stay outside the socket's shared pool.");
            if (server is SocketJack.Net.UdpServer udpServer) {
                var sharedManifest = WireContracts.Create(Options());
                var sharedCompatibility = WireContracts.Compare(sharedManifest, WireContracts.Create(Options()));
                Assert.ThrowsException<SecurityException>(() => udpServer.ValidateCompressionScope(serverConnection.EndPoint, sharedCompatibility),
                    "An existing private UDP endpoint must not switch to the shared scope before a handshake reply.");
                udpServer.ValidateCompressionScope(serverConnection.EndPoint, serverConnection.Compatibility);
                var injectedDefinition = Definition(12011);
                sharedManifest.BinaryDictionary = BinaryCompressionCodec.Update(new[] { injectedDefinition });
                byte[] hello = Encoding.ASCII.GetBytes("SJSM2").Concat(JsonSerializer.SerializeToUtf8Bytes(sharedManifest)).ToArray();
                var attemptedSwitch = new NetworkConnection(server, null) { EndPoint = serverConnection.EndPoint };
                try {
                    Assert.ThrowsException<SecurityException>(() => SafeModeHandshake.Validate(hello, server.Options, attemptedSwitch),
                        "A scope-changing UDP hello must be rejected before its dictionary snapshot can be imported.");
                } finally {
                    // This rejected hello has no socket or admitted UDP peer to disconnect.
                    serverSerializer.ReleaseCompressionCache(attemptedSwitch);
                }
                Assert.IsFalse(serverSerializer.GetCompressionCache(server).TryGet(injectedDefinition.Id, out _));
                Assert.IsFalse(serverSerializer.GetCompressionCache(server, serverConnection).TryGet(injectedDefinition.Id, out _));
            }
            Assert.IsTrue(client.Connected);
        }

        if (transport == "websocket") {
            using var server = new SocketJack.Net.WebSockets.WebSocketServer(port) { Options = serverOptions };
            using var client = new SocketJack.Net.WebSockets.WebSocketClient(clientOptions);
            await Exchange(server, client, server.Listen, () => client.Connect("127.0.0.1", port));
        } else {
            using var server = new SocketJack.Net.UdpServer(serverOptions, port);
            using var client = new SocketJack.Net.UdpClient(clientOptions);
            await Exchange(server, client, server.Listen, () => client.Connect("127.0.0.1", port));
        }
    }

    [TestMethod]
    public async Task SharedAndPrivateTcpClientsDoNotExchangeEachOthersDefinitions() {
        int port = Port();
        using var server = new SocketJack.Net.TcpServer(Options(), port);
        using var sharedSender = new SocketJack.Net.TcpClient(Options());
        using var sharedIdle = new SocketJack.Net.TcpClient(Options());
        using var privateSender = new SocketJack.Net.TcpClient(Options(false));
        using var privateLate = new SocketJack.Net.TcpClient(Options(false));
        var errors = new ConcurrentQueue<string>();
        server.OnError += e => errors.Enqueue(e.Exception.Message);
        var received = new ConcurrentDictionary<int, byte[]>();
        server.RegisterCallback<Message>(e => received[e.Object.Number] = e.Object.Data);
        Assert.IsTrue(server.Listen());
        Assert.IsTrue(await sharedSender.Connect("127.0.0.1", port));
        Assert.IsTrue(await sharedIdle.Connect("127.0.0.1", port));
        Assert.IsTrue(await privateSender.Connect("127.0.0.1", port));
        await Until(() => server.Clients.Count == 3, "Initial clients did not finish negotiation.");
        Assert.IsTrue(privateSender.Connection.Compatibility.BinaryCompressionPerClient);
        Assert.IsFalse(sharedSender.Connection.Compatibility.BinaryCompressionPerClient);
        var serverSerializer = (BinarySerializer)server.Options.Serializer;
        var sharedIdleSerializer = (BinarySerializer)sharedIdle.Options.Serializer;
        var privateSerializer = (BinarySerializer)privateSender.Options.Serializer;
        byte[] sharedData = RandomBytes(16384, 12005);
        for (int i = 0; i < 4; i++) {
            sharedSender.Send(new Message { Number = i, Data = sharedData });
            int expected = i;
            await Until(() => received.ContainsKey(expected), "Shared training message did not arrive.");
            CollectionAssert.AreEqual(sharedData, received[expected]);
        }
        await Until(() => serverSerializer.GetCompressionCache(server).DictionaryCount > 0 && sharedIdleSerializer.CompressionDictionaryCount > 0,
            "Shared clients must retain the original live definition fanout.");
        await Task.Delay(160);
        Assert.AreEqual(0, privateSerializer.CompressionDictionaryCount, "Existing private clients must not receive shared definition updates.");
        Assert.IsTrue(await privateLate.Connect("127.0.0.1", port));
        await Until(() => server.Clients.Count == 4, "The late private client did not connect.");
        var lateSerializer = (BinarySerializer)privateLate.Options.Serializer;
        await Task.Delay(160);
        Assert.AreEqual(0, lateSerializer.CompressionDictionaryCount, "Late private clients must receive neither the shared snapshot nor post-handshake shared pages.");

        byte[] privateData = RandomBytes(16384, 12006);
        for (int i = 0; i < 4; i++) {
            privateSender.Send(new Message { Number = 100 + i, Data = privateData });
            int expected = 100 + i;
            await Until(() => received.ContainsKey(expected), "Private training message did not arrive.");
            CollectionAssert.AreEqual(privateData, received[expected]);
        }
        await Until(() => privateSerializer.CompressionDictionaryCount > 0, "Private traffic should still learn and compress on its own connection.");
        var privateDefinitions = privateSerializer.GetCompressionCache(privateSender, privateSender.Connection).Definitions
            .Where(d => privateData.AsSpan().IndexOf(d.Data) >= 0).ToArray();
        Assert.IsTrue(privateDefinitions.Length > 0);
        await Task.Delay(160);
        foreach (var definition in privateDefinitions) {
            Assert.IsFalse(serverSerializer.GetCompressionCache(server).TryGet(definition.Id, out _), "Inbound private definitions must not enter the server's shared pool.");
            Assert.IsFalse(sharedIdleSerializer.GetCompressionCache(sharedIdle, sharedIdle.Connection).TryGet(definition.Id, out _), "Private definitions must not reach a shared idle client.");
            Assert.IsFalse(lateSerializer.GetCompressionCache(privateLate, privateLate.Connection).TryGet(definition.Id, out _), "One private client must not populate another private client's dictionary.");
        }
        Assert.IsTrue(sharedSender.Connected && sharedIdle.Connected && privateSender.Connected && privateLate.Connected, string.Join("; ", errors));
    }
}
