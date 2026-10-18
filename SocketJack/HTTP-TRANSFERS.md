# Resumable HTTP transfers

`HttpServer.MapTransfers(prefix, options)` registers durable sequential uploads and downloads. `MutableTcpServer.Http` exposes the same helper. Dispose the returned store when the host stops.

```csharp
using var transfers = server.MapTransfers("/transfers", new HttpTransferOptions {
    Directory = transferDirectory,
    Authorize = (connection, request) => ResolveAuthenticatedOwner(request),
    Commit = (session, payloadPath) => RegisterVerifiedFile(session, payloadPath)
});
```

The host supplies owner authentication, destination authorization, quota reservation/release callbacks, and optional existing-destination generation lookup. Never return an owner based solely on unverified client input. Share links may deliberately authorize public reads; upload and replacement operations still require the owner.

Operations below use the mapped prefix. Session responses contain `Id`, `Owner`, `Target`, `Generation`, `Length`, `Offset`, `Sha256`, `State`, `CreatedUtc`, and `LastProgressUtc`. State enum values are Uploading=0, Paused=1, Canceled=2, Stalled=3, Failed=4, Completed=5, Expired=6.

| Method | Operation | Input |
|---|---|---|
| POST | `/start` | JSON: `Target`, `Length`, `Sha256`, `Overwrite`, optional `ConfirmGeneration` |
| GET | `/status?id=...` | Session identity |
| POST | `/append?id=...&offset=...&sha256=...` | Raw chunk, at most 1 MiB by default |
| POST | `/complete?id=...` | Empty body; verify and commit |
| POST | `/cancel?id=...` | Empty body; add `pause=1` to pause |
| GET | `/download?id=...` | Optional single byte `Range` and `If-Range` |

Overwrite values: Ask=0 (default), Always=1, Never=2. Ask returns HTTP 409 and the existing `generation`; repeat with that exact `ConfirmGeneration`. An intervening replacement invalidates the confirmation. The host can locate the newest target using `Find(owner, target)` and choose to expose it immediately. Previously opened downloads remain pinned to their immutable generation.

Uploads acknowledge only flushed bytes and persisted offsets. A repeated chunk is accepted only if its bytes match. Re-selecting the same owner/target/length/hash resumes the current generation. Completed generations are immutable. The final SHA-256 must match before completion; the host commit callback must be idempotent because a crash may require replay.

Downloads send available durable prefixes and wait for new bytes. The last byte is withheld until verification and host commit. Fixed content lengths, ETags, byte ranges, and `X-Content-SHA256` let clients identify interruption and verify completion. Interrupted or canceled responses are aborted; retry using the stored ETag and received offset. HTTP intermediaries must preserve the length and range headers and avoid buffering streaming responses.

Default inactivity retention is one hour. Successful upload/download byte progress renews it; status polling and identical retries do not. Completed files remain stored. Canceled/paused files can resume until expiry. Startup replays metadata, discards bytes beyond the acknowledged offset, restores reservations, and rejects missing/truncated payloads. Keep the transfer directory private and include it in the host's storage accounting and backup policy.
