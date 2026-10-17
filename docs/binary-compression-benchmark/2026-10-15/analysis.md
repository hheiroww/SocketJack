# Binary compression transfer benchmark

Source: `docs\binary-compression-benchmark\2026-10-15\results.json`. Per-mode figures are medians of complete runs. On/off comparisons pair the same round. Compression is the only setting changed.

Socket bytes count both directions once, including dictionary updates, dictionary acknowledgements, application acknowledgements, and SocketJack framing. They exclude IP/TCP/UDP/Ethernet headers. Setup is reported separately and is included once in the lifetime totals. Lifetime totals use the ending cumulative counter, including traffic between phase snapshots.

Bulk throughput is useful application payload divided by the time from the first send through the last validated application acknowledgement. Bulk RTT uses eight outstanding messages; latency RTT uses one outstanding message after warm training. Byte counters drain after timing stops; settle time is excluded from throughput. Warm results use the dictionary learned during the reported cold phase.

Throughput ratios use the geometric mean of paired on/off ratios and 10,000 paired bootstrap resamples (seed 7301; percentile 95% interval). An interval spanning 1 does not establish a speed change. Round-tail medians are not a pooled per-message percentile. With 64 latency samples, p99 is effectively the maximum sample and is descriptive. Fresh processes make the cold phase include first-use JIT costs; warm phases reuse the process and cache. CPU figures cover the client process and its counting proxy. Setup timing measures Connect; setup byte totals also include its quiet-drain traffic. Loopback results do not establish WAN performance.

| Transport | Workload | Phase | Pairs | MiB/s off | MiB/s on | On/off speed [95% CI] | Total bytes off | Total bytes on | Bytes saved |
|---|---|---|---:|---:|---:|---|---:|---:|---:|
| reliable-udp | delta-32k | cold | 10 | 14.63 | 10.22 | 0.687× [0.634, 0.751] | 2,136,606 | 816,038 | 61.81% |
| reliable-udp | delta-32k | warm | 10 | 18.49 | 11.32 | 0.604× [0.565, 0.635] | 4,275,633 | 1,255,169 | 70.64% |
| reliable-udp | delta-32k | latency | 10 | 5.66 | 5.20 | 0.895× [0.847, 0.950] | 2,138,981 | 628,133 | 70.63% |
| reliable-udp | random-32k | cold | 10 | 15.35 | 10.15 | 0.684× [0.620, 0.760] | 2,136,606 | 2,136,602 | 0.00% |
| reliable-udp | random-32k | warm | 10 | 17.00 | 11.20 | 0.653× [0.568, 0.760] | 4,275,622 | 4,275,597 | 0.00% |
| reliable-udp | random-32k | latency | 10 | 5.24 | 4.74 | 1.183× [0.778, 2.490] | 2,138,981 | 2,138,981 | 0.00% |
| reliable-udp | repeated-1k | cold | 10 | 0.65 | 0.53 | 0.833× [0.778, 0.894] | 76,389 | 34,959 | 54.24% |
| reliable-udp | repeated-1k | warm | 10 | 0.71 | 0.67 | 0.913× [0.800, 1.015] | 153,197 | 56,173 | 63.33% |
| reliable-udp | repeated-1k | latency | 10 | 0.25 | 0.22 | 0.870× [0.789, 0.967] | 76,777 | 28,265 | 63.19% |
| reliable-udp | repeated-32k | cold | 10 | 15.29 | 9.51 | 0.604× [0.525, 0.667] | 2,136,595 | 360,374 | 83.13% |
| reliable-udp | repeated-32k | warm | 10 | 17.63 | 12.37 | 0.711× [0.649, 0.772] | 4,275,656 | 200,981 | 95.30% |
| reliable-udp | repeated-32k | latency | 10 | 5.60 | 5.28 | 0.914× [0.847, 0.990] | 2,138,981 | 100,837 | 95.29% |
| reliable-udp | runs-32k | cold | 10 | 15.02 | 11.05 | 0.716× [0.596, 0.838] | 2,136,618 | 23,537 | 98.90% |
| reliable-udp | runs-32k | warm | 10 | 18.24 | 12.79 | 0.655× [0.532, 0.757] | 4,275,622 | 36,140 | 99.15% |
| reliable-udp | runs-32k | latency | 10 | 5.81 | 5.39 | 0.845× [0.640, 1.029] | 2,139,005 | 17,253 | 99.19% |
| tcp | delta-32k | cold | 10 | 21.11 | 10.41 | 0.487× [0.463, 0.509] | 2,105,920 | 688,727 | 67.30% |
| tcp | delta-32k | warm | 10 | 24.85 | 12.42 | 0.517× [0.484, 0.552] | 4,212,096 | 1,232,000 | 70.75% |
| tcp | delta-32k | latency | 10 | 7.67 | 5.48 | 0.768× [0.675, 0.892] | 2,106,048 | 616,000 | 70.75% |
| tcp | random-32k | cold | 10 | 22.08 | 10.81 | 0.485× [0.424, 0.547] | 2,105,920 | 2,105,920 | 0.00% |
| tcp | random-32k | warm | 10 | 26.92 | 12.50 | 0.484× [0.442, 0.528] | 4,212,096 | 4,212,096 | 0.00% |
| tcp | random-32k | latency | 10 | 7.77 | 5.74 | 0.721× [0.668, 0.772] | 2,106,048 | 2,106,048 | 0.00% |
| tcp | repeated-1k | cold | 10 | 0.73 | 0.58 | 0.792× [0.756, 0.835] | 74,240 | 31,642 | 57.38% |
| tcp | repeated-1k | warm | 10 | 0.82 | 0.76 | 0.947× [0.881, 1.040] | 148,736 | 51,712 | 65.23% |
| tcp | repeated-1k | latency | 10 | 0.26 | 0.23 | 0.931× [0.866, 1.009] | 74,368 | 25,856 | 65.23% |
| tcp | repeated-32k | cold | 10 | 19.68 | 10.59 | 0.537× [0.510, 0.566] | 2,105,920 | 193,294 | 90.82% |
| tcp | repeated-32k | warm | 10 | 25.03 | 12.64 | 0.496× [0.448, 0.540] | 4,212,096 | 190,848 | 95.47% |
| tcp | repeated-32k | latency | 10 | 7.37 | 5.28 | 0.734× [0.691, 0.779] | 2,106,048 | 95,424 | 95.47% |
| tcp | runs-32k | cold | 10 | 22.25 | 12.48 | 0.575× [0.545, 0.606] | 2,105,920 | 18,968 | 99.10% |
| tcp | runs-32k | warm | 10 | 25.40 | 14.55 | 0.552× [0.512, 0.587] | 4,212,096 | 31,647 | 99.25% |
| tcp | runs-32k | latency | 10 | 7.63 | 6.03 | 0.766× [0.719, 0.810] | 2,106,048 | 14,784 | 99.30% |

## Application round-trip latency

| Transport | Workload | Phase | p50 ms off/on | p95 ms off/on | p99 ms off/on | p95 speedup off/on [95% CI] | Client CPU ms off/on | Definitions on | Cache bytes on |
|---|---|---|---|---|---|---|---|---:|---:|
| reliable-udp | delta-32k | cold | 9.067 / 7.080 | 16.517 / 16.477 | 26.279 / 34.833 | 1.166× [0.952, 1.480] | 23.438 / 31.250 | 80 | 335,545 |
| reliable-udp | delta-32k | warm | 7.579 / 7.185 | 11.844 / 10.528 | 13.983 / 13.571 | 1.050× [0.910, 1.224] | 46.875 / 70.312 | 80 | 335,316 |
| reliable-udp | delta-32k | latency | 5.097 / 5.625 | 7.540 / 8.140 | 11.846 / 10.908 | 0.953× [0.852, 1.057] | 62.500 / 54.688 | 80 | 339,528 |
| reliable-udp | random-32k | cold | 8.566 / 9.001 | 16.608 / 16.106 | 25.788 / 32.150 | 1.069× [0.982, 1.149] | 39.062 / 39.062 | 0 | 744,202 |
| reliable-udp | random-32k | warm | 8.253 / 8.341 | 12.469 / 12.634 | 13.810 / 14.342 | 1.038× [0.826, 1.359] | 54.688 / 101.562 | 0 | 733,074 |
| reliable-udp | random-32k | latency | 5.471 / 6.008 | 8.450 / 9.231 | 12.234 / 13.817 | 1.205× [0.691, 3.098] | 54.688 / 39.062 | 0 | 731,795 |
| reliable-udp | repeated-1k | cold | 5.505 / 5.722 | 12.897 / 11.872 | 21.012 / 21.978 | 1.091× [0.965, 1.220] | 7.812 / 15.625 | 6 | 18,384 |
| reliable-udp | repeated-1k | warm | 5.588 / 5.315 | 9.366 / 8.243 | 10.829 / 9.634 | 1.043× [0.807, 1.255] | 31.250 / 46.875 | 6 | 18,393 |
| reliable-udp | repeated-1k | latency | 3.642 / 4.169 | 5.617 / 7.339 | 8.490 / 10.617 | 0.818× [0.696, 0.993] | 31.250 / 39.062 | 6 | 18,393 |
| reliable-udp | repeated-32k | cold | 8.202 / 7.639 | 15.386 / 18.842 | 24.053 / 36.481 | 0.797× [0.649, 0.955] | 39.062 / 23.438 | 98 | 187,865 |
| reliable-udp | repeated-32k | warm | 7.946 / 6.826 | 15.075 / 9.963 | 17.369 / 11.817 | 1.468× [1.264, 1.681] | 31.250 / 78.125 | 98 | 187,874 |
| reliable-udp | repeated-32k | latency | 5.117 / 5.402 | 8.134 / 8.666 | 12.356 / 12.773 | 0.977× [0.885, 1.085] | 62.500 / 78.125 | 98 | 187,874 |
| reliable-udp | runs-32k | cold | 9.037 / 6.862 | 17.746 / 12.770 | 26.018 / 30.213 | 1.241× [0.878, 1.594] | 23.438 / 31.250 | 2 | 26,414 |
| reliable-udp | runs-32k | warm | 7.781 / 6.290 | 11.503 / 9.826 | 14.274 / 13.515 | 1.036× [0.704, 1.352] | 70.312 / 39.062 | 4 | 26,567 |
| reliable-udp | runs-32k | latency | 4.893 / 5.396 | 8.111 / 8.687 | 11.166 / 11.931 | 0.845× [0.549, 1.178] | 39.062 / 39.062 | 4 | 24,844 |
| tcp | delta-32k | cold | 5.100 / 5.749 | 11.562 / 9.411 | 20.256 / 26.894 | 1.196× [1.099, 1.312] | 15.625 / 23.438 | 80 | 355,652 |
| tcp | delta-32k | warm | 4.727 / 5.693 | 9.165 / 8.808 | 11.392 / 11.176 | 1.017× [0.906, 1.133] | 39.062 / 85.938 | 80 | 356,966 |
| tcp | delta-32k | latency | 3.813 / 5.328 | 5.973 / 8.159 | 9.204 / 11.806 | 0.765× [0.599, 1.004] | 15.625 / 39.062 | 80 | 362,460 |
| tcp | random-32k | cold | 4.882 / 5.729 | 11.923 / 10.509 | 21.647 / 28.313 | 1.076× [0.822, 1.317] | 0.000 / 39.062 | 0 | 812,868 |
| tcp | random-32k | warm | 4.720 / 5.973 | 7.997 / 8.815 | 9.608 / 11.828 | 0.850× [0.763, 0.951] | 15.625 / 54.688 | 0 | 800,141 |
| tcp | random-32k | latency | 3.725 / 5.109 | 5.534 / 8.039 | 7.960 / 11.518 | 0.680× [0.610, 0.750] | 15.625 / 39.062 | 0 | 800,462 |
| tcp | repeated-1k | cold | 4.698 / 4.850 | 11.769 / 10.914 | 19.737 / 20.784 | 1.056× [0.950, 1.170] | 0.000 / 15.625 | 6 | 19,330 |
| tcp | repeated-1k | warm | 4.787 / 4.562 | 7.499 / 7.120 | 8.960 / 9.270 | 1.178× [1.020, 1.369] | 23.438 / 23.438 | 6 | 19,340 |
| tcp | repeated-1k | latency | 3.496 / 3.792 | 5.190 / 6.047 | 8.556 / 9.043 | 0.906× [0.832, 0.996] | 23.438 / 23.438 | 6 | 19,340 |
| tcp | repeated-32k | cold | 5.343 / 5.830 | 11.822 / 12.823 | 20.283 / 25.821 | 0.981× [0.885, 1.077] | 15.625 / 15.625 | 98 | 194,700 |
| tcp | repeated-32k | warm | 5.121 / 5.626 | 8.100 / 9.197 | 10.145 / 11.042 | 0.859× [0.692, 0.981] | 0.000 / 78.125 | 98 | 194,710 |
| tcp | repeated-32k | latency | 3.911 / 5.451 | 6.548 / 8.361 | 9.794 / 12.166 | 0.804× [0.692, 0.937] | 15.625 / 31.250 | 98 | 194,710 |
| tcp | runs-32k | cold | 4.974 / 5.469 | 12.040 / 9.908 | 20.784 / 26.099 | 1.253× [1.078, 1.493] | 7.812 / 15.625 | 2 | 27,822 |
| tcp | runs-32k | warm | 4.911 / 5.220 | 7.537 / 8.283 | 8.804 / 10.010 | 0.888× [0.769, 1.007] | 31.250 / 31.250 | 4 | 27,975 |
| tcp | runs-32k | latency | 3.615 / 4.818 | 6.083 / 7.172 | 10.462 / 10.149 | 0.824× [0.713, 0.941] | 23.438 / 23.438 | 4 | 26,252 |

## Complete connection byte cost

Uses the ending cumulative counter, including setup, cold, warm, latency and any interphase traffic. Between-phase bytes are the gaps between setup/phase snapshots and are already included in the total.

| Transport | Workload | Pairs | Payload bytes | Total bytes off | Total bytes on | Bytes saved | Between-phase bytes off/on | Setup bytes off/on | Setup ms off/on |
|---|---|---:|---:|---:|---:|---:|---|---|---|
| reliable-udp | delta-32k | 10 | 8,388,608 | 8,561,679 | 2,709,852 | 68.35% | 0 / 0 | 10,469 / 10,519 | 365.76 / 359.23 |
| reliable-udp | random-32k | 10 | 8,388,608 | 8,561,667 | 8,561,729 | -0.00% | 0 / 0 | 10,469 / 10,519 | 380.09 / 374.58 |
| reliable-udp | repeated-1k | 10 | 262,144 | 316,832 | 129,916 | 59.00% | 0 / 0 | 10,469 / 10,519 | 344.06 / 366.41 |
| reliable-udp | repeated-32k | 10 | 8,388,608 | 8,561,701 | 677,691 | 92.10% | 0 / 0 | 10,469 / 10,519 | 354.52 / 367.44 |
| reliable-udp | runs-32k | 10 | 8,388,608 | 8,561,725 | 87,479 | 98.98% | 0 / 0 | 10,469 / 10,519 | 381.55 / 376.29 |
| tcp | delta-32k | 10 | 8,388,608 | 8,433,968 | 2,546,681 | 69.80% | 0 / 0 | 9,904 / 9,954 | 287.56 / 312.61 |
| tcp | random-32k | 10 | 8,388,608 | 8,433,968 | 8,434,018 | -0.00% | 0 / 0 | 9,904 / 9,954 | 292.50 / 317.23 |
| tcp | repeated-1k | 10 | 262,144 | 307,248 | 119,164 | 61.22% | 0 / 0 | 9,904 / 9,954 | 285.61 / 298.59 |
| tcp | repeated-32k | 10 | 8,388,608 | 8,433,968 | 489,520 | 94.20% | 0 / 0 | 9,904 / 9,954 | 315.21 / 324.23 |
| tcp | runs-32k | 10 | 8,388,608 | 8,433,968 | 75,353 | 99.11% | 0 / 0 | 9,904 / 9,954 | 293.91 / 316.90 |
