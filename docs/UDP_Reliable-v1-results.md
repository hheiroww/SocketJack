# UDP_Reliable: validation and performance results

Measured September 26, 2026 (America/Chicago); raw records use UTC.

**The speed acceptance criteria failed. Do not describe this implementation as faster than TCP.** Neither Adaptive nor FastLan established both a 10% delivered-throughput gain and a 10% p95 latency reduction against both TCP baselines with 95% confidence.

## Two-process Release benchmark

| Transport/profile | Mean delivered MiB/s | Mean per-run p95 RTT, ms |
|---|---:|---:|
| tcp | 90.01 | 0.6684 |
| raw-tcp | 50.57 | 0.8941 |
| adaptive | 27.49 | 0.9687 |
| fast-lan | 26.53 | 0.7705 |

These are means of ten per-run statistics. The pass/fail calculation uses paired relative changes, not ratios of these means. Outlying runs affect these aggregations differently.

| Comparison | Throughput improvement, 95% CI | p95 latency reduction, 95% CI | Acceptance |
|---|---:|---:|---|
| adaptive vs tcp | -69.3% [-70.6, -68.0] | -47.3% [-102.7, -3.5] | FAIL |
| adaptive vs raw-tcp | -45.0% [-48.3, -41.4] | -39.4% [-107.0, 6.8] | FAIL |
| fast-lan vs tcp | -70.3% [-71.8, -68.7] | -17.4% [-36.2, 0.6] | FAIL |
| fast-lan vs raw-tcp | -46.8% [-50.2, -42.7] | -18.7% [-49.2, 12.9] | FAIL |

Negative improvement/reduction means slower. Every throughput confidence interval is below zero, so additional latency samples cannot make the joint acceptance criterion pass for this measured configuration.

All 40 measured transfers matched their expected application byte counts and SHA-256. There were no UDP data retransmissions during the final clean-loopback measured intervals; this does not establish reliability under loss. The separate impairment tests cover loss recovery. TCP kernel retransmission and wire counters were not instrumented and are recorded as `null`, not zero.

## Correctness and build acceptance

| Check | Result |
|---|---|
| Core .NET Standard 2.1 Release build | PASS |
| WPF Release builds, .NET 8 and .NET 10 | PASS |
| Seeded 0% / 1% / 5% random loss plus burst loss, duplication, and reordering | PASS |
| Ordered delivery, independent delivery, replies, peer redirects, broadcasts | PASS |
| Lost completion ACK, stale sessions, malformed/version-mismatched frames | PASS |
| Backpressure, slow callback, cancellation, disconnect/reconnect, listener cleanup | PASS |
| Compression/cache operation and queue-boundary cache promotion | PASS |
| File rejection/overwrite protection and canceled partial-file cleanup | PASS |
| Ordinary UDP regression | PASS |
| File larger than 4 GiB, byte count and both on-disk hashes | PASS |
| Both speed targets against both TCP baselines | **FAIL** |
| Two-machine LAN/WAN or shared impaired-network TCP comparison | **NOT VERIFIED** |

Nineteen distinct test scenarios passed across the complete 18-case release run and the subsequent 18-case focused run adding the cache-boundary regression. The full run included the large-file test; the focused run did not repeat that test. Both WPF target builds completed with zero errors and existing SSH.NET package advisory warnings.

The generated file contained **4,295,032,833 bytes**. Sender, receiver, and on-disk SHA-256 all matched:

`7771ca9460665383d6c6f5cf418cdec3f61e62173edb315fadc5016b17b647b2`

The final full large-file run reported 198.24 seconds for transfer and hash validation, and **23,250,208 bytes** of peak sampled managed heap (about 22.2 MiB). That is a sampled managed-heap measurement, not a total-process memory bound or a TCP comparison.

[Validation records and library hashes](udp-reliable-results/validation.json) preserve the test scopes and binary provenance. The benchmark-directory binary predates the final listener/shutdown cleanup and cache-budget guard; the primary performance workload disables caching and compression. Release tests cover those final fixes.

## Method and environment

- Intel Xeon W-2140B, 8 cores / 16 logical processors; Windows 11 build 22631; .NET 8.0.31; Release builds.
- Separate server and client processes over IPv4 loopback. Ordinary desktop background activity was not disabled; results are machine-specific and latency shows variability.
- Ten complete rounds, rotating protocol order each round. Each process pair warms up with an 8-MiB transfer and 100 message round trips.
- Each measured round transfers 256 MiB in 256-KiB typed payloads, with eight outstanding messages and application receipt after each batch. Payload content is seeded incompressible data; serialization/compression work is matched across transports. Compression and pattern caching are disabled.
- Each latency measurement consists of 1,000 sequential 256-byte typed request/reply payloads. RTT ends at receipt of the application response; remote transport acknowledgment completion is awaited separately before the next sample.
- Both application receive handlers serialize their accounting/hash work. SocketJack TCP may decode messages concurrently internally; the minimal TCP baseline decodes sequentially. Each transport has the same eight-message application pipeline.
- SocketJack TCP has `NoDelay=true`, chunking disabled, FPS pacing disabled, and bandwidth caps disabled. The minimal TCP baseline uses length-prefixed SocketJack-serialized messages, asynchronous socket streams, and `NoDelay=true`.
- Both UDP profiles use the default 1,200-byte datagram ceiling and 512-packet flight limit. Throughput is measured at the receiver with application byte-count and SHA-256 verification, not from sender enqueue speed.
- Raw records include per-run p50/p95/p99 RTT, allocations, CPU time, peak process working set, and available UDP counters. Receiver CPU/allocation figures cover bulk transfer; sender figures cover bulk transfer, sender hash verification, and latency sampling. They should not be compared as identical timing scopes.
- Confidence intervals use 20,000 seeded paired bootstrap resamples of the ten per-run relative improvements. Require the lower 95% confidence bound to clear +10% for both metrics, for a named profile against both baselines.

## Investigation and limitations

The investigation corrected reservation scheduling that produced unnecessary retries for ordered messages, added an inline path for single-datagram messages, and replaced per-datagram Task allocations with reusable socket completion state. Those changes improved the implementation but did not meet the speed targets.

The remaining design sends and processes hundreds of thousands of individual UDP datagrams for this encoded bulk workload, with acknowledgment and reassembly work in managed code and sequential per-peer decoding. TCP benefits from larger application writes and operating-system stream handling. This is an implementation-level explanation consistent with the measurements, not proof that every reliable UDP design is slower than TCP.

No second-machine LAN/WAN benchmark or fair shared network-level impairment comparison against TCP was performed. No Internet performance or superiority claim is established. The seeded UDP proxy is a correctness harness, not a cross-protocol performance baseline.

## Evidence and reproduction

- [Raw 40-run measurements](udp-reliable-results/loopback-256MiB-10runs.json)
- [Bootstrap analysis](udp-reliable-results/loopback-256MiB-10runs-analysis.json)
- [API, protocol, options, and reproduction commands](UDP_Reliable.md)
- Benchmark harness: `tools/UdpReliable.Bench`; correctness tests: `SocketJack.UdpReliable.Tests`.

The implementation remains opt-in. Existing applications continue using ordinary UDP unless they explicitly select `UDP_Reliable`.
