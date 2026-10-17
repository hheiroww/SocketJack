# Binary compression transfer benchmark

Source: `docs\binary-compression-benchmark\2026-10-15-tcp-final\sustained.json`. Per-mode figures are medians of complete runs. On/off comparisons pair the same round. Compression is the only setting changed.

Socket bytes count both directions once, including dictionary updates, dictionary acknowledgements, application acknowledgements, and SocketJack framing. They exclude IP/TCP/UDP/Ethernet headers. Setup is reported separately and is included once in the lifetime totals. Lifetime totals use the ending cumulative counter, including traffic between phase snapshots.

Bulk throughput is useful application payload divided by the time from the first send through the last validated application acknowledgement. Bulk RTT uses eight outstanding messages; latency RTT uses one outstanding message after warm training. Byte counters drain after timing stops; settle time is excluded from throughput. Phase order and message counts follow the raw file's phasePlans metadata; legacy results without that field use cold, warm, latency. Every connection retains its adaptive cache across phases.

Throughput ratios use the geometric mean of paired on/off ratios and 10,000 paired bootstrap resamples (seed 7301; percentile 95% interval). An interval spanning 1 does not establish a speed change. Round-tail medians are not a pooled per-message percentile. With 64 latency samples, p99 is effectively the maximum sample and is descriptive. Fresh processes make the cold phase include first-use JIT costs; warm phases reuse the process and cache. CPU and optional managed-allocation figures cover the client process and its counting proxy, excluding the drain. Allocation is approximate process-wide allocation rather than retained memory. Setup timing measures Connect; setup byte totals also include its quiet-drain traffic. Loopback results do not establish WAN performance.

| Transport | Workload | Phase | Pairs | MiB/s off | MiB/s on | On/off speed [95% CI] | Total bytes off | Total bytes on | Bytes saved |
|---|---|---|---:|---:|---:|---|---:|---:|---:|
| tcp | sustained-delta-32k | cold | 6 | 23.31 | 19.41 | 0.814× [0.774, 0.850] | 2,105,920 | 797,564 | 62.13% |
| tcp | sustained-delta-32k | warm | 6 | 29.06 | 28.56 | 0.981× [0.961, 1.003] | 67,393,536 | 18,698,240 | 72.26% |
| tcp | sustained-delta-32k | latency | 6 | 8.72 | 9.11 | 1.043× [1.031, 1.053] | 8,424,192 | 2,337,280 | 72.26% |
| tcp | sustained-delta-blocks-32k | cold | 6 | 23.01 | 19.02 | 0.825× [0.786, 0.863] | 2,105,920 | 1,689,424 | 19.78% |
| tcp | sustained-delta-blocks-32k | warm | 6 | 28.52 | 28.29 | 0.992× [0.977, 1.008] | 67,393,536 | 33,020,857 | 51.00% |
| tcp | sustained-delta-blocks-32k | latency | 6 | 8.96 | 8.94 | 0.997× [0.977, 1.018] | 8,424,192 | 5,007,620 | 40.56% |
| tcp | sustained-random-32k | cold | 6 | 22.17 | 21.57 | 0.982× [0.937, 1.032] | 2,105,920 | 2,105,920 | 0.00% |
| tcp | sustained-random-32k | warm | 6 | 29.11 | 29.11 | 0.998× [0.990, 1.007] | 67,393,536 | 67,393,536 | 0.00% |
| tcp | sustained-random-32k | latency | 6 | 8.85 | 8.87 | 1.005× [0.995, 1.015] | 8,424,192 | 8,424,192 | 0.00% |

## Adaptive state and managed allocation

Adaptive counters and cache state are cumulative through the end of the phase, after its drain. Plan hits this phase subtract the preceding phase's ending counter (the first phase starts at zero). Allocation covers only the timed phase in the client process, including the proxy; it is approximate allocated volume rather than retained memory. Encoding attempts and bypasses count outgoing non-control messages, which can include queue-drain application barriers as well as measured payloads.

| Transport | Workload | Phase | Allocated bytes off/on | Candidates on | Attempts on | Bypasses on | Probe interval on | Plans on | Plan hits cumulative on | Plan hits this phase on |
|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|
| tcp | sustained-delta-32k | cold | 6,971,668 / 5,338,624 | 99 | 56 | 8 | 1 | 1 | 55 | 55 |
| tcp | sustained-delta-32k | warm | 215,541,104 / 137,483,148 | 953 | 2,104 | 8 | 1 | 2 | 2,102 | 2,047 |
| tcp | sustained-delta-32k | latency | 27,003,712 / 17,235,828 | 1,046 | 2,360 | 8 | 1 | 2 | 2,358 | 256 |
| tcp | sustained-delta-blocks-32k | cold | 7,052,088 / 8,243,180 | 147 | 57 | 7 | 1 | 1 | 56 | 56 |
| tcp | sustained-delta-blocks-32k | warm | 215,861,372 / 181,621,300 | 1,667 | 2,105 | 7 | 1 | 6 | 2,103 | 2,047 |
| tcp | sustained-delta-blocks-32k | latency | 26,997,696 / 25,529,452 | 1,831 | 2,361 | 7 | 1 | 8 | 2,359 | 256 |
| tcp | sustained-random-32k | cold | 7,029,804 / 7,040,424 | 0 | 0 | 64 | 64 | 0 | 0 | 0 |
| tcp | sustained-random-32k | warm | 215,878,924 / 215,902,832 | 0 | 0 | 2,112 | 64 | 0 | 0 | 0 |
| tcp | sustained-random-32k | latency | 27,009,064 / 27,005,168 | 0 | 0 | 2,368 | 64 | 0 | 0 | 0 |

## Application round-trip latency

| Transport | Workload | Phase | p50 ms off/on | p95 ms off/on | p99 ms off/on | p95 speedup off/on [95% CI] | Client CPU ms off/on | Definitions on | Cache bytes on |
|---|---|---|---|---|---|---|---|---:|---:|
| tcp | sustained-delta-32k | cold | 4.531 / 4.469 | 9.922 / 9.701 | 17.701 / 18.908 | 1.038× [0.920, 1.176] | 15.625 / 23.438 | 35 | 444,236 |
| tcp | sustained-delta-32k | warm | 4.279 / 4.239 | 5.392 / 5.439 | 7.756 / 8.450 | 0.986× [0.923, 1.054] | 476.562 / 554.688 | 35 | 1,314,563 |
| tcp | sustained-delta-32k | latency | 3.465 / 3.357 | 4.169 / 3.900 | 6.485 / 4.946 | 1.071× [1.041, 1.107] | 156.250 / 117.188 | 35 | 1,410,391 |
| tcp | sustained-delta-blocks-32k | cold | 4.587 / 4.720 | 10.287 / 9.102 | 18.258 / 19.020 | 1.101× [1.006, 1.227] | 31.250 / 15.625 | 28 | 495,711 |
| tcp | sustained-delta-blocks-32k | warm | 4.328 / 4.233 | 5.760 / 5.394 | 9.915 / 8.031 | 1.055× [0.973, 1.124] | 507.812 / 500.000 | 32 | 2,166,016 |
| tcp | sustained-delta-blocks-32k | latency | 3.385 / 3.376 | 4.073 / 4.067 | 6.279 / 5.482 | 1.010× [0.959, 1.060] | 156.250 / 101.562 | 34 | 2,349,003 |
| tcp | sustained-random-32k | cold | 4.714 / 4.578 | 10.310 / 11.924 | 18.401 / 20.570 | 0.872× [0.791, 0.949] | 23.438 / 15.625 | 0 | 18,560 |
| tcp | sustained-random-32k | warm | 4.254 / 4.233 | 5.453 / 5.371 | 8.181 / 7.933 | 1.016× [0.985, 1.054] | 554.688 / 445.312 | 0 | 18,560 |
| tcp | sustained-random-32k | latency | 3.385 / 3.401 | 4.079 / 4.006 | 7.420 / 6.054 | 1.020× [0.998, 1.041] | 148.438 / 148.438 | 0 | 18,560 |

## Complete connection byte cost

Uses the ending cumulative counter, including setup, every reported phase and any interphase traffic. Between-phase bytes are the gaps between setup/phase snapshots and are already included in the total.

| Transport | Workload | Pairs | Payload bytes | Total bytes off | Total bytes on | Bytes saved | Between-phase bytes off/on | Setup bytes off/on | Setup ms off/on |
|---|---|---:|---:|---:|---:|---:|---|---|---|
| tcp | sustained-delta-32k | 6 | 77,594,624 | 77,933,552 | 21,843,174 | 71.97% | 0 / 0 | 9,904 / 10,090 | 255.03 / 265.53 |
| tcp | sustained-delta-blocks-32k | 6 | 77,594,624 | 77,933,552 | 39,727,808 | 49.02% | 0 / 0 | 9,904 / 10,090 | 257.41 / 269.03 |
| tcp | sustained-random-32k | 6 | 77,594,624 | 77,933,552 | 77,933,738 | -0.00% | 0 / 0 | 9,904 / 10,090 | 256.62 / 264.43 |
