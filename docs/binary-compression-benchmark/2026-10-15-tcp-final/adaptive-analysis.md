# Binary compression transfer benchmark

Source: `docs\binary-compression-benchmark\2026-10-15-tcp-final\adaptive.json`. Per-mode figures are medians of complete runs. On/off comparisons pair the same round. Compression is the only setting changed.

Socket bytes count both directions once, including dictionary updates, dictionary acknowledgements, application acknowledgements, and SocketJack framing. They exclude IP/TCP/UDP/Ethernet headers. Setup is reported separately and is included once in the lifetime totals. Lifetime totals use the ending cumulative counter, including traffic between phase snapshots.

Bulk throughput is useful application payload divided by the time from the first send through the last validated application acknowledgement. Bulk RTT uses eight outstanding messages; latency RTT uses one outstanding message after warm training. Byte counters drain after timing stops; settle time is excluded from throughput. Phase order and message counts follow the raw file's phasePlans metadata; legacy results without that field use cold, warm, latency. Every connection retains its adaptive cache across phases.

Throughput ratios use the geometric mean of paired on/off ratios and 10,000 paired bootstrap resamples (seed 7301; percentile 95% interval). An interval spanning 1 does not establish a speed change. Round-tail medians are not a pooled per-message percentile. With 64 latency samples, p99 is effectively the maximum sample and is descriptive. Fresh processes make the cold phase include first-use JIT costs; warm phases reuse the process and cache. CPU and optional managed-allocation figures cover the client process and its counting proxy, excluding the drain. Allocation is approximate process-wide allocation rather than retained memory. Setup timing measures Connect; setup byte totals also include its quiet-drain traffic. Loopback results do not establish WAN performance.

| Transport | Workload | Phase | Pairs | MiB/s off | MiB/s on | On/off speed [95% CI] | Total bytes off | Total bytes on | Bytes saved |
|---|---|---|---:|---:|---:|---|---:|---:|---:|
| tcp | adaptive-32k | random | 5 | 23.66 | 23.55 | 0.937× [0.835, 1.009] | 2,105,920 | 2,105,920 | 0.00% |
| tcp | adaptive-32k | repeat | 5 | 28.36 | 22.93 | 0.790× [0.735, 0.832] | 2,106,048 | 348,718 | 83.44% |
| tcp | adaptive-32k | churn | 5 | 28.74 | 20.25 | 0.711× [0.621, 0.801] | 2,106,048 | 2,106,048 | 0.00% |
| tcp | adaptive-32k | return | 5 | 28.69 | 27.65 | 0.939× [0.880, 0.986] | 2,106,048 | 59,328 | 97.18% |
| tcp | adaptive-32k | runs | 5 | 29.26 | 27.48 | 0.931× [0.880, 0.963] | 2,106,048 | 16,955 | 99.19% |
| tcp | adaptive-32k | latency | 5 | 8.34 | 7.98 | 0.966× [0.935, 0.997] | 2,106,048 | 14,848 | 99.29% |
| tcp | marginal-32k | cold | 5 | 23.61 | 23.15 | 0.972× [0.951, 0.995] | 2,105,920 | 2,105,920 | 0.00% |
| tcp | marginal-32k | warm | 5 | 28.76 | 28.97 | 0.989× [0.966, 1.013] | 4,212,096 | 4,212,096 | 0.00% |
| tcp | marginal-32k | latency | 5 | 8.27 | 8.06 | 0.979× [0.958, 1.000] | 2,106,048 | 2,106,048 | 0.00% |
| tcp | spaced-32k | cold | 5 | 23.10 | 18.39 | 0.792× [0.788, 0.797] | 2,105,920 | 2,105,920 | 0.00% |
| tcp | spaced-32k | warm | 5 | 26.88 | 16.33 | 0.610× [0.598, 0.623] | 4,212,096 | 2,638,302 | 37.36% |
| tcp | spaced-32k | trained | 5 | 29.08 | 24.79 | 0.870× [0.839, 0.914] | 2,106,048 | 347,862 | 83.48% |
| tcp | spaced-32k | latency | 5 | 8.20 | 7.85 | 0.940× [0.896, 0.970] | 2,106,048 | 347,862 | 83.48% |

## Adaptive state and managed allocation

Adaptive counters and cache state are cumulative through the end of the phase, after its drain. Plan hits this phase subtract the preceding phase's ending counter (the first phase starts at zero). Allocation covers only the timed phase in the client process, including the proxy; it is approximate allocated volume rather than retained memory. Encoding attempts and bypasses count outgoing non-control messages, which can include queue-drain application barriers as well as measured payloads.

| Transport | Workload | Phase | Allocated bytes off/on | Candidates on | Attempts on | Bypasses on | Probe interval on | Plans on | Plan hits cumulative on | Plan hits this phase on |
|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|
| tcp | adaptive-32k | random | 7,005,784 / 7,007,848 | 0 | 0 | 64 | 64 | 0 | 0 | 0 |
| tcp | adaptive-32k | repeat | 6,785,632 / 3,860,856 | 7 | 56 | 72 | 1 | 1 | 55 | 55 |
| tcp | adaptive-32k | churn | 6,724,776 / 7,252,392 | 301 | 62 | 136 | 64 | 1 | 55 | 0 |
| tcp | adaptive-32k | return | 6,723,488 / 2,688,224 | 304 | 126 | 136 | 1 | 1 | 119 | 64 |
| tcp | adaptive-32k | runs | 6,740,240 / 3,035,848 | 306 | 190 | 136 | 1 | 1 | 119 | 0 |
| tcp | adaptive-32k | latency | 6,743,880 / 2,921,208 | 308 | 254 | 136 | 1 | 1 | 119 | 0 |
| tcp | marginal-32k | cold | 6,993,832 / 7,020,176 | 0 | 0 | 64 | 64 | 0 | 0 | 0 |
| tcp | marginal-32k | warm | 13,521,592 / 13,530,992 | 0 | 0 | 192 | 64 | 0 | 0 | 0 |
| tcp | marginal-32k | latency | 6,723,200 / 6,724,072 | 0 | 0 | 256 | 64 | 0 | 0 | 0 |
| tcp | spaced-32k | cold | 6,928,488 / 8,332,272 | 1,536 | 0 | 64 | 1 | 0 | 0 | 0 |
| tcp | spaced-32k | warm | 13,578,328 / 20,602,080 | 139 | 123 | 96 | 1 | 32 | 38 | 38 |
| tcp | spaced-32k | trained | 6,743,144 / 3,579,312 | 159 | 187 | 96 | 1 | 32 | 102 | 64 |
| tcp | spaced-32k | latency | 6,740,240 / 3,597,384 | 179 | 251 | 96 | 1 | 32 | 166 | 64 |

## Application round-trip latency

| Transport | Workload | Phase | p50 ms off/on | p95 ms off/on | p99 ms off/on | p95 speedup off/on [95% CI] | Client CPU ms off/on | Definitions on | Cache bytes on |
|---|---|---|---|---|---|---|---|---:|---:|
| tcp | adaptive-32k | random | 4.591 / 4.522 | 10.677 / 9.809 | 18.940 / 19.914 | 1.038× [0.890, 1.164] | 15.625 / 31.250 | 0 | 18,560 |
| tcp | adaptive-32k | repeat | 4.322 / 4.426 | 6.550 / 6.526 | 7.468 / 11.228 | 0.927× [0.830, 1.029] | 0.000 / 15.625 | 47 | 366,875 |
| tcp | adaptive-32k | churn | 4.368 / 4.909 | 5.005 / 7.107 | 5.783 / 16.295 | 0.744× [0.598, 0.893] | 15.625 / 31.250 | 47 | 671,306 |
| tcp | adaptive-32k | return | 4.384 / 4.579 | 5.373 / 5.952 | 6.244 / 6.885 | 0.895× [0.704, 1.104] | 15.625 / 15.625 | 47 | 673,190 |
| tcp | adaptive-32k | runs | 4.340 / 4.400 | 5.152 / 5.500 | 6.010 / 6.655 | 0.909× [0.853, 0.952] | 15.625 / 31.250 | 48 | 677,350 |
| tcp | adaptive-32k | latency | 3.537 / 3.719 | 4.561 / 4.641 | 7.034 / 7.402 | 0.975× [0.903, 1.054] | 15.625 / 78.125 | 48 | 680,038 |
| tcp | marginal-32k | cold | 4.560 / 4.626 | 10.441 / 9.702 | 19.139 / 19.769 | 1.067× [1.027, 1.111] | 0.000 / 0.000 | 0 | 18,560 |
| tcp | marginal-32k | warm | 4.330 / 4.276 | 5.696 / 5.340 | 6.818 / 6.154 | 0.998× [0.877, 1.142] | 31.250 / 31.250 | 0 | 18,560 |
| tcp | marginal-32k | latency | 3.675 / 3.690 | 4.320 / 4.414 | 5.145 / 7.119 | 0.987× [0.942, 1.031] | 15.625 / 15.625 | 0 | 18,560 |
| tcp | spaced-32k | cold | 4.582 / 4.843 | 9.808 / 9.970 | 18.021 / 20.281 | 1.001× [0.953, 1.065] | 15.625 / 15.625 | 0 | 1,658,746 |
| tcp | spaced-32k | warm | 4.387 / 4.659 | 9.334 / 10.205 | 9.835 / 16.524 | 0.913× [0.883, 0.952] | 0.000 / 46.875 | 1,491 | 2,004,323 |
| tcp | spaced-32k | trained | 4.333 / 4.226 | 5.364 / 7.388 | 6.138 / 8.470 | 0.874× [0.693, 1.094] | 15.625 / 15.625 | 1,491 | 2,024,417 |
| tcp | spaced-32k | latency | 3.660 / 3.773 | 4.432 / 4.929 | 5.446 / 6.877 | 0.876× [0.772, 0.959] | 0.000 / 15.625 | 1,491 | 2,044,511 |

## Complete connection byte cost

Uses the ending cumulative counter, including setup, every reported phase and any interphase traffic. Between-phase bytes are the gaps between setup/phase snapshots and are already included in the total.

| Transport | Workload | Pairs | Payload bytes | Total bytes off | Total bytes on | Bytes saved | Between-phase bytes off/on | Setup bytes off/on | Setup ms off/on |
|---|---|---:|---:|---:|---:|---:|---|---|---|
| tcp | adaptive-32k | 5 | 12,582,912 | 12,646,064 | 4,661,907 | 63.14% | 0 / 0 | 9,904 / 10,090 | 273.96 / 285.58 |
| tcp | marginal-32k | 5 | 8,388,608 | 8,433,968 | 8,434,154 | -0.00% | 0 / 0 | 9,904 / 10,090 | 265.44 / 279.62 |
| tcp | spaced-32k | 5 | 10,485,760 | 10,540,016 | 5,450,036 | 48.29% | 0 / 0 | 9,904 / 10,090 | 260.78 / 274.33 |
