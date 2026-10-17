# Binary compression transfer benchmark

Source: `docs\binary-compression-benchmark\2026-10-15-tcp-direct-plans\sustained.json`. Per-mode figures are medians of complete runs. On/off comparisons pair the same round. Compression is the only setting changed.

Socket bytes count both directions once, including dictionary updates, dictionary acknowledgements, application acknowledgements, and SocketJack framing. They exclude IP/TCP/UDP/Ethernet headers. Setup is reported separately and is included once in the lifetime totals. Lifetime totals use the ending cumulative counter, including traffic between phase snapshots.

Bulk throughput is useful application payload divided by the time from the first send through the last validated application acknowledgement. Bulk RTT uses eight outstanding messages; latency RTT uses one outstanding message after warm training. Byte counters drain after timing stops; settle time is excluded from throughput. Phase order and message counts follow the raw file's phasePlans metadata; legacy results without that field use cold, warm, latency. Every connection retains its adaptive cache across phases.

Throughput ratios use the geometric mean of paired on/off ratios and 10,000 paired bootstrap resamples (seed 7301; percentile 95% interval). An interval spanning 1 does not establish a speed change. Round-tail medians are not a pooled per-message percentile. With 64 latency samples, p99 is effectively the maximum sample and is descriptive. Fresh processes make the cold phase include first-use JIT costs; warm phases reuse the process and cache. CPU and optional managed-allocation figures cover the client process and its counting proxy, excluding the drain. Allocation is approximate process-wide allocation rather than retained memory. Setup timing measures Connect; setup byte totals also include its quiet-drain traffic. Loopback results do not establish WAN performance.

| Transport | Workload | Phase | Pairs | MiB/s off | MiB/s on | On/off speed [95% CI] | Total bytes off | Total bytes on | Bytes saved |
|---|---|---|---:|---:|---:|---|---:|---:|---:|
| tcp | sustained-delta-32k | cold | 6 | 23.44 | 20.08 | 0.839× [0.792, 0.875] | 2,105,920 | 797,564 | 62.13% |
| tcp | sustained-delta-32k | warm | 6 | 29.44 | 28.46 | 0.967× [0.948, 0.985] | 67,393,536 | 18,632,704 | 72.35% |
| tcp | sustained-delta-32k | latency | 6 | 8.92 | 8.88 | 0.987× [0.949, 1.021] | 8,424,192 | 2,329,088 | 72.35% |
| tcp | sustained-delta-blocks-32k | cold | 6 | 21.91 | 19.87 | 0.896× [0.864, 0.930] | 2,105,920 | 1,272,358 | 39.58% |
| tcp | sustained-delta-blocks-32k | warm | 6 | 29.09 | 28.34 | 0.978× [0.964, 1.001] | 67,393,536 | 35,177,070 | 47.80% |
| tcp | sustained-delta-blocks-32k | latency | 6 | 8.78 | 8.84 | 0.998× [0.976, 1.019] | 8,424,192 | 4,470,491 | 46.93% |
| tcp | sustained-random-32k | cold | 6 | 23.11 | 22.35 | 0.979× [0.947, 1.013] | 2,105,920 | 2,105,920 | 0.00% |
| tcp | sustained-random-32k | warm | 6 | 29.37 | 29.14 | 0.999× [0.984, 1.021] | 67,393,536 | 67,393,536 | 0.00% |
| tcp | sustained-random-32k | latency | 6 | 8.99 | 8.96 | 0.987× [0.951, 1.013] | 8,424,192 | 8,424,192 | 0.00% |

## Adaptive state and managed allocation

Adaptive counters and cache state are cumulative through the end of the phase, after its drain. Plan hits this phase subtract the preceding phase's ending counter (the first phase starts at zero). Allocation covers only the timed phase in the client process, including the proxy; it is approximate allocated volume rather than retained memory. Encoding attempts and bypasses count outgoing non-control messages, which can include queue-drain application barriers as well as measured payloads.

| Transport | Workload | Phase | Allocated bytes off/on | Candidates on | Attempts on | Bypasses on | Probe interval on | Plans on | Plan hits cumulative on | Plan hits this phase on |
|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|
| tcp | sustained-delta-32k | cold | 6,977,428 / 5,034,780 | 99 | 56 | 8 | 1 | 1 | 56 | 56 |
| tcp | sustained-delta-32k | warm | 215,681,264 / 137,422,572 | 953 | 2,104 | 8 | 1 | 2 | 2,104 | 2,048 |
| tcp | sustained-delta-32k | latency | 26,996,984 / 17,226,820 | 1,046 | 2,360 | 8 | 1 | 2 | 2,360 | 256 |
| tcp | sustained-delta-blocks-32k | cold | 7,018,528 / 6,593,072 | 147 | 57 | 7 | 1 | 4 | 57 | 57 |
| tcp | sustained-delta-blocks-32k | warm | 215,874,284 / 187,785,640 | 1,667 | 2,105 | 7 | 1 | 9 | 2,109 | 2,052 |
| tcp | sustained-delta-blocks-32k | latency | 26,997,744 / 23,751,844 | 1,831 | 2,361 | 7 | 1 | 11 | 2,367 | 258 |
| tcp | sustained-random-32k | cold | 7,020,676 / 7,056,384 | 0 | 0 | 64 | 64 | 0 | 0 | 0 |
| tcp | sustained-random-32k | warm | 215,858,156 / 215,613,624 | 0 | 0 | 2,112 | 64 | 0 | 0 | 0 |
| tcp | sustained-random-32k | latency | 27,003,232 / 27,002,768 | 0 | 0 | 2,368 | 64 | 0 | 0 | 0 |

## Application round-trip latency

| Transport | Workload | Phase | p50 ms off/on | p95 ms off/on | p99 ms off/on | p95 speedup off/on [95% CI] | Client CPU ms off/on | Definitions on | Cache bytes on |
|---|---|---|---|---|---|---|---|---:|---:|
| tcp | sustained-delta-32k | cold | 4.482 / 4.732 | 10.381 / 9.376 | 18.685 / 18.792 | 1.101× [0.962, 1.261] | 0.000 / 23.438 | 35 | 149,068 |
| tcp | sustained-delta-32k | warm | 4.231 / 4.265 | 5.239 / 5.586 | 7.282 / 7.560 | 0.966× [0.928, 1.008] | 429.688 / 507.812 | 35 | 1,019,395 |
| tcp | sustained-delta-32k | latency | 3.391 / 3.360 | 3.992 / 4.133 | 5.580 / 5.871 | 0.948× [0.844, 1.064] | 148.438 / 187.500 | 35 | 1,115,223 |
| tcp | sustained-delta-blocks-32k | cold | 4.521 / 4.459 | 10.628 / 9.343 | 19.065 / 18.755 | 1.189× [1.072, 1.332] | 0.000 / 31.250 | 28 | 203,895 |
| tcp | sustained-delta-blocks-32k | warm | 4.308 / 4.292 | 5.610 / 5.658 | 7.447 / 7.776 | 0.991× [0.947, 1.030] | 406.250 / 523.438 | 32 | 1,874,016 |
| tcp | sustained-delta-blocks-32k | latency | 3.439 / 3.376 | 4.109 / 4.268 | 5.604 / 6.134 | 0.962× [0.932, 0.994] | 218.750 / 187.500 | 34 | 2,057,411 |
| tcp | sustained-random-32k | cold | 4.644 / 4.476 | 10.241 / 10.600 | 18.114 / 19.621 | 0.972× [0.910, 1.030] | 15.625 / 15.625 | 0 | 18,560 |
| tcp | sustained-random-32k | warm | 4.245 / 4.214 | 5.427 / 5.323 | 7.720 / 7.649 | 1.020× [0.966, 1.087] | 515.625 / 468.750 | 0 | 18,560 |
| tcp | sustained-random-32k | latency | 3.356 / 3.372 | 4.073 / 4.058 | 5.012 / 5.852 | 0.967× [0.889, 1.038] | 101.562 / 109.375 | 0 | 18,560 |

## Complete connection byte cost

Uses the ending cumulative counter, including setup, every reported phase and any interphase traffic. Between-phase bytes are the gaps between setup/phase snapshots and are already included in the total.

| Transport | Workload | Pairs | Payload bytes | Total bytes off | Total bytes on | Bytes saved | Between-phase bytes off/on | Setup bytes off/on | Setup ms off/on |
|---|---|---:|---:|---:|---:|---:|---|---|---|
| tcp | sustained-delta-32k | 6 | 77,594,624 | 77,933,552 | 21,769,446 | 72.07% | 0 / 0 | 9,904 / 10,090 | 256.25 / 267.01 |
| tcp | sustained-delta-blocks-32k | 6 | 77,594,624 | 77,933,552 | 40,930,009 | 47.48% | 0 / 0 | 9,904 / 10,090 | 253.76 / 265.04 |
| tcp | sustained-random-32k | 6 | 77,594,624 | 77,933,552 | 77,933,738 | -0.00% | 0 / 0 | 9,904 / 10,090 | 254.58 / 263.85 |
