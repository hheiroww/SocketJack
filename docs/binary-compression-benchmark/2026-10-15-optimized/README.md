# First optimization iteration: adaptive binary compression

**Fresh random data approaches compression-off warm throughput. This iteration still has measurable overhead on some TCP and large-dictionary workloads.** Bandwidth savings remain substantial for repeated content. These complete intermediate results do not establish universal zero overhead; a further optimization iteration follows separately.

Measured October 15, 2026 on clean IPv4 loopback, with no imposed bandwidth limit, delay or loss. Ten paired main rounds covered 200 fresh server/client process trials and 600 phase measurements. Five paired supplementary rounds added 60 adaptive trials and 260 phase measurements. Five fanout pairs counted all four client links. Across all final series in this iteration, receivers verified **71,400 original payload objects byte for byte**.

Host: Intel Xeon W-2140B, 8 cores / 16 logical processors; Windows 10.0.22631; .NET 8.0.31; 15.68 GiB OS-visible physical RAM. Builds and correctness tests were kept out of the timing series. The desktop remained in normal use. See the [method and reproduction commands](../README.md).

## Main on/off comparison

Each cell is **off → on**. Speed is warmed useful-payload throughput with eight objects outstanding. Latency is warmed stop-and-wait application p95 RTT. Complete bytes include the handshake, cold learning, warm and latency transfers, dictionary updates/ACKs, framing, application receipts, reliable-UDP controls/retries, and interphase traffic in both directions. Counts exclude OS TCP acknowledgements and IP/TCP/UDP/link headers.

| Transport | Payload pattern | Warm MiB/s off → on | p95 RTT ms off → on | Complete bytes off → on | Saved |
|---|---|---:|---:|---:|---:|
| TCP | Repeated 1 KiB | 0.89 → 0.87 | 4.16 → 4.66 | 307,248 → 121,141 | 60.6% |
| TCP | Repeated 32 KiB | 28.41 → 25.68 | 4.31 → 4.66 | 8,433,968 → 655,539 | 92.2% |
| TCP | 75% stable 32 KiB | 28.50 → 23.83 | 4.31 → 4.52 | 8,433,968 → 2,575,915 | 69.5% |
| TCP | Fresh random 32 KiB | 29.28 → 28.42 | 4.31 → 4.44 | 8,433,968 → 8,434,018 | ~0% |
| TCP | Byte runs 32 KiB | 28.88 → 28.92 | 4.30 → 4.11 | 8,433,968 → 66,590 | 99.2% |
| Reliable UDP | Repeated 1 KiB | 0.84 → 0.81 | 4.15 → 4.35 | 316,832 → 130,756 | 58.7% |
| Reliable UDP | Repeated 32 KiB | 21.24 → 23.59 | 5.31 → 4.96 | 8,561,594 → 708,012 | 91.7% |
| Reliable UDP | 75% stable 32 KiB | 21.36 → 22.07 | 5.37 → 5.23 | 8,561,561 → 2,741,639 | 68.0% |
| Reliable UDP | Fresh random 32 KiB | 20.84 → 21.51 | 5.11 → 5.19 | 8,561,525 → 8,561,610 | ~0% |
| Reliable UDP | Byte runs 32 KiB | 21.40 → 26.37 | 5.33 → 4.29 | 8,561,548 → 76,174 | 99.1% |

Values are medians of ten runs per mode; byte medians are rounded to whole bytes in tables. Each 32 KiB connection delivers 8 MiB of original payload; each 1 KiB connection delivers 256 KiB. Warm speed excludes the cold phase, while complete totals include it. Random-data traffic is slightly larger: TCP adds exactly 50 negotiation bytes; UDP varies slightly with control traffic.

## Confidence and remaining overhead

Ratios use paired geometric means and deterministic 10,000-resample percentile 95% bootstrap intervals. Warm speed on/off above 1 means faster with compression; p95 RTT off/on above 1 means lower latency. An interval crossing 1 does not establish a directional change and is not proof of identical performance.

| Transport | Pattern | Warm speed on/off [95% CI] | p95 RTT off/on [95% CI] |
|---|---|---|---|
| TCP | Repeated 1 KiB | 0.967× [0.943, 0.989] | 0.975× [0.903, 1.047] |
| TCP | Repeated 32 KiB | 0.906× [0.880, 0.930] | 0.932× [0.886, 0.984] |
| TCP | 75% stable 32 KiB | 0.878× [0.791, 1.004] | 0.965× [0.857, 1.105] |
| TCP | Fresh random 32 KiB | 0.981× [0.958, 1.008] | 0.988× [0.958, 1.023] |
| TCP | Byte runs 32 KiB | 0.993× [0.966, 1.019] | 1.066× [1.014, 1.131] |
| Reliable UDP | Repeated 1 KiB | 1.041× [0.949, 1.228] | 1.045× [0.959, 1.198] |
| Reliable UDP | Repeated 32 KiB | 1.134× [1.099, 1.172] | 1.071× [1.019, 1.129] |
| Reliable UDP | 75% stable 32 KiB | 1.030× [0.955, 1.128] | 1.036× [0.964, 1.112] |
| Reliable UDP | Fresh random 32 KiB | 1.006× [0.969, 1.045] | 0.938× [0.840, 1.022] |
| Reliable UDP | Byte runs 32 KiB | 1.233× [1.198, 1.265] | 1.257× [1.197, 1.324] |

Fresh-random warm-throughput intervals cross 1 on both transports. TCP repeated 32 KiB remains about 9.4% slower by the paired estimate; repeated 1 KiB remains about 3.3% slower. TCP delta data has a lower median and a wide interval crossing 1. Reliable-UDP repeated 32 KiB and byte runs have intervals entirely above 1. Repeated TCP p95 RTT remains worse; repeated reliable-UDP and run-data p95 RTT improve.

Cold behavior still matters. Fresh-random TCP cold speed was **23.68 → 21.41 MiB/s**, paired **0.912× [0.864, 0.974]**. Reliable-UDP cold speed was **18.55 → 17.80 MiB/s**, with its interval crossing 1. Cold phases include first-use JIT and initial learning probes. Backoff removes most ongoing encoding work without removing every first-connection cost.

Each 256-object random-data connection made only **9 full encoding attempts**: 6 during cold 64, 2 during warm 128, and 1 during latency 64. All 256 objects were sent as original frames, and the interval reached 64. Warm approximate client-process allocations were TCP **13,520,348 → 13,708,188 bytes** and reliable UDP **10,454,860 → 10,647,108 bytes**. These include the proxy and measure allocated volume, not retained memory.

## Comparison with the previous implementation

These are sequential sessions, so absolute speed changes can reflect baseline or host drift. Each session keeps its own paired on/off comparison. [comparison.md](comparison.md) includes all original workloads and lifetime byte totals.

| Transport / payload | Previous enabled MiB/s | Optimized enabled MiB/s | Previous paired on/off | Optimized paired on/off |
|---|---:|---:|---:|---:|
| TCP, random-32k | 12.50 | 28.42 | 0.484× | 0.981× |
| TCP, repeated-32k | 12.64 | 25.68 | 0.496× | 0.906× |
| Reliable UDP, random-32k | 11.20 | 21.51 | 0.653× | 1.006× |
| Reliable UDP, repeated-32k | 12.37 | 23.59 | 0.711× | 1.134× |

## Dynamic traffic and recovery

Five paired supplementary rounds changed stream behavior without reconnecting or clearing the cache. Each phase below sends 64 objects; the first five use window eight and the final latency phase uses stop-and-wait. State counters are cumulative after the phase and drain. Attempts and bypasses overlap when an attempted encoding falls back to the original frame.

| Transport | Phase | MiB/s off → on | Phase bytes off → on | Definitions on | Encoding attempts on | Probe interval on |
|---|---|---:|---:|---:|---:|---:|
| TCP | random | 24.13 → 22.28 | 2,105,920 → 2,105,920 | 0 | 6 | 64 |
| TCP | repeat | 26.77 → 20.60 | 2,106,048 → 383,654 | 98 | 66 | 1 |
| TCP | churn | 28.88 → 26.92 | 2,106,048 → 2,106,048 | 98 | 72 | 64 |
| TCP | return | 28.80 → 26.69 | 2,106,048 → 97,408 | 98 | 136 | 1 |
| TCP | runs | 29.85 → 28.78 | 2,106,048 → 16,965 | 99 | 200 | 1 |
| TCP | latency | 8.79 → 8.87 | 2,106,048 → 14,848 | 99 | 264 | 1 |
| Reliable UDP | random | 19.23 → 18.41 | 2,136,549 → 2,136,549 | 0 | 6 | 64 |
| Reliable UDP | repeat | 23.48 → 19.60 | 2,136,811 → 391,695 | 98 | 66 | 1 |
| Reliable UDP | churn | 22.60 → 21.19 | 2,138,921 → 2,138,945 | 98 | 72 | 64 |
| Reliable UDP | return | 22.94 → 24.75 | 2,138,921 → 102,761 | 98 | 136 | 1 |
| Reliable UDP | runs | 23.21 → 27.75 | 2,138,981 → 20,650 | 99 | 200 | 1 |
| Reliable UDP | latency | 7.53 → 8.64 | 2,138,921 → 17,257 | 99 | 264 | 1 |

Random traffic backs off after six attempts. Repeated traffic wakes encoding and learns 98 definitions. After 64 unrelated churn objects, the original data reuses those definitions without a ten-object cutoff: return-phase traffic falls **95.4% on TCP** and **95.2% on reliable UDP**. Later byte runs also compress. This proves recovery and retention. Five-pair timing intervals are wider, and some supplementary phases still show overhead; [adaptive-analysis.md](adaptive-analysis.md) has every interval.

## Recurrence beyond ten objects

`spaced-32k` cycles 32 different high-entropy payloads: each returns only after 31 unrelated objects. Warm 128 is still learning and publishing a large dictionary. Trained bulk 64 follows it, then stop-and-wait latency 64. All 320 objects and early registration costs remain in complete totals.

| Transport | Phase | MiB/s off → on | Phase bytes off → on | Definitions on | Speed on/off [95% CI] |
|---|---|---:|---:|---:|---|
| TCP | cold | 23.49 → 18.04 | 2,105,920 → 2,176,220 | 213 | 0.757× [0.733, 0.780] |
| TCP | warm | 27.48 → 15.74 | 4,212,096 → 3,567,504 | 3,323 | 0.579× [0.572, 0.587] |
| TCP | trained | 29.82 → 21.08 | 2,106,048 → 583,104 | 3,323 | 0.701× [0.673, 0.728] |
| TCP | latency | 8.66 → 7.40 | 2,106,048 → 583,104 | 3,323 | 0.855× [0.838, 0.876] |
| Reliable UDP | cold | 18.17 → 15.17 | 2,136,549 → 2,206,620 | 213 | 0.830× [0.809, 0.855] |
| Reliable UDP | warm | 21.51 → 14.40 | 4,275,610 → 3,503,317 | 3,323 | 0.679× [0.663, 0.693] |
| Reliable UDP | trained | 22.58 → 19.71 | 2,138,921 → 625,263 | 3,323 | 0.863× [0.847, 0.882] |
| Reliable UDP | latency | 7.08 → 6.80 | 2,138,981 → 625,323 | 3,323 | 0.966× [0.945, 0.987] |

The larger pool demonstrates adaptive retention and a remaining performance limit: trained bulk stays about **30% slower on TCP** and **14% slower on reliable UDP** by paired estimates, despite **72.3% fewer TCP phase bytes** and **70.8% fewer reliable-UDP phase bytes**. Growing and searching a dictionary are not free, and byte savings do not automatically repay CPU cost on localhost.

| Transport | Cumulative objects | Complete bytes off → on |
|---|---:|---:|
| TCP | 64 | 2,115,824 → 2,186,174 |
| TCP | 192 | 6,327,920 → 5,753,678 |
| TCP | 256 | 8,433,968 → 6,336,782 |
| TCP | 320 | 10,540,016 → 6,919,886 |
| Reliable UDP | 64 | 2,147,018 → 2,217,139 |
| Reliable UDP | 192 | 6,422,628 → 5,720,433 |
| Reliable UDP | 256 | 8,561,549 → 6,333,717 |
| Reliable UDP | 320 | 10,700,530 → 6,959,040 |

Registration makes the first 64-object prefix larger with compression on. Its byte cost is repaid by the 192-object snapshot on both transports; snapshots cannot identify the exact crossing object. Elapsed-time cost differs from byte break-even.

## Mostly random data with a small stable region

`marginal-32k` keeps only 512 high-entropy bytes stable near a scout position, leaving 98.4% fresh random. It makes **11 full attempts in 256 objects**, reaches interval 64 and retains one definition. A matching small field no longer triggers a full encoder pass for every object.

| Transport | Warm MiB/s off → on | Warm speed on/off [95% CI] | Complete bytes off → on |
|---|---:|---|---:|
| TCP | 29.77 → 28.85 | 0.977× [0.945, 1.016] | 8,433,968 → 8,434,291 |
| Reliable UDP | 22.29 → 20.97 | 0.932× [0.890, 0.975] | 8,561,526 → 8,561,926 |

The TCP warm interval crosses 1. Reliable UDP remains about 6.8% slower in this supplementary case. Total traffic is slightly larger because the small definition and negotiation do not repay their cost in this run.

## Complete supplementary connection cost

| Transport | Workload | Original payload bytes | Complete bytes off → on | Saved |
|---|---|---:|---:|---:|
| TCP | adaptive-32k | 12,582,912 | 12,646,064 → 4,734,797 | 62.56% |
| TCP | spaced-32k | 10,485,760 | 10,540,016 → 6,919,886 | 34.35% |
| TCP | marginal-32k | 8,388,608 | 8,433,968 → 8,434,291 | -0.004% |
| Reliable UDP | adaptive-32k | 12,582,912 | 12,839,596 → 4,818,400 | 62.47% |
| Reliable UDP | spaced-32k | 10,485,760 | 10,700,530 → 6,959,040 | 34.97% |
| Reliable UDP | marginal-32k | 8,388,608 | 8,561,526 → 8,561,926 | -0.005% |

## Dictionary propagation to idle clients

Five alternating TCP pairs sent 100 repeated 32 KiB uploads from one active client while three remained idle. All four links had separate counting proxies. Timing is auxiliary because these fanout endpoints share one process.

| Metric | Off | On |
|---|---:|---:|
| Complete bytes, including all four handshakes | 3,331,148 | 395,706 |
| Bytes after setup, including cold learning | 3,291,372 | 355,730 |
| Server-to-client bytes after setup | 6,836 | 109,926 |
| Idle-client update/control bytes after setup | 0 | 105,498 |
| Definitions retained by each idle client | 0 | 98 |

Complete fanout traffic fell **88.1%**. The enabled total already includes **105,498 bytes** across the three idle links: 35,166 bytes each. Dictionary propagation was measured and paid for.

## Implementation under test

Compression remains enabled by default through `BinarySerializer.EnableCompression`. Its wire format and independent inline definitions remain compatible with the original implementation. Optimization changes the work performed before choosing a compressed or original frame:

- Objects below 256 serialized bytes bypass network compression setup.
- A persistent bounded dictionary index replaces rebuilding and rehashing the entire dictionary for each object. Faster exact-byte comparisons, a smaller LZ lookup table, and bounded miss skipping reduce work during an encoding attempt.
- Eight distributed samples scout for recurrence. Unsuccessful deep attempts back off exponentially to one every 64 eligible objects. A small stable field cannot repeatedly wake full encoding on otherwise random data.
- A new recurrence signal gets an immediate confirmation opportunity, and newly acknowledged definitions wake one probe so references can repay their cost after inline registration is no longer needed. Meaningful savings of at least roughly 12.5% keep active encoding running. Otherwise periodic probes and cheap scouting continue, allowing traffic changes to wake learning again. A frame is still emitted only when its complete encoded representation, including any inline definitions, is smaller than the original.
- A useful existing dictionary continues learning newly stable regions. Stronger recurrence triggers two confirmation passes, and profitable dictionary streams receive occasional background confirmation passes. This avoids freezing the learned pattern merely because an unchanged prefix already saves bytes.
- Candidate byte segments are retained by inactivity and memory pressure rather than the previous ten-object ring. Unpromoted candidates expire after two minutes of inactivity when learning next sweeps them; the candidate pool uses at most half the per-cache budget. Acknowledged definitions remain immutable for the connection, preserving references held by peers.

The existing 4 MiB per-cache budget and process-wide retained-state admission limit of at most one sixteenth of physical RAM remain in force. Dictionary index, candidate metadata, scout arrays and acknowledged peer shortcuts are included in admission accounting. Retained state differs from transient codec buffers, network queues and total application memory.

## Reading the results

The new paired on/off comparison is the primary evidence for current processing overhead. The previous and optimized experiments are sequential sessions: absolute speed differences can also reflect host activity or baseline drift. [The comparison helper](../../../tools/BinaryCompression.Bench/compare.py) retains each session's own paired estimates and confidence intervals rather than pairing unrelated old/new rounds.

Warm speed uses eight outstanding payloads, while latency uses warmed stop-and-wait application RTT. Complete connection byte totals include setup, cold learning, every measured phase, dictionary updates and acknowledgements, and interphase traffic. Neither socket counts nor loopback measurements establish WAN or link-limited performance. Separate fanout totals include all four client links and updates received by three otherwise idle clients.

The supplementary adaptive series uses five paired rounds. `adaptive-32k` changes from random data to repeated data, sends 64 unrelated objects, returns to the original, and then switches to byte runs. `spaced-32k` cycles 32 unrelated high-entropy payloads, proving recurrence farther apart than ten objects; its warm phase can still include dictionary growth, so an additional 64-object `trained` bulk phase precedes stop-and-wait latency. All 320 objects, including early registrations, remain in its lifetime totals. Cold registration and promotion can temporarily reduce throughput in this larger candidate pool, even when later recurrence repays their byte cost. `marginal-32k` leaves 98.4% of each object random and keeps a small scout-visible region stable, checking bounded work for low-value recurrence. Candidate counts and encoding attempts are cumulative end-of-phase diagnostics; allocated-byte figures cover only the client process and its counting proxy during timing.

## Website compatibility


Browser dictionaries require explicit permission for the signed-in account on that browser, a browser persistent-storage grant, and working IndexedDB. Account/application/endpoint/version partitions, fresh connection confirmation, committed-storage-before-ACK, and safe channel closure on revocation or storage failure protect decoding and account separation. Denied or unavailable storage leaves compression disabled.

The static deployment backed up and hash-verified 15 assets without restarting Portal. Five representative public script/style assets matched source hashes, the module had the correct JavaScript MIME type, and all five hosted services remained online. A real signed-in Account page showed the new checkbox unchecked with its normal-format fallback message, and no browser console errors. No real user permission was changed. The public anonymous Portal WebSocket still returned JSON text for session and services requests.


!Verified live account setting; permission remains off

## Validation and reproducibility

- .NET correctness runs passed **224 tests**, with **one TypeScript-compiler-dependent test skipped** because the compiler was unavailable. Main: 179 passed, one skipped; reliable UDP: 45 passed. Main log, reliable-UDP log, TRX evidence.
- Release WPF build passed with one pre-existing unused-field warning for `DataServer._persistenceDisposed`. Build log.
- Both modes, both benchmark programs, core Release output and both test copies used core SHA-256 **`0CAD85FC066C48873CB9D9DBA94989978321E2F133BBA2F30A67EB64BAE603F8`**. DLL freeze.
- Twenty-five relevant source files were frozen before timing. Source manifest.
- Main: [raw measurements](results.json), [statistics](analysis.md), [machine-readable analysis](analysis.json), run log.
- Adaptive: [raw measurements](adaptive.json), [statistics](adaptive-analysis.md), [machine-readable analysis](adaptive-analysis.json), run log.
- Previous versus optimized: [tables](comparison.md), [machine-readable data](comparison.json).
- Fanout: [raw measurements](fanout.json), run log.
- `initial-diagnostic.json`, `pilot.json`, `adaptive-pilot.json`, and their logs are excluded preliminary diagnostics. Only final `results.json`, `adaptive.json`, and `fanout.json` contribute to this iteration's report.

