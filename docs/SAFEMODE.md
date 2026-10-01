# SocketJack SafeMode and approved DLLs

`NetworkOptions.SafeMode` defaults to `true`. Configure both ends before listening or connecting. Existing clients need to be upgraded: an old client that sends a message without the handshake is rejected.

## What the handshake verifies

The client sends its own allowed type names and the MD5 and SHA-256 of each type's local assembly file. The server compares that manifest with its own allowlist and verified local DLLs before deserializing application traffic. The two type lists and assembly bytes must match exactly. Both sides must register the same message contracts, including types used only for sending. Use one shared, byte-identical contracts DLL; separately compiling a class with the same name is insufficient. Runtime assemblies must also match; different operating systems/runtime builds can have different fingerprints.

The server never sends its expected DLL fingerprints, its manifest, or approved pins. TCP acceptance is only `SJOK1`; WebSocket acceptance is the ordinary upgrade response. Invalid claims, omitted claims, duplicates and oversized manifests are rejected. TCP/WebSocket connections close, while UDP peers are refused/removed. Silent TCP and reliable-UDP handshakes expire after five seconds. The type list is frozen for each connection: changing it requires reconnection.

**A client-reported hash is not proof of code execution or client identity.** A malicious program can copy a legitimate DLL's hashes. MD5 is not a secure authenticity primitive. The additional SHA-256 comparison avoids relying on MD5 collisions, but does not turn the handshake into attestation. Authenticate clients with TLS/mTLS or application credentials, authorize each operation, and use an authenticated channel for hostile networks. Reliable UDP's cookie establishes return-path reachability, not authenticated application identity.

## Approving application DLLs

Register an expected SHA-256 obtained from your trusted release pipeline, signed manifest, or administrator-controlled configuration:

```csharp
var options = new NetworkOptions(); // SafeMode = true
options.VerifiedAssemblies.Add(
    typeof(ChatMessage).Assembly,
    trustedReleaseManifest.ChatContractsSha256);
options.Whitelist.Add(typeof(ChatMessage));

var server = new SocketJack.Net.TcpServer(options, 12345);
server.RegisterCallback<ChatMessage>(e => HandleAuthorizedMessage(e));
server.Listen();
```

Configure the client with its approved copy of the same contracts DLL and the same allowed types before `Connect`. `VerifiedAssemblies.Add` rejects a wrong pin. Unapproved application DLLs are rejected even when a type was registered for callbacks. SocketJack's own protocol assembly and the local core/collection runtime assemblies are trusted runtime dependencies; their bytes are still compared during handshaking. The registry does not automatically trust other assemblies, and is excluded from JSON serialization.

**Do not obtain trusted pins from connecting clients.** Computing a hash of an unknown DLL and immediately accepting it is not provenance verification. Assemblies loaded without a physical file (dynamic assemblies, some single-file/AOT deployments) fail closed. This API verifies message contract DLL files; it does not sandbox code, block arbitrary assembly loading elsewhere in the host application, or attest a remote process.

## Transports

- TCP: length-framed handshake before client-connected callbacks, peer initialization or typed receive loops.
- Mutable TCP: handshake in the SocketJack protocol handler before its typed client-connected event. The base raw-connection event only indicates TCP acceptance and must not grant application privileges.
- UDP / reliable UDP: handshake before application peer creation and typed/transfer delivery. Ordinary UDP has no authenticated source address; use a secure transport when spoofing matters.
- .NET WebSocket: client manifest in `X-SocketJack-Client-Dlls` during the HTTP upgrade. Typed WebSocket upgrades without it are refused. Browser WebSocket APIs cannot set this header; keep browser applications on separate explicitly configured endpoints rather than weakening a protected .NET endpoint.
- Typed HTTP bodies: the same client-only header is checked before deserialization and typed route invocation. SocketJack's HTTP client adds it for wrapped bodies. Ordinary untyped HTTP routes are unchanged; they still require their own authentication/authorization.

Handshake data is bounded to 60,000 bytes / 256 types and parsed as inert strings, independent of the selected message serializer. Reliable UDP message/queue budgets must accommodate that manifest. The built-in gzip/deflate receive paths now cap decompressed objects at `MaximumBufferSize`.

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
