# Binary compression transfer benchmark

Source: `docs\binary-compression-benchmark\2026-10-15-optimized\adaptive.json`. Per-mode figures are medians of complete runs. On/off comparisons pair the same round. Compression is the only setting changed.

Socket bytes count both directions once, including dictionary updates, dictionary acknowledgements, application acknowledgements, and SocketJack framing. They exclude IP/TCP/UDP/Ethernet headers. Setup is reported separately and is included once in the lifetime totals. Lifetime totals use the ending cumulative counter, including traffic between phase snapshots.

Bulk throughput is useful application payload divided by the time from the first send through the last validated application acknowledgement. Bulk RTT uses eight outstanding messages; latency RTT uses one outstanding message after warm training. Byte counters drain after timing stops; settle time is excluded from throughput. Phase order and message counts follow the raw file's phasePlans metadata; legacy results without that field use cold, warm, latency. Every connection retains its adaptive cache across phases.

Throughput ratios use the geometric mean of paired on/off ratios and 10,000 paired bootstrap resamples (seed 7301; percentile 95% interval). An interval spanning 1 does not establish a speed change. Round-tail medians are not a pooled per-message percentile. With 64 latency samples, p99 is effectively the maximum sample and is descriptive. Fresh processes make the cold phase include first-use JIT costs; warm phases reuse the process and cache. CPU and optional managed-allocation figures cover the client process and its counting proxy, excluding the drain. Allocation is approximate process-wide allocation rather than retained memory. Setup timing measures Connect; setup byte totals also include its quiet-drain traffic. Loopback results do not establish WAN performance.

| Transport | Workload | Phase | Pairs | MiB/s off | MiB/s on | On/off speed [95% CI] | Total bytes off | Total bytes on | Bytes saved |
|---|---|---|---:|---:|---:|---|---:|---:|---:|
| reliable-udp | adaptive-32k | random | 5 | 19.23 | 18.41 | 0.742× [0.457, 0.958] | 2,136,549 | 2,136,549 | 0.00% |
| reliable-udp | adaptive-32k | repeat | 5 | 23.48 | 19.60 | 0.801× [0.667, 0.911] | 2,136,811 | 391,695 | 81.67% |
| reliable-udp | adaptive-32k | churn | 5 | 22.60 | 21.19 | 0.895× [0.773, 0.988] | 2,138,921 | 2,138,945 | -0.00% |
| reliable-udp | adaptive-32k | return | 5 | 22.94 | 24.75 | 0.916× [0.666, 1.111] | 2,138,921 | 102,761 | 95.20% |
| reliable-udp | adaptive-32k | runs | 5 | 23.21 | 27.75 | 1.034× [0.709, 1.304] | 2,138,981 | 20,650 | 99.03% |
| reliable-udp | adaptive-32k | latency | 5 | 7.53 | 8.64 | 1.088× [0.919, 1.205] | 2,138,921 | 17,257 | 99.19% |
| reliable-udp | marginal-32k | cold | 5 | 18.91 | 15.25 | 0.831× [0.805, 0.858] | 2,136,549 | 2,136,853 | -0.01% |
| reliable-udp | marginal-32k | warm | 5 | 22.29 | 20.97 | 0.932× [0.890, 0.975] | 4,275,587 | 4,275,610 | -0.00% |
| reliable-udp | marginal-32k | latency | 5 | 7.04 | 6.83 | 0.989× [0.963, 1.027] | 2,138,921 | 2,138,921 | 0.00% |
| reliable-udp | spaced-32k | cold | 5 | 18.17 | 15.17 | 0.830× [0.809, 0.855] | 2,136,549 | 2,206,620 | -3.28% |
| reliable-udp | spaced-32k | warm | 5 | 21.51 | 14.40 | 0.679× [0.663, 0.693] | 4,275,610 | 3,503,317 | 18.06% |
| reliable-udp | spaced-32k | trained | 5 | 22.58 | 19.71 | 0.863× [0.847, 0.882] | 2,138,921 | 625,263 | 70.77% |
| reliable-udp | spaced-32k | latency | 5 | 7.08 | 6.80 | 0.966× [0.945, 0.987] | 2,138,981 | 625,323 | 70.77% |
| tcp | adaptive-32k | random | 5 | 24.13 | 22.28 | 0.906× [0.857, 0.938] | 2,105,920 | 2,105,920 | 0.00% |
| tcp | adaptive-32k | repeat | 5 | 26.77 | 20.60 | 0.758× [0.714, 0.797] | 2,106,048 | 383,654 | 81.78% |
| tcp | adaptive-32k | churn | 5 | 28.88 | 26.92 | 0.968× [0.892, 1.078] | 2,106,048 | 2,106,048 | 0.00% |
| tcp | adaptive-32k | return | 5 | 28.80 | 26.69 | 0.937× [0.904, 0.972] | 2,106,048 | 97,408 | 95.37% |
| tcp | adaptive-32k | runs | 5 | 29.85 | 28.78 | 0.975× [0.955, 0.992] | 2,106,048 | 16,965 | 99.19% |
| tcp | adaptive-32k | latency | 5 | 8.79 | 8.87 | 1.016× [1.006, 1.026] | 2,106,048 | 14,848 | 99.29% |
| tcp | marginal-32k | cold | 5 | 24.99 | 19.37 | 0.772× [0.764, 0.779] | 2,105,920 | 2,106,193 | -0.01% |
| tcp | marginal-32k | warm | 5 | 29.77 | 28.85 | 0.977× [0.945, 1.016] | 4,212,096 | 4,212,096 | 0.00% |
| tcp | marginal-32k | latency | 5 | 8.49 | 8.48 | 1.004× [0.988, 1.022] | 2,106,048 | 2,106,048 | 0.00% |
| tcp | spaced-32k | cold | 5 | 23.49 | 18.04 | 0.757× [0.733, 0.780] | 2,105,920 | 2,176,220 | -3.34% |
| tcp | spaced-32k | warm | 5 | 27.48 | 15.74 | 0.579× [0.572, 0.587] | 4,212,096 | 3,567,504 | 15.30% |
| tcp | spaced-32k | trained | 5 | 29.82 | 21.08 | 0.701× [0.673, 0.728] | 2,106,048 | 583,104 | 72.31% |
| tcp | spaced-32k | latency | 5 | 8.66 | 7.40 | 0.855× [0.838, 0.876] | 2,106,048 | 583,104 | 72.31% |

## Adaptive state and managed allocation

Adaptive counters and cache state are cumulative through the end of the phase, after its drain. Allocation covers only the timed phase in the client process, including the proxy; it is approximate allocated volume rather than retained memory. Encoding attempts and bypasses count outgoing non-control messages, which can include queue-drain application barriers as well as measured payloads.

| Transport | Workload | Phase | Allocated bytes off/on | Candidates on | Attempts on | Bypasses on | Probe interval on |
|---|---|---|---:|---:|---:|---:|---:|
| reliable-udp | adaptive-32k | random | 5,408,672 / 6,037,544 | 617 | 6 | 64 | 64 |
| reliable-udp | adaptive-32k | repeat | 5,254,536 / 4,372,936 | 621 | 66 | 72 | 1 |
| reliable-udp | adaptive-32k | churn | 5,214,280 / 5,768,096 | 1,219 | 72 | 136 | 64 |
| reliable-udp | adaptive-32k | return | 5,235,760 / 3,401,016 | 1,221 | 136 | 136 | 1 |
| reliable-udp | adaptive-32k | runs | 5,236,984 / 3,085,040 | 1,225 | 200 | 136 | 1 |
| reliable-udp | adaptive-32k | latency | 5,361,792 / 3,173,008 | 1,227 | 264 | 136 | 1 |
| reliable-udp | marginal-32k | cold | 5,390,136 / 6,454,024 | 775 | 8 | 64 | 64 |
| reliable-udp | marginal-32k | warm | 10,482,480 / 10,712,704 | 995 | 10 | 192 | 64 |
| reliable-udp | marginal-32k | latency | 5,367,728 / 5,469,432 | 1,105 | 11 | 256 | 64 |
| reliable-udp | spaced-32k | cold | 5,360,368 / 8,006,176 | 1,639 | 20 | 64 | 1 |
| reliable-udp | spaced-32k | warm | 10,534,944 / 21,199,416 | 98 | 137 | 119 | 1 |
| reliable-udp | spaced-32k | trained | 5,236,920 / 6,297,648 | 124 | 201 | 119 | 1 |
| reliable-udp | spaced-32k | latency | 5,378,944 / 6,349,328 | 150 | 265 | 119 | 1 |
| tcp | adaptive-32k | random | 7,001,336 / 7,615,216 | 617 | 6 | 64 | 64 |
| tcp | adaptive-32k | repeat | 6,794,280 / 4,512,104 | 621 | 66 | 72 | 1 |
| tcp | adaptive-32k | churn | 6,735,384 / 7,319,328 | 1,219 | 72 | 136 | 64 |
| tcp | adaptive-32k | return | 6,725,992 / 3,198,128 | 1,221 | 136 | 136 | 1 |
| tcp | adaptive-32k | runs | 6,733,864 / 2,889,400 | 1,225 | 200 | 136 | 1 |
| tcp | adaptive-32k | latency | 6,746,736 / 2,877,024 | 1,227 | 264 | 136 | 1 |
| tcp | marginal-32k | cold | 6,996,320 / 8,031,576 | 814 | 8 | 64 | 64 |
| tcp | marginal-32k | warm | 13,529,168 / 13,749,128 | 1,006 | 10 | 192 | 64 |
| tcp | marginal-32k | latency | 6,738,448 / 6,828,184 | 1,107 | 11 | 256 | 64 |
| tcp | spaced-32k | cold | 6,972,272 / 9,713,296 | 1,639 | 20 | 64 | 1 |
| tcp | spaced-32k | warm | 13,601,480 / 23,554,088 | 97 | 130 | 126 | 1 |
| tcp | spaced-32k | trained | 6,736,648 / 6,462,024 | 123 | 194 | 126 | 1 |
| tcp | spaced-32k | latency | 6,745,048 / 6,494,832 | 149 | 258 | 126 | 1 |

## Application round-trip latency

| Transport | Workload | Phase | p50 ms off/on | p95 ms off/on | p99 ms off/on | p95 speedup off/on [95% CI] | Client CPU ms off/on | Definitions on | Cache bytes on |
|---|---|---|---|---|---|---|---|---:|---:|
| reliable-udp | adaptive-32k | random | 6.717 / 6.792 | 12.677 / 12.600 | 19.915 / 25.081 | 0.748× [0.346, 1.165] | 15.625 / 31.250 | 0 | 452,328 |
| reliable-udp | adaptive-32k | repeat | 6.267 / 5.135 | 7.763 / 16.309 | 8.069 / 17.788 | 0.475× [0.377, 0.578] | 15.625 / 15.625 | 98 | 813,695 |
| reliable-udp | adaptive-32k | churn | 6.435 / 6.413 | 7.572 / 7.731 | 8.906 / 8.719 | 0.909× [0.765, 1.046] | 15.625 / 15.625 | 98 | 1,227,949 |
| reliable-udp | adaptive-32k | return | 6.297 / 4.939 | 7.299 / 6.603 | 8.229 / 7.115 | 0.926× [0.549, 1.275] | 15.625 / 15.625 | 98 | 1,229,205 |
| reliable-udp | adaptive-32k | runs | 6.336 / 4.424 | 7.529 / 6.601 | 7.909 / 7.214 | 0.894× [0.358, 1.717] | 15.625 / 31.250 | 99 | 1,235,088 |
| reliable-udp | adaptive-32k | latency | 4.025 / 3.282 | 4.700 / 4.992 | 5.591 / 7.664 | 0.972× [0.804, 1.155] | 78.125 / 46.875 | 99 | 1,237,904 |
| reliable-udp | marginal-32k | cold | 6.951 / 7.130 | 13.867 / 9.974 | 21.310 / 32.143 | 1.371× [1.129, 1.658] | 15.625 / 31.250 | 1 | 872,581 |
| reliable-udp | marginal-32k | warm | 6.515 / 6.847 | 9.066 / 9.936 | 9.760 / 10.740 | 0.876× [0.814, 0.941] | 31.250 / 93.750 | 1 | 1,022,253 |
| reliable-udp | marginal-32k | latency | 4.285 / 4.313 | 5.056 / 5.561 | 7.673 / 9.314 | 0.911× [0.842, 0.981] | 15.625 / 31.250 | 1 | 1,097,107 |
| reliable-udp | spaced-32k | cold | 7.358 / 7.142 | 13.604 / 15.063 | 20.797 / 25.567 | 0.909× [0.854, 0.968] | 31.250 / 15.625 | 213 | 1,575,640 |
| reliable-udp | spaced-32k | warm | 6.382 / 7.154 | 10.670 / 17.051 | 12.865 / 19.620 | 0.633× [0.560, 0.697] | 31.250 / 78.125 | 3,323 | 2,263,127 |
| reliable-udp | spaced-32k | trained | 6.349 / 5.189 | 7.599 / 6.973 | 8.377 / 8.167 | 1.130× [0.990, 1.338] | 15.625 / 46.875 | 3,323 | 2,279,471 |
| reliable-udp | spaced-32k | latency | 4.311 / 4.333 | 5.241 / 5.954 | 7.367 / 8.337 | 0.862× [0.790, 0.945] | 15.625 / 31.250 | 3,323 | 2,295,815 |
| tcp | adaptive-32k | random | 4.409 / 4.528 | 10.326 / 8.072 | 17.685 / 22.163 | 1.255× [1.153, 1.356] | 15.625 / 0.000 | 0 | 452,328 |
| tcp | adaptive-32k | repeat | 4.445 / 4.563 | 7.591 / 8.084 | 7.820 / 16.721 | 0.894× [0.728, 1.079] | 0.000 / 15.625 | 98 | 813,695 |
| tcp | adaptive-32k | churn | 4.388 / 4.257 | 5.733 / 5.936 | 6.036 / 6.675 | 1.096× [0.858, 1.587] | 0.000 / 0.000 | 98 | 1,227,949 |
| tcp | adaptive-32k | return | 4.273 / 4.196 | 5.544 / 5.124 | 6.714 / 5.896 | 1.099× [0.880, 1.368] | 15.625 / 15.625 | 98 | 1,229,205 |
| tcp | adaptive-32k | runs | 4.288 / 4.111 | 5.083 / 5.593 | 5.729 / 6.652 | 0.936× [0.771, 1.107] | 15.625 / 15.625 | 99 | 1,235,088 |
| tcp | adaptive-32k | latency | 3.428 / 3.418 | 4.277 / 4.035 | 6.153 / 4.701 | 1.064× [1.035, 1.094] | 15.625 / 31.250 | 99 | 1,237,904 |
| tcp | marginal-32k | cold | 4.330 / 4.439 | 9.507 / 5.649 | 17.539 / 21.986 | 1.521× [1.302, 1.689] | 15.625 / 31.250 | 1 | 887,497 |
| tcp | marginal-32k | warm | 4.162 / 4.195 | 5.507 / 5.410 | 6.756 / 7.172 | 0.958× [0.872, 1.044] | 46.875 / 46.875 | 1 | 1,026,394 |
| tcp | marginal-32k | latency | 3.522 / 3.535 | 4.197 / 4.385 | 6.612 / 4.805 | 1.020× [0.965, 1.081] | 0.000 / 31.250 | 1 | 1,097,792 |
| tcp | spaced-32k | cold | 4.423 / 4.742 | 9.550 / 9.742 | 18.040 / 22.468 | 0.985× [0.948, 1.026] | 15.625 / 15.625 | 213 | 1,575,640 |
| tcp | spaced-32k | warm | 4.377 / 4.624 | 9.052 / 9.537 | 9.403 / 11.696 | 0.980× [0.910, 1.086] | 15.625 / 31.250 | 3,323 | 2,261,620 |
| tcp | spaced-32k | trained | 4.323 / 4.442 | 5.058 / 6.172 | 5.406 / 6.580 | 0.747× [0.525, 0.958] | 15.625 / 46.875 | 3,323 | 2,277,964 |
| tcp | spaced-32k | latency | 3.503 / 4.139 | 4.053 / 5.135 | 6.194 / 6.758 | 0.775× [0.748, 0.800] | 0.000 / 46.875 | 3,323 | 2,294,308 |

## Complete connection byte cost

Uses the ending cumulative counter, including setup, every reported phase and any interphase traffic. Between-phase bytes are the gaps between setup/phase snapshots and are already included in the total.

| Transport | Workload | Pairs | Payload bytes | Total bytes off | Total bytes on | Bytes saved | Between-phase bytes off/on | Setup bytes off/on | Setup ms off/on |
|---|---|---:|---:|---:|---:|---:|---|---|---|
| reliable-udp | adaptive-32k | 5 | 12,582,912 | 12,839,596 | 4,818,400 | 62.47% | 0 / 0 | 10,469 / 10,519 | 307.66 / 318.50 |
| reliable-udp | marginal-32k | 5 | 8,388,608 | 8,561,526 | 8,561,926 | -0.00% | 0 / 0 | 10,469 / 10,519 | 314.76 / 318.22 |
| reliable-udp | spaced-32k | 5 | 10,485,760 | 10,700,530 | 6,959,040 | 34.97% | 0 / 0 | 10,469 / 10,519 | 304.42 / 310.18 |
| tcp | adaptive-32k | 5 | 12,582,912 | 12,646,064 | 4,734,797 | 62.56% | 0 / 0 | 9,904 / 9,954 | 262.60 / 267.68 |
| tcp | marginal-32k | 5 | 8,388,608 | 8,433,968 | 8,434,291 | -0.00% | 0 / 0 | 9,904 / 9,954 | 266.42 / 276.98 |
| tcp | spaced-32k | 5 | 10,485,760 | 10,540,016 | 6,919,886 | 34.35% | 0 / 0 | 9,904 / 9,954 | 255.32 / 268.38 |
