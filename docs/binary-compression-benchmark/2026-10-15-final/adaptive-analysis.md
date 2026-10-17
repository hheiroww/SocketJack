# Binary compression transfer benchmark

Source: `docs\binary-compression-benchmark\2026-10-15-final\adaptive.json`. Per-mode figures are medians of complete runs. On/off comparisons pair the same round. Compression is the only setting changed.

Socket bytes count both directions once, including dictionary updates, dictionary acknowledgements, application acknowledgements, and SocketJack framing. They exclude IP/TCP/UDP/Ethernet headers. Setup is reported separately and is included once in the lifetime totals. Lifetime totals use the ending cumulative counter, including traffic between phase snapshots.

Bulk throughput is useful application payload divided by the time from the first send through the last validated application acknowledgement. Bulk RTT uses eight outstanding messages; latency RTT uses one outstanding message after warm training. Byte counters drain after timing stops; settle time is excluded from throughput. Phase order and message counts follow the raw file's phasePlans metadata; legacy results without that field use cold, warm, latency. Every connection retains its adaptive cache across phases.

Throughput ratios use the geometric mean of paired on/off ratios and 10,000 paired bootstrap resamples (seed 7301; percentile 95% interval). An interval spanning 1 does not establish a speed change. Round-tail medians are not a pooled per-message percentile. With 64 latency samples, p99 is effectively the maximum sample and is descriptive. Fresh processes make the cold phase include first-use JIT costs; warm phases reuse the process and cache. CPU and optional managed-allocation figures cover the client process and its counting proxy, excluding the drain. Allocation is approximate process-wide allocation rather than retained memory. Setup timing measures Connect; setup byte totals also include its quiet-drain traffic. Loopback results do not establish WAN performance.

| Transport | Workload | Phase | Pairs | MiB/s off | MiB/s on | On/off speed [95% CI] | Total bytes off | Total bytes on | Bytes saved |
|---|---|---|---:|---:|---:|---|---:|---:|---:|
| reliable-udp | adaptive-32k | random | 5 | 16.45 | 16.40 | 0.972× [0.902, 1.048] | 2,136,549 | 2,136,572 | -0.00% |
| reliable-udp | adaptive-32k | repeat | 5 | 19.38 | 19.36 | 0.865× [0.597, 1.113] | 2,136,834 | 391,695 | 81.67% |
| reliable-udp | adaptive-32k | churn | 5 | 20.44 | 19.07 | 0.861× [0.667, 0.998] | 2,138,945 | 2,138,945 | -0.00% |
| reliable-udp | adaptive-32k | return | 5 | 20.87 | 23.75 | 1.284× [1.171, 1.452] | 2,138,981 | 102,821 | 95.19% |
| reliable-udp | adaptive-32k | runs | 5 | 21.43 | 26.78 | 1.175× [1.091, 1.250] | 2,138,921 | 20,590 | 99.04% |
| reliable-udp | adaptive-32k | latency | 5 | 7.19 | 8.69 | 1.214× [1.048, 1.494] | 2,138,981 | 17,317 | 99.19% |
| reliable-udp | marginal-32k | cold | 5 | 16.23 | 14.73 | 0.871× [0.764, 1.005] | 2,136,549 | 2,136,876 | -0.02% |
| reliable-udp | marginal-32k | warm | 5 | 18.29 | 20.05 | 0.988× [0.835, 1.142] | 4,275,563 | 4,275,609 | -0.00% |
| reliable-udp | marginal-32k | latency | 5 | 5.85 | 6.10 | 0.973× [0.877, 1.079] | 2,138,981 | 2,138,981 | 0.00% |
| reliable-udp | spaced-32k | cold | 5 | 16.76 | 14.17 | 0.859× [0.831, 0.884] | 2,136,572 | 2,206,643 | -3.28% |
| reliable-udp | spaced-32k | warm | 5 | 19.29 | 12.54 | 0.649× [0.628, 0.672] | 4,275,586 | 3,464,020 | 18.98% |
| reliable-udp | spaced-32k | trained | 5 | 20.35 | 19.61 | 0.942× [0.867, 0.997] | 2,138,921 | 594,131 | 72.22% |
| reliable-udp | spaced-32k | latency | 5 | 6.19 | 6.31 | 1.019× [0.981, 1.047] | 2,138,981 | 594,191 | 72.22% |
| tcp | adaptive-32k | random | 5 | 22.31 | 15.54 | 0.651× [0.437, 0.850] | 2,105,920 | 2,105,920 | 0.00% |
| tcp | adaptive-32k | repeat | 5 | 26.56 | 19.69 | 0.549× [0.284, 0.807] | 2,106,048 | 383,654 | 81.78% |
| tcp | adaptive-32k | churn | 5 | 26.44 | 20.57 | 0.581× [0.253, 0.917] | 2,106,048 | 2,106,048 | 0.00% |
| tcp | adaptive-32k | return | 5 | 27.29 | 24.93 | 0.705× [0.371, 1.007] | 2,106,048 | 97,408 | 95.37% |
| tcp | adaptive-32k | runs | 5 | 27.41 | 24.54 | 0.803× [0.540, 1.037] | 2,106,048 | 16,965 | 99.19% |
| tcp | adaptive-32k | latency | 5 | 8.15 | 7.08 | 0.708× [0.383, 1.002] | 2,106,048 | 14,848 | 99.29% |
| tcp | marginal-32k | cold | 5 | 23.29 | 16.92 | 0.972× [0.741, 1.575] | 2,105,920 | 2,106,193 | -0.01% |
| tcp | marginal-32k | warm | 5 | 27.30 | 25.27 | 1.036× [0.914, 1.292] | 4,212,096 | 4,212,096 | 0.00% |
| tcp | marginal-32k | latency | 5 | 7.70 | 7.73 | 1.133× [0.944, 1.411] | 2,106,048 | 2,106,048 | 0.00% |
| tcp | spaced-32k | cold | 5 | 21.12 | 16.15 | 1.029× [0.750, 1.851] | 2,105,920 | 2,176,220 | -3.34% |
| tcp | spaced-32k | warm | 5 | 24.43 | 14.64 | 0.792× [0.566, 1.217] | 4,212,096 | 3,468,041 | 17.66% |
| tcp | spaced-32k | trained | 5 | 25.51 | 20.55 | 1.064× [0.848, 1.527] | 2,106,048 | 583,104 | 72.31% |
| tcp | spaced-32k | latency | 5 | 7.76 | 7.19 | 1.218× [0.915, 1.909] | 2,106,048 | 583,104 | 72.31% |

## Adaptive state and managed allocation

Adaptive counters and cache state are cumulative through the end of the phase, after its drain. Plan hits this phase subtract the preceding phase's ending counter (the first phase starts at zero). Allocation covers only the timed phase in the client process, including the proxy; it is approximate allocated volume rather than retained memory. Encoding attempts and bypasses count outgoing non-control messages, which can include queue-drain application barriers as well as measured payloads.

| Transport | Workload | Phase | Allocated bytes off/on | Candidates on | Attempts on | Bypasses on | Probe interval on | Plans on | Plan hits cumulative on | Plan hits this phase on |
|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|
| reliable-udp | adaptive-32k | random | 5,393,744 / 6,024,208 | 617 | 6 | 64 | 64 | 0 | 0 | 0 |
| reliable-udp | adaptive-32k | repeat | 5,256,568 / 4,040,392 | 621 | 66 | 72 | 1 | 1 | 55 | 55 |
| reliable-udp | adaptive-32k | churn | 5,263,184 / 5,852,840 | 1,219 | 72 | 136 | 64 | 1 | 55 | 0 |
| reliable-udp | adaptive-32k | return | 5,258,072 / 3,007,272 | 1,221 | 136 | 136 | 1 | 1 | 119 | 64 |
| reliable-udp | adaptive-32k | runs | 5,262,608 / 3,140,376 | 1,225 | 200 | 136 | 1 | 1 | 119 | 0 |
| reliable-udp | adaptive-32k | latency | 5,363,192 / 3,196,632 | 1,227 | 264 | 136 | 1 | 1 | 119 | 0 |
| reliable-udp | marginal-32k | cold | 5,404,712 / 6,481,792 | 775 | 8 | 64 | 64 | 0 | 0 | 0 |
| reliable-udp | marginal-32k | warm | 10,465,544 / 10,716,384 | 995 | 10 | 192 | 64 | 0 | 0 | 0 |
| reliable-udp | marginal-32k | latency | 5,375,232 / 5,466,672 | 1,105 | 11 | 256 | 64 | 0 | 0 | 0 |
| reliable-udp | spaced-32k | cold | 5,360,728 / 8,019,000 | 1,639 | 20 | 64 | 1 | 0 | 0 | 0 |
| reliable-udp | spaced-32k | warm | 10,523,976 / 21,743,240 | 98 | 137 | 119 | 1 | 32 | 2 | 2 |
| reliable-udp | spaced-32k | trained | 5,220,800 / 4,064,080 | 124 | 201 | 119 | 1 | 32 | 66 | 64 |
| reliable-udp | spaced-32k | latency | 5,360,344 / 4,144,888 | 150 | 265 | 119 | 1 | 32 | 130 | 64 |
| tcp | adaptive-32k | random | 6,985,440 / 7,637,624 | 617 | 6 | 64 | 64 | 0 | 0 | 0 |
| tcp | adaptive-32k | repeat | 6,800,192 / 4,154,432 | 621 | 66 | 72 | 1 | 1 | 55 | 55 |
| tcp | adaptive-32k | churn | 6,723,664 / 7,364,720 | 1,219 | 72 | 136 | 64 | 1 | 55 | 0 |
| tcp | adaptive-32k | return | 6,719,568 / 2,792,008 | 1,221 | 136 | 136 | 1 | 1 | 119 | 64 |
| tcp | adaptive-32k | runs | 6,736,016 / 2,930,520 | 1,225 | 200 | 136 | 1 | 1 | 119 | 0 |
| tcp | adaptive-32k | latency | 6,751,152 / 2,911,888 | 1,227 | 264 | 136 | 1 | 1 | 119 | 0 |
| tcp | marginal-32k | cold | 6,997,904 / 8,035,016 | 814 | 8 | 64 | 64 | 0 | 0 | 0 |
| tcp | marginal-32k | warm | 13,528,704 / 13,778,488 | 1,006 | 10 | 192 | 64 | 0 | 0 | 0 |
| tcp | marginal-32k | latency | 6,727,216 / 6,841,416 | 1,107 | 11 | 256 | 64 | 0 | 0 | 0 |
| tcp | spaced-32k | cold | 6,960,896 / 9,818,000 | 1,639 | 20 | 64 | 1 | 0 | 0 | 0 |
| tcp | spaced-32k | warm | 13,668,808 / 24,223,672 | 97 | 134 | 122 | 1 | 31 | 0 | 0 |
| tcp | spaced-32k | trained | 6,723,880 / 5,347,560 | 123 | 198 | 122 | 1 | 32 | 42 | 42 |
| tcp | spaced-32k | latency | 6,745,448 / 4,306,832 | 149 | 262 | 122 | 1 | 32 | 106 | 64 |

## Application round-trip latency

| Transport | Workload | Phase | p50 ms off/on | p95 ms off/on | p99 ms off/on | p95 speedup off/on [95% CI] | Client CPU ms off/on | Definitions on | Cache bytes on |
|---|---|---|---|---|---|---|---|---:|---:|
| reliable-udp | adaptive-32k | random | 7.944 / 7.398 | 15.530 / 13.384 | 24.666 / 28.457 | 1.042× [0.860, 1.232] | 62.500 / 31.250 | 0 | 452,328 |
| reliable-udp | adaptive-32k | repeat | 7.322 / 5.403 | 11.101 / 11.371 | 12.687 / 14.830 | 0.678× [0.283, 1.225] | 15.625 / 31.250 | 98 | 816,687 |
| reliable-udp | adaptive-32k | churn | 7.093 / 7.079 | 9.016 / 10.083 | 9.828 / 10.566 | 0.742× [0.414, 1.050] | 15.625 / 31.250 | 98 | 1,230,941 |
| reliable-udp | adaptive-32k | return | 6.963 / 5.151 | 11.042 / 6.645 | 13.939 / 7.086 | 1.618× [1.407, 1.930] | 31.250 / 15.625 | 98 | 1,232,197 |
| reliable-udp | adaptive-32k | runs | 6.912 / 4.671 | 7.942 / 6.059 | 8.712 / 7.030 | 1.270× [1.144, 1.384] | 46.875 / 31.250 | 99 | 1,238,080 |
| reliable-udp | adaptive-32k | latency | 4.111 / 3.414 | 5.129 / 4.564 | 10.154 / 6.846 | 1.330× [0.980, 2.347] | 31.250 / 62.500 | 99 | 1,240,896 |
| reliable-udp | marginal-32k | cold | 7.946 / 7.730 | 15.476 / 11.204 | 25.380 / 35.029 | 1.340× [1.122, 1.565] | 31.250 / 46.875 | 1 | 872,581 |
| reliable-udp | marginal-32k | warm | 7.840 / 7.050 | 11.973 / 11.357 | 12.701 / 12.060 | 0.999× [0.705, 1.260] | 78.125 / 46.875 | 1 | 1,022,253 |
| reliable-udp | marginal-32k | latency | 5.016 / 4.867 | 6.928 / 7.064 | 9.303 / 8.660 | 0.913× [0.790, 1.058] | 31.250 / 31.250 | 1 | 1,097,107 |
| reliable-udp | spaced-32k | cold | 8.084 / 7.689 | 16.268 / 14.809 | 23.250 / 28.425 | 1.064× [0.959, 1.182] | 31.250 / 31.250 | 213 | 1,575,640 |
| reliable-udp | spaced-32k | warm | 7.455 / 8.202 | 13.801 / 18.133 | 15.174 / 24.443 | 0.725× [0.680, 0.777] | 46.875 / 93.750 | 3,323 | 2,345,512 |
| reliable-udp | spaced-32k | trained | 7.299 / 5.995 | 9.729 / 9.524 | 11.618 / 11.235 | 1.018× [0.891, 1.201] | 15.625 / 15.625 | 3,323 | 2,361,856 |
| reliable-udp | spaced-32k | latency | 4.800 / 4.663 | 6.401 / 6.431 | 10.121 / 9.931 | 0.979× [0.883, 1.086] | 31.250 / 31.250 | 3,323 | 2,378,200 |
| tcp | adaptive-32k | random | 4.766 / 5.998 | 10.535 / 14.809 | 20.331 / 35.221 | 0.661× [0.486, 0.883] | 15.625 / 15.625 | 0 | 452,328 |
| tcp | adaptive-32k | repeat | 4.868 / 5.512 | 6.360 / 10.993 | 6.610 / 13.530 | 0.420× [0.166, 0.707] | 15.625 / 15.625 | 98 | 816,687 |
| tcp | adaptive-32k | churn | 4.821 / 5.658 | 6.091 / 7.871 | 6.429 / 9.437 | 0.488× [0.118, 1.228] | 0.000 / 15.625 | 98 | 1,230,941 |
| tcp | adaptive-32k | return | 4.564 / 5.117 | 6.951 / 6.107 | 7.671 / 7.756 | 0.680× [0.203, 1.479] | 15.625 / 15.625 | 98 | 1,232,197 |
| tcp | adaptive-32k | runs | 4.418 / 4.780 | 6.089 / 6.141 | 7.193 / 10.525 | 0.695× [0.222, 1.560] | 31.250 / 31.250 | 99 | 1,238,080 |
| tcp | adaptive-32k | latency | 3.637 / 4.191 | 4.577 / 5.820 | 5.618 / 6.113 | 0.512× [0.155, 1.002] | 15.625 / 46.875 | 99 | 1,240,896 |
| tcp | marginal-32k | cold | 4.788 / 5.022 | 12.383 / 7.256 | 20.748 / 25.452 | 2.011× [1.455, 3.387] | 15.625 / 46.875 | 1 | 887,497 |
| tcp | marginal-32k | warm | 4.574 / 4.861 | 5.610 / 6.179 | 6.827 / 6.862 | 1.085× [0.876, 1.535] | 31.250 / 62.500 | 1 | 1,026,394 |
| tcp | marginal-32k | latency | 3.896 / 3.854 | 5.486 / 4.846 | 6.931 / 6.251 | 1.295× [0.980, 1.748] | 46.875 / 46.875 | 1 | 1,097,792 |
| tcp | spaced-32k | cold | 5.299 / 5.357 | 11.137 / 11.274 | 19.821 / 24.425 | 1.479× [0.986, 3.157] | 31.250 / 15.625 | 213 | 1,575,640 |
| tcp | spaced-32k | warm | 5.190 / 5.376 | 10.178 / 10.093 | 11.415 / 12.362 | 1.419× [0.914, 2.473] | 46.875 / 46.875 | 3,323 | 2,343,996 |
| tcp | spaced-32k | trained | 5.295 / 4.556 | 8.177 / 6.031 | 8.451 / 7.585 | 1.627× [1.077, 2.978] | 15.625 / 31.250 | 3,323 | 2,360,656 |
| tcp | spaced-32k | latency | 3.804 / 4.195 | 4.983 / 5.665 | 6.335 / 8.538 | 1.315× [0.836, 2.556] | 31.250 / 0.000 | 3,323 | 2,377,000 |

## Complete connection byte cost

Uses the ending cumulative counter, including setup, every reported phase and any interphase traffic. Between-phase bytes are the gaps between setup/phase snapshots and are already included in the total.

| Transport | Workload | Pairs | Payload bytes | Total bytes off | Total bytes on | Bytes saved | Between-phase bytes off/on | Setup bytes off/on | Setup ms off/on |
|---|---|---:|---:|---:|---:|---:|---|---|---|
| reliable-udp | adaptive-32k | 5 | 12,582,912 | 12,839,703 | 4,818,663 | 62.47% | 0 / 0 | 10,469 / 10,655 | 360.55 / 344.20 |
| reliable-udp | marginal-32k | 5 | 8,388,608 | 8,561,586 | 8,562,122 | -0.01% | 0 / 0 | 10,469 / 10,655 | 332.39 / 330.39 |
| reliable-udp | spaced-32k | 5 | 10,485,760 | 10,700,529 | 6,832,399 | 36.15% | 0 / 0 | 10,469 / 10,655 | 330.97 / 334.54 |
| tcp | adaptive-32k | 5 | 12,582,912 | 12,646,064 | 4,734,933 | 62.56% | 0 / 0 | 9,904 / 10,090 | 292.83 / 343.90 |
| tcp | marginal-32k | 5 | 8,388,608 | 8,433,968 | 8,434,427 | -0.01% | 0 / 0 | 9,904 / 10,090 | 282.12 / 301.92 |
| tcp | spaced-32k | 5 | 10,485,760 | 10,540,016 | 6,849,133 | 35.02% | 0 / 0 | 9,904 / 10,090 | 305.58 / 292.83 |
