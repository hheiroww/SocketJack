# Binary compression: on versus off

Measured October 15, 2026 on IPv4 loopback. **This implementation reduced data substantially for repeated content, but reduced trained throughput in the tested 32 KiB cases. It did not demonstrate a universal speed or latency improvement.**

Ten paired alternating rounds covered TCP and reliable UDP with five workloads: 200 fresh two-process connections, 600 phase measurements, and 51,200 byte-verified payload objects. A further five paired fanout trials verified 1,000 objects and counted traffic to three idle clients.

Host: Intel Xeon W-2140B, 8 cores / 16 logical processors; Windows 10.0.22631; .NET 8.0.31; 15.68 GiB OS-visible physical RAM. The desktop remained in normal use. Builds and correctness tests were kept out of the primary timing series.

## Main comparison

Each cell is **off → on**. Speed is warm delivered useful-payload throughput with eight objects outstanding. Latency is warmed stop-and-wait application p95 RTT, sending the same workload payload and receiving a small application receipt. Total bytes cover the entire connection: handshake, cold learning, warm transfers, latency transfers, all dictionary updates/ACKs, framing, interphase traffic, application receipts, and reliable-UDP controls/retries. Counts exclude OS and network-layer headers.

| Transport | Payload pattern | Warm MiB/s off → on | p95 RTT ms off → on | Complete bytes off → on | Saved |
|---|---|---:|---:|---:|---:|
| TCP | Repeated 1 KiB | 0.82 → 0.76 | 5.19 → 6.05 | 307,248 → 119,164 | 61.2% |
| TCP | Repeated 32 KiB | 25.03 → 12.64 | 6.55 → 8.36 | 8,433,968 → 489,520 | 94.2% |
| TCP | 75% stable 32 KiB | 24.85 → 12.42 | 5.97 → 8.16 | 8,433,968 → 2,546,681 | 69.8% |
| TCP | Fresh random 32 KiB | 26.92 → 12.50 | 5.53 → 8.04 | 8,433,968 → 8,434,018 | ~0% |
| TCP | Repeated runs 32 KiB | 25.40 → 14.55 | 6.08 → 7.17 | 8,433,968 → 75,353 | 99.1% |
| Reliable UDP | Repeated 1 KiB | 0.71 → 0.67 | 5.62 → 7.34 | 316,832 → 129,916 | 59.0% |
| Reliable UDP | Repeated 32 KiB | 17.63 → 12.37 | 8.13 → 8.67 | 8,561,701 → 677,691 | 92.1% |
| Reliable UDP | 75% stable 32 KiB | 18.49 → 11.32 | 7.54 → 8.14 | 8,561,679 → 2,709,852 | 68.3% |
| Reliable UDP | Fresh random 32 KiB | 17.00 → 11.20 | 8.45 → 9.23 | 8,561,667 → 8,561,729 | ~0% |
| Reliable UDP | Repeated runs 32 KiB | 18.24 → 12.79 | 8.11 → 8.69 | 8,561,725 → 87,479 | 99.0% |

Values are medians of ten trials per mode. Every 32 KiB row delivered 8 MiB of useful payload per connection; the 1 KiB rows delivered 256 KiB. Complete byte totals include the cold/training phase even though the speed column describes the warm phase. Fresh random TCP traffic was exactly 50 bytes larger when enabled, from capability negotiation; there was no useful payload compression. Reliable UDP byte totals also vary with retransmissions and timing-dependent controls.

## Speed and latency confidence

Paired geometric mean warm throughput ratios (on/off) for the 32 KiB cases were below 1 with 95% bootstrap intervals entirely below 1. TCP repeated data: **0.496× [0.448, 0.540]**; reliable UDP repeated data: **0.711× [0.649, 0.772]**. Small 1 KiB throughput differences were inconclusive. Most reliable-UDP stop-and-wait p95 latency intervals crossed 1; these data do not establish a general UDP latency change. The full phase tables and intervals are in [analysis.md](analysis.md).

This is a clean loopback comparison with a counting proxy, not a WAN or link-bandwidth limit test. Less socket data is a bandwidth saving; it does not by itself establish faster delivered transfers. The cold phase also includes first-use JIT costs. See the [method and reproduction commands](../README.md).

## Dictionary fanout cost

Five alternating pairs used 100 repeated 32 KiB uploads from one active TCP client, plus three idle clients. All four connections were counted. These are byte-accounting measurements; their same-process timing is not part of the process-isolated speed comparison.

| Metric | Off | On |
|---|---:|---:|
| Complete bytes, including four handshakes | 3,331,148 | 393,244 |
| Bytes after setup, including cold dictionary learning | 3,291,372 | 353,268 |
| Server-to-client bytes after setup | 6,836 | 109,926 |
| Idle-client update/control bytes after setup | 0 | 105,498 |
| Definitions retained by each idle client | 0 | 98 |

Complete fanout traffic fell by **88.2%**, from 3,331,148 to 393,244 bytes. That enabled total already includes **105,498 bytes** of update/control traffic on the three idle connections. Definitions were not treated as free.

## Evidence and reproducibility

- [Raw main measurements](results.json), [full statistical tables](analysis.md), [machine-readable analysis](analysis.json), and run log.
- [Raw fanout measurements](fanout.json) and fanout log.
- Source hashes before timing and final verification. All 20 recorded core/harness source files remained unchanged; both modes and both benchmark programs used the same core DLL.
- Main core SHA-256: `8C68C58CD9409D180ADCD5ED48E1F7B5057B3722E2687B6209F054F4C30A4B1C`.
- Measured primary harness SHA-256: `8EC5ECB5A064E1D35B9A905E3E6B0C678A97B889DF3BAAF2D6D342E4E738FD52`.
- Pilot files are diagnostics and were excluded from the 600-measurement statistical analysis.
