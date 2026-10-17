# Binary compression transfer benchmark

Source: `docs\binary-compression-benchmark\2026-10-15-tcp-direct-plans\results.json`. Per-mode figures are medians of complete runs. On/off comparisons pair the same round. Compression is the only setting changed.

Socket bytes count both directions once, including dictionary updates, dictionary acknowledgements, application acknowledgements, and SocketJack framing. They exclude IP/TCP/UDP/Ethernet headers. Setup is reported separately and is included once in the lifetime totals. Lifetime totals use the ending cumulative counter, including traffic between phase snapshots.

Bulk throughput is useful application payload divided by the time from the first send through the last validated application acknowledgement. Bulk RTT uses eight outstanding messages; latency RTT uses one outstanding message after warm training. Byte counters drain after timing stops; settle time is excluded from throughput. Phase order and message counts follow the raw file's phasePlans metadata; legacy results without that field use cold, warm, latency. Every connection retains its adaptive cache across phases.

Throughput ratios use the geometric mean of paired on/off ratios and 10,000 paired bootstrap resamples (seed 7301; percentile 95% interval). An interval spanning 1 does not establish a speed change. Round-tail medians are not a pooled per-message percentile. With 64 latency samples, p99 is effectively the maximum sample and is descriptive. Fresh processes make the cold phase include first-use JIT costs; warm phases reuse the process and cache. CPU and optional managed-allocation figures cover the client process and its counting proxy, excluding the drain. Allocation is approximate process-wide allocation rather than retained memory. Setup timing measures Connect; setup byte totals also include its quiet-drain traffic. Loopback results do not establish WAN performance.

| Transport | Workload | Phase | Pairs | MiB/s off | MiB/s on | On/off speed [95% CI] | Total bytes off | Total bytes on | Bytes saved |
|---|---|---|---:|---:|---:|---|---:|---:|---:|
| tcp | cold-first-32k | first | 6 | 1.74 | 1.57 | 0.888× [0.832, 0.930] | 32,905 | 32,905 | 0.00% |
| tcp | cold-first-32k | cold | 6 | 25.65 | 21.81 | 0.864× [0.829, 0.902] | 2,073,015 | 764,659 | 63.11% |
| tcp | cold-first-32k | warm | 6 | 27.42 | 27.85 | 1.003× [0.977, 1.031] | 4,212,096 | 1,164,544 | 72.35% |
| tcp | cold-first-32k | latency | 6 | 8.11 | 7.90 | 0.969× [0.942, 0.991] | 2,106,048 | 582,272 | 72.35% |
| tcp | delta-32k | cold | 6 | 23.75 | 20.24 | 0.808× [0.720, 0.864] | 2,105,920 | 797,564 | 62.13% |
| tcp | delta-32k | warm | 6 | 28.10 | 28.06 | 0.963× [0.908, 1.009] | 4,212,096 | 1,164,544 | 72.35% |
| tcp | delta-32k | latency | 6 | 8.31 | 8.28 | 0.994× [0.971, 1.019] | 2,106,048 | 582,272 | 72.35% |
| tcp | delta-blocks-32k | cold | 6 | 24.33 | 19.91 | 0.818× [0.798, 0.839] | 2,105,920 | 1,270,791 | 39.66% |
| tcp | delta-blocks-32k | warm | 6 | 29.26 | 27.98 | 0.951× [0.939, 0.962] | 4,212,096 | 2,241,825 | 46.78% |
| tcp | delta-blocks-32k | latency | 6 | 8.26 | 8.08 | 0.970× [0.949, 0.989] | 2,106,048 | 1,089,984 | 48.25% |
| tcp | random-32k | cold | 6 | 23.94 | 22.68 | 0.935× [0.902, 0.970] | 2,105,920 | 2,105,920 | 0.00% |
| tcp | random-32k | warm | 6 | 28.79 | 28.15 | 0.978× [0.953, 1.008] | 4,212,096 | 4,212,096 | 0.00% |
| tcp | random-32k | latency | 6 | 8.28 | 8.01 | 0.969× [0.950, 0.992] | 2,106,048 | 2,106,048 | 0.00% |
| tcp | repeated-1k | cold | 6 | 0.79 | 0.69 | 0.887× [0.861, 0.909] | 74,240 | 30,766 | 58.56% |
| tcp | repeated-1k | warm | 6 | 0.90 | 0.88 | 0.991× [0.895, 1.116] | 148,736 | 47,232 | 68.24% |
| tcp | repeated-1k | latency | 6 | 0.26 | 0.25 | 0.944× [0.894, 0.991] | 74,368 | 23,616 | 68.24% |
| tcp | repeated-32k | cold | 6 | 23.15 | 19.83 | 0.841× [0.790, 0.885] | 2,105,920 | 346,798 | 83.53% |
| tcp | repeated-32k | warm | 6 | 28.31 | 26.90 | 0.956× [0.935, 0.979] | 4,212,096 | 114,560 | 97.28% |
| tcp | repeated-32k | latency | 6 | 8.23 | 8.17 | 0.992× [0.983, 1.002] | 2,106,048 | 57,280 | 97.28% |
| tcp | runs-32k | cold | 6 | 23.01 | 21.97 | 0.953× [0.914, 0.995] | 2,105,920 | 14,016 | 99.33% |
| tcp | runs-32k | warm | 6 | 28.65 | 28.48 | 1.002× [0.966, 1.049] | 4,212,096 | 28,412 | 99.33% |
| tcp | runs-32k | latency | 6 | 8.25 | 8.28 | 1.012× [1.000, 1.032] | 2,106,048 | 14,208 | 99.33% |

## Adaptive state and managed allocation

Adaptive counters and cache state are cumulative through the end of the phase, after its drain. Plan hits this phase subtract the preceding phase's ending counter (the first phase starts at zero). Allocation covers only the timed phase in the client process, including the proxy; it is approximate allocated volume rather than retained memory. Encoding attempts and bypasses count outgoing non-control messages, which can include queue-drain application barriers as well as measured payloads.

| Transport | Workload | Phase | Allocated bytes off/on | Candidates on | Attempts on | Bypasses on | Probe interval on | Plans on | Plan hits cumulative on | Plan hits this phase on |
|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|
| tcp | cold-first-32k | first | 208,668 / 226,492 | 0 | 0 | 1 | 64 | 0 | 0 | 0 |
| tcp | cold-first-32k | cold | 6,636,900 / 4,794,652 | 99 | 56 | 8 | 1 | 1 | 56 | 56 |
| tcp | cold-first-32k | warm | 13,506,460 / 8,655,228 | 150 | 184 | 8 | 1 | 2 | 184 | 128 |
| tcp | cold-first-32k | latency | 6,745,708 / 4,318,584 | 178 | 248 | 8 | 1 | 2 | 248 | 64 |
| tcp | delta-32k | cold | 6,962,384 / 5,017,428 | 99 | 56 | 8 | 1 | 1 | 56 | 56 |
| tcp | delta-32k | warm | 13,508,616 / 8,580,672 | 150 | 184 | 8 | 1 | 2 | 184 | 128 |
| tcp | delta-32k | latency | 6,746,588 / 4,333,760 | 178 | 248 | 8 | 1 | 2 | 248 | 64 |
| tcp | delta-blocks-32k | cold | 7,009,732 / 6,581,124 | 147 | 57 | 7 | 1 | 4 | 57 | 57 |
| tcp | delta-blocks-32k | warm | 13,525,652 / 11,902,672 | 236 | 185 | 7 | 1 | 7 | 187 | 130 |
| tcp | delta-blocks-32k | latency | 6,726,768 / 5,833,256 | 286 | 249 | 7 | 1 | 7 | 251 | 64 |
| tcp | random-32k | cold | 7,014,360 / 7,047,696 | 0 | 0 | 64 | 64 | 0 | 0 | 0 |
| tcp | random-32k | warm | 13,516,964 / 13,520,040 | 0 | 0 | 192 | 64 | 0 | 0 | 0 |
| tcp | random-32k | latency | 6,732,208 / 6,734,128 | 0 | 0 | 256 | 64 | 0 | 0 | 0 |
| tcp | repeated-1k | cold | 871,540 / 731,292 | 7 | 56 | 8 | 1 | 1 | 56 | 56 |
| tcp | repeated-1k | warm | 1,257,860 / 1,110,340 | 11 | 184 | 8 | 1 | 2 | 184 | 128 |
| tcp | repeated-1k | latency | 652,880 / 554,448 | 13 | 248 | 8 | 1 | 2 | 248 | 64 |
| tcp | repeated-32k | cold | 6,985,332 / 3,648,156 | 7 | 56 | 8 | 1 | 1 | 56 | 56 |
| tcp | repeated-32k | warm | 13,552,344 / 5,407,564 | 11 | 184 | 8 | 1 | 2 | 184 | 128 |
| tcp | repeated-32k | latency | 6,731,012 / 2,717,544 | 13 | 248 | 8 | 1 | 2 | 248 | 64 |
| tcp | runs-32k | cold | 7,012,164 / 3,213,236 | 0 | 64 | 0 | 1 | 0 | 0 | 0 |
| tcp | runs-32k | warm | 13,535,264 / 5,721,756 | 0 | 192 | 0 | 1 | 0 | 0 | 0 |
| tcp | runs-32k | latency | 6,733,444 / 2,894,772 | 0 | 256 | 0 | 1 | 0 | 0 | 0 |

## Application round-trip latency

| Transport | Workload | Phase | p50 ms off/on | p95 ms off/on | p99 ms off/on | p95 speedup off/on [95% CI] | Client CPU ms off/on | Definitions on | Cache bytes on |
|---|---|---|---|---|---|---|---|---:|---:|
| tcp | cold-first-32k | first | 16.865 / 18.818 | 16.865 / 18.818 | 16.865 / 18.818 | 0.880× [0.823, 0.924] | 0.000 / 0.000 | 0 | 18,560 |
| tcp | cold-first-32k | cold | 4.569 / 4.517 | 6.273 / 7.327 | 7.619 / 10.411 | 0.917× [0.716, 1.180] | 0.000 / 31.250 | 35 | 149,068 |
| tcp | cold-first-32k | warm | 4.315 / 4.332 | 7.516 / 5.559 | 8.678 / 6.075 | 1.235× [0.968, 1.548] | 15.625 / 23.438 | 35 | 204,484 |
| tcp | cold-first-32k | latency | 3.660 / 3.747 | 4.341 / 4.433 | 8.073 / 9.562 | 0.968× [0.892, 1.047] | 31.250 / 7.812 | 35 | 232,412 |
| tcp | delta-32k | cold | 4.528 / 4.673 | 10.269 / 9.703 | 18.402 / 19.108 | 0.992× [0.855, 1.108] | 15.625 / 15.625 | 35 | 149,068 |
| tcp | delta-32k | warm | 4.450 / 4.318 | 5.352 / 5.095 | 5.929 / 5.918 | 0.999× [0.939, 1.046] | 31.250 / 54.688 | 35 | 204,484 |
| tcp | delta-32k | latency | 3.641 / 3.639 | 4.275 / 4.327 | 5.813 / 5.353 | 0.963× [0.901, 1.032] | 15.625 / 7.812 | 35 | 232,412 |
| tcp | delta-blocks-32k | cold | 4.422 / 4.542 | 10.056 / 10.179 | 18.268 / 19.367 | 0.973× [0.878, 1.061] | 15.625 / 0.000 | 28 | 203,895 |
| tcp | delta-blocks-32k | warm | 4.249 / 4.338 | 5.290 / 5.829 | 5.789 / 6.530 | 0.874× [0.775, 0.960] | 7.812 / 23.438 | 30 | 309,921 |
| tcp | delta-blocks-32k | latency | 3.656 / 3.680 | 4.443 / 4.526 | 5.953 / 6.248 | 0.975× [0.935, 1.020] | 23.438 / 39.062 | 30 | 362,401 |
| tcp | random-32k | cold | 4.476 / 4.466 | 9.791 / 9.946 | 18.488 / 20.869 | 0.929× [0.842, 1.001] | 15.625 / 15.625 | 0 | 18,560 |
| tcp | random-32k | warm | 4.270 / 4.330 | 5.833 / 5.919 | 7.400 / 8.247 | 1.023× [0.870, 1.222] | 39.062 / 39.062 | 0 | 18,560 |
| tcp | random-32k | latency | 3.628 / 3.708 | 4.335 / 4.651 | 6.912 / 6.768 | 0.934× [0.899, 0.968] | 15.625 / 15.625 | 0 | 18,560 |
| tcp | repeated-1k | cold | 4.385 / 4.453 | 9.955 / 9.458 | 17.902 / 19.694 | 1.025× [0.932, 1.115] | 31.250 / 0.000 | 3 | 25,069 |
| tcp | repeated-1k | warm | 4.230 / 4.318 | 7.054 / 6.570 | 7.850 / 8.135 | 0.984× [0.686, 1.365] | 39.062 / 39.062 | 3 | 28,289 |
| tcp | repeated-1k | latency | 3.601 / 3.631 | 4.214 / 5.349 | 6.358 / 8.188 | 0.778× [0.674, 0.891] | 7.812 / 23.438 | 3 | 29,543 |
| tcp | repeated-32k | cold | 4.559 / 4.652 | 9.659 / 9.866 | 17.873 / 18.645 | 0.984× [0.922, 1.080] | 0.000 / 15.625 | 47 | 71,700 |
| tcp | repeated-32k | warm | 4.371 / 4.363 | 6.859 / 6.345 | 8.410 / 7.029 | 1.014× [0.826, 1.193] | 31.250 / 23.438 | 47 | 75,468 |
| tcp | repeated-32k | latency | 3.636 / 3.670 | 4.486 / 4.366 | 6.298 / 5.959 | 1.017× [0.989, 1.045] | 15.625 / 31.250 | 47 | 76,468 |
| tcp | runs-32k | cold | 4.768 / 4.458 | 10.186 / 13.409 | 18.999 / 26.790 | 0.797× [0.729, 0.870] | 15.625 / 15.625 | 0 | 18,560 |
| tcp | runs-32k | warm | 4.351 / 4.266 | 5.970 / 5.507 | 6.832 / 5.889 | 1.075× [0.954, 1.220] | 31.250 / 23.438 | 0 | 18,560 |
| tcp | runs-32k | latency | 3.649 / 3.611 | 4.424 / 4.376 | 6.065 / 7.267 | 1.013× [0.990, 1.032] | 23.438 / 0.000 | 0 | 18,560 |

## Complete connection byte cost

Uses the ending cumulative counter, including setup, every reported phase and any interphase traffic. Between-phase bytes are the gaps between setup/phase snapshots and are already included in the total.

| Transport | Workload | Pairs | Payload bytes | Total bytes off | Total bytes on | Bytes saved | Between-phase bytes off/on | Setup bytes off/on | Setup ms off/on |
|---|---|---:|---:|---:|---:|---:|---|---|---|
| tcp | cold-first-32k | 6 | 8,388,608 | 8,433,968 | 2,554,470 | 69.71% | 0 / 0 | 9,904 / 10,090 | 261.51 / 274.43 |
| tcp | delta-32k | 6 | 8,388,608 | 8,433,968 | 2,554,470 | 69.71% | 0 / 0 | 9,904 / 10,090 | 261.04 / 273.27 |
| tcp | delta-blocks-32k | 6 | 8,388,608 | 8,433,968 | 4,612,690 | 45.31% | 0 / 0 | 9,904 / 10,090 | 268.99 / 282.56 |
| tcp | random-32k | 6 | 8,388,608 | 8,433,968 | 8,434,154 | -0.00% | 0 / 0 | 9,904 / 10,090 | 265.53 / 275.21 |
| tcp | repeated-1k | 6 | 262,144 | 307,248 | 111,704 | 63.64% | 0 / 0 | 9,904 / 10,090 | 270.02 / 272.25 |
| tcp | repeated-32k | 6 | 8,388,608 | 8,433,968 | 528,728 | 93.73% | 0 / 0 | 9,904 / 10,090 | 262.47 / 275.39 |
| tcp | runs-32k | 6 | 8,388,608 | 8,433,968 | 66,726 | 99.21% | 0 / 0 | 9,904 / 10,090 | 273.36 / 281.18 |
