using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Net;
using SocketJack.Net.P2P;

namespace SocketJack.Tests;

[TestClass]
public sealed class TcpLifecycleTests {
    [TestMethod]
    public void HandshakeCompletingAfterServerDisposalDoesNotInitializeAPeer() {
        var server = new SocketJack.Net.TcpServer(new NetworkOptions { UsePeerToPeer = true }, 0);
        using var connection = new NetworkConnection(server, null) {
            _Identity = new Identifier { ID = "late-handshake" }
        };
        server.Dispose();

        // A late accept callback must return without accessing the released peer dictionary.
        server.InitializePeer(connection);
        Assert.IsNull(server.Peers);
    }
}
