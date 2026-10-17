# TCP compression optimization and static HTTP caching

This report measures the final Release DLL with `BinarySerializer.EnableCompression` on and off, including cold connections, partially changing objects, and longer transfers. Byte savings and transfer speed are reported separately. All controls and dictionary updates are counted. Timing does not imply that compression is free or that every workload becomes faster.

Core SHA-256: **`0EA8A20BECCA03F886933EEE3330F5C4CE21A7B7E89481D68280D0698F46D5AB`**. The main series has six paired rounds; the sustained series has six; adaptive and shared-fanout series have five each. All 117,352 delivered objects were byte-checked. Separate-process endpoints generate their fixtures independently; the fanout experiment shares one process and one expected fixture. All final series use this same DLL. The previous intermediate run is retained separately and is never pooled into these statistics.

The final DLL still adds measurable CPU work. The 75%-stable sustained case sends **72.1% fewer complete socket bytes**, while its paired throughput is **3.3% slower** (0.967× [0.948, 0.985]). Short distributed-change throughput is **4.9% slower**. Sustained random throughput has no established directional difference (0.999× [0.984, 1.021]), and random objects enter neither full encoding nor exact learning. Cold training still has costs; these results do **not** meet a universal no-slowdown guarantee.

For the common 32 KiB delta warm fixture, retained client cache state fell from **499,652 to 204,484 bytes** across versions, because direct plans avoid the general dictionary index. This is repository memory, not total process allocation. The earlier version has its own saved data and hash; cross-version timing comparisons below are descriptive.

## Implementation

- Cold, unproven random traffic uses bounded sampled hashes. Periodic rotating grids check between the fixed samples; exact candidate learning starts only after evidence of recurrence. The fresh-random test sends 256 objects with zero encoding attempts, zero exact learning passes and no retained payload candidates.
- Profitable, verified encoding plans skip the local repetition scan. Every referenced span still receives a byte comparison; changed bytes remain literals. Plans remain bounded and are invalidated when the dictionary changes. Periodic exact learning continues on useful streams so newly stable fields can join the dictionary.
- Exact learning now records a plan at the positions of the confirmed chunks. The first referenced object can use it without building the general dictionary index or rescanning the object. Recent plans also try their original positions and a small whole-message length shift, allowing header growth/shrinkage and appended fields to reuse verified bytes. Both alternatives verify every reference; the shorter valid frame wins. A bounded plan is retained for the new layout. Unrelated layouts still use the general checked encoder.
- Stream dictionary updates use bounded 16 KiB batches, reducing the number of update frames and acknowledgements during training. Ordinary UDP retains its datagram bound. A peer supplying an immutable definition is already known to own it and can receive a referenced reply immediately.
- Sample hashing, dictionary-ID reads, and cache lookup are cheaper. Chunk boundaries favor larger definitions within the unchanged 64–1024-byte wire limits. Dictionary-only frames can deserialize directly from literal and immutable dictionary slices, avoiding a temporary expanded-message buffer. Local LZ, inline definitions and larger segment layouts retain the full checked decoder fallback.
- SB1 and SBC v1 wire formats, negotiation, whitelist/authorization checks, private/shared scope, browser consent and the process-wide retained-state ceiling of physical RAM / 16 remain in effect. Each native repository is additionally capped at 4 MiB. Compression is default-on only for the custom `BinarySerializer` and can be disabled before connecting.

## Short-connection warm throughput

Each connection sends 64 cold objects, 128 warm objects and 64 stop-and-wait latency objects. Bulk throughput includes validated receipt delivery with eight outstanding messages. In this table, p95 is the **loaded bulk RTT**, not the separate stop-and-wait result.

| Workload | MiB/s off → on | Paired on/off speed [95% CI] | p95 application RTT ms off → on |
|---|---:|---|---:|
| `cold-first-32k` | 27.42 → 27.85 | 1.003× [0.977, 1.031] | 7.516 → 5.559 |
| `delta-32k` | 28.10 → 28.06 | 0.963× [0.908, 1.009] | 5.352 → 5.095 |
| `delta-blocks-32k` | 29.26 → 27.98 | 0.951× [0.939, 0.962] | 5.290 → 5.829 |
| `random-32k` | 28.79 → 28.15 | 0.978× [0.953, 1.008] | 5.833 → 5.919 |
| `repeated-1k` | 0.90 → 0.88 | 0.991× [0.895, 1.116] | 7.054 → 6.570 |
| `repeated-32k` | 28.31 → 26.90 | 0.956× [0.935, 0.979] | 6.859 → 6.345 |
| `runs-32k` | 28.65 → 28.48 | 1.002× [0.966, 1.049] | 5.970 → 5.507 |

Paired geometric means and 10,000 paired bootstrap resamples produce the 95% intervals. An interval crossing 1 does not establish a speed difference. The medians in the speed columns are computed independently and therefore their quotient is not the paired geometric mean. Current warm classifications are:

- `cold-first-32k`: no established directional difference; 1.003× [0.977, 1.031].
- `delta-32k`: no established directional difference; 0.963× [0.908, 1.009].
- `delta-blocks-32k`: slower; 0.951× [0.939, 0.962].
- `random-32k`: no established directional difference; 0.978× [0.953, 1.008].
- `repeated-1k`: no established directional difference; 0.991× [0.895, 1.116].
- `repeated-32k`: slower; 0.956× [0.935, 0.979].
- `runs-32k`: no established directional difference; 1.002× [0.966, 1.049].

## Cold starts

Cold timings include first-use JIT, formatting, cache setup, dictionary training and actual receipt delivery. The first-object case sends no throwaway payload. Its following 63-object cold phase is separated by the normal quiet drain; it therefore differs from the uninterrupted 64-object delta cold phase. It uses exactly the same 75%-stable payload pattern.

| Workload | MiB/s off → on | Paired on/off speed [95% CI] | p95 application RTT ms off → on |
|---|---:|---|---:|
| `cold-first-32k` | 1.74 → 1.57 | 0.888× [0.832, 0.930] | 16.865 → 18.818 |

| Workload | MiB/s off → on | Paired on/off speed [95% CI] | p95 application RTT ms off → on |
|---|---:|---|---:|
| `cold-first-32k` | 25.65 → 21.81 | 0.864× [0.829, 0.902] | 6.273 → 7.327 |
| `delta-32k` | 23.75 → 20.24 | 0.808× [0.720, 0.864] | 10.269 → 9.703 |
| `delta-blocks-32k` | 24.33 → 19.91 | 0.818× [0.798, 0.839] | 10.056 → 10.179 |
| `random-32k` | 23.94 → 22.68 | 0.935× [0.902, 0.970] | 9.791 → 9.946 |
| `repeated-1k` | 0.79 → 0.69 | 0.887× [0.861, 0.909] | 9.955 → 9.458 |
| `repeated-32k` | 23.15 → 19.83 | 0.841× [0.790, 0.885] | 9.659 → 9.866 |
| `runs-32k` | 23.01 → 21.97 | 0.953× [0.914, 0.995] | 10.186 → 13.409 |

Cold costs are material and remain visible here and in complete connection totals. Applications with isolated short-lived requests can disable binary compression.

## Sustained throughput and latency

These trials use the same deterministic payload patterns but send 64 cold, **2,048 warm**, and **256 stop-and-wait latency** objects per fresh connection. They give the process and cache more time to settle. They are reported separately, rather than replacing short-connection or first-use results. Warm p95 below still uses a bulk window of eight.

| Workload | MiB/s off → on | Paired on/off speed [95% CI] | p95 application RTT ms off → on |
|---|---:|---|---:|
| `sustained-delta-32k` | 29.44 → 28.46 | 0.967× [0.948, 0.985] | 5.239 → 5.586 |
| `sustained-delta-blocks-32k` | 29.09 → 28.34 | 0.978× [0.964, 1.001] | 5.610 → 5.658 |
| `sustained-random-32k` | 29.37 → 29.14 | 0.999× [0.984, 1.021] | 5.427 → 5.323 |

Stop-and-wait p95 has one outstanding object:

| Workload | MiB/s off → on | Paired on/off speed [95% CI] | p95 application RTT ms off → on |
|---|---:|---|---:|
| `sustained-delta-32k` | 8.92 → 8.88 | 0.987× [0.949, 1.021] | 3.992 → 4.133 |
| `sustained-delta-blocks-32k` | 8.78 → 8.84 | 0.998× [0.976, 1.019] | 4.109 → 4.268 |
| `sustained-random-32k` | 8.99 → 8.96 | 0.987× [0.951, 1.013] | 4.073 → 4.058 |

Short-connection stop-and-wait results:

| Workload | MiB/s off → on | Paired on/off speed [95% CI] | p95 application RTT ms off → on |
|---|---:|---|---:|
| `cold-first-32k` | 8.11 → 7.90 | 0.969× [0.942, 0.991] | 4.341 → 4.433 |
| `delta-32k` | 8.31 → 8.28 | 0.994× [0.971, 1.019] | 4.275 → 4.327 |
| `delta-blocks-32k` | 8.26 → 8.08 | 0.970× [0.949, 0.989] | 4.443 → 4.526 |
| `random-32k` | 8.28 → 8.01 | 0.969× [0.950, 0.992] | 4.335 → 4.651 |
| `repeated-1k` | 0.26 → 0.25 | 0.944× [0.894, 0.991] | 4.214 → 5.349 |
| `repeated-32k` | 8.23 → 8.17 | 0.992× [0.983, 1.002] | 4.486 → 4.366 |
| `runs-32k` | 8.25 → 8.28 | 1.012× [1.000, 1.032] | 4.424 → 4.376 |

These are medians of per-trial tail latencies, not pooled message percentiles. With 64 samples, short-case p99 is effectively a maximum; complete p50/p95/p99 and confidence intervals are retained in the linked analyses.

## Total data, including updates

Totals count both directions exactly once, including setup, capability negotiation, dictionary definitions, acknowledgements, framing, application receipts, trailing controls and gaps between phase snapshots. They exclude OS TCP acknowledgements, IP/TCP/link headers, and the harness shutdown message. Retained binary definitions are native memory state; HTTP browser caching is a separate feature.

Short connections:

| Workload | Complete socket bytes off | Complete socket bytes on | Paired median reduction |
|---|---:|---:|---:|
| `cold-first-32k` | 8,433,968 | 2,554,470 | 69.712% |
| `delta-32k` | 8,433,968 | 2,554,470 | 69.712% |
| `delta-blocks-32k` | 8,433,968 | 4,612,690 | 45.308% |
| `random-32k` | 8,433,968 | 8,434,154 | -0.002% |
| `repeated-1k` | 307,248 | 111,704 | 63.644% |
| `repeated-32k` | 8,433,968 | 528,728 | 93.731% |
| `runs-32k` | 8,433,968 | 66,726 | 99.209% |

Sustained connections:

| Workload | Complete socket bytes off | Complete socket bytes on | Paired median reduction |
|---|---:|---:|---:|
| `sustained-delta-32k` | 77,933,552 | 21,769,446 | 72.067% |
| `sustained-delta-blocks-32k` | 77,933,552 | 40,930,009 | 47.481% |
| `sustained-random-32k` | 77,933,552 | 77,933,738 | -0.000239% |

Fully random data does not shrink; the extra negotiation bytes remain in the total. Smaller byte totals alone do not establish a throughput or latency improvement on unconstrained loopback.

## Adaptive traffic and shared propagation

The adaptive stream changes from random data to repetition, random churn, an old pattern returning after 64 unrelated objects, and local runs. The spaced case cycles 32 different objects. The marginal case repeats only 512 bytes (1.6%) of otherwise changing random data. Private connection caches persist across phases. See [all adaptive phase timings and state](adaptive-analysis.md).

| Workload | Complete socket bytes off | Complete socket bytes on | Paired median reduction |
|---|---:|---:|---:|
| `adaptive-32k` | 12,646,064 | 4,822,438 | 61.866% |
| `marginal-32k` | 8,433,968 | 8,434,154 | -0.002% |
| `spaced-32k` | 10,540,016 | 5,448,735 | 48.304% |

The separate shared-repository experiment uses one active uploader and three idle clients. It counts all four links, including the idle clients' dictionary traffic. Median complete bytes are **3,331,148 off → 360,656 on**, a **89.17%** reduction. Idle-client data-phase traffic with compression has median **100,650 bytes**; their definition counts are recorded in [raw fanout data](fanout.json). Timing is auxiliary because the endpoints share one process. Independent website users continue to negotiate private repositories and do not receive this fanout.

## Earlier iteration

The [previous measurements](../2026-10-15-tcp-final/README.md) used DLL `E139DEE103A085F3E3FB028C467DC758F1DDA6552DFE7FD4FCB568205FFF9EEF`. Those results prompted direct plan creation during learning and verified reuse across small length changes. The earlier main matrix had ten paired rounds; the current main matrix has six, with the same fixtures and phase lengths. Its confidence intervals therefore have fewer paired samples. The descriptive comparison below does not pair trials across different versions or hold desktop background load constant. Absolute cross-run rates should not be attributed entirely to code changes.

| Warm workload | Intermediate on/off speed [95% CI] | Final on/off speed [95% CI] |
|---|---|---|
| `delta-32k` | 0.967× [0.944, 0.989] | 0.963× [0.908, 1.009] |
| `delta-blocks-32k` | 0.898× [0.885, 0.914] | 0.951× [0.939, 0.962] |
| `random-32k` | 1.017× [0.990, 1.048] | 0.978× [0.953, 1.008] |
| `repeated-32k` | 0.969× [0.940, 0.997] | 0.956× [0.935, 0.979] |

## HTTP caching

`HttpServer` and `MutableTcpServer.Http` now apply conditional caching only to physical `MapFile` and `MapDirectory` responses. GET/HEAD assets get a SHA-256 content ETag, `Last-Modified`, and `public, max-age=0, must-revalidate` (or private for authenticated/personal requests). A matching validator returns 304. Changed file bytes reset the ETag even with unchanged size and timestamps; deletion returns an uncached error.

Dynamic handlers, generated responses, templates and streaming routes use `no-store`. A `.js` URL does not make generated output static. Rendered `RouteFile`/`RouteString` content is recomputed on every request. Explicit `no-store` disables static caching too. Static validation reads the current file without a second retained RAM copy; this costs file I/O but avoids stale metadata-only caches. See [policy and scope](../../HTTP-caching.md). Seven new HTTP cases include real listener requests and hosted dispatch. This verifies library behavior; it is not a new production Portal deployment.

## Method and verification

Each main/sustained/adaptive trial starts separate fresh server and client processes on IPv4 loopback with a transparent counting proxy in the client process. On/off order alternates and workload order rotates. Both modes use identical settings except `BinarySerializer.EnableCompression`. SafeMode and typed admission are enabled; caches are private on both ends. Outer compression, legacy pattern caching, P2P, chunking, FPS and bandwidth limits are off. Fixtures are prepared outside timers and independently verified by the receiver; duplicate or missing delivery fails the run. Both sends and typed receipts must complete. Byte counters drain for 160 ms of quiet after the timer; the drain is excluded from throughput and included in bytes.

The desktop remained in normal use. Builds and correctness tests ran before timing. All samples, including scheduling outliers, are retained. Client CPU includes the proxy and excludes the server. Allocation records approximate process-wide allocated volume, rather than retained cache memory. This is unconstrained loopback without artificial bandwidth, latency or loss; it does not predict Internet performance or guarantee that compression never adds CPU work.

`planHits` counts successful plan encoding passes. A single object can count twice when a learning pass refines its first encoded frame; per-phase differences are therefore not unique message counts. Throughput and delivery counts use validated receipts and are independent of this diagnostic.

- Main correctness: **225 passed, one TypeScript-compiler-dependent case skipped**. Reliable UDP: **45 passed**, excluding the large-file category. Total **270 passed, one skipped**. Main log, reliable log, TRX.
- A JSON-path UDP reconnect test failed once in the first full reliable run. Its isolated rerun and the subsequent full 45-case run passed without source changes. The first attempt and isolated rerun remain available; retries are not added to the pass count.
- WPF Release builds pass for net8.0-windows7.0 and net10.0-windows7.0, with one existing unused-field warning. Build log.
- **40 source hashes, 5 core DLL copies and two benchmark DLLs verified unchanged** after timing; every final series records the same core hash. Source freeze, core DLL freeze, harness freeze, [measured code and binaries](measured-source/), verification.
- Main: [raw](results.json), [analysis](analysis.md), [machine-readable statistics](analysis.json), log.
- Sustained: [raw](sustained.json), [analysis](sustained-analysis.md), [statistics](sustained-analysis.json), log.
- Adaptive: [raw](adaptive.json), [analysis](adaptive-analysis.md), [statistics](adaptive-analysis.json), log.
- Shared fanout: [raw](fanout.json), log.

Reproduce from the repository root after building the two benchmark projects in Release (run each command sequentially):

```powershell
dotnet tools/BinaryCompression.Bench/bin/Release/net8.0/BinaryCompression.Bench.dll run docs/binary-compression-benchmark/2026-10-15-tcp-direct-plans/results.json 6 tcp repeated-1k,repeated-32k,delta-32k,delta-blocks-32k,random-32k,runs-32k,cold-first-32k
dotnet tools/BinaryCompression.Bench/bin/Release/net8.0/BinaryCompression.Bench.dll run docs/binary-compression-benchmark/2026-10-15-tcp-direct-plans/sustained.json 6 tcp sustained-delta-32k,sustained-delta-blocks-32k,sustained-random-32k
dotnet tools/BinaryCompression.Bench/bin/Release/net8.0/BinaryCompression.Bench.dll run docs/binary-compression-benchmark/2026-10-15-tcp-direct-plans/adaptive.json 5 tcp adaptive-32k,spaced-32k,marginal-32k
dotnet tools/BinaryCompression.FanoutBench/bin/Release/net8.0/BinaryCompression.FanoutBench.dll docs/binary-compression-benchmark/2026-10-15-tcp-direct-plans/fanout.json 5
python tools/BinaryCompression.Bench/analyze.py docs/binary-compression-benchmark/2026-10-15-tcp-direct-plans/results.json docs/binary-compression-benchmark/2026-10-15-tcp-direct-plans/analysis
python tools/BinaryCompression.Bench/analyze.py docs/binary-compression-benchmark/2026-10-15-tcp-direct-plans/sustained.json docs/binary-compression-benchmark/2026-10-15-tcp-direct-plans/sustained-analysis
python tools/BinaryCompression.Bench/analyze.py docs/binary-compression-benchmark/2026-10-15-tcp-direct-plans/adaptive.json docs/binary-compression-benchmark/2026-10-15-tcp-direct-plans/adaptive-analysis
```

The prior TCP/reliable-UDP results remain [here](../2026-10-15-final/README.md). Those earlier bytes and timings are not final-DLL measurements.
