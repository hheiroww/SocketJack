# Binary compression transfer benchmark

Source: `docs\binary-compression-benchmark\2026-10-15-final\results.json`. Per-mode figures are medians of complete runs. On/off comparisons pair the same round. Compression is the only setting changed.

Socket bytes count both directions once, including dictionary updates, dictionary acknowledgements, application acknowledgements, and SocketJack framing. They exclude IP/TCP/UDP/Ethernet headers. Setup is reported separately and is included once in the lifetime totals. Lifetime totals use the ending cumulative counter, including traffic between phase snapshots.

Bulk throughput is useful application payload divided by the time from the first send through the last validated application acknowledgement. Bulk RTT uses eight outstanding messages; latency RTT uses one outstanding message after warm training. Byte counters drain after timing stops; settle time is excluded from throughput. Phase order and message counts follow the raw file's phasePlans metadata; legacy results without that field use cold, warm, latency. Every connection retains its adaptive cache across phases.

Throughput ratios use the geometric mean of paired on/off ratios and 10,000 paired bootstrap resamples (seed 7301; percentile 95% interval). An interval spanning 1 does not establish a speed change. Round-tail medians are not a pooled per-message percentile. With 64 latency samples, p99 is effectively the maximum sample and is descriptive. Fresh processes make the cold phase include first-use JIT costs; warm phases reuse the process and cache. CPU and optional managed-allocation figures cover the client process and its counting proxy, excluding the drain. Allocation is approximate process-wide allocation rather than retained memory. Setup timing measures Connect; setup byte totals also include its quiet-drain traffic. Loopback results do not establish WAN performance.

| Transport | Workload | Phase | Pairs | MiB/s off | MiB/s on | On/off speed [95% CI] | Total bytes off | Total bytes on | Bytes saved |
|---|---|---|---:|---:|---:|---|---:|---:|---:|
| reliable-udp | delta-32k | cold | 10 | 17.41 | 14.95 | 0.876× [0.836, 0.925] | 2,136,560 | 841,926 | 60.59% |
| reliable-udp | delta-32k | warm | 10 | 20.15 | 22.03 | 1.064× [0.961, 1.159] | 4,275,587 | 1,259,137 | 70.55% |
| reliable-udp | delta-32k | latency | 10 | 6.46 | 7.24 | 1.144× [1.082, 1.213] | 2,138,921 | 630,057 | 70.54% |
| reliable-udp | random-32k | cold | 10 | 18.20 | 16.56 | 0.851× [0.733, 0.965] | 2,136,549 | 2,136,572 | -0.00% |
| reliable-udp | random-32k | warm | 10 | 19.95 | 20.43 | 0.954× [0.896, 1.015] | 4,275,610 | 4,275,603 | -0.00% |
| reliable-udp | random-32k | latency | 10 | 6.45 | 6.63 | 1.009× [0.928, 1.121] | 2,138,921 | 2,138,921 | 0.00% |
| reliable-udp | repeated-1k | cold | 10 | 0.69 | 0.59 | 0.877× [0.815, 0.959] | 76,389 | 35,799 | 53.14% |
| reliable-udp | repeated-1k | warm | 10 | 0.79 | 0.73 | 0.979× [0.909, 1.081] | 153,197 | 56,173 | 63.33% |
| reliable-udp | repeated-1k | latency | 10 | 0.25 | 0.25 | 1.078× [0.996, 1.223] | 76,777 | 28,265 | 63.19% |
| reliable-udp | repeated-32k | cold | 10 | 16.85 | 16.59 | 0.933× [0.863, 1.001] | 2,136,595 | 389,753 | 81.76% |
| reliable-udp | repeated-32k | warm | 10 | 19.58 | 22.49 | 1.082× [0.972, 1.175] | 4,275,610 | 204,979 | 95.21% |
| reliable-udp | repeated-32k | latency | 10 | 6.30 | 7.40 | 1.056× [0.876, 1.180] | 2,138,921 | 102,761 | 95.20% |
| reliable-udp | runs-32k | cold | 10 | 16.93 | 20.37 | 1.177× [1.101, 1.261] | 2,136,549 | 16,165 | 99.24% |
| reliable-udp | runs-32k | warm | 10 | 19.01 | 23.24 | 1.208× [1.154, 1.266] | 4,275,580 | 32,873 | 99.23% |
| reliable-udp | runs-32k | latency | 10 | 5.83 | 8.05 | 1.332× [1.258, 1.413] | 2,138,951 | 16,617 | 99.22% |
| tcp | delta-32k | cold | 10 | 22.65 | 16.98 | 0.726× [0.678, 0.773] | 2,105,920 | 712,009 | 66.19% |
| tcp | delta-32k | warm | 10 | 28.04 | 25.80 | 0.922× [0.875, 0.967] | 4,212,096 | 1,235,968 | 70.66% |
| tcp | delta-32k | latency | 10 | 8.11 | 7.94 | 0.958× [0.907, 1.005] | 2,106,048 | 617,984 | 70.66% |
| tcp | random-32k | cold | 10 | 23.59 | 20.28 | 0.977× [0.838, 1.263] | 2,105,920 | 2,105,920 | 0.00% |
| tcp | random-32k | warm | 10 | 27.98 | 27.17 | 0.992× [0.952, 1.049] | 4,212,096 | 4,212,096 | 0.00% |
| tcp | random-32k | latency | 10 | 8.29 | 8.21 | 0.837× [0.597, 1.010] | 2,106,048 | 2,106,048 | 0.00% |
| tcp | repeated-1k | cold | 10 | 0.72 | 0.63 | 0.841× [0.766, 0.905] | 74,240 | 33,619 | 54.72% |
| tcp | repeated-1k | warm | 10 | 0.82 | 0.83 | 0.839× [0.626, 1.015] | 148,736 | 51,712 | 65.23% |
| tcp | repeated-1k | latency | 10 | 0.26 | 0.24 | 0.897× [0.764, 1.031] | 74,368 | 25,856 | 65.23% |
| tcp | repeated-32k | cold | 10 | 21.12 | 17.44 | 0.966× [0.790, 1.353] | 2,105,920 | 356,878 | 83.05% |
| tcp | repeated-32k | warm | 10 | 26.24 | 25.18 | 1.167× [0.873, 1.949] | 4,212,096 | 194,816 | 95.37% |
| tcp | repeated-32k | latency | 10 | 7.97 | 7.44 | 0.930× [0.851, 1.003] | 2,106,048 | 97,408 | 95.37% |
| tcp | runs-32k | cold | 10 | 23.30 | 21.13 | 0.902× [0.730, 1.053] | 2,105,920 | 14,016 | 99.33% |
| tcp | runs-32k | warm | 10 | 26.48 | 24.72 | 0.848× [0.646, 1.007] | 4,212,096 | 28,412 | 99.33% |
| tcp | runs-32k | latency | 10 | 7.74 | 8.07 | 1.197× [1.023, 1.461] | 2,106,048 | 14,208 | 99.33% |

## Adaptive state and managed allocation

Adaptive counters and cache state are cumulative through the end of the phase, after its drain. Plan hits this phase subtract the preceding phase's ending counter (the first phase starts at zero). Allocation covers only the timed phase in the client process, including the proxy; it is approximate allocated volume rather than retained memory. Encoding attempts and bypasses count outgoing non-control messages, which can include queue-drain application barriers as well as measured payloads.

| Transport | Workload | Phase | Allocated bytes off/on | Candidates on | Attempts on | Bypasses on | Probe interval on | Plans on | Plan hits cumulative on | Plan hits this phase on |
|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|
| reliable-udp | delta-32k | cold | 5,363,428 / 5,160,820 | 102 | 60 | 8 | 1 | 1 | 55 | 55 |
| reliable-udp | delta-32k | warm | 10,524,456 / 8,388,908 | 214 | 188 | 8 | 1 | 2 | 182 | 127 |
| reliable-udp | delta-32k | latency | 5,391,776 / 4,253,452 | 278 | 252 | 8 | 1 | 2 | 246 | 64 |
| reliable-udp | random-32k | cold | 5,396,604 / 6,015,188 | 617 | 6 | 64 | 64 | 0 | 0 | 0 |
| reliable-udp | random-32k | warm | 10,474,660 / 10,655,052 | 809 | 8 | 192 | 64 | 0 | 0 | 0 |
| reliable-udp | random-32k | latency | 5,377,456 / 5,486,020 | 912 | 9 | 256 | 64 | 0 | 0 | 0 |
| reliable-udp | repeated-1k | cold | 947,272 / 1,291,204 | 4 | 60 | 8 | 1 | 1 | 55 | 55 |
| reliable-udp | repeated-1k | warm | 1,534,472 / 1,507,280 | 8 | 188 | 8 | 1 | 2 | 182 | 127 |
| reliable-udp | repeated-1k | latency | 818,524 / 790,840 | 10 | 252 | 8 | 1 | 2 | 246 | 64 |
| reliable-udp | repeated-32k | cold | 5,391,976 / 4,205,344 | 4 | 60 | 8 | 1 | 1 | 55 | 55 |
| reliable-udp | repeated-32k | warm | 10,499,864 / 6,090,440 | 8 | 188 | 8 | 1 | 2 | 182 | 127 |
| reliable-udp | repeated-32k | latency | 5,361,704 / 3,041,932 | 10 | 252 | 8 | 1 | 2 | 246 | 64 |
| reliable-udp | runs-32k | cold | 5,401,196 / 3,319,352 | 0 | 64 | 0 | 1 | 0 | 0 | 0 |
| reliable-udp | runs-32k | warm | 10,472,124 / 6,123,912 | 0 | 192 | 0 | 1 | 0 | 0 | 0 |
| reliable-udp | runs-32k | latency | 5,374,632 / 3,063,296 | 0 | 256 | 0 | 1 | 0 | 0 | 0 |
| tcp | delta-32k | cold | 6,969,252 / 5,386,160 | 78 | 64 | 3 | 1 | 1 | 60 | 60 |
| tcp | delta-32k | warm | 13,514,120 / 8,886,212 | 190 | 192 | 3 | 1 | 2 | 187 | 127 |
| tcp | delta-32k | latency | 6,739,128 / 4,436,940 | 254 | 256 | 3 | 1 | 2 | 251 | 64 |
| tcp | random-32k | cold | 7,008,604 / 7,602,480 | 617 | 6 | 64 | 64 | 0 | 0 | 0 |
| tcp | random-32k | warm | 13,521,036 / 13,734,832 | 809 | 8 | 192 | 64 | 0 | 0 | 0 |
| tcp | random-32k | latency | 6,724,592 / 6,830,584 | 912 | 9 | 256 | 64 | 0 | 0 | 0 |
| tcp | repeated-1k | cold | 868,472 / 1,095,152 | 4 | 60 | 8 | 1 | 1 | 55 | 55 |
| tcp | repeated-1k | warm | 1,259,532 / 1,125,284 | 8 | 188 | 8 | 1 | 2 | 182 | 127 |
| tcp | repeated-1k | latency | 656,920 / 594,056 | 10 | 252 | 8 | 1 | 2 | 246 | 64 |
| tcp | repeated-32k | cold | 7,002,388 / 4,233,404 | 4 | 61 | 7 | 1 | 1 | 56 | 56 |
| tcp | repeated-32k | warm | 13,546,996 / 5,688,892 | 8 | 189 | 7 | 1 | 2 | 183 | 127 |
| tcp | repeated-32k | latency | 6,732,492 / 2,835,308 | 10 | 253 | 7 | 1 | 2 | 247 | 64 |
| tcp | runs-32k | cold | 7,015,468 / 3,194,900 | 0 | 64 | 0 | 1 | 0 | 0 | 0 |
| tcp | runs-32k | warm | 13,527,944 / 5,737,684 | 0 | 192 | 0 | 1 | 0 | 0 | 0 |
| tcp | runs-32k | latency | 6,724,764 / 2,844,936 | 0 | 256 | 0 | 1 | 0 | 0 | 0 |

## Application round-trip latency

| Transport | Workload | Phase | p50 ms off/on | p95 ms off/on | p99 ms off/on | p95 speedup off/on [95% CI] | Client CPU ms off/on | Definitions on | Cache bytes on |
|---|---|---|---|---|---|---|---|---:|---:|
| reliable-udp | delta-32k | cold | 7.727 / 5.807 | 13.896 / 12.518 | 21.560 / 36.805 | 0.973× [0.744, 1.255] | 31.250 / 31.250 | 80 | 429,399 |
| reliable-udp | delta-32k | warm | 7.155 / 5.908 | 8.985 / 7.557 | 10.137 / 8.474 | 1.143× [0.901, 1.363] | 46.875 / 39.062 | 80 | 509,307 |
| reliable-udp | delta-32k | latency | 4.579 / 4.062 | 5.920 / 5.282 | 9.146 / 7.663 | 1.095× [0.968, 1.202] | 39.062 / 31.250 | 80 | 551,044 |
| reliable-udp | random-32k | cold | 7.232 / 7.283 | 14.234 / 13.940 | 22.371 / 28.366 | 0.920× [0.740, 1.079] | 31.250 / 31.250 | 0 | 452,328 |
| reliable-udp | random-32k | warm | 7.212 / 7.175 | 9.847 / 10.014 | 10.989 / 11.339 | 0.919× [0.795, 1.067] | 39.062 / 70.312 | 0 | 591,651 |
| reliable-udp | random-32k | latency | 4.579 / 4.545 | 5.957 / 5.424 | 8.601 / 7.226 | 1.085× [0.950, 1.266] | 39.062 / 54.688 | 0 | 664,030 |
| reliable-udp | repeated-1k | cold | 5.335 / 5.181 | 11.350 / 9.016 | 18.817 / 25.291 | 1.206× [1.053, 1.365] | 15.625 / 23.438 | 6 | 319,519 |
| reliable-udp | repeated-1k | warm | 4.938 / 5.110 | 8.249 / 8.048 | 9.387 / 9.471 | 1.086× [0.933, 1.292] | 62.500 / 23.438 | 6 | 322,299 |
| reliable-udp | repeated-1k | latency | 3.604 / 3.692 | 4.759 / 4.633 | 7.053 / 6.102 | 1.158× [1.026, 1.381] | 31.250 / 23.438 | 6 | 323,553 |
| reliable-udp | repeated-32k | cold | 7.570 / 6.189 | 14.490 / 12.793 | 22.059 / 31.729 | 1.115× [0.967, 1.277] | 15.625 / 54.688 | 98 | 382,915 |
| reliable-udp | repeated-32k | warm | 7.202 / 5.481 | 10.161 / 8.320 | 11.811 / 9.047 | 1.156× [0.986, 1.339] | 62.500 / 23.438 | 98 | 387,907 |
| reliable-udp | repeated-32k | latency | 4.715 / 4.056 | 6.135 / 5.229 | 9.112 / 6.234 | 1.016× [0.753, 1.219] | 31.250 / 31.250 | 98 | 388,907 |
| reliable-udp | runs-32k | cold | 8.055 / 5.081 | 14.272 / 12.541 | 22.205 / 25.271 | 1.102× [0.971, 1.238] | 23.438 / 31.250 | 0 | 18,560 |
| reliable-udp | runs-32k | warm | 7.492 / 5.060 | 10.282 / 7.283 | 11.699 / 8.433 | 1.351× [1.211, 1.515] | 62.500 / 39.062 | 0 | 18,560 |
| reliable-udp | runs-32k | latency | 5.005 / 3.665 | 7.261 / 4.949 | 9.857 / 6.463 | 1.388× [1.226, 1.582] | 46.875 / 23.438 | 0 | 18,560 |
| tcp | delta-32k | cold | 4.640 / 4.954 | 9.862 / 9.683 | 17.983 / 24.169 | 1.033× [0.882, 1.228] | 15.625 / 31.250 | 80 | 414,540 |
| tcp | delta-32k | warm | 4.490 / 4.572 | 5.776 / 5.981 | 6.453 / 7.794 | 0.888× [0.753, 1.018] | 31.250 / 39.062 | 80 | 491,504 |
| tcp | delta-32k | latency | 3.668 / 3.743 | 4.551 / 4.743 | 7.863 / 8.393 | 0.970× [0.851, 1.087] | 23.438 / 15.625 | 80 | 533,241 |
| tcp | random-32k | cold | 4.701 / 4.748 | 10.437 / 9.392 | 18.549 / 24.116 | 1.216× [0.995, 1.637] | 15.625 / 15.625 | 0 | 452,328 |
| tcp | random-32k | warm | 4.498 / 4.631 | 6.335 / 6.057 | 7.262 / 6.859 | 1.101× [0.944, 1.303] | 31.250 / 31.250 | 0 | 591,651 |
| tcp | random-32k | latency | 3.597 / 3.657 | 4.432 / 4.531 | 6.928 / 7.588 | 0.806× [0.521, 1.034] | 15.625 / 15.625 | 0 | 664,030 |
| tcp | repeated-1k | cold | 4.724 / 4.790 | 11.045 / 7.896 | 20.147 / 25.198 | 1.297× [1.053, 1.515] | 15.625 / 15.625 | 6 | 319,519 |
| tcp | repeated-1k | warm | 4.639 / 4.653 | 6.843 / 6.509 | 9.037 / 8.779 | 0.759× [0.397, 1.192] | 31.250 / 46.875 | 6 | 322,299 |
| tcp | repeated-1k | latency | 3.604 / 3.809 | 5.074 / 5.216 | 7.786 / 6.712 | 0.857× [0.667, 1.063] | 15.625 / 39.062 | 6 | 323,553 |
| tcp | repeated-32k | cold | 5.022 / 5.280 | 11.432 / 10.802 | 19.370 / 24.946 | 1.364× [0.995, 2.358] | 23.438 / 15.625 | 98 | 382,915 |
| tcp | repeated-32k | warm | 4.741 / 4.793 | 7.264 / 8.301 | 9.022 / 8.837 | 1.396× [0.809, 3.675] | 23.438 / 31.250 | 98 | 387,907 |
| tcp | repeated-32k | latency | 3.691 / 3.943 | 4.675 / 5.274 | 9.967 / 8.926 | 0.888× [0.780, 0.983] | 15.625 / 23.438 | 98 | 388,907 |
| tcp | runs-32k | cold | 4.826 / 4.729 | 10.254 / 13.011 | 18.265 / 26.598 | 0.804× [0.600, 0.988] | 0.000 / 15.625 | 0 | 18,560 |
| tcp | runs-32k | warm | 4.762 / 4.967 | 6.592 / 6.834 | 7.605 / 7.692 | 0.794× [0.535, 1.042] | 39.062 / 31.250 | 0 | 18,560 |
| tcp | runs-32k | latency | 3.827 / 3.659 | 5.120 / 4.781 | 7.744 / 7.431 | 1.289× [0.995, 1.744] | 31.250 / 23.438 | 0 | 18,560 |

## Complete connection byte cost

Uses the ending cumulative counter, including setup, every reported phase and any interphase traffic. Between-phase bytes are the gaps between setup/phase snapshots and are already included in the total.

| Transport | Workload | Pairs | Payload bytes | Total bytes off | Total bytes on | Bytes saved | Between-phase bytes off/on | Setup bytes off/on | Setup ms off/on |
|---|---|---:|---:|---:|---:|---:|---|---|---|
| reliable-udp | delta-32k | 10 | 8,388,608 | 8,561,550 | 2,741,775 | 67.98% | 0 / 0 | 10,469 / 10,655 | 325.26 / 337.68 |
| reliable-udp | random-32k | 10 | 8,388,608 | 8,561,584 | 8,561,747 | -0.00% | 0 / 0 | 10,469 / 10,655 | 333.00 / 337.68 |
| reliable-udp | repeated-1k | 10 | 262,144 | 316,832 | 130,892 | 58.69% | 0 / 0 | 10,469 / 10,655 | 327.13 / 336.15 |
| reliable-udp | repeated-32k | 10 | 8,388,608 | 8,561,584 | 708,148 | 91.73% | 0 / 0 | 10,469 / 10,655 | 321.13 / 329.78 |
| reliable-udp | runs-32k | 10 | 8,388,608 | 8,561,567 | 76,310 | 99.11% | 0 / 0 | 10,469 / 10,655 | 337.94 / 357.37 |
| tcp | delta-32k | 10 | 8,388,608 | 8,433,968 | 2,576,051 | 69.46% | 0 / 0 | 9,904 / 10,090 | 277.52 / 294.61 |
| tcp | random-32k | 10 | 8,388,608 | 8,433,968 | 8,434,154 | -0.00% | 0 / 0 | 9,904 / 10,090 | 270.06 / 291.54 |
| tcp | repeated-1k | 10 | 262,144 | 307,248 | 121,277 | 60.53% | 0 / 0 | 9,904 / 10,090 | 277.97 / 332.39 |
| tcp | repeated-32k | 10 | 8,388,608 | 8,433,968 | 659,192 | 92.18% | 0 / 0 | 9,904 / 10,090 | 281.98 / 308.16 |
| tcp | runs-32k | 10 | 8,388,608 | 8,433,968 | 66,726 | 99.21% | 0 / 0 | 9,904 / 10,090 | 286.18 / 293.81 |
