# TCP compression optimization and static HTTP caching

This report measures the final Release DLL with `BinarySerializer.EnableCompression` on and off, including cold connections, partially changing objects, and longer transfers. Byte savings and transfer speed are reported separately. All controls and dictionary updates are counted. Timing does not imply that compression is free or that every workload becomes faster.

Core SHA-256: **`E139DEE103A085F3E3FB028C467DC758F1DDA6552DFE7FD4FCB568205FFF9EEF`**. The main series has ten paired rounds; the sustained series has six; adaptive and shared-fanout series have five each. All 131,688 delivered objects were byte-checked. Separate-process endpoints generate their fixtures independently; the fanout experiment shares one process and one expected fixture. All final series use this same DLL. The previous intermediate run is retained separately and is never pooled into these statistics.

## Implementation

- Cold, unproven random traffic uses bounded sampled hashes. Periodic rotating grids check between the fixed samples; exact candidate learning starts only after evidence of recurrence. The fresh-random test sends 256 objects with zero encoding attempts, zero exact learning passes and no retained payload candidates.
- Profitable, verified encoding plans skip the local repetition scan. Every referenced span still receives a byte comparison; changed bytes remain literals. Plans remain bounded and are invalidated when the dictionary changes. Periodic exact learning continues on useful streams so newly stable fields can join the dictionary.
- Stream dictionary updates use bounded 16 KiB batches, reducing the number of update frames and acknowledgements during training. Ordinary UDP retains its datagram bound. A peer supplying an immutable definition is already known to own it and can receive a referenced reply immediately.
- Sample hashing, dictionary-ID reads, and cache lookup are cheaper. Chunk boundaries favor larger definitions within the unchanged 64–1024-byte wire limits. Dictionary-only frames can deserialize directly from literal and immutable dictionary slices, avoiding a temporary expanded-message buffer. Local LZ, inline definitions and larger segment layouts retain the full checked decoder fallback.
- SB1 and SBC v1 wire formats, negotiation, whitelist/authorization checks, private/shared scope, browser consent and the process-wide retained-state ceiling of physical RAM / 16 remain in effect. Each native repository is additionally capped at 4 MiB. Compression is default-on only for the custom `BinarySerializer` and can be disabled before connecting.

## Short-connection warm throughput

Each connection sends 64 cold objects, 128 warm objects and 64 stop-and-wait latency objects. Bulk throughput includes validated receipt delivery with eight outstanding messages. In this table, p95 is the **loaded bulk RTT**, not the separate stop-and-wait result.

| Workload | MiB/s off → on | Paired on/off speed [95% CI] | p95 application RTT ms off → on |
|---|---:|---|---:|
| `cold-first-32k` | 28.36 → 27.41 | 0.972× [0.946, 0.999] | 5.909 → 5.748 |
| `delta-32k` | 28.72 → 27.79 | 0.967× [0.944, 0.989] | 5.409 → 5.532 |
| `delta-blocks-32k` | 28.63 → 25.76 | 0.898× [0.885, 0.914] | 5.807 → 5.706 |
| `random-32k` | 28.66 → 28.75 | 1.017× [0.990, 1.048] | 5.583 → 5.506 |
| `repeated-1k` | 0.89 → 0.93 | 1.040× [1.021, 1.058] | 6.547 → 5.003 |
| `repeated-32k` | 28.41 → 27.53 | 0.969× [0.940, 0.997] | 6.598 → 6.099 |
| `runs-32k` | 29.12 → 28.11 | 0.974× [0.954, 0.991] | 5.263 → 5.490 |

Paired geometric means and 10,000 paired bootstrap resamples produce the 95% intervals. An interval crossing 1 does not establish a speed difference. The medians in the speed columns are computed independently and therefore their quotient is not the paired geometric mean. Current warm classifications are:

- `cold-first-32k`: slower; 0.972× [0.946, 0.999].
- `delta-32k`: slower; 0.967× [0.944, 0.989].
- `delta-blocks-32k`: slower; 0.898× [0.885, 0.914].
- `random-32k`: no established directional difference; 1.017× [0.990, 1.048].
- `repeated-1k`: faster; 1.040× [1.021, 1.058].
- `repeated-32k`: slower; 0.969× [0.940, 0.997].
- `runs-32k`: slower; 0.974× [0.954, 0.991].

## Cold starts

Cold timings include first-use JIT, formatting, cache setup, dictionary training and actual receipt delivery. The first-object case sends no throwaway payload. Its following 63-object cold phase is separated by the normal quiet drain; it therefore differs from the uninterrupted 64-object delta cold phase. It uses exactly the same 75%-stable payload pattern.

| Workload | MiB/s off → on | Paired on/off speed [95% CI] | p95 application RTT ms off → on |
|---|---:|---|---:|
| `cold-first-32k` | 1.71 → 1.58 | 0.919× [0.895, 0.944] | 17.072 → 18.673 |

| Workload | MiB/s off → on | Paired on/off speed [95% CI] | p95 application RTT ms off → on |
|---|---:|---|---:|
| `cold-first-32k` | 26.01 → 20.65 | 0.803× [0.778, 0.831] | 6.588 → 8.261 |
| `delta-32k` | 23.46 → 19.62 | 0.806× [0.729, 0.860] | 9.824 → 9.213 |
| `delta-blocks-32k` | 23.48 → 19.45 | 0.834× [0.814, 0.858] | 10.057 → 8.715 |
| `random-32k` | 24.05 → 23.04 | 0.970× [0.920, 1.015] | 9.871 → 9.952 |
| `repeated-1k` | 0.78 → 0.70 | 0.892× [0.871, 0.912] | 9.449 → 9.239 |
| `repeated-32k` | 23.06 → 19.41 | 0.845× [0.831, 0.861] | 9.959 → 8.639 |
| `runs-32k` | 24.10 → 22.14 | 0.917× [0.885, 0.948] | 10.161 → 12.987 |

Cold costs are material and remain visible here and in complete connection totals. Applications with isolated short-lived requests can disable binary compression.

## Sustained throughput and latency

These trials use the same deterministic payload patterns but send 64 cold, **2,048 warm**, and **256 stop-and-wait latency** objects per fresh connection. They give the process and cache more time to settle. They are reported separately, rather than replacing short-connection or first-use results. Warm p95 below still uses a bulk window of eight.

| Workload | MiB/s off → on | Paired on/off speed [95% CI] | p95 application RTT ms off → on |
|---|---:|---|---:|
| `sustained-delta-32k` | 29.06 → 28.56 | 0.981× [0.961, 1.003] | 5.392 → 5.439 |
| `sustained-delta-blocks-32k` | 28.52 → 28.29 | 0.992× [0.977, 1.008] | 5.760 → 5.394 |
| `sustained-random-32k` | 29.11 → 29.11 | 0.998× [0.990, 1.007] | 5.453 → 5.371 |

Stop-and-wait p95 has one outstanding object:

| Workload | MiB/s off → on | Paired on/off speed [95% CI] | p95 application RTT ms off → on |
|---|---:|---|---:|
| `sustained-delta-32k` | 8.72 → 9.11 | 1.043× [1.031, 1.053] | 4.169 → 3.900 |
| `sustained-delta-blocks-32k` | 8.96 → 8.94 | 0.997× [0.977, 1.018] | 4.073 → 4.067 |
| `sustained-random-32k` | 8.85 → 8.87 | 1.005× [0.995, 1.015] | 4.079 → 4.006 |

Short-connection stop-and-wait results:

| Workload | MiB/s off → on | Paired on/off speed [95% CI] | p95 application RTT ms off → on |
|---|---:|---|---:|
| `cold-first-32k` | 8.15 → 8.16 | 1.001× [0.992, 1.010] | 4.498 → 4.354 |
| `delta-32k` | 8.28 → 8.16 | 0.995× [0.962, 1.032] | 4.253 → 4.474 |
| `delta-blocks-32k` | 8.20 → 8.01 | 0.966× [0.938, 0.987] | 4.429 → 4.587 |
| `random-32k` | 8.21 → 8.15 | 1.024× [0.987, 1.092] | 4.414 → 4.354 |
| `repeated-1k` | 0.26 → 0.26 | 0.976× [0.960, 0.990] | 4.404 → 4.587 |
| `repeated-32k` | 8.21 → 8.23 | 0.997× [0.983, 1.010] | 4.460 → 4.455 |
| `runs-32k` | 8.28 → 8.22 | 0.994× [0.977, 1.008] | 4.340 → 4.298 |

These are medians of per-trial tail latencies, not pooled message percentiles. With 64 samples, short-case p99 is effectively a maximum; complete p50/p95/p99 and confidence intervals are retained in the linked analyses.

## Total data, including updates

Totals count both directions exactly once, including setup, capability negotiation, dictionary definitions, acknowledgements, framing, application receipts, trailing controls and gaps between phase snapshots. They exclude OS TCP acknowledgements, IP/TCP/link headers, and the harness shutdown message. Retained binary definitions are native memory state; HTTP browser caching is a separate feature.

Short connections:

| Workload | Complete socket bytes off | Complete socket bytes on | Paired median reduction |
|---|---:|---:|---:|
| `cold-first-32k` | 8,433,968 | 2,560,614 | 69.639% |
| `delta-32k` | 8,433,968 | 2,560,614 | 69.639% |
| `delta-blocks-32k` | 8,433,968 | 5,047,066 | 40.158% |
| `random-32k` | 8,433,968 | 8,434,154 | -0.002% |
| `repeated-1k` | 307,248 | 112,600 | 63.352% |
| `repeated-32k` | 8,433,968 | 534,872 | 93.658% |
| `runs-32k` | 8,433,968 | 66,726 | 99.209% |

Sustained connections:

| Workload | Complete socket bytes off | Complete socket bytes on | Paired median reduction |
|---|---:|---:|---:|
| `sustained-delta-32k` | 77,933,552 | 21,843,174 | 71.972% |
| `sustained-delta-blocks-32k` | 77,933,552 | 39,727,808 | 49.023% |
| `sustained-random-32k` | 77,933,552 | 77,933,738 | -0.000% |

Fully random data does not shrink; the extra negotiation bytes remain in the total. Smaller byte totals alone do not establish a throughput or latency improvement on unconstrained loopback.

## Adaptive traffic and shared propagation

The adaptive stream changes from random data to repetition, random churn, an old pattern returning after 64 unrelated objects, and local runs. The spaced case cycles 32 different objects. The marginal case repeats only 512 bytes (1.6%) of otherwise changing random data. Private connection caches persist across phases. See [all adaptive phase timings and state](adaptive-analysis.md).

| Workload | Complete socket bytes off | Complete socket bytes on | Paired median reduction |
|---|---:|---:|---:|
| `adaptive-32k` | 12,646,064 | 4,661,907 | 63.136% |
| `marginal-32k` | 8,433,968 | 8,434,154 | -0.002% |
| `spaced-32k` | 10,540,016 | 5,450,036 | 48.292% |

The separate shared-repository experiment uses one active uploader and three idle clients. It counts all four links, including the idle clients' dictionary traffic. Median complete bytes are **3,331,148 off → 363,184 on**, a **89.10%** reduction. Idle-client data-phase traffic with compression has median **100,650 bytes**; their definition counts are recorded in [raw fanout data](fanout.json). Timing is auxiliary because the endpoints share one process. Independent website users continue to negotiate private repositories and do not receive this fanout.

## Earlier iteration

The [intermediate measurements](../2026-10-15-tcp-optimized/analysis.md) used DLL `C422ECED0F297DB562E4156F2147923117426FB85F5B9E44CDD188B2F0D433F9`. That iteration prompted the final cold-scout, plan and stream-batching changes. The descriptive paired-ratio comparison below does not pair trials across different versions or hold desktop background load constant. Absolute cross-run rates should not be attributed entirely to code changes.

| Warm workload | Intermediate on/off speed [95% CI] | Final on/off speed [95% CI] |
|---|---|---|
| `delta-32k` | 0.922× [0.851, 1.012] | 0.967× [0.944, 0.989] |
| `delta-blocks-32k` | 0.878× [0.833, 0.938] | 0.898× [0.885, 0.914] |
| `random-32k` | 0.998× [0.852, 1.141] | 1.017× [0.990, 1.048] |
| `repeated-32k` | 0.873× [0.666, 1.030] | 0.969× [0.940, 0.997] |

## HTTP caching

`HttpServer` and `MutableTcpServer.Http` now apply conditional caching only to physical `MapFile` and `MapDirectory` responses. GET/HEAD assets get a SHA-256 content ETag, `Last-Modified`, and `public, max-age=0, must-revalidate` (or private for authenticated/personal requests). A matching validator returns 304. Changed file bytes reset the ETag even with unchanged size and timestamps; deletion returns an uncached error.

Dynamic handlers, generated responses, templates and streaming routes use `no-store`. A `.js` URL does not make generated output static. Rendered `RouteFile`/`RouteString` content is recomputed on every request. Explicit `no-store` disables static caching too. Static validation reads the current file without a second retained RAM copy; this costs file I/O but avoids stale metadata-only caches. See [policy and scope](../../HTTP-caching.md). Seven new HTTP cases include real listener requests and hosted dispatch. This verifies library behavior; it is not a new production Portal deployment.

## Method and verification

Each main/sustained/adaptive trial starts separate fresh server and client processes on IPv4 loopback with a transparent counting proxy in the client process. On/off order alternates and workload order rotates. Both modes use identical settings except `BinarySerializer.EnableCompression`. SafeMode and typed admission are enabled; caches are private on both ends. Outer compression, legacy pattern caching, P2P, chunking, FPS and bandwidth limits are off. Fixtures are prepared outside timers and independently verified by the receiver; duplicate or missing delivery fails the run. Both sends and typed receipts must complete. Byte counters drain for 160 ms of quiet after the timer; the drain is excluded from throughput and included in bytes.

The desktop remained in normal use. Builds and correctness tests ran before timing. All samples, including scheduling outliers, are retained. Client CPU includes the proxy and excludes the server. Allocation records approximate process-wide allocated volume, rather than retained cache memory. This is unconstrained loopback without artificial bandwidth, latency or loss; it does not predict Internet performance or guarantee that compression never adds CPU work.

- Main correctness: **219 passed, one TypeScript-compiler-dependent case skipped**. Reliable UDP: **45 passed**, excluding the large-file category. Total **264 passed, one skipped**. Main log, reliable log, TRX.
- WPF Release builds pass for net8.0-windows7.0 and net10.0-windows7.0, with one existing unused-field warning. Build log.
- **40 source hashes and 5 core DLL copies verified unchanged** after timing; every final series records the same core hash. Source freeze, DLL freeze, [measured code and binaries](measured-source/), verification.
- Main: [raw](results.json), [analysis](analysis.md), [machine-readable statistics](analysis.json), log.
- Sustained: [raw](sustained.json), [analysis](sustained-analysis.md), [statistics](sustained-analysis.json), log.
- Adaptive: [raw](adaptive.json), [analysis](adaptive-analysis.md), [statistics](adaptive-analysis.json), log.
- Shared fanout: [raw](fanout.json), log.

Reproduce from the repository root after building the two benchmark projects in Release (run each command sequentially):

```powershell
dotnet tools/BinaryCompression.Bench/bin/Release/net8.0/BinaryCompression.Bench.dll run docs/binary-compression-benchmark/2026-10-15-tcp-final/results.json 10 tcp repeated-1k,repeated-32k,delta-32k,delta-blocks-32k,random-32k,runs-32k,cold-first-32k
dotnet tools/BinaryCompression.Bench/bin/Release/net8.0/BinaryCompression.Bench.dll run docs/binary-compression-benchmark/2026-10-15-tcp-final/sustained.json 6 tcp sustained-delta-32k,sustained-delta-blocks-32k,sustained-random-32k
dotnet tools/BinaryCompression.Bench/bin/Release/net8.0/BinaryCompression.Bench.dll run docs/binary-compression-benchmark/2026-10-15-tcp-final/adaptive.json 5 tcp adaptive-32k,spaced-32k,marginal-32k
dotnet tools/BinaryCompression.FanoutBench/bin/Release/net8.0/BinaryCompression.FanoutBench.dll docs/binary-compression-benchmark/2026-10-15-tcp-final/fanout.json 5
python tools/BinaryCompression.Bench/analyze.py docs/binary-compression-benchmark/2026-10-15-tcp-final/results.json docs/binary-compression-benchmark/2026-10-15-tcp-final/analysis
python tools/BinaryCompression.Bench/analyze.py docs/binary-compression-benchmark/2026-10-15-tcp-final/sustained.json docs/binary-compression-benchmark/2026-10-15-tcp-final/sustained-analysis
python tools/BinaryCompression.Bench/analyze.py docs/binary-compression-benchmark/2026-10-15-tcp-final/adaptive.json docs/binary-compression-benchmark/2026-10-15-tcp-final/adaptive-analysis
```

The prior TCP/reliable-UDP results remain [here](../2026-10-15-final/README.md). Those earlier bytes and timings are not final-DLL measurements.
