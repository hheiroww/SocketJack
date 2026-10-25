# SocketJackSecurityLoader access facade

`SocketJack.Security.SocketJackSecurityLoader` is included in the existing SocketJack package and retains .NET Standard 2.1 compatibility. It exposes application-scoped access to registered remote operations and authenticated local managed components. The application publisher supplies the runtime through its verified bootstrap installed by heirowInstaller and maintained by heirowUpdater.

The public library contains the access contract and provider registration. Authentication, entitlement checks, encryption, protected caches, assembly activation, execution workers, and publisher implementation belong to the application's separately distributed runtime and service. Registering a provider alone never establishes account or server authorization.

The application initializes its verified bootstrap, calls `RegisterRuntimeProvider` once, then creates a runtime with `ApplicationId`, the HTTPS `ServiceUri`, and a `SessionProvider` callback reading its existing account session. The callback must reflect account switches and logout; credentials are kept out of JSON serialization and diagnostic text. The application calls `InvalidateAsync` on session changes and disposes outstanding module leases and the runtime when no longer needed.

Remote requests name an allowlisted operation and logical existing-object references, plus a JSON argument object limited to 64 KiB in UTF-8. References never select local paths, executable filenames, or CLR methods. Job results are JSON documents limited to 1 MiB. The service must independently authorize every referenced object and operation; the facade's validation is only an API boundary.

Approved managed worker adapters implement `ISecurityLoaderOperation.ExecuteAsync(SecurityLoaderOperationInput, CancellationToken)`. Inputs contain the operation name, a JSON argument object, and resolved byte buffers; results contain JSON plus optional byte artifacts. Both directions allow at most 16 buffers and 64 MiB of aggregate bytes, with defensive copies on construction and reads. The hosting service independently applies its smaller per-operation budgets, publisher approval, object authorization, and execution isolation. The shared contract performs no loading, decryption, or execution itself.

Jobs expose `ArtifactCount` from zero to 16. Runtimes may separately implement `ISecurityLoaderArtifacts.GetArtifactAsync(jobId, artifactIndex, cancellationToken)` for authorized artifact retrieval; the base runtime interface remains unchanged. Hosting defines retention and stricter output limits, and callers should handle artifacts that have expired or are unavailable.

Local components are requested by component identifier through `LoadLocalModuleAsync<T>`, with `T` defined in the application's shared contract assembly. Lease disposal releases ownership but cannot guarantee immediate erasure or prevent an authorized client with control of its process from inspecting loaded code. Features requiring code secrecy must execute on the service.

The bootstrap must contain the minimum ordinary executable code needed to authenticate and activate the private runtime. It cannot depend on an already decrypted runtime or contain persistent content keys. Trust originates in a pinned publisher key and verified signed release metadata; changing a writable profile cannot establish a new trusted publisher.

The facade never downloads code, selects an assembly from disk, launches processes, or supplies a fallback runtime. Calls fail when the bootstrap has not registered a provider. Application-specific fallback behavior should preserve local user work while reporting the unavailable protected feature.

Run the focused public contract checks with:

```powershell
dotnet test SocketJack.SecurityLoader.Tests/SocketJack.SecurityLoader.Tests.csproj --configuration Release -p:BuildPackage=false -p:GeneratePackageOnBuild=false
```
