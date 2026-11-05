# Relays and linked servers

This guide covers two independent features. A **relay** forwards allocated gameplay traffic between a host and clients. A **linked server** joins a canonical account/social hub so players can share identities, Global chat and Communities across game servers. You can use either feature without the other.

Complete a direct two-player game and [Accounts and hosting](accounts-and-hosting.md) first. The supplied services are self-hosted: you provide reachable endpoints, certificates, network policy and capacity. SocketJack does not supply a public relay address, a hosted subscription or automatic account-database replication.

## 1. Choose the route

| Route | Use when | Important behavior |
| --- | --- | --- |
| Direct UDP | Native players can reach the game's UDP listener. | Default gameplay path; reliable unordered delivery avoids waiting for missing earlier messages. Payloads are plaintext. |
| Authenticated UDP relay | Native players need an allocated intermediary. | Retains UDP game transport semantics, with an extra authenticated routing envelope. It does not encrypt game payloads. |
| Direct WS/WSS gameplay | A browser joins a native WebSocket game listener. | Browser-compatible; WSS supplies connection encryption when TLS is configured. Ordered TCP stream behavior remains. |
| Authenticated WS/WSS relay | Native/browser clients join a host through a self-hosted WebSocket intermediary. | Short-lived role-bound tickets; still an ordered WebSocket/TCP path. |
| Hub plus enrolled leaves | Several game servers share canonical accounts/social state. | Does not move a scene, replicate a world or balance gameplay automatically. |

A UDP host is not automatically reachable through a WebSocket relay. The host-side WebSocket relay attaches to a `WebSocketGameTransport`; use compatible game transports and settings at both ends. There is no silent automatic UDP-to-WSS fallback. Your connection UI must choose a route and report its actual outcome.

UPnP can assist a direct listening game's port mapping on supported routers. It cannot guarantee connectivity through carrier-grade NAT, restrictive networks or a provider firewall. A successful WebSocket account connection does not prove UDP reachability.

## 2. Enable the account-backed relay service

On the canonical native `SocketJackWebHost`:

1. Enable **Enable Relay**. The Unity component defaults it on; a programmatically created `SocketJackWebOptions` requires `EnableRelay = true` explicitly.
2. Configure HTTPS and exact Allowed Origins before public use. The account-backed WSS route is `/ws/relay` on the website's listener.
3. Choose **Datagram Relay Port**. `-1` uses the next port after the website port: a website on `7778` uses UDP `7779`. Do not confuse this with the game listener's port. The setup wizard normally places the website one port after the game and the datagram relay one further port after that.
4. Set **Public Relay Host** to the publicly reachable relay hostname, such as `game.example`, when binding to a wildcard address. `0.0.0.0` and `::` are bind addresses, not destinations clients can use.
5. Configure the host/firewall/NAT for that UDP port and the website's TCP/TLS port. Test them independently from an external connection.

Relay hosting runs on the canonical service, not each linked leaf. Authenticated requests sent through a leaf can be forwarded to its fixed configured hub, but your client still needs the correct canonical relay endpoint. Do not derive an arbitrary proxy destination from a player's message.

### Allocation operations

After login, use `SocketJackWebClient.RequestAsync` or `accounts.Client.RequestAsync` from the Unity behaviour:

| Operation | Request | Result or permission |
| --- | --- | --- |
| `relay/create` | `{transport:"udp",name:"Friends"}` or `transport:"websocket"` | Creates an account-owned session and a host-role grant. |
| `relay/join` | `{sessionId}` | Issues a client-role grant for that available session. Knowing an ID does not make the caller the host. |
| `relay/renew` | `{sessionId}` | Host extends the session lifetime. |
| `relay/renew` | `{sessionId,allocationId}` | Account renews its own UDP allocation; routing ID and key remain stable. |
| `relay/close` | `{sessionId}` | Host or administrator closes the session and active routes. |

Keep returned grants in memory and deliver them only to the account's intended connection. Do not serialize tickets/keys into prefabs, put them in URLs, print them, or display them as share codes. A relay session ID can be your lobby's opaque lookup value; the role grant is the credential.

The current account service limits session creation and grants per account, in addition to transport queues/rates. A newly created session expires after two hours unless renewed. WSS join tickets expire after sixty seconds and are consumed once. UDP allocations last thirty minutes unless renewed. Monitor the actual returned expiry rather than assuming a permanent connection. Bridge keepalives verify path readiness; they are not account allocation renewal.

Relay permission and game admission are separate checks. A valid relay grant allows the route; your game's normal connection data, protocol checks and account requirements still apply. Request a game ticket for the **actual target game server** and pass those bytes through its handshake.

## 3. Attach a WebSocket game host and client

The following is native transport integration code. It uses the account service for grants and leaves ownership/lifetime explicit. The caller starts `gameServer`, retains/disposes the returned tunnel and pumps the game transports through its normal session update. A standalone transport needs `Poll`; a `NetworkManager` integration installs the transport through `TransportFactory` before startup and lets the manager poll it.

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using SocketJack.Multiplayer.Transport;
using SocketJack.Multiplayer.Web;

public static class RelayGameConnection
{
    public static async Task<(WebSocketRelayHost tunnel, string sessionId)> AttachHostAsync(
        SocketJackWebClient account,
        WebSocketGameTransport gameServer,
        Uri relayUri,
        CancellationToken cancellationToken)
    {
        var grant = await account.RequestAsync("relay/create",
            new { transport = "websocket", name = "Friends" }, cancellationToken);
        string id = grant.GetProperty("sessionId").GetString();
        string ticket = grant.GetProperty("ticket").GetString();
        var tunnel = await WebSocketRelayHost.ConnectAsync(
            gameServer, relayUri, ticket, cancellationToken);
        return (tunnel, id);
    }

    public static async Task<WebSocketGameTransport> ConnectClientAsync(
        SocketJackWebClient account,
        Uri relayUri,
        string sessionId,
        byte[] gameAdmission,
        CancellationToken cancellationToken)
    {
        var grant = await account.RequestAsync("relay/join",
            new { sessionId }, cancellationToken);
        var transport = new WebSocketGameTransport(clientUri: relayUri)
        {
            ClientConnector = WebSocketRelayService.CreateClientConnector(
                grant.GetProperty("ticket").GetString())
        };
        try
        {
            transport.StartClient(relayUri, gameAdmission);
            return transport;
        }
        catch { transport.Dispose(); throw; }
    }
}
```

Use a trusted configured endpoint such as `wss://game.example:7778/ws/relay`. The first method's `gameServer` must already be running; relay attachment does not start your game or create a world. `StartClient` starts connection work: wait for the transport/session's connected event and baseline, rather than treating its return as a completed join. If attachment fails after creating a session, close that allocation through `relay/close` in your caller's error handling.

For a Unity browser client, add `SocketJackBrowserTransport` beside the manager before starting networking. Set its `Endpoint` to the WSS relay URI and assign its runtime `RelayTicket` from an account-issued client grant while stopped. Then join with the normal game admission bytes. The component supplies the JavaScript game WebSocket connector; the native `CreateClientConnector` shown above cannot substitute for a browser bridge.

An HTTPS game page requires WSS. Match the relay's browser Origin policy to that page's exact origin. Account text, account voice and gameplay use separate sockets even when they share a hostname. Their permissions and queue budgets remain separate.

**Expected result:** the host's tunnel reports connected, a client receives a normal game peer ID, and admitted gameplay works through the route. Reusing a consumed ticket is denied. Closing the session stops active routes; reconnect requires a valid new grant and ordinary game admission.

## 4. Use a UDP relay without breaking the packet budget

The UDP API result contains `allocationId` as a decimal string, `sessionId`, `isHost`, base64 `secret`, `expiresUtc`, `host`, `port`, `maxNativeDatagramBytes` and `reservedRelayHeaderBytes`. Construct `DatagramRelayAllocation` from those trusted response fields. Resolve a configured/returned hostname to an appropriate relay IP in your native application; `IPEndPoint` itself does not perform DNS resolution.

The host uses a reserved-budget native listener and a host bridge. The client uses a client bridge and the `DatagramRelayGameTransport` decorator:

```csharp
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using SocketJack.Multiplayer.Transport;

public static class DatagramRelayConnection
{
    // hostAllocation/clientAllocation come from separate authenticated role grants.
    // Retain all returned objects; stop/dispose them when the game ends.
    public static async Task<(UdpGameTransport server,
        DatagramRelayHostBridge hostBridge,
        DatagramRelayClientBridge clientBridge,
        DatagramRelayGameTransport client)> OpenLocalExampleAsync(
        DatagramRelayAllocation hostAllocation,
        DatagramRelayAllocation clientAllocation,
        IPEndPoint relayEndPoint,
        CancellationToken cancellationToken)
    {
        var server = new UdpGameTransport(DatagramRelayGameTransport.ReserveOverhead());
        DatagramRelayHostBridge host = null;
        DatagramRelayClientBridge bridge = null;
        try
        {
            server.StartServer(new IPEndPoint(IPAddress.Loopback, 0));
            host = new DatagramRelayHostBridge(
                hostAllocation, relayEndPoint, server.LocalEndPoint);
            bridge = new DatagramRelayClientBridge(clientAllocation, relayEndPoint);
            await host.StartAsync(cancellationToken);
            await bridge.StartAsync(cancellationToken);
            var client = new DatagramRelayGameTransport(bridge);
            return (server, host, bridge, client);
        }
        catch
        {
            if (bridge != null) await bridge.StopAsync();
            if (host != null) await host.StopAsync();
            server.Dispose();
            throw;
        }
    }
}
```

This example creates both sides in one native application to make ownership clear. In a deployed game the host and client each create only their own side. After the bridges are ready, call `client.StartClient(relayEndPoint, gameAdmission)` and pump the native game sessions. Pass the game's normal admission validator/settings to the real server; the small example does not configure account admission for you. On shutdown, stop the game transports, await each bridge's `StopAsync`, dispose the transports and close the account-owned relay session when appropriate.

Do not wrap an ordinary full-size UDP transport in an extra envelope. The complete reserved budget is:

| Part | Maximum reserved bytes |
| --- | ---: |
| IPv6 header | 40 |
| UDP header | 8 |
| Authenticated relay envelope | 48 |
| Native SocketJack datagram, including its reliability header | 902 |
| **Complete reserved packet** | **998** |

The 48-byte relay envelope contains routing/authentication information; it is not free payload. `DatagramRelayGameTransport.ReserveOverhead` reserves it, clamps native datagrams to 902 bytes, and honors a lower configured MTU/extra overhead. The session's object-frame decorator further reserves native reliability, frame and session headers. Large logical objects split into bounded smaller frames; small values avoid multipart rebuilding. A 902-byte native datagram is not a 902-byte object state allowance.

The relay validates allocated credentials, authenticated packets, replay windows and reachable endpoints before forwarding. The host bridge preserves separate native peer histories through separate local gateway sockets. The protocol uses HMAC authentication and **does not encrypt** game data. The relay and an observer on an unencrypted path can see payload contents. WSS supplies encryption to its TLS endpoint, not end-to-end secrecy from the relay operator.

**Expected result:** `StartAsync` completes only after the relay path is ready, `IsReady` remains true while its reachability checks succeed, and the normal UDP game handshake completes. Expired/revoked grants or a blocked UDP path must produce a failed/unready join, not a success inferred from an earlier HTTPS request.

## 5. Enroll game servers under one hub

Choose one canonical account/social hub. Keep its **Hub Address** empty. Its private SQLite store owns canonical accounts and social membership. Each leaf has its own game listener, website and private store for its local node credential; leaves forward authorized account/social work to the fixed configured hub. This is a single canonical service design, not multi-master SQLite replication or automatic high availability.

1. Give the hub and every leaf a different stable **Node Id**, using 1–48 letters, digits, `_` or `-`. Give each a separate private Data Directory.
2. Configure the hub's HTTPS hostname/certificate and prove a normal account login first.
3. Sign in to the hub as an administrator. Call `nodes/issue-link` through its account API. The result contains `linkToken` and `expiresInSeconds:600`. It is one-use enrollment data; protect it like a secret.
4. On the leaf's `SocketJackWebHost`, set **Hub Address** to the canonical HTTPS origin, for example `https://game.example:7778/`, without credentials, a path, query or fragment. Loopback HTTP is accepted for isolated development only.
5. Set **Enrollment Token Environment** to your chosen environment-variable name; the default is `SOCKETJACK_NODE_ENROLLMENT_TOKEN`. Provide the one-use value to the leaf process through private host configuration, not an Inspector string, scene asset or command-line URL.
6. Start the leaf. It enrolls, saves its returned node credential in its private database and authenticates its outbound hub WebSocket. The hub keeps a hash of that credential. After successful enrollment, later restarts use the saved node credential; do not issue a new enrollment grant for every restart.
7. Verify `leaf.Server.IsLinkedToHub` and inspect `FederationError` when needed. As administrator, `nodes` lists currently connected nodes. Use `nodes/revoke` with `nodeId` to revoke a leaf; investigate and deliberately re-enroll under your own recovery process rather than bypassing the denial.

An administrator can obtain an enrollment grant with the public client API:

```csharp
// admin is an already authenticated SocketJackWebClient connected to the hub.
var enrollment = await admin.RequestAsync("nodes/issue-link", new { });
string oneUseValue = enrollment.GetProperty("linkToken").GetString();
// Deliver oneUseValue through your protected server configuration. Do not log it.
```

The leaf's HTTP bridge is bounded and fixed to its configured hub; it is not an arbitrary URL proxy. Public player requests cannot authenticate themselves as a node. A revoked/replaced node loses its trusted presence connection. Enrollment, credential backup and server identity management are administration tasks, separate from ordinary player registration.

## 6. Verify cross-server identities, chat and admission

Use a player account created at the hub. Sign in through a leaf's configured account endpoint, or directly at the hub. Send Global and Community text from players on different enrolled servers and confirm they share the intended canonical social service. Rooms still define game visibility independently of these social channels.

To join a leaf through the hub, call `JoinGameAsync(manager, "arena-east")`, where `arena-east` is that leaf's actual enrolled Node Id. Configure the manager's game address/port separately. The hub installs the ticket at the target leaf and waits for acknowledgment before returning it. An offline leaf or an acknowledgment timeout is a join failure, not permission to retry with anonymous admission.

The Unity host publishes trusted player/world/room/position snapshots from the main thread. This supports cross-node routing without accepting browser-provided positions. Global/Community social access can span nodes; proximity still requires validated presence in the same configured world/room/range. Browser-only users do not acquire a game position by opening the website.

Authenticated cross-server voice uses the optional Voice.Web module and canonical membership checks. A leaf must use the supplied authorized audience resolution; an unavailable membership source defaults to denial. Follow [Voice](voice.md) for components, opt-in capture and audio limits rather than treating a linked text socket as a voice connection.

## Troubleshooting and capacity

| Symptom | Check |
| --- | --- |
| Account API works, UDP relay does not | Check the separate UDP port, advertised Public Relay Host, NAT/firewall and fresh allocation. HTTPS success says nothing about that UDP path. |
| WSS relay connects but game cannot join | Check host attachment, transport/protocol settings, game admission bytes and baseline compatibility. A route grant is not a game ticket. |
| Browser upgrade is denied | Check exact Allowed Origins, WSS certificate trust and mixed-content restrictions. Acquire a new one-use relay ticket after a failed/consumed attempt. |
| Datagram send is rejected after adding a tunnel | Apply ReserveOverhead to the host and use the supplied client decorator. Keep logical message/reassembly bounds; do not enlarge native datagrams to hide the error. |
| Leaf never links | Check Hub Address is a fixed origin, certificate trust, unique Node Id, enrollment expiry and private database permissions. Inspect FederationError without logging credentials. |
| Same username behaves as separate accounts | Confirm all leaves point at one canonical hub rather than independent standalone databases. Do not copy live account databases between nodes. |
| Leaf ticket request says offline/timed out | Restore its authenticated hub connection and request a new ticket. Check its game destination independently. |
| Latency increases on WSS | WebSocket/TCP has ordered-stream head-of-line behavior. Inspect actual queues/RTT and representative loss/load; UDP delivery modes cannot remove TCP ordering. |

Transport metrics and the administrator diagnostics API help identify retries, queue pressure, rejected work and active connections. Their byte counters do not include every IP/TCP/UDP/TLS layer. Measure CPU, bandwidth, visibility and gameplay simulation with your real topology. Finite allocation/queue bounds and successful 2,000-peer loopback transport tests are not a guarantee of 2,000 mutually visible players or relay capacity on your host.

Portable tests cover authenticated WSS/UDP relay allocation, native traffic, replayed tickets, renewal/revocation, packet boundaries and hub/leaf admission. Actual Unity 2022 WebGL gameplay has used direct and relayed loopback WebSockets. Native TLS tests are separate. These do not establish a public WSS browser route, WAN NAT traversal, regional latency or your production uptime. Verify those on your actual deployment before inviting players.

Public source references: [account relay operations](https://github.com/hheiroww/SocketJack/blob/codex/unity-multiplayer/SocketJack.Unity.Web/Runtime/SocketJackWebServer.Relay.cs), [datagram bridges and budget](https://github.com/hheiroww/SocketJack/blob/codex/unity-multiplayer/SocketJack.Unity/Transport/DatagramRelayBridges.cs), [WebSocket relay](https://github.com/hheiroww/SocketJack/blob/codex/unity-multiplayer/SocketJack.Unity/Transport/WebSocketRelay.cs), [hub/leaf implementation](https://github.com/hheiroww/SocketJack/blob/codex/unity-multiplayer/SocketJack.Unity.Web/Runtime/SocketJackWebServer.Federation.cs).
