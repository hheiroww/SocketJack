# UDP_Reliable v2: compact IDs, binary serialization, and speed results

Measured September 26, 2026 (America/Chicago); raw records use UTC.

**Speed acceptance: FAIL.** No tested UDP profile established both 10% more delivered throughput and 10% lower p95 application RTT against both TCP baselines, with the lower 95% confidence bounds also clearing 10%. These are loopback results, not a LAN/Internet superiority claim.

## Implementation changes

- Independent delivery is the default. `UdpReliable.Ordering = true` opts into sequential ordered callbacks. Four bounded workers decode independent objects; a slow callback does not block later complete objects. Each object is decoded only after all indexed segments arrive.
- Reliable wire v2 has numeric session IDs, variable-length object/packet IDs, and **no GUID fields**. Small DATA headers shrink from 40 to **14 bytes**. Packet indexes restart at zero for every object/stream chunk; the session/object tuple distinguishes stale packets. Message sequences do not wrap unsafely.
- Reusable I/O and requested socket buffers are capped at **32 KiB**. Default datagrams remain 1,200 bytes; explicit controlled-path configuration permits 32 KiB datagrams. Large objects retain bounded whole-object reassembly; 32 KiB is not a total memory limit.
- Stream frames are at most 32 KiB, use 32-bit direction-separated transfer counters and 64-bit byte offsets, and retain SHA-256 verification and explicit destination acceptance.
- `SocketJack.Serialization.BinarySerializer` implements the shared serializer interface for TCP and UDP. Byte arrays are sent directly, integers use compact encoding, and decoded wrappers still pass through type whitelisting. JSON remains the default for existing applications.
- Receive/send socket completion state is reused, socket continuations avoid an extra thread-pool hop, and endpoint lookup avoids per-packet endpoint-string creation.

## Two-process Release comparison

Every transport uses the **same new binary serializer**, payloads, eight-message application concurrency, receiver hash/accounting work, and disabled compression/cache settings. TCP retains `NoDelay=true`. Fifty measured transfers completed: ten alternating rounds across five modes, each with 256 MiB bulk data and 1,000 sequential 256-byte request/reply samples after warm-up. All application byte counts and SHA-256 hashes matched.

| Mode | Datagram ceiling | Mean delivered MiB/s | Mean per-run p95 RTT, ms | UDP retries |
|---|---:|---:|---:|---:|
| tcp | TCP stream | 137.40 | 0.6988 | not measured |
| raw-tcp | TCP stream | 105.98 | 0.5114 | not measured |
| adaptive | 1,200 B | 35.09 | 0.6216 | 13 |
| fast-lan | 1,200 B | 39.03 | 0.6245 | 0 |
| fast-lan-32k | 32 KiB | 77.72 | 0.7921 | 0 |

These are means of per-run statistics. Acceptance uses paired relative changes, not the ratio of these means. Negative changes mean slower. TCP kernel wire/retransmission counters were not instrumented and remain null.

| Comparison | Throughput improvement, 95% CI | p95 RTT reduction, 95% CI | Result |
|---|---:|---:|---|
| adaptive vs tcp | -74.4% [-76.0, -73.1] | 10.0% [2.3, 16.6] | FAIL |
| adaptive vs raw-tcp | -66.8% [-68.7, -64.8] | -23.0% [-33.0, -13.0] | FAIL |
| fast-lan vs tcp | -71.6% [-72.4, -70.8] | 9.5% [0.8, 18.0] | FAIL |
| fast-lan vs raw-tcp | -63.1% [-64.6, -61.7] | -23.9% [-35.2, -10.7] | FAIL |
| fast-lan-32k vs tcp | -43.4% [-45.2, -41.7] | -14.8% [-44.9, 10.9] | FAIL |
| fast-lan-32k vs raw-tcp | -26.5% [-30.4, -22.7] | -55.9% [-97.5, -22.3] | FAIL |

Throughput intervals are entirely below zero against both baselines for every UDP mode, so extending latency measurements cannot make this implementation pass the joint target under these conditions.

## Investigation

See the subsequent [measured bottleneck diagnosis](UDP_Reliable-diagnosis.md) for timing probes, exception counts, isolated experiments, and prioritized fixes.

A short diagnostic run initially showed frequent UDP retries after shrinking socket buffers to 32 KiB. The prior 512-packet flight window could overwhelm that buffer. Limiting flight to the local buffer capacity and adjusting ACK batching removed retries in the subsequent short diagnostic. A reserved small-message slot also preserves independent delivery when large-object packets are missing. The complete measurement series recorded 13 Adaptive retries and zero FastLan retries; clean paths can still experience local queue pressure or scheduling delays.

The compact serializer removes Base64 overhead for byte arrays and benefits TCP too. Even after parallel independent decoding, managed per-datagram scheduling/ACK work and the deliberately small socket window remain costs that the tuned TCP stream paths avoid. The 32 KiB datagram experiment reduces packet count substantially but leaves only one bulk packet in flight at this buffer size. This is an implementation-level explanation consistent with the trials, not a universal claim about UDP. Larger datagrams on physical networks may fragment; the default remains 1,200 bytes.

The [earlier v1 report](UDP_Reliable-v1-results.md) used JSON, larger socket buffers, and sequential decoding. Its results are historical context, not a controlled measurement isolating one of these changes.

## Correctness and builds

All **30 of 30 Release tests passed** in one complete run.

| Check | Result |
|---|---|
| Core .NET Standard 2.1 Release | PASS |
| WPF .NET 8 and .NET 10 Release | PASS, zero errors; four existing SSH.NET advisory warnings |
| Independent immediate decoding and explicit ordered callbacks | PASS |
| Binary reconstruction under 1%/5% loss, bursts, duplicates, reordering; 1,200-byte and 32-KiB datagrams | PASS |
| Ordered JSON messages under seeded impairments; replies and peer redirects with both serializers | PASS |
| Compression/cache and broadcasts with JSON and binary serializers | PASS |
| Backpressure, slow receivers, cancellation, no downgrade, malformed frames, compact integer boundaries | PASS |
| Packet-index reuse, duplicate suppression, stale packet replay after reconnect | PASS |
| File acceptance, overwrite protection, partial-file cleanup | PASS |
| Ordinary UDP with both serializers | PASS |
| File larger than 4 GiB with bounded buffering and SHA-256 | PASS |
| Both 10% speed improvements against both TCP baselines | **FAIL** |
| Two-machine confirmation and fair impaired-path comparison | **NOT VERIFIED** |

The generated file contained **4,295,032,833 bytes**. Sender, receiver, and both on-disk SHA-256 hashes matched:

`7771ca9460665383d6c6f5cf418cdec3f61e62173edb315fadc5016b17b647b2`

Transfer plus hash verification took 219.70 seconds. Peak sampled managed heap during transfer was **23,941,408 bytes (22.8 MiB)**. This is a managed-heap sample, not a total-process cap or a comparison against TCP. The WPF build overlapped early correctness testing; the speed benchmark had no concurrent builds or test runs.

The benchmark and correctness tests used byte-identical core library binaries. [Validation records](udp-reliable-results/compact-validation.json), [full test results](udp-reliable-results/compact-full.trx), [test log](udp-reliable-results/compact-full-tests.log), and [WPF build log](udp-reliable-results/compact-wpf-build.log) preserve the evidence.

## Environment, method, and limits

- Intel Xeon W-2140B, 8 cores / 16 logical processors; Windows 11 build 22631; .NET 8.0.31; Release builds.
- Separate client/server processes over IPv4 loopback. Ordinary desktop background activity was not disabled. No tests or builds ran concurrently with the measured benchmark series.
- Active Wi-Fi: Intel Wi-Fi 6E AX210, advertised link rate 2.4 Gbps when inspected. The benchmark did not traverse Wi-Fi; that link rate is not measured useful throughput.
- Each pair warms up with 8 MiB bulk and 100 RTT samples. Measured bulk uses seeded incompressible 256-KiB typed byte-array payloads with eight outstanding messages and application acknowledgment after every batch. Receiver time measures useful delivered bytes and hash/accounting work.
- Small-message timing ends at application echo receipt. Remote transport acceptance is awaited separately before the next sample. Compression/cache are disabled for the primary comparison; enabled configurations are correctness tests, not additional speed claims.
- SocketJack TCP may decode concurrently; raw TCP uses a minimal sequential length-prefixed reader. All modes serialize application hash/accounting work and use identical application concurrency.
- Raw records contain p50/p95/p99 RTT, process CPU, allocations, peak working set, hashes and available UDP counters. Receiver CPU/allocation measurements cover bulk; sender figures include bulk, hash verification and latency sampling, so those scopes are not interchangeable.
- Confidence intervals: 20,000 paired bootstrap samples, seed 7301, percentile 95% intervals on ten matched relative changes. Each speed criterion must clear 10% against each baseline.
- Two-machine confirmation and equal network-level impairment for TCP/UDP remain **unverified**. The seeded UDP impairment proxy validates correctness, not comparative impaired-network speed.

## Evidence and reproduction

- [Raw 50-run measurements](udp-reliable-results/compact-v2-256MiB-10runs.json)
- [Confidence intervals and pass/fail analysis](udp-reliable-results/compact-v2-256MiB-10runs-analysis.json)
- [API, options, wire format, and reproducible commands](UDP_Reliable.md)
- [Validation and binary hashes](udp-reliable-results/compact-validation.json)
