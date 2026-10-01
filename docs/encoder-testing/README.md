# Encoder testing README

## Subsequent release validation (2026.14.0)

The benchmark tables below preserve their original measurement snapshot. Subsequent release validation passed 111 core tests (one optional TypeScript compiler check skipped) and all 45 reliable-UDP/codec tests excluding the large-file test. The separate large-file test also passed: 4,295,032,833 bytes transferred in 206.82 seconds with matching SHA-256 and 26,756,272 bytes peak managed memory. These correctness checks do not replace or change the historical performance measurements or their TCP acceptance limits.


Recorded September 30, 2026. This documents the completed custom `SB` binary encoder/decoder, wrapper, and legacy segment tests. The comparison uses the previous optimized binary implementation as its baseline, and separately compares the updated implementation with SocketJack's System.Text.Json adapter. No BinaryFormatter is used.

[UDP transport results](../UDP_Reliable-results.md) · [Protocol guide](../UDP_Reliable.md) · [Main README](../../README.md)

## Results: binary before and after

Times are means of ten paired rounds. Speedup is before/after time; larger is better. Brackets are 95% confidence intervals. An interval including 1 is **inconclusive**.

| Payload | Operation | Before (µs) | After (µs) | Speedup [95% interval] | Allocation reduction |
|---|---|---:|---:|---|---:|
| 256 B | Encode | 1.62 | 0.81 | 2.00× [1.85, 2.26] | 28.0% |
| 256 B | Raw decode | 2.31 | 1.54 | 1.50× [1.21, 1.98] | 59.7% |
| 256 B | Wrap + encode | 570.00 | 589.12 | 0.97× [0.74, 1.19] **inconclusive** | 88.0% |
| 256 B | Decode + unwrap | 1762.48 | 1195.09 | 1.47× [1.31, 1.65] | 97.1% |
| 32 KiB | Encode | 5.64 | 4.51 | 1.25× [1.12, 1.39] | 0.5% |
| 32 KiB | Raw decode | 5.67 | 4.27 | 1.33× [1.17, 1.50] | 4.1% |
| 32 KiB | Wrap + encode | 592.29 | 491.08 | 1.21× [1.11, 1.32] | 28.1% |
| 32 KiB | Decode + unwrap | 2002.75 | 1007.56 | 1.99× [1.56, 2.64] | 78.4% |
| 256 KiB | Encode | 85.19 | 73.84 | 1.15× [0.78, 1.78] **inconclusive** | 0.1% |
| 256 KiB | Raw decode | 73.83 | 72.15 | 1.02× [0.73, 1.40] **inconclusive** | 0.5% |
| 256 KiB | Wrap + encode | 796.30 | 641.23 | 1.24× [1.06, 1.50] | 5.2% |
| 256 KiB | Decode + unwrap | 2330.24 | 1139.31 | 2.05× [1.72, 2.38] | 33.2% |

At 32 KiB, encode is 1.25× faster, raw decode 1.33×, wrap + encode 1.21×, and decode + unwrap 1.99×. Small-message wrapping and additional 256 KiB raw-codec gains are inconclusive. Full wrapping/unwrapping still performs live DLL fingerprint verification, which remains a major cost. Raw decode produces inert values; it does not include typed object activation.

## Updated binary versus System.Text.Json

JSON uses the existing SocketJack adapter and its normal converters/options, not a source-generated JSON implementation. Full-pipeline results include the same approval policy on both paths.

| Payload | Operation | JSON (µs) | Binary (µs) | Binary speedup [95% interval] |
|---|---|---:|---:|---|
| 256 B | Encode | 5.65 | 0.81 | 6.99× [4.58, 11.27] |
| 256 B | Raw decode | 8.55 | 1.54 | 5.56× [3.66, 8.32] |
| 256 B | Wrap + encode | 529.35 | 589.12 | 0.90× [0.70, 1.09] **inconclusive** |
| 256 B | Decode + unwrap | 1132.74 | 1195.09 | 0.95× [0.83, 1.06] **inconclusive** |
| 32 KiB | Encode | 47.28 | 4.51 | 10.49× [7.18, 15.51] |
| 32 KiB | Raw decode | 100.27 | 4.27 | 23.49× [21.79, 25.27] |
| 32 KiB | Wrap + encode | 617.46 | 491.08 | 1.26× [1.21, 1.30] |
| 32 KiB | Decode + unwrap | 1210.41 | 1007.56 | 1.20× [1.09, 1.32] |
| 256 KiB | Encode | 287.10 | 73.84 | 3.89× [3.11, 5.04] |
| 256 KiB | Raw decode | 534.34 | 72.15 | 7.41× [5.78, 9.82] |
| 256 KiB | Wrap + encode | 897.61 | 641.23 | 1.40× [1.35, 1.46] |
| 256 KiB | Decode + unwrap | 2893.90 | 1139.31 | 2.54× [2.40, 2.69] |

| Payload | Binary encoded bytes | JSON encoded bytes |
|---|---:|---:|
| 256 B | 405 | 514 |
| 32 KiB | 32,919 | 43,864 |
| 256 KiB | 262,295 | 349,788 |

## Legacy segment builder and reconstruction

| Operation, 4 MiB | Before (ms) | After (ms) | Speedup [95% interval] | Allocated before / after (MiB) |
|---|---:|---:|---|---:|
| segment-build | 18.76 | 14.68 | 1.28× [1.07, 1.54] | 14.810 / 10.753 |
| segment-rebuild | 955.89 | 8.82 | 108.32× [98.30, 119.81] | 2105.292 / 4.020 |

Reconstruction now allocates the final array once and decodes each Base64 segment into its indexed slice. The old path repeatedly concatenated arrays. Allocation figures are cumulative managed allocations, not peak resident memory. Reliable UDP already uses raw fragments and does **not** call this legacy `Segment.Rebuild` helper; its transport speedup cannot be inferred from this table.

Indexes restart per object: one-based for the compatible legacy envelope, zero-based for reliable UDP fragments. Legacy object IDs are numeric rather than GUID strings, separate from fragment indexes. Temporary encoder buffers and configurable raw segment chunks stay within 32 KiB; final materialized objects may exceed that size subject to object limits.

## Correctness coverage and recorded outcomes

| Check | Recorded result |
|---|---|
| Focused codec, compact-frame, optimization and segment checks | 24/24 passed against the measured DLL |
| Reliable suite excluding >4 GiB file | 43 passed / 2 failed / 45 total |
| SafeMode and authorization suite | 36 passed / 2 failed / 38 total |
| Core .NET Standard 2.1 and WPF .NET 8/.NET 10 Release builds | Passed at the measured snapshot; WPF had an existing unused-field warning |

Coverage includes exact `SB` v1 bytes, primitive and enum boundaries, arrays and nullable values, nesting/malformed inputs, reordered fields, byte-buffer ownership, shared serializer concurrency, allowlist/blacklist/pin revocation, approval before authorization, per-object counters, duplicate/malformed segments, shuffled reconstruction, allocation bounds, and actual TCP segmented delivery using binary and JSON.

Known failures were already present: `PeerRedirectAndInterfaceReplyRemainReliable(False/True)` conflicts with the current authenticated peer-redirect policy; `ArbitraryHttpBodyCannotInvokeCallbackBeforeRequestGate` sees a closed connection rather than the expected HTTP response; `ReflectedAdminEndpointCannotExecuteClrMethod` expects a removed endpoint. This is not an all-tests-pass claim. The >4 GiB file test was not repeated in this codec pass; see the earlier transport run below.

## Method and limitations

- Host: DESKTOP-KSSU21A, Xeon W-2140B (8 cores/16 threads), approximately 16 GiB RAM, Windows 11 Pro 10.0.22631, .NET 8.0.31. No network operation is timed in this encoder benchmark.
- Ten paired rounds alternate separate baseline/updated processes using an identical harness and different core DLLs. Codec and phase order rotate by round. Each codec phase warms for at least 200 ms, runs a full GC, then measures at least 150 ms in batches of 32. Segment phases warm once and measure at least 150 ms; the old rebuild takes one timed operation per round.
- Seeded-random byte arrays of 256 B, 32 KiB and 256 KiB sit inside equivalent typed messages. Payload size excludes wrapper metadata. Round trips and reconstructed segments are byte-verified. Compression and pattern caching are disabled; SafeMode stays enabled.
- Encode uses a prebuilt wrapper. Raw decode produces an inert wrapper. Wrap + encode includes wrapper construction. Decode + unwrap includes approval/graph validation and DTO construction. Allocations are measured on the calling thread; codec rows also record operation counts and Gen0/Gen2 collections.
- Ratios of means use 20,000 paired bootstrap resamples, seed 7301, percentile 95% intervals. Diagnostic/aborted runs are excluded. Desktop applications remained running; builds/tests finished before timing.
- These shared optimizations benefit TCP too. They do not establish UDP superiority. Different DTOs, JSON fallback types, GC behavior and security configuration can change results.

## Frozen build identity

| Binary | SHA-256 |
|---|---|
| Previous core | `3FA84278B92335272661F3E74C8F01897F0F302AE5AA648EB30B0D1AD9AA49D2` |
| Updated core | `B137C0CBC9B5B2E23541DF7A1BC41B5A9C6509B7028A767988D5E5F2A77F36B5` |
| Shared harness | `BF40933E40E7A2151C34FF8C8EC6396077D0346110A5DC6C725E94D9EE589BE3` |

Unrelated repository edits arrived after the timing series. The final focused tests were repeated in an isolated directory with the measured DLL. The saved PDB checksum audit confirmed all eight codec/wrapper/segment integration source files matched it. These outcomes describe the frozen snapshot, not every later rebuild or published package.

## Reproduce

From the repository root, with .NET and Python installed. Source-build commands test the current checkout; retaining the old DLL is necessary for a controlled before/after comparison.

```powershell
$flags = @('-c','Release','-p:BuildPackage=false','-p:GeneratePackageOnBuild=false','-p:DocumentationFile=bin/Release/SocketJack.udp.xml','-p:NoWarn=1591')
dotnet test SocketJack.UdpReliable.Tests/SocketJack.UdpReliable.Tests.csproj @flags --filter 'FullyQualifiedName~CodecPipelineTests|FullyQualifiedName~OptimizationTests|FullyQualifiedName~CompactTests'
dotnet build tools/UdpReliable.Bench/UdpReliable.Bench.csproj @flags

# Single current-build comparison with JSON (no historical DLL required):
dotnet tools/UdpReliable.Bench/bin/Release/net8.0/UdpReliable.Bench.dll codec-focus artifacts/encoder-current.json 10

# Controlled comparison: prepare identical harness/dependency directories,
# differing only in the old/new SocketJack.dll. Local frozen copies use these paths:
./tools/UdpReliable.Bench/run-codec-focus.ps1 `
  -BaselineDirectory artifacts/binary-codec-focus/baseline-bin `
  -OptimizedDirectory artifacts/binary-codec-focus/optimized-bin `
  -OutputDirectory artifacts/binary-codec-focus/repeat-rounds -Runs 10
python tools/UdpReliable.Bench/analyze-codec-focus.py artifacts/binary-codec-focus/repeat-rounds

# Recompute statistics directly from the saved repository measurement data:
python tools/UdpReliable.Bench/analyze-codec-focus.py docs/udp-reliable-results/binary-codec-focus/verified-rounds
```

The frozen DLL directories are local build artifacts, not distributed binaries. The paired runner refuses to replace existing measurements; choose a new output directory for each run. No new benchmark was run merely to update this documentation.

## Evidence and source

- [All paired statistics](../udp-reliable-results/binary-codec-focus/verified-rounds/analysis.json); the same directory contains all twenty individual round JSON files (520 operation rows total).
- [Focused TRX](../udp-reliable-results/binary-codec-focus/codec-frozen.trx), [reliable TRX](../udp-reliable-results/binary-codec-focus/reliable-verified.trx), [security TRX](../udp-reliable-results/binary-codec-focus/security-verified.trx).
- [Machine details](../udp-reliable-results/binary-codec-focus/machine.json), [DLL hashes](../udp-reliable-results/binary-codec-focus/final-hashes.json), [compiled source audit](../udp-reliable-results/binary-codec-focus/source-audit.json).
- [Harness](../../tools/UdpReliable.Bench/CodecFocusBench.cs), [paired runner](../../tools/UdpReliable.Bench/run-codec-focus.ps1), [analysis](../../tools/UdpReliable.Bench/analyze-codec-focus.py), [regression tests](../../SocketJack.UdpReliable.Tests/CodecPipelineTests.cs).
