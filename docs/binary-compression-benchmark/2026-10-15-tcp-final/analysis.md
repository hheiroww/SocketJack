# Binary compression transfer benchmark

Source: `docs\binary-compression-benchmark\2026-10-15-tcp-final\results.json`. Per-mode figures are medians of complete runs. On/off comparisons pair the same round. Compression is the only setting changed.

Socket bytes count both directions once, including dictionary updates, dictionary acknowledgements, application acknowledgements, and SocketJack framing. They exclude IP/TCP/UDP/Ethernet headers. Setup is reported separately and is included once in the lifetime totals. Lifetime totals use the ending cumulative counter, including traffic between phase snapshots.

Bulk throughput is useful application payload divided by the time from the first send through the last validated application acknowledgement. Bulk RTT uses eight outstanding messages; latency RTT uses one outstanding message after warm training. Byte counters drain after timing stops; settle time is excluded from throughput. Phase order and message counts follow the raw file's phasePlans metadata; legacy results without that field use cold, warm, latency. Every connection retains its adaptive cache across phases.

Throughput ratios use the geometric mean of paired on/off ratios and 10,000 paired bootstrap resamples (seed 7301; percentile 95% interval). An interval spanning 1 does not establish a speed change. Round-tail medians are not a pooled per-message percentile. With 64 latency samples, p99 is effectively the maximum sample and is descriptive. Fresh processes make the cold phase include first-use JIT costs; warm phases reuse the process and cache. CPU and optional managed-allocation figures cover the client process and its counting proxy, excluding the drain. Allocation is approximate process-wide allocation rather than retained memory. Setup timing measures Connect; setup byte totals also include its quiet-drain traffic. Loopback results do not establish WAN performance.

| Transport | Workload | Phase | Pairs | MiB/s off | MiB/s on | On/off speed [95% CI] | Total bytes off | Total bytes on | Bytes saved |
|---|---|---|---:|---:|---:|---|---:|---:|---:|
| tcp | cold-first-32k | first | 10 | 1.71 | 1.58 | 0.919× [0.895, 0.944] | 32,905 | 32,905 | 0.00% |
| tcp | cold-first-32k | cold | 10 | 26.01 | 20.65 | 0.803× [0.778, 0.831] | 2,073,015 | 764,659 | 63.11% |
| tcp | cold-first-32k | warm | 10 | 28.36 | 27.41 | 0.972× [0.946, 0.999] | 4,212,096 | 1,168,640 | 72.26% |
| tcp | cold-first-32k | latency | 10 | 8.15 | 8.16 | 1.001× [0.992, 1.010] | 2,106,048 | 584,320 | 72.26% |
| tcp | delta-32k | cold | 10 | 23.46 | 19.62 | 0.806× [0.729, 0.860] | 2,105,920 | 797,564 | 62.13% |
| tcp | delta-32k | warm | 10 | 28.72 | 27.79 | 0.967× [0.944, 0.989] | 4,212,096 | 1,168,640 | 72.26% |
| tcp | delta-32k | latency | 10 | 8.28 | 8.16 | 0.995× [0.962, 1.032] | 2,106,048 | 584,320 | 72.26% |
| tcp | delta-blocks-32k | cold | 10 | 23.48 | 19.45 | 0.834× [0.814, 0.858] | 2,105,920 | 1,689,240 | 19.79% |
| tcp | delta-blocks-32k | warm | 10 | 28.63 | 25.76 | 0.898× [0.885, 0.914] | 4,212,096 | 2,324,567 | 44.81% |
| tcp | delta-blocks-32k | latency | 10 | 8.20 | 8.01 | 0.966× [0.938, 0.987] | 2,106,048 | 1,023,168 | 51.42% |
| tcp | random-32k | cold | 10 | 24.05 | 23.04 | 0.970× [0.920, 1.015] | 2,105,920 | 2,105,920 | 0.00% |
| tcp | random-32k | warm | 10 | 28.66 | 28.75 | 1.017× [0.990, 1.048] | 4,212,096 | 4,212,096 | 0.00% |
| tcp | random-32k | latency | 10 | 8.21 | 8.15 | 1.024× [0.987, 1.092] | 2,106,048 | 2,106,048 | 0.00% |
| tcp | repeated-1k | cold | 10 | 0.78 | 0.70 | 0.892× [0.871, 0.912] | 74,240 | 31,662 | 57.35% |
| tcp | repeated-1k | warm | 10 | 0.89 | 0.93 | 1.040× [1.021, 1.058] | 148,736 | 47,232 | 68.24% |
| tcp | repeated-1k | latency | 10 | 0.26 | 0.26 | 0.976× [0.960, 0.990] | 74,368 | 23,616 | 68.24% |
| tcp | repeated-32k | cold | 10 | 23.06 | 19.41 | 0.845× [0.831, 0.861] | 2,105,920 | 346,798 | 83.53% |
| tcp | repeated-32k | warm | 10 | 28.41 | 27.53 | 0.969× [0.940, 0.997] | 4,212,096 | 118,656 | 97.18% |
| tcp | repeated-32k | latency | 10 | 8.21 | 8.23 | 0.997× [0.983, 1.010] | 2,106,048 | 59,328 | 97.18% |
| tcp | runs-32k | cold | 10 | 24.10 | 22.14 | 0.917× [0.885, 0.948] | 2,105,920 | 14,016 | 99.33% |
| tcp | runs-32k | warm | 10 | 29.12 | 28.11 | 0.974× [0.954, 0.991] | 4,212,096 | 28,412 | 99.33% |
| tcp | runs-32k | latency | 10 | 8.28 | 8.22 | 0.994× [0.977, 1.008] | 2,106,048 | 14,208 | 99.33% |

## Adaptive state and managed allocation

Adaptive counters and cache state are cumulative through the end of the phase, after its drain. Plan hits this phase subtract the preceding phase's ending counter (the first phase starts at zero). Allocation covers only the timed phase in the client process, including the proxy; it is approximate allocated volume rather than retained memory. Encoding attempts and bypasses count outgoing non-control messages, which can include queue-drain application barriers as well as measured payloads.

| Transport | Workload | Phase | Allocated bytes off/on | Candidates on | Attempts on | Bypasses on | Probe interval on | Plans on | Plan hits cumulative on | Plan hits this phase on |
|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|
| tcp | cold-first-32k | first | 204,896 / 228,048 | 0 | 0 | 1 | 64 | 0 | 0 | 0 |
| tcp | cold-first-32k | cold | 6,631,296 / 5,125,960 | 99 | 56 | 8 | 1 | 1 | 55 | 55 |
| tcp | cold-first-32k | warm | 13,533,216 / 8,665,024 | 150 | 184 | 8 | 1 | 2 | 182 | 127 |
| tcp | cold-first-32k | latency | 6,744,020 / 4,323,492 | 178 | 248 | 8 | 1 | 2 | 246 | 64 |
| tcp | delta-32k | cold | 6,967,568 / 5,344,468 | 99 | 56 | 8 | 1 | 1 | 55 | 55 |
| tcp | delta-32k | warm | 13,506,632 / 8,631,444 | 150 | 184 | 8 | 1 | 2 | 182 | 127 |
| tcp | delta-32k | latency | 6,734,460 / 4,322,272 | 178 | 248 | 8 | 1 | 2 | 246 | 64 |
| tcp | delta-blocks-32k | cold | 7,010,996 / 8,222,880 | 147 | 57 | 7 | 1 | 1 | 56 | 56 |
| tcp | delta-blocks-32k | warm | 13,525,148 / 12,501,672 | 236 | 185 | 7 | 1 | 4 | 183 | 127 |
| tcp | delta-blocks-32k | latency | 6,732,716 / 5,654,628 | 286 | 249 | 7 | 1 | 4 | 247 | 64 |
| tcp | random-32k | cold | 7,002,700 / 7,023,308 | 0 | 0 | 64 | 64 | 0 | 0 | 0 |
| tcp | random-32k | warm | 13,528,832 / 13,526,888 | 0 | 0 | 192 | 64 | 0 | 0 | 0 |
| tcp | random-32k | latency | 6,722,928 / 6,727,788 | 0 | 0 | 256 | 64 | 0 | 0 | 0 |
| tcp | repeated-1k | cold | 876,324 / 1,079,532 | 7 | 56 | 8 | 1 | 1 | 55 | 55 |
| tcp | repeated-1k | warm | 1,250,716 / 1,129,688 | 11 | 184 | 8 | 1 | 2 | 182 | 127 |
| tcp | repeated-1k | latency | 658,812 / 549,004 | 13 | 248 | 8 | 1 | 2 | 246 | 64 |
| tcp | repeated-32k | cold | 6,982,424 / 3,940,172 | 7 | 56 | 8 | 1 | 1 | 55 | 55 |
| tcp | repeated-32k | warm | 13,549,312 / 5,458,312 | 11 | 184 | 8 | 1 | 2 | 182 | 127 |
| tcp | repeated-32k | latency | 6,735,780 / 2,708,848 | 13 | 248 | 8 | 1 | 2 | 246 | 64 |
| tcp | runs-32k | cold | 7,005,804 / 3,223,376 | 0 | 64 | 0 | 1 | 0 | 0 | 0 |
| tcp | runs-32k | warm | 13,525,440 / 5,711,012 | 0 | 192 | 0 | 1 | 0 | 0 | 0 |
| tcp | runs-32k | latency | 6,732,732 / 2,833,256 | 0 | 256 | 0 | 1 | 0 | 0 | 0 |

## Application round-trip latency

| Transport | Workload | Phase | p50 ms off/on | p95 ms off/on | p99 ms off/on | p95 speedup off/on [95% CI] | Client CPU ms off/on | Definitions on | Cache bytes on |
|---|---|---|---|---|---|---|---|---:|---:|
| tcp | cold-first-32k | first | 17.072 / 18.673 | 17.072 / 18.673 | 17.072 / 18.673 | 0.913× [0.888, 0.939] | 0.000 / 0.000 | 0 | 18,560 |
| tcp | cold-first-32k | cold | 4.520 / 4.597 | 6.588 / 8.261 | 7.696 / 12.056 | 0.831× [0.767, 0.899] | 15.625 / 0.000 | 35 | 444,236 |
| tcp | cold-first-32k | warm | 4.314 / 4.350 | 5.909 / 5.748 | 8.163 / 6.679 | 1.061× [0.959, 1.190] | 15.625 / 15.625 | 35 | 499,652 |
| tcp | cold-first-32k | latency | 3.675 / 3.704 | 4.498 / 4.354 | 6.517 / 6.182 | 1.021× [0.997, 1.046] | 15.625 / 15.625 | 35 | 527,580 |
| tcp | delta-32k | cold | 4.523 / 4.609 | 9.824 / 9.213 | 18.161 / 19.804 | 0.997× [0.848, 1.109] | 15.625 / 15.625 | 35 | 444,236 |
| tcp | delta-32k | warm | 4.334 / 4.367 | 5.409 / 5.532 | 6.057 / 5.958 | 0.964× [0.869, 1.057] | 31.250 / 23.438 | 35 | 499,652 |
| tcp | delta-32k | latency | 3.669 / 3.670 | 4.253 / 4.474 | 5.584 / 5.842 | 0.969× [0.906, 1.049] | 7.812 / 15.625 | 35 | 527,580 |
| tcp | delta-blocks-32k | cold | 4.616 / 4.504 | 10.057 / 8.715 | 18.349 / 19.282 | 1.200× [1.099, 1.315] | 15.625 / 15.625 | 28 | 495,711 |
| tcp | delta-blocks-32k | warm | 4.358 / 4.378 | 5.807 / 5.706 | 6.741 / 6.549 | 1.034× [0.941, 1.148] | 15.625 / 15.625 | 30 | 601,753 |
| tcp | delta-blocks-32k | latency | 3.643 / 3.692 | 4.429 / 4.587 | 6.124 / 8.323 | 0.939× [0.869, 1.009] | 15.625 / 15.625 | 30 | 654,233 |
| tcp | random-32k | cold | 4.471 / 4.618 | 9.871 / 9.952 | 18.491 / 19.962 | 1.013× [0.954, 1.087] | 15.625 / 15.625 | 0 | 18,560 |
| tcp | random-32k | warm | 4.290 / 4.297 | 5.583 / 5.506 | 6.199 / 6.064 | 1.048× [0.979, 1.128] | 31.250 / 15.625 | 0 | 18,560 |
| tcp | random-32k | latency | 3.647 / 3.699 | 4.414 / 4.354 | 6.084 / 6.065 | 1.053× [0.996, 1.139] | 15.625 / 7.812 | 0 | 18,560 |
| tcp | repeated-1k | cold | 4.369 / 4.376 | 9.449 / 9.239 | 18.223 / 19.220 | 1.051× [1.024, 1.081] | 15.625 / 15.625 | 3 | 320,237 |
| tcp | repeated-1k | warm | 4.294 / 4.186 | 6.547 / 5.003 | 8.011 / 5.614 | 1.326× [1.148, 1.554] | 23.438 / 31.250 | 3 | 323,457 |
| tcp | repeated-1k | latency | 3.590 / 3.611 | 4.404 / 4.587 | 7.602 / 7.489 | 0.909× [0.815, 0.996] | 7.812 / 15.625 | 3 | 324,711 |
| tcp | repeated-32k | cold | 4.624 / 4.507 | 9.959 / 8.639 | 18.256 / 19.459 | 1.123× [1.042, 1.210] | 15.625 / 15.625 | 47 | 366,868 |
| tcp | repeated-32k | warm | 4.350 / 4.292 | 6.598 / 6.099 | 7.583 / 7.142 | 1.075× [1.016, 1.140] | 46.875 / 31.250 | 47 | 370,636 |
| tcp | repeated-32k | latency | 3.652 / 3.675 | 4.460 / 4.455 | 6.363 / 5.054 | 1.012× [0.953, 1.078] | 15.625 / 23.438 | 47 | 371,636 |
| tcp | runs-32k | cold | 4.459 / 4.444 | 10.161 / 12.987 | 18.229 / 25.753 | 0.789× [0.756, 0.824] | 23.438 / 15.625 | 0 | 18,560 |
| tcp | runs-32k | warm | 4.301 / 4.305 | 5.263 / 5.490 | 5.768 / 6.011 | 0.991× [0.908, 1.091] | 31.250 / 23.438 | 0 | 18,560 |
| tcp | runs-32k | latency | 3.650 / 3.640 | 4.340 / 4.298 | 5.451 / 6.638 | 1.017× [0.977, 1.054] | 0.000 / 31.250 | 0 | 18,560 |

## Complete connection byte cost

Uses the ending cumulative counter, including setup, every reported phase and any interphase traffic. Between-phase bytes are the gaps between setup/phase snapshots and are already included in the total.

| Transport | Workload | Pairs | Payload bytes | Total bytes off | Total bytes on | Bytes saved | Between-phase bytes off/on | Setup bytes off/on | Setup ms off/on |
|---|---|---:|---:|---:|---:|---:|---|---|---|
| tcp | cold-first-32k | 10 | 8,388,608 | 8,433,968 | 2,560,614 | 69.64% | 0 / 0 | 9,904 / 10,090 | 265.60 / 278.71 |
| tcp | delta-32k | 10 | 8,388,608 | 8,433,968 | 2,560,614 | 69.64% | 0 / 0 | 9,904 / 10,090 | 266.13 / 275.42 |
| tcp | delta-blocks-32k | 10 | 8,388,608 | 8,433,968 | 5,047,066 | 40.16% | 0 / 0 | 9,904 / 10,090 | 267.38 / 276.89 |
| tcp | random-32k | 10 | 8,388,608 | 8,433,968 | 8,434,154 | -0.00% | 0 / 0 | 9,904 / 10,090 | 265.20 / 280.34 |
| tcp | repeated-1k | 10 | 262,144 | 307,248 | 112,600 | 63.35% | 0 / 0 | 9,904 / 10,090 | 264.48 / 277.31 |
| tcp | repeated-32k | 10 | 8,388,608 | 8,433,968 | 534,872 | 93.66% | 0 / 0 | 9,904 / 10,090 | 266.21 / 276.63 |
| tcp | runs-32k | 10 | 8,388,608 | 8,433,968 | 66,726 | 99.21% | 0 / 0 | 9,904 / 10,090 | 266.16 / 278.21 |
