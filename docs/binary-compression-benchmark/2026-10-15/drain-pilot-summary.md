# Binary compression transfer benchmark

Source: `docs\binary-compression-benchmark\2026-10-15\drain-pilot.json`. Per-mode figures are medians of complete runs. On/off comparisons pair the same round. Compression is the only setting changed.

Socket bytes count both directions once, including dictionary updates, dictionary acknowledgements, application acknowledgements, and SocketJack framing. They exclude IP/TCP/UDP/Ethernet headers. Setup is reported separately and is included once in the lifetime totals.

Bulk throughput is useful application payload divided by the time from the first send through the last validated application acknowledgement. Bulk RTT uses eight outstanding messages; latency RTT uses one outstanding message after warm training. Byte counters drain after timing stops; settle time is excluded from throughput. Warm results use the dictionary learned during the reported cold phase.

Throughput ratios use the geometric mean of paired on/off ratios and 10,000 paired bootstrap resamples (seed 7301; percentile 95% interval). An interval spanning 1 does not establish a speed change. Round-tail medians are not a pooled per-message percentile. With 64 latency samples, p99 is effectively the maximum sample and is descriptive. Fresh processes make the cold phase include first-use JIT costs; warm phases reuse the process and cache. CPU figures cover the client process and its counting proxy. Setup timing measures Connect; setup byte totals also include its quiet-drain traffic. Loopback results do not establish WAN performance.

| Transport | Workload | Phase | Pairs | MiB/s off | MiB/s on | On/off speed [95% CI] | Total bytes off | Total bytes on | Bytes saved |
|---|---|---|---:|---:|---:|---|---:|---:|---:|
| reliable-udp | repeated-32k | cold | 1 | 12.50 | 7.72 | 0.618× | 2,136,678 | 312,597 | 85.37% |
| reliable-udp | repeated-32k | warm | 1 | 12.93 | 6.86 | 0.531× | 4,275,717 | 201,104 | 95.30% |
| reliable-udp | repeated-32k | latency | 1 | 3.03 | 4.44 | 1.463× | 2,138,981 | 100,777 | 95.29% |

## Application round-trip latency

| Transport | Workload | Phase | p50 ms off/on | p95 ms off/on | p99 ms off/on | p95 speedup off/on [95% CI] | Client CPU ms off/on | Definitions on | Cache bytes on |
|---|---|---|---|---|---|---|---|---:|---:|
| reliable-udp | repeated-32k | cold | 10.421 / 10.097 | 19.800 / 26.669 | 27.602 / 36.354 | 0.742× | 109.375 / 78.125 | 98 | 187,865 |
| reliable-udp | repeated-32k | warm | 10.520 / 7.584 | 20.538 / 90.755 | 22.220 / 98.067 | 0.226× | 296.875 / 171.875 | 98 | 187,874 |
| reliable-udp | repeated-32k | latency | 9.845 / 6.697 | 15.640 / 9.616 | 21.268 / 13.091 | 1.626× | 125.000 / 78.125 | 98 | 187,874 |

## Complete connection byte cost

Includes setup once plus cold, warm and latency phases; all prior cold/training traffic remains included.

| Transport | Workload | Pairs | Payload bytes | Total bytes off | Total bytes on | Bytes saved | Setup bytes off/on | Setup ms off/on |
|---|---|---:|---:|---:|---:|---:|---|---|
| reliable-udp | repeated-32k | 1 | 8,388,608 | 8,561,845 | 624,997 | 92.70% | 10,469 / 10,519 | 577.33 / 444.54 |
