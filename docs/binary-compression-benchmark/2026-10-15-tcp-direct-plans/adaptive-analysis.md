# Binary compression transfer benchmark

Source: `docs\binary-compression-benchmark\2026-10-15-tcp-direct-plans\adaptive.json`. Per-mode figures are medians of complete runs. On/off comparisons pair the same round. Compression is the only setting changed.

Socket bytes count both directions once, including dictionary updates, dictionary acknowledgements, application acknowledgements, and SocketJack framing. They exclude IP/TCP/UDP/Ethernet headers. Setup is reported separately and is included once in the lifetime totals. Lifetime totals use the ending cumulative counter, including traffic between phase snapshots.

Bulk throughput is useful application payload divided by the time from the first send through the last validated application acknowledgement. Bulk RTT uses eight outstanding messages; latency RTT uses one outstanding message after warm training. Byte counters drain after timing stops; settle time is excluded from throughput. Phase order and message counts follow the raw file's phasePlans metadata; legacy results without that field use cold, warm, latency. Every connection retains its adaptive cache across phases.

Throughput ratios use the geometric mean of paired on/off ratios and 10,000 paired bootstrap resamples (seed 7301; percentile 95% interval). An interval spanning 1 does not establish a speed change. Round-tail medians are not a pooled per-message percentile. With 64 latency samples, p99 is effectively the maximum sample and is descriptive. Fresh processes make the cold phase include first-use JIT costs; warm phases reuse the process and cache. CPU and optional managed-allocation figures cover the client process and its counting proxy, excluding the drain. Allocation is approximate process-wide allocation rather than retained memory. Setup timing measures Connect; setup byte totals also include its quiet-drain traffic. Loopback results do not establish WAN performance.

| Transport | Workload | Phase | Pairs | MiB/s off | MiB/s on | On/off speed [95% CI] | Total bytes off | Total bytes on | Bytes saved |
|---|---|---|---:|---:|---:|---|---:|---:|---:|
| tcp | adaptive-32k | random | 5 | 23.18 | 23.29 | 0.976× [0.959, 0.996] | 2,105,920 | 2,105,920 | 0.00% |
| tcp | adaptive-32k | repeat | 5 | 27.98 | 22.97 | 0.815× [0.773, 0.859] | 2,106,048 | 346,926 | 83.53% |
| tcp | adaptive-32k | churn | 5 | 29.36 | 23.03 | 0.792× [0.742, 0.851] | 2,106,048 | 2,106,048 | 0.00% |
| tcp | adaptive-32k | return | 5 | 28.12 | 29.23 | 1.020× [0.998, 1.043] | 2,106,048 | 57,280 | 97.28% |
| tcp | adaptive-32k | runs | 5 | 29.11 | 27.23 | 0.957× [0.920, 0.999] | 2,106,048 | 97,038 | 95.39% |
| tcp | adaptive-32k | latency | 5 | 8.43 | 8.60 | 1.022× [1.007, 1.034] | 2,106,048 | 99,136 | 95.29% |
| tcp | marginal-32k | cold | 5 | 24.33 | 23.17 | 0.987× [0.945, 1.033] | 2,105,920 | 2,105,920 | 0.00% |
| tcp | marginal-32k | warm | 5 | 28.64 | 28.76 | 1.018× [0.974, 1.060] | 4,212,096 | 4,212,096 | 0.00% |
| tcp | marginal-32k | latency | 5 | 8.20 | 8.16 | 0.997× [0.990, 1.004] | 2,106,048 | 2,106,048 | 0.00% |
| tcp | spaced-32k | cold | 5 | 23.52 | 18.79 | 0.797× [0.780, 0.810] | 2,105,920 | 2,105,920 | 0.00% |
| tcp | spaced-32k | warm | 5 | 27.31 | 16.49 | 0.609× [0.601, 0.622] | 4,212,096 | 2,637,129 | 37.39% |
| tcp | spaced-32k | trained | 5 | 29.46 | 24.75 | 0.860× [0.837, 0.883] | 2,106,048 | 347,798 | 83.49% |
| tcp | spaced-32k | latency | 5 | 8.30 | 7.87 | 0.946× [0.936, 0.955] | 2,106,048 | 347,798 | 83.49% |

## Adaptive state and managed allocation

Adaptive counters and cache state are cumulative through the end of the phase, after its drain. Plan hits this phase subtract the preceding phase's ending counter (the first phase starts at zero). Allocation covers only the timed phase in the client process, including the proxy; it is approximate allocated volume rather than retained memory. Encoding attempts and bypasses count outgoing non-control messages, which can include queue-drain application barriers as well as measured payloads.

| Transport | Workload | Phase | Allocated bytes off/on | Candidates on | Attempts on | Bypasses on | Probe interval on | Plans on | Plan hits cumulative on | Plan hits this phase on |
|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|
| tcp | adaptive-32k | random | 6,987,240 / 7,033,360 | 0 | 0 | 64 | 64 | 0 | 0 | 0 |
| tcp | adaptive-32k | repeat | 6,778,424 / 3,557,072 | 7 | 56 | 72 | 1 | 1 | 56 | 56 |
| tcp | adaptive-32k | churn | 6,710,784 / 7,531,152 | 301 | 62 | 136 | 64 | 1 | 56 | 0 |
| tcp | adaptive-32k | return | 6,732,192 / 2,691,568 | 304 | 126 | 136 | 1 | 1 | 120 | 64 |
| tcp | adaptive-32k | runs | 6,729,912 / 2,904,152 | 306 | 190 | 136 | 1 | 2 | 180 | 60 |
| tcp | adaptive-32k | latency | 6,753,960 / 2,853,408 | 308 | 254 | 136 | 1 | 2 | 244 | 64 |
| tcp | marginal-32k | cold | 7,012,600 / 7,018,832 | 0 | 0 | 64 | 64 | 0 | 0 | 0 |
| tcp | marginal-32k | warm | 13,523,848 / 13,528,056 | 0 | 0 | 192 | 64 | 0 | 0 | 0 |
| tcp | marginal-32k | latency | 6,728,272 / 6,721,712 | 0 | 0 | 256 | 64 | 0 | 0 | 0 |
| tcp | spaced-32k | cold | 6,972,560 / 8,328,008 | 1,536 | 0 | 64 | 1 | 0 | 0 | 0 |
| tcp | spaced-32k | warm | 13,577,320 / 20,890,704 | 139 | 124 | 96 | 1 | 32 | 45 | 45 |
| tcp | spaced-32k | trained | 6,722,544 / 3,640,392 | 159 | 188 | 96 | 1 | 32 | 109 | 64 |
| tcp | spaced-32k | latency | 6,733,032 / 3,665,912 | 179 | 252 | 96 | 1 | 32 | 173 | 64 |

## Application round-trip latency

| Transport | Workload | Phase | p50 ms off/on | p95 ms off/on | p99 ms off/on | p95 speedup off/on [95% CI] | Client CPU ms off/on | Definitions on | Cache bytes on |
|---|---|---|---|---|---|---|---|---:|---:|
| tcp | adaptive-32k | random | 4.541 / 4.492 | 10.395 / 10.421 | 19.254 / 20.512 | 0.998× [0.932, 1.052] | 0.000 / 31.250 | 0 | 18,560 |
| tcp | adaptive-32k | repeat | 4.413 / 4.492 | 5.809 / 7.003 | 6.914 / 11.508 | 0.823× [0.706, 0.951] | 15.625 / 31.250 | 47 | 71,707 |
| tcp | adaptive-32k | churn | 4.319 / 4.237 | 5.001 / 5.478 | 5.571 / 12.406 | 0.933× [0.768, 1.172] | 15.625 / 15.625 | 47 | 671,306 |
| tcp | adaptive-32k | return | 4.376 / 4.109 | 5.420 / 4.879 | 6.813 / 5.108 | 1.254× [1.043, 1.508] | 31.250 / 15.625 | 47 | 673,190 |
| tcp | adaptive-32k | runs | 4.305 / 4.099 | 5.115 / 6.568 | 6.168 / 6.858 | 0.843× [0.607, 1.157] | 0.000 / 15.625 | 48 | 678,734 |
| tcp | adaptive-32k | latency | 3.566 / 3.506 | 4.324 / 4.101 | 5.327 / 5.155 | 1.053× [1.014, 1.091] | 31.250 / 15.625 | 48 | 681,422 |
| tcp | marginal-32k | cold | 4.469 / 4.425 | 10.568 / 9.719 | 18.547 / 19.559 | 1.085× [0.990, 1.206] | 15.625 / 15.625 | 0 | 18,560 |
| tcp | marginal-32k | warm | 4.348 / 4.300 | 7.088 / 5.292 | 8.107 / 5.843 | 1.205× [0.960, 1.454] | 31.250 / 15.625 | 0 | 18,560 |
| tcp | marginal-32k | latency | 3.633 / 3.658 | 4.691 / 4.423 | 7.502 / 7.341 | 1.038× [0.973, 1.107] | 31.250 / 15.625 | 0 | 18,560 |
| tcp | spaced-32k | cold | 4.570 / 4.606 | 9.709 / 9.647 | 17.857 / 19.774 | 1.002× [0.982, 1.023] | 15.625 / 0.000 | 0 | 1,658,746 |
| tcp | spaced-32k | warm | 4.349 / 4.500 | 8.945 / 9.878 | 9.798 / 15.688 | 0.944× [0.848, 1.074] | 31.250 / 31.250 | 1,491 | 2,004,323 |
| tcp | spaced-32k | trained | 4.252 / 4.305 | 5.078 / 5.370 | 5.604 / 6.113 | 0.970× [0.792, 1.218] | 15.625 / 15.625 | 1,491 | 2,024,417 |
| tcp | spaced-32k | latency | 3.624 / 3.755 | 4.320 / 4.768 | 5.862 / 6.438 | 0.912× [0.887, 0.948] | 0.000 / 31.250 | 1,491 | 2,044,511 |

## Complete connection byte cost

Uses the ending cumulative counter, including setup, every reported phase and any interphase traffic. Between-phase bytes are the gaps between setup/phase snapshots and are already included in the total.

| Transport | Workload | Pairs | Payload bytes | Total bytes off | Total bytes on | Bytes saved | Between-phase bytes off/on | Setup bytes off/on | Setup ms off/on |
|---|---|---:|---:|---:|---:|---:|---|---|---|
| tcp | adaptive-32k | 5 | 12,582,912 | 12,646,064 | 4,822,438 | 61.87% | 0 / 0 | 9,904 / 10,090 | 272.25 / 273.47 |
| tcp | marginal-32k | 5 | 8,388,608 | 8,433,968 | 8,434,154 | -0.00% | 0 / 0 | 9,904 / 10,090 | 271.30 / 280.18 |
| tcp | spaced-32k | 5 | 10,485,760 | 10,540,016 | 5,448,735 | 48.30% | 0 / 0 | 9,904 / 10,090 | 264.29 / 277.08 |
