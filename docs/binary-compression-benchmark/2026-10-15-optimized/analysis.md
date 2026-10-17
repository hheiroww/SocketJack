# Binary compression transfer benchmark

Source: `docs\binary-compression-benchmark\2026-10-15-optimized\results.json`. Per-mode figures are medians of complete runs. On/off comparisons pair the same round. Compression is the only setting changed.

Socket bytes count both directions once, including dictionary updates, dictionary acknowledgements, application acknowledgements, and SocketJack framing. They exclude IP/TCP/UDP/Ethernet headers. Setup is reported separately and is included once in the lifetime totals. Lifetime totals use the ending cumulative counter, including traffic between phase snapshots.

Bulk throughput is useful application payload divided by the time from the first send through the last validated application acknowledgement. Bulk RTT uses eight outstanding messages; latency RTT uses one outstanding message after warm training. Byte counters drain after timing stops; settle time is excluded from throughput. Phase order and message counts follow the raw file's phasePlans metadata; legacy results without that field use cold, warm, latency. Every connection retains its adaptive cache across phases.

Throughput ratios use the geometric mean of paired on/off ratios and 10,000 paired bootstrap resamples (seed 7301; percentile 95% interval). An interval spanning 1 does not establish a speed change. Round-tail medians are not a pooled per-message percentile. With 64 latency samples, p99 is effectively the maximum sample and is descriptive. Fresh processes make the cold phase include first-use JIT costs; warm phases reuse the process and cache. CPU and optional managed-allocation figures cover the client process and its counting proxy, excluding the drain. Allocation is approximate process-wide allocation rather than retained memory. Setup timing measures Connect; setup byte totals also include its quiet-drain traffic. Loopback results do not establish WAN performance.

| Transport | Workload | Phase | Pairs | MiB/s off | MiB/s on | On/off speed [95% CI] | Total bytes off | Total bytes on | Bytes saved |
|---|---|---|---:|---:|---:|---|---:|---:|---:|
| reliable-udp | delta-32k | cold | 10 | 18.39 | 15.05 | 0.828× [0.786, 0.883] | 2,136,560 | 841,903 | 60.60% |
| reliable-udp | delta-32k | warm | 10 | 21.36 | 22.07 | 1.030× [0.955, 1.128] | 4,275,598 | 1,259,137 | 70.55% |
| reliable-udp | delta-32k | latency | 10 | 6.79 | 7.02 | 1.034× [1.019, 1.048] | 2,138,921 | 630,057 | 70.54% |
| reliable-udp | random-32k | cold | 10 | 18.55 | 17.80 | 0.963× [0.919, 1.006] | 2,136,549 | 2,136,572 | -0.00% |
| reliable-udp | random-32k | warm | 10 | 20.84 | 21.51 | 1.006× [0.969, 1.045] | 4,275,574 | 4,275,587 | -0.00% |
| reliable-udp | random-32k | latency | 10 | 6.95 | 6.89 | 0.970× [0.919, 1.010] | 2,138,921 | 2,138,921 | 0.00% |
| reliable-udp | repeated-1k | cold | 10 | 0.74 | 0.69 | 1.011× [0.912, 1.204] | 76,389 | 35,799 | 53.14% |
| reliable-udp | repeated-1k | warm | 10 | 0.84 | 0.81 | 1.041× [0.949, 1.228] | 153,197 | 56,173 | 63.33% |
| reliable-udp | repeated-1k | latency | 10 | 0.27 | 0.27 | 1.030× [0.979, 1.126] | 76,777 | 28,265 | 63.19% |
| reliable-udp | repeated-32k | cold | 10 | 17.84 | 16.10 | 0.924× [0.891, 0.961] | 2,136,595 | 389,753 | 81.76% |
| reliable-udp | repeated-32k | warm | 10 | 21.24 | 23.59 | 1.134× [1.099, 1.172] | 4,275,610 | 204,979 | 95.21% |
| reliable-udp | repeated-32k | latency | 10 | 6.86 | 7.50 | 1.087× [1.066, 1.105] | 2,138,921 | 102,761 | 95.20% |
| reliable-udp | runs-32k | cold | 10 | 18.27 | 22.29 | 1.203× [1.167, 1.243] | 2,136,560 | 16,165 | 99.24% |
| reliable-udp | runs-32k | warm | 10 | 21.40 | 26.37 | 1.233× [1.198, 1.265] | 4,275,586 | 32,873 | 99.23% |
| reliable-udp | runs-32k | latency | 10 | 6.85 | 8.43 | 1.235× [1.217, 1.255] | 2,138,921 | 16,617 | 99.22% |
| tcp | delta-32k | cold | 10 | 22.58 | 15.64 | 0.706× [0.651, 0.777] | 2,105,920 | 712,009 | 66.19% |
| tcp | delta-32k | warm | 10 | 28.50 | 23.83 | 0.878× [0.791, 1.004] | 4,212,096 | 1,235,968 | 70.66% |
| tcp | delta-32k | latency | 10 | 8.44 | 7.98 | 0.950× [0.886, 1.026] | 2,106,048 | 617,984 | 70.66% |
| tcp | random-32k | cold | 10 | 23.68 | 21.41 | 0.912× [0.864, 0.974] | 2,105,920 | 2,105,920 | 0.00% |
| tcp | random-32k | warm | 10 | 29.28 | 28.42 | 0.981× [0.958, 1.008] | 4,212,096 | 4,212,096 | 0.00% |
| tcp | random-32k | latency | 10 | 8.44 | 8.50 | 1.007× [0.997, 1.017] | 2,106,048 | 2,106,048 | 0.00% |
| tcp | repeated-1k | cold | 10 | 0.80 | 0.71 | 0.902× [0.884, 0.921] | 74,240 | 33,619 | 54.72% |
| tcp | repeated-1k | warm | 10 | 0.89 | 0.87 | 0.967× [0.943, 0.989] | 148,736 | 51,712 | 65.23% |
| tcp | repeated-1k | latency | 10 | 0.27 | 0.26 | 0.981× [0.953, 1.007] | 74,368 | 25,856 | 65.23% |
| tcp | repeated-32k | cold | 10 | 22.70 | 17.33 | 0.757× [0.733, 0.780] | 2,105,920 | 353,361 | 83.22% |
| tcp | repeated-32k | warm | 10 | 28.41 | 25.68 | 0.906× [0.880, 0.930] | 4,212,096 | 194,816 | 95.37% |
| tcp | repeated-32k | latency | 10 | 8.46 | 8.17 | 0.962× [0.952, 0.971] | 2,106,048 | 97,408 | 95.37% |
| tcp | runs-32k | cold | 10 | 24.28 | 22.90 | 0.951× [0.935, 0.967] | 2,105,920 | 14,016 | 99.33% |
| tcp | runs-32k | warm | 10 | 28.88 | 28.92 | 0.993× [0.966, 1.019] | 4,212,096 | 28,412 | 99.33% |
| tcp | runs-32k | latency | 10 | 8.46 | 8.69 | 1.028× [1.013, 1.043] | 2,106,048 | 14,208 | 99.33% |

## Adaptive state and managed allocation

Adaptive counters and cache state are cumulative through the end of the phase, after its drain. Allocation covers only the timed phase in the client process, including the proxy; it is approximate allocated volume rather than retained memory. Encoding attempts and bypasses count outgoing non-control messages, which can include queue-drain application barriers as well as measured payloads.

| Transport | Workload | Phase | Allocated bytes off/on | Candidates on | Attempts on | Bypasses on | Probe interval on |
|---|---|---|---:|---:|---:|---:|---:|
| reliable-udp | delta-32k | cold | 5,362,764 / 6,493,808 | 102 | 60 | 8 | 1 |
| reliable-udp | delta-32k | warm | 10,531,624 / 11,496,744 | 214 | 188 | 8 | 1 |
| reliable-udp | delta-32k | latency | 5,388,308 / 5,856,168 | 278 | 252 | 8 | 1 |
| reliable-udp | random-32k | cold | 5,397,644 / 6,007,904 | 617 | 6 | 64 | 64 |
| reliable-udp | random-32k | warm | 10,454,860 / 10,647,108 | 809 | 8 | 192 | 64 |
| reliable-udp | random-32k | latency | 5,371,536 / 5,490,132 | 912 | 9 | 256 | 64 |
| reliable-udp | repeated-1k | cold | 942,312 / 1,435,888 | 4 | 60 | 8 | 1 |
| reliable-udp | repeated-1k | warm | 1,537,484 / 1,824,276 | 8 | 188 | 8 | 1 |
| reliable-udp | repeated-1k | latency | 809,976 / 916,336 | 10 | 252 | 8 | 1 |
| reliable-udp | repeated-32k | cold | 5,387,080 / 4,578,176 | 4 | 60 | 8 | 1 |
| reliable-udp | repeated-32k | warm | 10,493,344 / 6,904,400 | 8 | 188 | 8 | 1 |
| reliable-udp | repeated-32k | latency | 5,371,064 / 3,467,520 | 10 | 252 | 8 | 1 |
| reliable-udp | runs-32k | cold | 5,395,908 / 3,311,792 | 0 | 64 | 0 | 1 |
| reliable-udp | runs-32k | warm | 10,473,736 / 6,142,168 | 0 | 192 | 0 | 1 |
| reliable-udp | runs-32k | latency | 5,373,384 / 3,060,064 | 0 | 256 | 0 | 1 |
| tcp | delta-32k | cold | 6,966,984 / 6,840,364 | 78 | 64 | 3 | 1 |
| tcp | delta-32k | warm | 13,520,460 / 11,971,264 | 190 | 192 | 3 | 1 |
| tcp | delta-32k | latency | 6,741,540 / 6,019,624 | 254 | 256 | 3 | 1 |
| tcp | random-32k | cold | 6,998,764 / 7,609,540 | 617 | 6 | 64 | 64 |
| tcp | random-32k | warm | 13,520,348 / 13,708,188 | 809 | 8 | 192 | 64 |
| tcp | random-32k | latency | 6,729,512 / 6,825,336 | 912 | 9 | 256 | 64 |
| tcp | repeated-1k | cold | 876,576 / 1,225,660 | 4 | 60 | 8 | 1 |
| tcp | repeated-1k | warm | 1,245,220 / 1,412,268 | 8 | 188 | 8 | 1 |
| tcp | repeated-1k | latency | 655,560 / 724,212 | 10 | 252 | 8 | 1 |
| tcp | repeated-32k | cold | 6,982,232 / 4,609,776 | 4 | 61 | 7 | 1 |
| tcp | repeated-32k | warm | 13,561,232 / 6,493,280 | 8 | 189 | 7 | 1 |
| tcp | repeated-32k | latency | 6,734,972 / 3,208,940 | 10 | 253 | 7 | 1 |
| tcp | runs-32k | cold | 7,008,032 / 3,215,796 | 0 | 64 | 0 | 1 |
| tcp | runs-32k | warm | 13,537,924 / 5,734,212 | 0 | 192 | 0 | 1 |
| tcp | runs-32k | latency | 6,722,860 / 2,834,616 | 0 | 256 | 0 | 1 |

## Application round-trip latency

| Transport | Workload | Phase | p50 ms off/on | p95 ms off/on | p99 ms off/on | p95 speedup off/on [95% CI] | Client CPU ms off/on | Definitions on | Cache bytes on |
|---|---|---|---|---|---|---|---|---:|---:|
| reliable-udp | delta-32k | cold | 7.022 / 5.395 | 12.987 / 19.470 | 20.436 / 35.913 | 0.675× [0.592, 0.789] | 23.438 / 31.250 | 80 | 426,839 |
| reliable-udp | delta-32k | warm | 6.664 / 5.303 | 9.285 / 7.355 | 10.364 / 8.358 | 1.350× [1.214, 1.559] | 62.500 / 46.875 | 80 | 504,187 |
| reliable-udp | delta-32k | latency | 4.391 / 4.214 | 5.368 / 5.234 | 8.557 / 8.754 | 1.036× [0.964, 1.112] | 31.250 / 39.062 | 80 | 545,924 |
| reliable-udp | random-32k | cold | 7.201 / 6.794 | 13.891 / 12.217 | 21.379 / 25.671 | 1.124× [1.060, 1.182] | 15.625 / 23.438 | 0 | 452,328 |
| reliable-udp | random-32k | warm | 6.987 / 6.782 | 9.973 / 9.009 | 11.388 / 10.853 | 1.087× [0.992, 1.192] | 46.875 / 62.500 | 0 | 591,651 |
| reliable-udp | random-32k | latency | 4.303 / 4.353 | 5.109 / 5.194 | 7.310 / 6.993 | 0.938× [0.840, 1.022] | 15.625 / 46.875 | 0 | 664,030 |
| reliable-udp | repeated-1k | cold | 4.799 / 4.726 | 9.936 / 7.245 | 17.658 / 21.537 | 1.428× [1.168, 1.785] | 23.438 / 31.250 | 6 | 318,735 |
| reliable-udp | repeated-1k | warm | 4.564 / 4.512 | 7.741 / 7.418 | 8.472 / 8.321 | 1.162× [0.968, 1.471] | 31.250 / 46.875 | 6 | 320,731 |
| reliable-udp | repeated-1k | latency | 3.492 / 3.520 | 4.154 / 4.347 | 6.508 / 5.298 | 1.045× [0.959, 1.198] | 7.812 / 15.625 | 6 | 321,985 |
| reliable-udp | repeated-32k | cold | 7.400 / 5.281 | 13.381 / 21.105 | 20.565 / 30.497 | 0.665× [0.623, 0.706] | 23.438 / 15.625 | 98 | 379,923 |
| reliable-udp | repeated-32k | warm | 6.689 / 4.885 | 9.935 / 7.433 | 11.664 / 8.884 | 1.447× [1.330, 1.593] | 46.875 / 31.250 | 98 | 381,923 |
| reliable-udp | repeated-32k | latency | 4.377 / 3.964 | 5.307 / 4.958 | 6.920 / 6.819 | 1.071× [1.019, 1.129] | 23.438 / 23.438 | 98 | 382,923 |
| reliable-udp | runs-32k | cold | 7.256 / 4.681 | 13.406 / 11.542 | 21.035 / 22.665 | 1.161× [1.101, 1.229] | 31.250 / 15.625 | 0 | 18,560 |
| reliable-udp | runs-32k | warm | 6.650 / 4.612 | 9.141 / 6.422 | 9.649 / 7.260 | 1.393× [1.239, 1.561] | 54.688 / 31.250 | 0 | 18,560 |
| reliable-udp | runs-32k | latency | 4.353 / 3.550 | 5.330 / 4.287 | 6.852 / 6.175 | 1.257× [1.197, 1.324] | 78.125 / 15.625 | 0 | 18,560 |
| tcp | delta-32k | cold | 4.663 / 4.516 | 10.918 / 13.188 | 18.604 / 23.309 | 0.813× [0.753, 0.885] | 15.625 / 31.250 | 80 | 411,980 |
| tcp | delta-32k | warm | 4.358 / 4.539 | 5.840 / 6.164 | 7.077 / 6.922 | 1.035× [0.849, 1.299] | 31.250 / 15.625 | 80 | 486,384 |
| tcp | delta-32k | latency | 3.576 / 3.730 | 4.313 / 4.517 | 6.021 / 8.214 | 0.965× [0.857, 1.105] | 7.812 / 23.438 | 80 | 528,121 |
| tcp | random-32k | cold | 4.536 / 4.665 | 9.986 / 8.911 | 18.742 / 22.981 | 1.157× [1.079, 1.238] | 15.625 / 15.625 | 0 | 452,328 |
| tcp | random-32k | warm | 4.311 / 4.314 | 5.368 / 5.481 | 5.836 / 7.117 | 1.015× [0.974, 1.061] | 23.438 / 23.438 | 0 | 591,651 |
| tcp | random-32k | latency | 3.544 / 3.526 | 4.309 / 4.438 | 6.259 / 5.740 | 0.988× [0.958, 1.023] | 23.438 / 23.438 | 0 | 664,030 |
| tcp | repeated-1k | cold | 4.319 / 4.426 | 9.728 / 6.343 | 17.835 / 21.066 | 1.506× [1.408, 1.583] | 15.625 / 15.625 | 6 | 318,735 |
| tcp | repeated-1k | warm | 4.298 / 4.274 | 5.616 / 5.706 | 8.018 / 7.632 | 0.960× [0.842, 1.081] | 15.625 / 31.250 | 6 | 320,731 |
| tcp | repeated-1k | latency | 3.458 / 3.520 | 4.160 / 4.656 | 6.611 / 7.066 | 0.975× [0.903, 1.047] | 0.000 / 7.812 | 6 | 321,985 |
| tcp | repeated-32k | cold | 4.721 / 4.632 | 9.849 / 9.313 | 17.927 / 22.734 | 1.054× [1.010, 1.095] | 7.812 / 15.625 | 98 | 379,923 |
| tcp | repeated-32k | warm | 4.355 / 4.331 | 7.023 / 6.410 | 7.895 / 7.221 | 1.124× [1.041, 1.227] | 23.438 / 23.438 | 98 | 381,923 |
| tcp | repeated-32k | latency | 3.518 / 3.647 | 4.306 / 4.660 | 6.362 / 6.092 | 0.932× [0.886, 0.984] | 7.812 / 7.812 | 98 | 382,923 |
| tcp | runs-32k | cold | 4.438 / 4.457 | 10.209 / 11.001 | 18.367 / 22.672 | 0.915× [0.878, 0.953] | 15.625 / 15.625 | 0 | 18,560 |
| tcp | runs-32k | warm | 4.358 / 4.298 | 5.374 / 5.400 | 6.086 / 5.888 | 1.030× [0.921, 1.185] | 15.625 / 31.250 | 0 | 18,560 |
| tcp | runs-32k | latency | 3.532 / 3.468 | 4.299 / 4.114 | 6.355 / 4.827 | 1.066× [1.014, 1.131] | 0.000 / 23.438 | 0 | 18,560 |

## Complete connection byte cost

Uses the ending cumulative counter, including setup, every reported phase and any interphase traffic. Between-phase bytes are the gaps between setup/phase snapshots and are already included in the total.

| Transport | Workload | Pairs | Payload bytes | Total bytes off | Total bytes on | Bytes saved | Between-phase bytes off/on | Setup bytes off/on | Setup ms off/on |
|---|---|---:|---:|---:|---:|---:|---|---|---|
| reliable-udp | delta-32k | 10 | 8,388,608 | 8,561,561 | 2,741,639 | 67.98% | 0 / 0 | 10,469 / 10,519 | 306.46 / 313.44 |
| reliable-udp | random-32k | 10 | 8,388,608 | 8,561,525 | 8,561,610 | -0.00% | 0 / 0 | 10,469 / 10,519 | 319.67 / 320.48 |
| reliable-udp | repeated-1k | 10 | 262,144 | 316,832 | 130,756 | 58.73% | 0 / 0 | 10,469 / 10,519 | 305.95 / 318.55 |
| reliable-udp | repeated-32k | 10 | 8,388,608 | 8,561,594 | 708,012 | 91.73% | 0 / 0 | 10,469 / 10,519 | 306.50 / 315.35 |
| reliable-udp | runs-32k | 10 | 8,388,608 | 8,561,548 | 76,174 | 99.11% | 0 / 0 | 10,469 / 10,519 | 313.66 / 318.63 |
| tcp | delta-32k | 10 | 8,388,608 | 8,433,968 | 2,575,915 | 69.46% | 0 / 0 | 9,904 / 9,954 | 266.49 / 279.10 |
| tcp | random-32k | 10 | 8,388,608 | 8,433,968 | 8,434,018 | -0.00% | 0 / 0 | 9,904 / 9,954 | 264.16 / 277.57 |
| tcp | repeated-1k | 10 | 262,144 | 307,248 | 121,141 | 60.57% | 0 / 0 | 9,904 / 9,954 | 265.38 / 272.72 |
| tcp | repeated-32k | 10 | 8,388,608 | 8,433,968 | 655,539 | 92.23% | 0 / 0 | 9,904 / 9,954 | 262.38 / 275.50 |
| tcp | runs-32k | 10 | 8,388,608 | 8,433,968 | 66,590 | 99.21% | 0 / 0 | 9,904 / 9,954 | 271.94 / 285.54 |
