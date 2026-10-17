# Adaptive binary compression: final network benchmarks

**Adaptive backoff and verified encoding plans reduce ongoing compression work substantially. Fresh-random warmed throughput is close to compression off in this experiment, with confidence intervals spanning no change. Some cold, TCP and large-dictionary costs remain; this is not a zero-overhead guarantee.**

The final October 15, 2026 series completed **200 main process-isolated trials, 60 supplementary adaptive trials and 10 shared-fanout trials**. Receivers byte-verified **71,400 original payload objects**. The main series contains 600 phase measurements; the adaptive series contains 260. Ten paired main rounds and five paired supplementary rounds alternate compression-off/on order.

Host: Intel Xeon W-2140B, 8 cores / 16 logical processors; Windows 10.0.22631; .NET 8.0.31; 15.68 GiB OS-visible RAM. All timing uses actual clean IPv4 loopback sockets, without artificial bandwidth, delay or loss. Builds and tests were kept outside the final timing series. The desktop remained in normal use, and all timing outliers were retained.

## Main comparison

Cells are **off → on**. Speed is warm useful-payload throughput with eight objects outstanding. Latency is warmed stop-and-wait application p95 RTT. Complete bytes include setup, cold learning, warm and latency transfers, definitions/ACKs, framing, application receipts, reliable-UDP controls/retries, and interphase traffic in both directions. OS TCP acknowledgements and IP/TCP/UDP/link headers are excluded.

Both endpoints use **private per-client dictionaries** in these main/adaptive pairs; shared propagation to idle clients is measured separately below. Only `EnableCompression` changes within each pair.

| Transport | Payload pattern | Warm MiB/s off → on | p95 RTT ms off → on | Complete bytes off → on | Saved |
|---|---|---:|---:|---:|---:|
| TCP | Repeated 1 KiB | 0.82 → 0.83 | 5.07 → 5.22 | 307,248 → 121,277 | 60.5% |
| TCP | Repeated 32 KiB | 26.24 → 25.18 | 4.67 → 5.27 | 8,433,968 → 659,192 | 92.2% |
| TCP | 75% stable 32 KiB | 28.04 → 25.80 | 4.55 → 4.74 | 8,433,968 → 2,576,051 | 69.5% |
| TCP | Fresh random 32 KiB | 27.98 → 27.17 | 4.43 → 4.53 | 8,433,968 → 8,434,154 | ~0% |
| TCP | Byte runs 32 KiB | 26.48 → 24.72 | 5.12 → 4.78 | 8,433,968 → 66,726 | 99.2% |
| Reliable UDP | Repeated 1 KiB | 0.79 → 0.73 | 4.76 → 4.63 | 316,832 → 130,892 | 58.7% |
| Reliable UDP | Repeated 32 KiB | 19.58 → 22.49 | 6.13 → 5.23 | 8,561,584 → 708,148 | 91.7% |
| Reliable UDP | 75% stable 32 KiB | 20.15 → 22.03 | 5.92 → 5.28 | 8,561,550 → 2,741,775 | 68.0% |
| Reliable UDP | Fresh random 32 KiB | 19.95 → 20.43 | 5.96 → 5.42 | 8,561,584 → 8,561,747 | ~0% |
| Reliable UDP | Byte runs 32 KiB | 19.01 → 23.24 | 7.26 → 4.95 | 8,561,567 → 76,310 | 99.1% |

Figures are medians of ten trials per mode; byte medians are rounded to whole bytes. Each 32 KiB connection delivers 8 MiB of original payload; each 1 KiB connection delivers 256 KiB. Warm speed excludes cold training, while complete bytes include it. Fresh-random TCP is exactly **186 bytes larger** when enabled, from capability negotiation; no payload frame compressed. Reliable-UDP controls add small timing-dependent variation.

## Confidence and remaining costs

Ratios use paired geometric means and deterministic 10,000-resample percentile 95% bootstrap intervals. Warm speed on/off above 1 means faster with compression. p95 RTT off/on above 1 means lower latency with compression. An interval spanning 1 does not establish a directional change and does not prove identical performance.

| Transport | Pattern | Warm speed on/off [95% CI] | p95 RTT off/on [95% CI] |
|---|---|---|---|
| TCP | Repeated 1 KiB | 0.839× [0.626, 1.015] | 0.857× [0.667, 1.063] |
| TCP | Repeated 32 KiB | 1.167× [0.873, 1.949] | 0.888× [0.780, 0.983] |
| TCP | 75% stable 32 KiB | 0.922× [0.875, 0.967] | 0.970× [0.851, 1.087] |
| TCP | Fresh random 32 KiB | 0.992× [0.952, 1.049] | 0.806× [0.521, 1.034] |
| TCP | Byte runs 32 KiB | 0.848× [0.646, 1.007] | 1.289× [0.995, 1.744] |
| Reliable UDP | Repeated 1 KiB | 0.979× [0.909, 1.081] | 1.158× [1.026, 1.381] |
| Reliable UDP | Repeated 32 KiB | 1.082× [0.972, 1.175] | 1.016× [0.753, 1.219] |
| Reliable UDP | 75% stable 32 KiB | 1.064× [0.961, 1.159] | 1.095× [0.968, 1.202] |
| Reliable UDP | Fresh random 32 KiB | 0.954× [0.896, 1.015] | 1.085× [0.950, 1.266] |
| Reliable UDP | Byte runs 32 KiB | 1.208× [1.154, 1.266] | 1.388× [1.226, 1.582] |

Fresh-random warm intervals cross 1 on both transports. TCP 75%-stable throughput remains about **7.8% lower** by the paired estimate, with its interval entirely below 1. Reliable-UDP byte runs are about **20.8% faster**, with their interval entirely above 1. Most other warm speed comparisons are inconclusive in this noisy series. TCP repeated-32-KiB stop-and-wait p95 RTT remains worse; reliable-UDP 1-KiB and byte-run p95 RTT improve.

All samples remain in the statistics, including a 674.7 ms bulk p95 RTT in one compression-off TCP repeated-32-KiB warm phase and a 113.5 ms bulk p95 in one enabled 1-KiB warm phase. Consequently some paired estimates differ markedly from ratios of the displayed medians, and several intervals are wide. These measurements support workload-specific conclusions, not a universal speed claim.

Cold random-data costs also remain. TCP cold throughput was **23.59 → 20.28 MiB/s**, with a wide interval spanning 1. Reliable UDP cold throughput was **18.20 → 16.56 MiB/s**, paired **0.851× [0.733, 0.965]**. Cold phases include first-use JIT and initial probes; warmed bypass results do not erase those startup costs.

Each 256-object random connection made **9 encoder attempts**: 6 in cold 64, 2 in warm 128, and 1 in latency 64. All 256 objects were sent as original frames, with no dictionary definitions or plans, and the probe interval reached 64. Thus incompressible streams avoid an encoder pass on almost every subsequent object. The full [analysis](analysis.md) also reports client-process CPU, allocation, cache state and latency tails.

## Verified plan reuse

The warm repeated and delta phases use a verified cached plan for **127 of 128 objects**. Changed literals remain current; a plan never substitutes bytes without comparing each referenced span. Local byte runs use the message-local encoder and require no dictionary plan.

| Transport | Workload | Plans at warm end | Plan hits in cold / warm / latency |
|---|---|---:|---:|
| TCP | repeated-1k | 2 | 55 / 127 / 64 |
| TCP | repeated-32k | 2 | 56 / 127 / 64 |
| TCP | delta-32k | 2 | 60 / 127 / 64 |
| Reliable UDP | repeated-1k | 2 | 55 / 127 / 64 |
| Reliable UDP | repeated-32k | 2 | 55 / 127 / 64 |
| Reliable UDP | delta-32k | 2 | 55 / 127 / 64 |

Plan counters are cumulative in the raw files; the phase figures above subtract each preceding end counter before taking the median. Encoding-attempt counts include plan attempts, so a high attempt count on repeated data does not imply repeated full dictionary searches.

## Dynamic traffic and recovery

A single connection changes from fresh random bytes to repeated bytes, then sends 64 unrelated churn objects, returns to the original payload, and finally switches to byte runs. Each phase sends 64 objects. The first five use window eight; the final latency phase uses stop-and-wait. Counts are cumulative state at the phase end, except the explicitly per-phase plan-hit column.

| Transport | Phase | MiB/s off → on | Phase bytes off → on | Definitions on | Encoder attempts on | Probe interval on | Plan hits this phase |
|---|---|---:|---:|---:|---:|---:|---:|
| TCP | random | 22.31 → 15.54 | 2,105,920 → 2,105,920 | 0 | 6 | 64 | 0 |
| TCP | repeat | 26.56 → 19.69 | 2,106,048 → 383,654 | 98 | 66 | 1 | 55 |
| TCP | churn | 26.44 → 20.57 | 2,106,048 → 2,106,048 | 98 | 72 | 64 | 0 |
| TCP | return | 27.29 → 24.93 | 2,106,048 → 97,408 | 98 | 136 | 1 | 64 |
| TCP | runs | 27.41 → 24.54 | 2,106,048 → 16,965 | 99 | 200 | 1 | 0 |
| TCP | latency | 8.15 → 7.08 | 2,106,048 → 14,848 | 99 | 264 | 1 | 0 |
| Reliable UDP | random | 16.45 → 16.40 | 2,136,549 → 2,136,572 | 0 | 6 | 64 | 0 |
| Reliable UDP | repeat | 19.38 → 19.36 | 2,136,834 → 391,695 | 98 | 66 | 1 | 55 |
| Reliable UDP | churn | 20.44 → 19.07 | 2,138,945 → 2,138,945 | 98 | 72 | 64 | 0 |
| Reliable UDP | return | 20.87 → 23.75 | 2,138,981 → 102,821 | 98 | 136 | 1 | 64 |
| Reliable UDP | runs | 21.43 → 26.78 | 2,138,921 → 20,590 | 99 | 200 | 1 | 0 |
| Reliable UDP | latency | 7.19 → 8.69 | 2,138,981 → 17,317 | 99 | 264 | 1 | 0 |

Both transports back off on fresh random input, wake when repetition appears, preserve 98 definitions through 64 unrelated objects, and reuse a plan on every return object. The return phase sends **95.4% fewer TCP bytes** and **95.2% fewer reliable-UDP bytes**, establishing retention beyond ten objects. Reliable-UDP return speed has paired ratio **1.284× [1.171, 1.452]**. TCP return timing is noisy and inconclusive despite its lower median; [adaptive-analysis.md](adaptive-analysis.md) contains all phase intervals.

## Thirty-two-object recurrence cycle

`spaced-32k` cycles 32 different high-entropy payloads: each returns after 31 unrelated objects. The 128-object warm phase still includes substantial learning and registrations. An additional 64-object trained bulk phase follows, then stop-and-wait latency 64. All 320 objects, including early dictionary costs, remain in the complete totals.

| Transport | Phase | MiB/s off → on | Phase bytes off → on | Definitions on | Plans on | Plan hits this phase | Speed on/off [95% CI] |
|---|---|---:|---:|---:|---:|---:|---|
| TCP | cold | 21.12 → 16.15 | 2,105,920 → 2,176,220 | 213 | 0 | 0 | 1.029× [0.750, 1.851] |
| TCP | warm | 24.43 → 14.64 | 4,212,096 → 3,468,041 | 3,323 | 31 | 0 | 0.792× [0.566, 1.217] |
| TCP | trained | 25.51 → 20.55 | 2,106,048 → 583,104 | 3,323 | 32 | 42 | 1.064× [0.848, 1.527] |
| TCP | latency | 7.76 → 7.19 | 2,106,048 → 583,104 | 3,323 | 32 | 64 | 1.218× [0.915, 1.909] |
| Reliable UDP | cold | 16.76 → 14.17 | 2,136,572 → 2,206,643 | 213 | 0 | 0 | 0.859× [0.831, 0.884] |
| Reliable UDP | warm | 19.29 → 12.54 | 4,275,586 → 3,464,020 | 3,323 | 32 | 2 | 0.649× [0.628, 0.672] |
| Reliable UDP | trained | 20.35 → 19.61 | 2,138,921 → 594,131 | 3,323 | 32 | 64 | 0.942× [0.867, 0.997] |
| Reliable UDP | latency | 6.19 → 6.31 | 2,138,981 → 594,191 | 3,323 | 32 | 64 | 1.019× [0.981, 1.047] |

This establishes that the learner can retain a 32-payload candidate pool rather than only ten recent objects. By the trained phase it holds 32 reusable plans. The trained TCP median remains lower; its paired interval is wide and crosses 1. Reliable-UDP trained throughput remains about **5.8% lower** by its paired estimate, with a 95% interval just below 1. Training is still expensive, and byte savings alone do not establish faster delivery.

| Transport | Cumulative objects | Complete bytes off → on |
|---|---:|---:|
| TCP | 64 | 2,115,824 → 2,186,310 |
| TCP | 192 | 6,327,920 → 5,654,351 |
| TCP | 256 | 8,433,968 → 6,249,431 |
| TCP | 320 | 10,540,016 → 6,849,133 |
| Reliable UDP | 64 | 2,147,041 → 2,217,298 |
| Reliable UDP | 192 | 6,422,627 → 5,681,318 |
| Reliable UDP | 256 | 8,561,548 → 6,248,228 |
| Reliable UDP | 320 | 10,700,529 → 6,832,399 |

The first 64-object prefix costs more bytes with compression on. Registration byte cost is repaid by the 192-object snapshot on both transports. Phase snapshots cannot locate the exact break-even object. Byte break-even differs from elapsed-time break-even.

## Mostly random input with a stable small region

`marginal-32k` leaves 98.4% of each object random and holds 512 high-entropy bytes stable near a scout position. It makes **11 attempts in 256 objects**, retains one definition, and reaches interval 64. A small repeated field does not force continuous full scans.

| Transport | Warm MiB/s off → on | Warm speed on/off [95% CI] | Complete bytes off → on |
|---|---:|---|---:|
| TCP | 27.30 → 25.27 | 1.036× [0.914, 1.292] | 8,433,968 → 8,434,427 |
| Reliable UDP | 18.29 → 20.05 | 0.988× [0.835, 1.142] | 8,561,586 → 8,562,122 |

Both warm intervals cross 1. The complete connection is slightly larger when enabled because the small definition and negotiation do not repay their byte cost in this run.

## Complete supplementary connection costs

| Transport | Workload | Original payload bytes | Complete bytes off → on | Saved |
|---|---|---:|---:|---:|
| TCP | adaptive-32k | 12,582,912 | 12,646,064 → 4,734,933 | 62.558% |
| TCP | spaced-32k | 10,485,760 | 10,540,016 → 6,849,133 | 35.018% |
| TCP | marginal-32k | 8,388,608 | 8,433,968 → 8,434,427 | -0.005% |
| Reliable UDP | adaptive-32k | 12,582,912 | 12,839,703 → 4,818,663 | 62.471% |
| Reliable UDP | spaced-32k | 10,485,760 | 10,700,529 → 6,832,399 | 36.149% |
| Reliable UDP | marginal-32k | 8,388,608 | 8,561,586 → 8,562,122 | -0.006% |

## Shared dictionary propagation to idle clients

Five alternating TCP pairs upload 100 repeated 32 KiB objects from one active client while three stay idle. This separate experiment deliberately enables the default shared repository. All four independent proxy links are counted. Timing is auxiliary because endpoints share one process.

| Metric | Off | On |
|---|---:|---:|
| Complete bytes, including all four handshakes | 3,331,148 | 395,978 |
| Bytes after setup, including cold learning | 3,291,372 | 355,730 |
| Server-to-client bytes after setup | 6,836 | 109,926 |
| Idle-client update/control bytes after setup | 0 | 105,498 |
| Definitions retained by each idle client | 0 | 98 |

Complete fanout traffic falls **88.1%**. The enabled total already pays **105,498 bytes** of dictionary update/control traffic over the three idle links: 35,166 bytes each. Main private-cache measurements do not propagate definitions to unrelated peers.

## Earlier iterations as context

The [original](../2026-10-15/) and [first optimization](../2026-10-15-optimized/) reports remain unchanged. They use shared rather than private repositories. Sequential sessions, configuration changes and host variability prevent attributing absolute old/new speed differences solely to plans. The final paired on/off estimates above are the primary evidence.

| Transport / payload | Original enabled MiB/s | First iteration enabled MiB/s | Final enabled MiB/s |
|---|---:|---:|---:|
| TCP, random-32k | 12.50 | 28.42 | 27.17 |
| TCP, repeated-32k | 12.64 | 25.68 | 25.18 |
| Reliable UDP, random-32k | 11.20 | 21.51 | 20.43 |
| Reliable UDP, repeated-32k | 12.37 | 23.59 | 22.49 |

## What this iteration changes

Repeated payload layouts can reuse encoding plans selected by three sampled anchors and the serialized length. Every referenced span is compared with the current bytes before it becomes a shortcut. Changed spans become current literals or fall back to a new encoding pass. The encoder sizes the complete frame before allocating one output buffer. This avoids repeated dictionary searches and multiple output copies on a useful repeated layout while preserving exact decoding.

Plans use recency eviction within a budget of at most 512 KiB per cache, including conservative lookup metadata. They share the existing 4 MiB per-cache and process-wide physical-RAM/16 retained-state admission limits. They do not extend a definition's lifetime or remove its acknowledgement requirement.

The adaptive learner retains memory-bounded candidates rather than the last ten objects. Cheap recurrence samples continue on eligible objects, unsuccessful work backs off, and new useful patterns wake learning. Tiny messages skip setup. The private/shared configuration below controls which peers can receive learned bytes.

## Measurement scope

The final main and adaptive experiments explicitly use `ShareCompressionDictionary = false` on both endpoints. Each connection owns its dictionary, candidates, plans and acknowledgements. Compression-off and compression-on otherwise use the same final DLL and settings; `EnableCompression` alone varies within each pair. New negotiation fields and all private dictionary updates are included in the socket byte counters.

The original and first-iteration experiments used a shared socket repository. Their one-active-client workloads remain useful context, but the final private-cache configuration differs, so cross-session speed changes cannot be attributed solely to the encoding-plan optimization. The final contemporaneous on/off pairs provide the primary evidence for current overhead.

The separate four-client fanout experiment retains shared repositories to measure propagation to three idle clients. Its totals include every client link. Its timing is auxiliary because the endpoints share one process.

Main workloads and fixtures retain ten paired rounds, fresh server/client processes, cold 64, warm 128 and stop-and-wait latency 64. Supplementary adaptive experiments retain five paired rounds, including a 32-object recurrence cycle with an additional trained bulk phase. All payloads are independently generated and byte-verified at the receiver. Byte totals include handshakes, dictionary definitions/ACKs, framing, application receipts, retransmissions and interphase traffic in both directions. OS TCP acknowledgements and IP/TCP/UDP/link headers are excluded.

Plan counts are retained layouts. Plan hits are cumulative messages encoded through a verified layout; per-phase hits subtract the preceding phase's end counter. Encoding attempts include plan attempts and ordinary codec attempts. Raw-frame bypasses overlap attempts when an attempted encoding falls back to the original bytes. Approximate allocation and CPU figures cover the client process and its counting proxy, not both endpoints.

[Shared method and commands](../README.md). The small [CPU-before](cpu-before.json) and [CPU-after](cpu-after.json) files are codec-only diagnostics with simulated immediate acknowledgements. They omit object formatting, actual sockets and dictionary-update bytes, and are not transfer benchmarks. Pilot files are also excluded from final network statistics.

## Website compatibility and private dictionaries


The browser always requests and verifies private-cache negotiation. Native applications can still intentionally share authorized common data with the default `ShareCompressionDictionary = true`; website servers exchanging independent client data should set it to `false`. Existing Portal JSON traffic remains unchanged and does not advertise binary compression.

The private adapter passed 59 controlled behavior checks. The module was deployed without a Portal restart, and public verification matched SHA-256 `2EC8A4FD32C6015CC590980787C0E7FCB7CF69CF61497C4AD257CD86D7691143`. All five hosted services remained online. A real signed-in Account page showed the permission unchecked, the private-cache text and normal-format fallback notice, with no browser console errors. No real user's setting was changed.


!Verified live account setting; permission remains off

## Validation and evidence

- Managed correctness runs passed **240 tests**, with **one TypeScript-compiler-dependent test skipped**: main 195 passed, one skipped; reliable UDP 45 passed. Final main log, reliable-UDP log, and TRX results. Initial fixture-failure logs are retained diagnostics and do not supply the final totals.
- Release WPF builds passed for **net8.0-windows7.0 and net10.0-windows7.0**, with one pre-existing unused-field warning for `DataServer._persistenceDisposed`. Build log.
- Both modes and all three final series used core SHA-256 **`70EB30F0CF7082B0746BE22AF2703B4BB2334EE2CFB5A1F8BF2C490FA65271EC`**. The same hash appears in the core Release output, both test copies and both benchmark copies. DLL freeze.
- **34 frozen source files and five DLL copies were verified unchanged**; all run hashes match. Source manifest, [measured source snapshot](measured-source/), and final verification.
- Main: [raw measurements](results.json), [full statistics](analysis.md), [machine-readable analysis](analysis.json), run log.
- Adaptive: [raw measurements](adaptive.json), [full statistics](adaptive-analysis.md), [machine-readable analysis](adaptive-analysis.json), run log.
- Shared fanout: [raw measurements](fanout.json), run log.
- CPU-only before/after diagnostics, plan pilots, initial fixture failures and focused test runs are excluded from the final network statistics. CPU-after diagnostics predate the final decoder lock optimization and do not measure final end-to-end transfer behavior.

