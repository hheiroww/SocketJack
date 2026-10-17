# Binary compression transfer benchmark

Source: `docs\binary-compression-benchmark\2026-10-15-tcp-optimized\adaptive.json`. Per-mode figures are medians of complete runs. On/off comparisons pair the same round. Compression is the only setting changed.

Socket bytes count both directions once, including dictionary updates, dictionary acknowledgements, application acknowledgements, and SocketJack framing. They exclude IP/TCP/UDP/Ethernet headers. Setup is reported separately and is included once in the lifetime totals. Lifetime totals use the ending cumulative counter, including traffic between phase snapshots.

Bulk throughput is useful application payload divided by the time from the first send through the last validated application acknowledgement. Bulk RTT uses eight outstanding messages; latency RTT uses one outstanding message after warm training. Byte counters drain after timing stops; settle time is excluded from throughput. Phase order and message counts follow the raw file's phasePlans metadata; legacy results without that field use cold, warm, latency. Every connection retains its adaptive cache across phases.

Throughput ratios use the geometric mean of paired on/off ratios and 10,000 paired bootstrap resamples (seed 7301; percentile 95% interval). An interval spanning 1 does not establish a speed change. Round-tail medians are not a pooled per-message percentile. With 64 latency samples, p99 is effectively the maximum sample and is descriptive. Fresh processes make the cold phase include first-use JIT costs; warm phases reuse the process and cache. CPU and optional managed-allocation figures cover the client process and its counting proxy, excluding the drain. Allocation is approximate process-wide allocation rather than retained memory. Setup timing measures Connect; setup byte totals also include its quiet-drain traffic. Loopback results do not establish WAN performance.

| Transport | Workload | Phase | Pairs | MiB/s off | MiB/s on | On/off speed [95% CI] | Total bytes off | Total bytes on | Bytes saved |
|---|---|---|---:|---:|---:|---|---:|---:|---:|
| tcp | adaptive-32k | random | 5 | 20.16 | 20.32 | 1.000× [0.951, 1.051] | 2,105,920 | 2,105,920 | 0.00% |
| tcp | adaptive-32k | repeat | 5 | 25.70 | 21.09 | 0.788× [0.736, 0.836] | 2,106,048 | 349,600 | 83.40% |
| tcp | adaptive-32k | churn | 5 | 25.98 | 20.32 | 0.794× [0.754, 0.837] | 2,106,048 | 2,106,048 | 0.00% |
| tcp | adaptive-32k | return | 5 | 26.05 | 25.86 | 0.962× [0.908, 1.023] | 2,106,048 | 59,328 | 97.18% |
| tcp | adaptive-32k | runs | 5 | 26.68 | 24.58 | 0.946× [0.861, 1.024] | 2,106,048 | 16,955 | 99.19% |
| tcp | adaptive-32k | latency | 5 | 8.18 | 7.96 | 0.982× [0.951, 1.013] | 2,106,048 | 14,848 | 99.29% |
| tcp | marginal-32k | cold | 5 | 22.45 | 21.06 | 0.877× [0.735, 0.980] | 2,105,920 | 2,105,920 | 0.00% |
| tcp | marginal-32k | warm | 5 | 25.50 | 26.28 | 0.989× [0.874, 1.082] | 4,212,096 | 4,212,096 | 0.00% |
| tcp | marginal-32k | latency | 5 | 7.38 | 7.61 | 1.094× [0.956, 1.309] | 2,106,048 | 2,106,048 | 0.00% |
| tcp | spaced-32k | cold | 5 | 21.70 | 17.69 | 0.809× [0.797, 0.823] | 2,105,920 | 2,105,920 | 0.00% |
| tcp | spaced-32k | warm | 5 | 25.39 | 14.62 | 0.577× [0.550, 0.605] | 4,212,096 | 2,664,216 | 36.75% |
| tcp | spaced-32k | trained | 5 | 27.15 | 24.12 | 0.886× [0.825, 0.952] | 2,106,048 | 347,862 | 83.48% |
| tcp | spaced-32k | latency | 5 | 7.66 | 7.46 | 0.962× [0.931, 0.991] | 2,106,048 | 347,862 | 83.48% |

## Adaptive state and managed allocation

Adaptive counters and cache state are cumulative through the end of the phase, after its drain. Plan hits this phase subtract the preceding phase's ending counter (the first phase starts at zero). Allocation covers only the timed phase in the client process, including the proxy; it is approximate allocated volume rather than retained memory. Encoding attempts and bypasses count outgoing non-control messages, which can include queue-drain application barriers as well as measured payloads.

| Transport | Workload | Phase | Allocated bytes off/on | Candidates on | Attempts on | Bypasses on | Probe interval on | Plans on | Plan hits cumulative on | Plan hits this phase on |
|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|
| tcp | adaptive-32k | random | 6,998,776 / 7,069,832 | 46 | 0 | 64 | 64 | 0 | 0 | 0 |
| tcp | adaptive-32k | repeat | 6,782,152 / 3,983,728 | 53 | 56 | 72 | 1 | 1 | 55 | 55 |
| tcp | adaptive-32k | churn | 6,730,224 / 7,174,688 | 347 | 62 | 136 | 64 | 1 | 55 | 0 |
| tcp | adaptive-32k | return | 6,731,848 / 2,694,488 | 350 | 126 | 136 | 1 | 1 | 119 | 64 |
| tcp | adaptive-32k | runs | 6,720,664 / 3,019,408 | 352 | 190 | 136 | 1 | 1 | 119 | 0 |
| tcp | adaptive-32k | latency | 6,752,112 / 2,912,616 | 354 | 254 | 136 | 1 | 1 | 119 | 0 |
| tcp | marginal-32k | cold | 6,978,128 / 7,049,072 | 46 | 0 | 64 | 64 | 0 | 0 | 0 |
| tcp | marginal-32k | warm | 13,522,304 / 13,619,440 | 147 | 0 | 192 | 64 | 0 | 0 | 0 |
| tcp | marginal-32k | latency | 6,719,024 / 6,761,000 | 194 | 0 | 256 | 64 | 0 | 0 | 0 |
| tcp | spaced-32k | cold | 6,966,160 / 8,343,048 | 1,536 | 0 | 64 | 1 | 0 | 0 | 0 |
| tcp | spaced-32k | warm | 13,584,568 / 20,886,328 | 139 | 123 | 96 | 1 | 32 | 38 | 38 |
| tcp | spaced-32k | trained | 6,723,432 / 3,574,968 | 159 | 187 | 96 | 1 | 32 | 102 | 64 |
| tcp | spaced-32k | latency | 6,744,688 / 3,588,928 | 179 | 251 | 96 | 1 | 32 | 166 | 64 |

## Application round-trip latency

| Transport | Workload | Phase | p50 ms off/on | p95 ms off/on | p99 ms off/on | p95 speedup off/on [95% CI] | Client CPU ms off/on | Definitions on | Cache bytes on |
|---|---|---|---|---|---|---|---|---:|---:|
| tcp | adaptive-32k | random | 5.479 / 5.181 | 11.824 / 10.942 | 20.366 / 21.286 | 1.117× [0.995, 1.264] | 0.000 / 0.000 | 0 | 69,050 |
| tcp | adaptive-32k | repeat | 4.980 / 4.956 | 6.395 / 8.867 | 6.905 / 12.564 | 0.690× [0.637, 0.723] | 15.625 / 15.625 | 47 | 417,365 |
| tcp | adaptive-32k | churn | 5.052 / 4.888 | 6.098 / 6.726 | 7.126 / 16.520 | 0.963× [0.829, 1.123] | 15.625 / 15.625 | 47 | 721,796 |
| tcp | adaptive-32k | return | 4.897 / 4.809 | 6.166 / 5.840 | 6.946 / 7.096 | 0.985× [0.852, 1.139] | 0.000 / 0.000 | 47 | 723,680 |
| tcp | adaptive-32k | runs | 4.859 / 4.721 | 6.112 / 7.167 | 7.496 / 7.862 | 0.817× [0.722, 0.938] | 15.625 / 15.625 | 48 | 727,840 |
| tcp | adaptive-32k | latency | 3.670 / 3.689 | 4.644 / 4.732 | 6.092 / 6.800 | 0.953× [0.925, 0.995] | 31.250 / 31.250 | 48 | 730,528 |
| tcp | marginal-32k | cold | 4.915 / 4.929 | 11.330 / 10.150 | 20.009 / 20.200 | 0.984× [0.750, 1.159] | 0.000 / 15.625 | 0 | 69,050 |
| tcp | marginal-32k | warm | 4.761 / 4.738 | 6.369 / 5.871 | 6.892 / 7.478 | 1.044× [0.846, 1.213] | 0.000 / 31.250 | 0 | 173,452 |
| tcp | marginal-32k | latency | 3.919 / 3.801 | 4.994 / 4.913 | 7.828 / 7.420 | 1.196× [0.943, 1.674] | 15.625 / 46.875 | 0 | 224,327 |
| tcp | spaced-32k | cold | 5.029 / 5.111 | 11.143 / 11.306 | 20.108 / 21.026 | 0.972× [0.941, 1.005] | 15.625 / 31.250 | 0 | 1,658,746 |
| tcp | spaced-32k | warm | 4.884 / 5.141 | 9.398 / 14.162 | 10.800 / 18.594 | 0.669× [0.618, 0.724] | 15.625 / 46.875 | 1,491 | 2,004,323 |
| tcp | spaced-32k | trained | 4.572 / 4.576 | 5.331 / 5.571 | 5.914 / 6.760 | 0.967× [0.926, 1.003] | 15.625 / 15.625 | 1,491 | 2,024,417 |
| tcp | spaced-32k | latency | 3.830 / 3.988 | 4.965 / 5.945 | 8.294 / 7.894 | 0.836× [0.749, 0.956] | 46.875 / 15.625 | 1,491 | 2,044,511 |

## Complete connection byte cost

Uses the ending cumulative counter, including setup, every reported phase and any interphase traffic. Between-phase bytes are the gaps between setup/phase snapshots and are already included in the total.

| Transport | Workload | Pairs | Payload bytes | Total bytes off | Total bytes on | Bytes saved | Between-phase bytes off/on | Setup bytes off/on | Setup ms off/on |
|---|---|---:|---:|---:|---:|---:|---|---|---|
| tcp | adaptive-32k | 5 | 12,582,912 | 12,646,064 | 4,662,789 | 63.13% | 0 / 0 | 9,904 / 10,090 | 279.48 / 296.09 |
| tcp | marginal-32k | 5 | 8,388,608 | 8,433,968 | 8,434,154 | -0.00% | 0 / 0 | 9,904 / 10,090 | 284.85 / 294.12 |
| tcp | spaced-32k | 5 | 10,485,760 | 10,540,016 | 5,475,950 | 48.05% | 0 / 0 | 9,904 / 10,090 | 275.59 / 283.06 |
