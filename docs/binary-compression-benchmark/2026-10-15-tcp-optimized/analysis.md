# Binary compression transfer benchmark

Source: `docs\binary-compression-benchmark\2026-10-15-tcp-optimized\results.json`. Per-mode figures are medians of complete runs. On/off comparisons pair the same round. Compression is the only setting changed.

Socket bytes count both directions once, including dictionary updates, dictionary acknowledgements, application acknowledgements, and SocketJack framing. They exclude IP/TCP/UDP/Ethernet headers. Setup is reported separately and is included once in the lifetime totals. Lifetime totals use the ending cumulative counter, including traffic between phase snapshots.

Bulk throughput is useful application payload divided by the time from the first send through the last validated application acknowledgement. Bulk RTT uses eight outstanding messages; latency RTT uses one outstanding message after warm training. Byte counters drain after timing stops; settle time is excluded from throughput. Phase order and message counts follow the raw file's phasePlans metadata; legacy results without that field use cold, warm, latency. Every connection retains its adaptive cache across phases.

Throughput ratios use the geometric mean of paired on/off ratios and 10,000 paired bootstrap resamples (seed 7301; percentile 95% interval). An interval spanning 1 does not establish a speed change. Round-tail medians are not a pooled per-message percentile. With 64 latency samples, p99 is effectively the maximum sample and is descriptive. Fresh processes make the cold phase include first-use JIT costs; warm phases reuse the process and cache. CPU and optional managed-allocation figures cover the client process and its counting proxy, excluding the drain. Allocation is approximate process-wide allocation rather than retained memory. Setup timing measures Connect; setup byte totals also include its quiet-drain traffic. Loopback results do not establish WAN performance.

| Transport | Workload | Phase | Pairs | MiB/s off | MiB/s on | On/off speed [95% CI] | Total bytes off | Total bytes on | Bytes saved |
|---|---|---|---:|---:|---:|---|---:|---:|---:|
| tcp | cold-first-32k | first | 10 | 1.33 | 1.05 | 0.979× [0.831, 1.200] | 32,905 | 32,905 | 0.00% |
| tcp | cold-first-32k | cold | 10 | 19.04 | 15.34 | 0.849× [0.791, 0.929] | 2,073,015 | 779,430 | 62.40% |
| tcp | cold-first-32k | warm | 10 | 20.62 | 20.65 | 0.999× [0.902, 1.135] | 4,212,096 | 1,168,640 | 72.26% |
| tcp | cold-first-32k | latency | 10 | 5.60 | 6.08 | 1.063× [0.975, 1.179] | 2,106,048 | 584,320 | 72.26% |
| tcp | delta-32k | cold | 10 | 17.33 | 14.37 | 0.805× [0.732, 0.888] | 2,105,920 | 798,236 | 62.10% |
| tcp | delta-32k | warm | 10 | 21.46 | 19.30 | 0.922× [0.851, 1.012] | 4,212,096 | 1,168,640 | 72.26% |
| tcp | delta-32k | latency | 10 | 6.21 | 5.67 | 0.880× [0.776, 0.980] | 2,106,048 | 584,320 | 72.26% |
| tcp | delta-blocks-32k | cold | 10 | 16.31 | 15.01 | 0.852× [0.785, 0.936] | 2,105,920 | 1,689,956 | 19.75% |
| tcp | delta-blocks-32k | warm | 10 | 20.79 | 19.33 | 0.878× [0.833, 0.938] | 4,212,096 | 2,325,030 | 44.80% |
| tcp | delta-blocks-32k | latency | 10 | 5.82 | 5.76 | 0.940× [0.853, 1.029] | 2,106,048 | 1,023,168 | 51.42% |
| tcp | random-32k | cold | 10 | 16.82 | 16.36 | 0.855× [0.704, 0.982] | 2,105,920 | 2,105,920 | 0.00% |
| tcp | random-32k | warm | 10 | 19.07 | 20.06 | 0.998× [0.852, 1.141] | 4,212,096 | 4,212,096 | 0.00% |
| tcp | random-32k | latency | 10 | 5.94 | 6.00 | 0.981× [0.911, 1.043] | 2,106,048 | 2,106,048 | 0.00% |
| tcp | repeated-1k | cold | 10 | 0.60 | 0.52 | 0.892× [0.849, 0.947] | 74,240 | 31,662 | 57.35% |
| tcp | repeated-1k | warm | 10 | 0.65 | 0.70 | 1.120× [1.031, 1.233] | 148,736 | 47,232 | 68.24% |
| tcp | repeated-1k | latency | 10 | 0.19 | 0.20 | 1.087× [1.004, 1.226] | 74,368 | 23,616 | 68.24% |
| tcp | repeated-32k | cold | 10 | 16.43 | 14.52 | 0.669× [0.439, 0.882] | 2,105,920 | 341,139 | 83.80% |
| tcp | repeated-32k | warm | 10 | 20.32 | 19.52 | 0.873× [0.666, 1.030] | 4,212,096 | 118,656 | 97.18% |
| tcp | repeated-32k | latency | 10 | 6.10 | 5.70 | 0.910× [0.725, 1.045] | 2,106,048 | 59,328 | 97.18% |
| tcp | runs-32k | cold | 10 | 16.86 | 16.95 | 1.003× [0.907, 1.136] | 2,105,920 | 14,016 | 99.33% |
| tcp | runs-32k | warm | 10 | 22.87 | 20.88 | 1.088× [0.892, 1.447] | 4,212,096 | 28,412 | 99.33% |
| tcp | runs-32k | latency | 10 | 6.44 | 5.94 | 0.983× [0.850, 1.138] | 2,106,048 | 14,208 | 99.33% |

## Adaptive state and managed allocation

Adaptive counters and cache state are cumulative through the end of the phase, after its drain. Plan hits this phase subtract the preceding phase's ending counter (the first phase starts at zero). Allocation covers only the timed phase in the client process, including the proxy; it is approximate allocated volume rather than retained memory. Encoding attempts and bypasses count outgoing non-control messages, which can include queue-drain application barriers as well as measured payloads.

| Transport | Workload | Phase | Allocated bytes off/on | Candidates on | Attempts on | Bypasses on | Probe interval on | Plans on | Plan hits cumulative on | Plan hits this phase on |
|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|
| tcp | cold-first-32k | first | 205,916 / 221,340 | 0 | 0 | 1 | 64 | 0 | 0 | 0 |
| tcp | cold-first-32k | cold | 6,657,780 / 5,173,232 | 106 | 56 | 8 | 1 | 1 | 55 | 55 |
| tcp | cold-first-32k | warm | 13,518,888 / 8,642,680 | 157 | 184 | 8 | 1 | 2 | 182 | 127 |
| tcp | cold-first-32k | latency | 6,737,312 / 4,324,084 | 185 | 248 | 8 | 1 | 2 | 246 | 64 |
| tcp | delta-32k | cold | 7,000,744 / 5,390,024 | 99 | 56 | 8 | 1 | 1 | 55 | 55 |
| tcp | delta-32k | warm | 13,519,892 / 8,656,508 | 150 | 184 | 8 | 1 | 2 | 182 | 127 |
| tcp | delta-32k | latency | 6,742,712 / 4,325,152 | 178 | 248 | 8 | 1 | 2 | 246 | 64 |
| tcp | delta-blocks-32k | cold | 7,059,648 / 8,266,860 | 147 | 57 | 7 | 1 | 1 | 56 | 56 |
| tcp | delta-blocks-32k | warm | 13,521,956 / 12,523,560 | 236 | 185 | 7 | 1 | 4 | 183 | 127 |
| tcp | delta-blocks-32k | latency | 6,730,976 / 5,638,832 | 286 | 249 | 7 | 1 | 4 | 247 | 64 |
| tcp | random-32k | cold | 7,010,992 / 7,104,236 | 46 | 0 | 64 | 64 | 0 | 0 | 0 |
| tcp | random-32k | warm | 13,630,488 / 13,690,648 | 147 | 0 | 192 | 64 | 0 | 0 | 0 |
| tcp | random-32k | latency | 6,737,360 / 6,780,716 | 194 | 0 | 256 | 64 | 0 | 0 | 0 |
| tcp | repeated-1k | cold | 860,316 / 1,076,804 | 7 | 56 | 8 | 1 | 1 | 55 | 55 |
| tcp | repeated-1k | warm | 1,259,788 / 1,129,384 | 11 | 184 | 8 | 1 | 2 | 182 | 127 |
| tcp | repeated-1k | latency | 653,832 / 547,960 | 13 | 248 | 8 | 1 | 2 | 246 | 64 |
| tcp | repeated-32k | cold | 6,987,652 / 3,979,196 | 6 | 56 | 8 | 1 | 1 | 56 | 56 |
| tcp | repeated-32k | warm | 13,688,424 / 5,438,976 | 10 | 184 | 8 | 1 | 2 | 182 | 127 |
| tcp | repeated-32k | latency | 6,744,956 / 2,720,476 | 12 | 248 | 8 | 1 | 2 | 246 | 64 |
| tcp | runs-32k | cold | 7,045,484 / 3,169,252 | 0 | 64 | 0 | 1 | 0 | 0 | 0 |
| tcp | runs-32k | warm | 13,662,828 / 5,735,176 | 0 | 192 | 0 | 1 | 0 | 0 | 0 |
| tcp | runs-32k | latency | 6,726,332 / 2,872,172 | 0 | 256 | 0 | 1 | 0 | 0 | 0 |

## Application round-trip latency

| Transport | Workload | Phase | p50 ms off/on | p95 ms off/on | p99 ms off/on | p95 speedup off/on [95% CI] | Client CPU ms off/on | Definitions on | Cache bytes on |
|---|---|---|---|---|---|---|---|---:|---:|
| tcp | cold-first-32k | first | 22.306 / 27.138 | 22.306 / 27.138 | 22.306 / 27.138 | 0.981× [0.825, 1.217] | 0.000 / 0.000 | 0 | 18,560 |
| tcp | cold-first-32k | cold | 6.528 / 6.238 | 9.449 / 12.075 | 10.882 / 17.087 | 0.870× [0.746, 1.028] | 15.625 / 23.438 | 35 | 451,218 |
| tcp | cold-first-32k | warm | 6.219 / 5.829 | 8.871 / 8.263 | 9.973 / 9.812 | 1.171× [0.872, 1.708] | 31.250 / 31.250 | 35 | 506,634 |
| tcp | cold-first-32k | latency | 5.226 / 5.009 | 7.610 / 6.762 | 9.190 / 7.600 | 1.049× [0.948, 1.167] | 46.875 / 31.250 | 35 | 534,562 |
| tcp | delta-32k | cold | 6.405 / 6.533 | 13.641 / 14.434 | 24.951 / 29.207 | 0.924× [0.777, 1.103] | 0.000 / 15.625 | 35 | 444,236 |
| tcp | delta-32k | warm | 5.740 / 6.168 | 7.833 / 9.119 | 9.012 / 10.951 | 0.946× [0.765, 1.256] | 15.625 / 62.500 | 35 | 499,652 |
| tcp | delta-32k | latency | 4.742 / 5.346 | 6.663 / 7.525 | 8.195 / 9.186 | 0.842× [0.685, 1.008] | 31.250 / 31.250 | 35 | 527,580 |
| tcp | delta-blocks-32k | cold | 6.543 / 6.001 | 14.079 / 11.895 | 26.263 / 24.217 | 1.040× [0.826, 1.291] | 31.250 / 23.438 | 28 | 495,711 |
| tcp | delta-blocks-32k | warm | 6.128 / 5.912 | 9.402 / 8.546 | 10.246 / 11.409 | 0.936× [0.818, 1.062] | 46.875 / 46.875 | 30 | 601,753 |
| tcp | delta-blocks-32k | latency | 5.176 / 4.951 | 7.451 / 7.395 | 8.905 / 11.400 | 0.930× [0.798, 1.075] | 23.438 / 31.250 | 30 | 654,233 |
| tcp | random-32k | cold | 6.199 / 6.399 | 13.544 / 14.162 | 24.363 / 28.856 | 0.772× [0.545, 1.004] | 23.438 / 15.625 | 0 | 69,050 |
| tcp | random-32k | warm | 6.263 / 6.125 | 10.640 / 8.259 | 12.420 / 9.416 | 1.129× [0.868, 1.488] | 39.062 / 54.688 | 0 | 173,452 |
| tcp | random-32k | latency | 4.880 / 5.124 | 7.561 / 6.639 | 8.752 / 8.455 | 1.025× [0.907, 1.147] | 31.250 / 23.438 | 0 | 224,327 |
| tcp | repeated-1k | cold | 5.892 / 5.806 | 13.102 / 12.638 | 24.195 / 27.517 | 1.063× [0.994, 1.146] | 15.625 / 23.438 | 3 | 320,237 |
| tcp | repeated-1k | warm | 6.046 / 5.595 | 8.945 / 7.734 | 11.110 / 8.302 | 1.306× [1.158, 1.513] | 46.875 / 31.250 | 3 | 323,457 |
| tcp | repeated-1k | latency | 4.874 / 4.687 | 6.773 / 6.684 | 8.418 / 9.731 | 1.121× [0.964, 1.377] | 31.250 / 23.438 | 3 | 324,711 |
| tcp | repeated-32k | cold | 6.712 / 6.281 | 13.538 / 13.398 | 23.988 / 26.826 | 0.772× [0.467, 1.080] | 31.250 / 46.875 | 47 | 366,618 |
| tcp | repeated-32k | warm | 6.267 / 6.122 | 9.635 / 9.201 | 11.635 / 11.040 | 0.938× [0.617, 1.202] | 39.062 / 39.062 | 47 | 370,386 |
| tcp | repeated-32k | latency | 4.848 / 5.431 | 6.594 / 6.998 | 8.476 / 8.138 | 0.891× [0.619, 1.127] | 31.250 / 46.875 | 47 | 371,386 |
| tcp | runs-32k | cold | 6.201 / 6.003 | 14.304 / 17.475 | 25.844 / 35.394 | 0.889× [0.740, 1.120] | 15.625 / 15.625 | 0 | 18,560 |
| tcp | runs-32k | warm | 5.618 / 6.004 | 7.654 / 8.820 | 9.137 / 9.720 | 1.190× [0.827, 1.925] | 39.062 / 46.875 | 0 | 18,560 |
| tcp | runs-32k | latency | 4.716 / 5.115 | 6.607 / 6.731 | 7.622 / 8.719 | 0.971× [0.743, 1.257] | 23.438 / 31.250 | 0 | 18,560 |

## Complete connection byte cost

Uses the ending cumulative counter, including setup, every reported phase and any interphase traffic. Between-phase bytes are the gaps between setup/phase snapshots and are already included in the total.

| Transport | Workload | Pairs | Payload bytes | Total bytes off | Total bytes on | Bytes saved | Between-phase bytes off/on | Setup bytes off/on | Setup ms off/on |
|---|---|---:|---:|---:|---:|---:|---|---|---|
| tcp | cold-first-32k | 10 | 8,388,608 | 8,433,968 | 2,575,384 | 69.46% | 0 / 0 | 9,904 / 10,090 | 354.92 / 365.34 |
| tcp | delta-32k | 10 | 8,388,608 | 8,433,968 | 2,561,286 | 69.63% | 0 / 0 | 9,904 / 10,090 | 362.08 / 377.74 |
| tcp | delta-blocks-32k | 10 | 8,388,608 | 8,433,968 | 5,052,964 | 40.09% | 0 / 0 | 9,904 / 10,090 | 349.44 / 372.69 |
| tcp | random-32k | 10 | 8,388,608 | 8,433,968 | 8,434,154 | -0.00% | 0 / 0 | 9,904 / 10,090 | 338.47 / 359.25 |
| tcp | repeated-1k | 10 | 262,144 | 307,248 | 112,600 | 63.35% | 0 / 0 | 9,904 / 10,090 | 344.27 / 368.09 |
| tcp | repeated-32k | 10 | 8,388,608 | 8,433,968 | 529,213 | 93.73% | 0 / 0 | 9,904 / 10,090 | 338.48 / 376.70 |
| tcp | runs-32k | 10 | 8,388,608 | 8,433,968 | 66,726 | 99.21% | 0 / 0 | 9,904 / 10,090 | 341.41 / 342.82 |
