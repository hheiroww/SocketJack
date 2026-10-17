# SocketJack SafeMode and approved DLLs

`NetworkOptions.SafeMode` defaults to `true`. Configure both ends before listening or connecting. Existing clients need to be upgraded: an old client that sends a message without the handshake is rejected.

## What the handshake verifies

SafeMode and `EnableCompatibilityNegotiation` default to `true`. The version 2 handshake (`SJSM2`) compares locally reflected wire contracts independently of assembly SHA256. Different builds can exchange compatible messages, including additions of optional fields. Missing types, changed field types, missing required fields, and contracts whose custom conversion cannot be inferred disable the affected message. Enum operations are checked individually: existing numeric/name pairs remain usable while newer values are blocked. Serializer/compression/framing settings must agree; incompatible transport encodings reject the connection.

Each endpoint retains local SHA256 integrity checks. Peers exchange bounded schemas, never server DLL fingerprints or pins. TCP uses length-framed replies; WebSocket and typed HTTP return `X-SocketJack-Compatibility`. Invalid, duplicate, and oversized manifests are rejected. Silent TCP and reliable-UDP handshakes expire after five seconds. Changing the whitelist requires reconnection. Schemas never load remote types or widen local whitelists.

Compatibility is not authentication or proof of application semantics. TLS, authentication, authorization, blacklists and structural validation still apply. Reflection identifies wire-format differences; it cannot infer changed business behavior or arbitrary custom converter semantics.

## Handling disabled features

```csharp
options.FeatureDisabled += (_, e) => ShowNotification(e.Feature + " disabled", e.Reason);

// Check before opening capture devices or changing UI state.
if (!connection.TryUseFeature("Voice chat", typeof(VoiceStart), typeof(VoiceChunk))) return;
// For a feature represented by an enum operation in a shared message:
if (!connection.TryUseFeatureValue("Screen sharing", MessageKind.ScreenShare)) return;
```

`connection.Compatibility` exposes `Supports`, `SupportsEnum`, and `DisabledTypes`. Typed sends check compatibility automatically and raise the event before sending an unsupported message. A blocked operation leaves other messages available. Reliable UDP's acknowledgement-returning send methods additionally return a failed task. Inbound incompatible types are rejected before materialization. Typed HTTP responds with 409 for a disabled route contract without invoking its handler.

## Builds predating negotiation

Install a negotiation-capable library on both endpoints once. A server enforcing exact hashes cannot accept a new client merely by changing the client. Older `SJSM1` manifests still work when they satisfy the original exact-hash/type rules. `EnableCompatibilityNegotiation = false` selects that strict legacy protocol explicitly. There is no insecure automatic retry and SafeMode remains enabled. A historical hash alone contains no schema, so unknown pre-negotiation binaries cannot safely be declared compatible automatically. After this baseline update, compatible older and newer builds negotiate without maintaining manual SHA256 whitelists.

## Approving application DLLs

Register a callback or add the message type to the whitelist. SocketJack registers its assembly, resolves its DLL location, and computes SHA-256 automatically:

```csharp
var options = new NetworkOptions(); // SafeMode = true
options.Whitelist.Add(typeof(ChatMessage));

var server = new SocketJack.Net.TcpServer(options, 12345);
server.RegisterCallback<ChatMessage>(e => HandleAuthorizedMessage(e));
server.Listen();
```

Register each endpoint's local contracts before `Connect`, including types used only for sending. Callback registration and `Whitelist.Add(typeof(ChatMessage))` automatically pin the local assembly while SafeMode is enabled. Locally configured string entries are pinned when resolved. Existing pins are preserved, and other types in the same assembly still require their own whitelist entries. SocketJack's own protocol assembly and the local core/collection runtime assemblies are trusted runtime dependencies; their schemas are compared during negotiation. The registry does not automatically trust assemblies outside locally registered message types, and is excluded from JSON serialization.

`Add(assembly, expectedSha256)` remains available when a signed release manifest or administrator-controlled pin is required. The automatic overload approves the local DLL selected by the application; it is integrity and peer-version matching, not provenance verification. Assemblies loaded from an uncompressed embedded `.dll` resource are supported when a loaded owner contains a resource matching the DLL name and module ID. Dynamic assemblies and assemblies with no discoverable DLL image fail closed. This API does not sandbox code, block arbitrary assembly loading elsewhere in the host application, or attest a remote process.

## Transports

- TCP: length-framed handshake before client-connected callbacks, peer initialization or typed receive loops.
- Mutable TCP: handshake in the SocketJack protocol handler before its typed client-connected event. The base raw-connection event only indicates TCP acceptance and must not grant application privileges.
- UDP / reliable UDP: handshake before application peer creation and typed/transfer delivery. Ordinary UDP has no authenticated source address; use a secure transport when spoofing matters.
- .NET WebSocket: client manifest in `X-SocketJack-Client-Dlls` during the HTTP upgrade. Typed WebSocket upgrades without it are refused. Browser WebSocket APIs cannot set this header; keep browser applications on separate explicitly configured endpoints rather than weakening a protected .NET endpoint.
- Typed HTTP bodies: the same client-only header is checked before deserialization and typed route invocation. SocketJack's HTTP client adds it for wrapped bodies. Ordinary untyped HTTP routes are unchanged; they still require their own authentication/authorization.

Handshake data is bounded to 60,000 bytes / 256 root types / 512 schema nodes and parsed as inert data, independent of the selected message serializer. Reliable UDP message/queue budgets must accommodate that manifest. The built-in gzip/deflate receive paths now cap decompressed objects at `MaximumBufferSize`.

## Explicit unsafe legacy mode

```csharp
// WARNING: unsafe on untrusted networks. Disables the DLL handshake and application DLL pin requirement.
// Do not use this to silence a rejected client or an unexplained DLL mismatch.
var isolatedLegacyOptions = new NetworkOptions { SafeMode = false };
```

**WARNING: setting `SafeMode = false` is not a smart move for an exposed endpoint.** It must be a deliberate local configuration choice; peers cannot negotiate this downgrade. Type allowlists/blacklists and structural checks still apply. The property setter emits a trace warning. Older/browsing clients may need an isolated legacy endpoint, but never automatically retry against a protected endpoint with verification disabled.

## Related audit changes and limits

JSON peer redirects now resolve only explicitly allowed local types before construction, including their nested payload. Declared and nested redirect types must agree. Generic allowlist entries retain their full type arguments. Nested object graphs are checked before framework deserialization. Property conversion errors reject the complete message. The HTTP client now uses normal TLS certificate validation instead of accepting every certificate.

Network receive paths use connection-scoped segment buffers, with aggregate size, count, duplicate and expiry checks. Reassembled data passes through the same type and authorization gates before activation. Application constructors, setters, custom serializers/converters, callbacks and authorization rules are trusted application code and remain part of the threat model. This review is not a complete security certification.

Primary reference: [.NET System.Text.Json threat model](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Text.Json/docs/ThreatModel.md).

## Authentication and authorization (2026.14)

`options.Authorization.RequireAuthentication` defaults to `true`. A registered callback is not an authorization grant. Explicitly add only inert login DTOs to `AnonymousMessageTypes`. After validating credentials against your trusted account store, assign a server-created principal with a future expiry:

```csharp
options.UseSsl = true;
options.Authorization.AnonymousMessageTypes.Add(typeof(LoginRequest));
options.Authorization.AuthorizeMessage = context =>
    AccountPolicy.CanSend(context.Principal, context.MessageType);
server.RegisterCallback<LoginRequest>(e => {
    // Verify credentials locally; never accept client-supplied roles or IsAdmin flags.
    var principal = AccountStore.ValidateCredentials(e.Object);
    if (principal == null) { e.Connection.CloseConnection(); return; }
    e.Connection.SetAuthenticatedPrincipal(principal, DateTimeOffset.UtcNow.AddMinutes(15));
});
```

`LoginRequest`, `AccountStore`, and `AccountPolicy` represent application contracts and services, not SocketJack APIs. Approve the contracts DLL and register matching types before connecting. Configure the server certificate and normal client certificate validation. `SetAuthenticatedPrincipal` rejects a connection without encrypted TLS. Authentication expires automatically; call `ClearAuthentication` on logout. Returned principals are defensive copies.

Mark command DTOs `[AdministrativeMessage]` or register them in `AdministrativeMessageTypes`. Such messages require both an authenticated `Administrator` role and an explicit successful `AuthorizeMessage` callback before deserialization. Roles must originate in the account store. Remote CLR reflection invocation is disabled, including for an administrator.

Peer redirects additionally require `UsePeerToPeer`, authenticated TLS, and `AuthorizePeerRedirect`; check `context.Recipient` as well as the type and identity. The server replaces the claimed sender with its own connection ID. Administrative/control envelopes cannot be embedded in redirected payloads. Peer identity/control messages require `AuthorizePeerControl`, which defaults to denial. Reliable UDP is not an authenticated channel and cannot carry these privileged operations. Setting `SafeMode = false` does not disable authentication or peer authorization.

For intentionally public data, add an exact type to `AnonymousMessageTypes`. `RequireAuthentication = false` permits ordinary anonymous application data but still denies privileged operations without authentication and policy. Do not enable anonymous command DTOs with side-effecting setters. Use explicit HTTP `Map<T>` routes for typed bodies; request gates run before decoding and the declared type must match the mapped DTO. Raw HTTP handlers implement their own request authentication.
