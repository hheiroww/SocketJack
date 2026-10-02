# SocketJack changelog

## 2026.15.0

- Require explicit MutableTcpServer protocol enablement and default SQL access to local clients.
- Add local assembly fingerprint discovery, including matching embedded DLL resources.
- Preserve reliable UDP and binary serializer benchmark documentation.

## 2026.14.1

- Restore reliable UDP and custom binary serializer benchmark documentation, historical results, and raw datasets.

- Remove a stale admin-page event handler after application UI cleanup, and validate event bindings against page elements.

## 2026.14.0

- Require authenticated, locally assigned principals for application messages by default, with explicit anonymous DTO registration.
- Authorize message graphs before deserialization; restrict peer routing and administrative commands and disable remote CLR reflection invocation.
- Verify approved contract DLLs during handshaking without returning server fingerprints; keep SafeMode enabled by default.
- Isolate segment reassembly per connection and bound resources.
- Optimize the custom binary codec, wrapping accessors and segment reconstruction.
- Separate application services, pages and development artifacts from the library repository.
- Retain SSH.NET 2026.0.0 and dependency auditing during publishing.

See [migration and security configuration](docs/SAFEMODE.md) before upgrading.

## 2026.13.1

- Use NuGet-compatible Markdown in the package README.

## 2026.13.0

- Reliable UDP sessions and chunked, hash-verified transfers.
- Custom binary serialization, TypeScript generation and SSH.NET 2026.0.0.
- NuGet Trusted Publishing through GitHub Actions.
