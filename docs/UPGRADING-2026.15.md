# SocketJack 2026.15: setup and upgrade guide

2026.15 makes server access an explicit choice. Existing `MutableTcpServer` applications must enable their required protocols before listening. Ordinary dedicated TCP/HTTP servers are not configured through this protocol list.

## Choose the services your port accepts

Registering a handler prepares it; enabling its name allows connections to reach it. Enable only the services your application uses:

```csharp
var server = new MutableTcpServer(8080, "My server");
server.EnabledProtocols.UnionWith(new[] {
    MutableTcpProtocols.Http,
    MutableTcpProtocols.SocketJack
});
server.Listen();
```

Built-in names include HTTP, SocketJack, WebSocket, RTMP and TDS. For a custom handler, use its `Name`. Register the handler and configure its service as usual; the list does not create or configure it for you. Stop listening and disconnect existing clients before changing protocol or SQL access settings.

## Decide who can reach SQL

Enabling TDS still allows only connections from the same machine by default. SQL authentication remains separate from this network access setting.

```csharp
server.EnabledProtocols.Add(MutableTcpProtocols.Tds);
server.SqlOptions.RemoteAccess = SqlRemoteAccessMode.LocalNetwork;
// Alternatively, keep LocalOnly and allow one specific address:
// server.SqlOptions.AllowedRemoteIpAddresses.Add("192.168.1.25");
```

`LocalOnly` accepts loopback connections; `LocalNetwork` also accepts private and link-local addresses. `Any` accepts all addresses and should be an intentional deployment choice. The address list adds exceptions to the selected mode; it does not narrow `Any`. Register and configure the TDS handler separately. Configure login, firewall rules and transport protection for your environment.

## Approve a local message DLL

A fingerprint identifies the DLL bytes. In 2026.15.0, you can let SocketJack calculate it instead of copying it manually:

```csharp
var options = new NetworkOptions(); // SafeMode is on
options.VerifiedAssemblies.Add(typeof(ChatMessage).Assembly);
options.Whitelist.Add(typeof(ChatMessage));
```

Use your application's message type in place of `ChatMessage`. Register the types used on both endpoints before connecting. Approving a DLL does not allow every type inside it; message types still need their own registration.

The overload `Add(assembly, expectedSha256)` checks against a trusted release hash. Automatic approval trusts the local DLL selected by your application; it does not prove who produced that DLL. Matching embedded, uncompressed DLL resources are supported. Missing or ambiguous DLL images are rejected.

In published 2026.15.0, the client's registered type names and DLL fingerprints must match the server's expectations during the handshake. The server does not reveal its fingerprints. This is a compatibility check, not a login or proof of what a remote process is running. Keep TLS, authentication and permissions in place. Do not turn off SafeMode to work around an unexplained mismatch.

## Further reading

- [SafeMode and authentication](SAFEMODE.md)
- [Reliable UDP guide](UDP_Reliable.md)
- [UDP benchmark results](UDP_Reliable-results.md)
- [Binary serializer benchmarks](encoder-testing/README.md)
