# Binary network compression benchmark

The latest [TCP optimization report](2026-10-15-tcp-direct-plans/README.md) measures cold starts, partially changing objects, sustained transfers, private per-client caches, adaptive traffic, and shared-client fanout. It includes complete dictionary-update traffic and confidence intervals. The [previous TCP and reliable-UDP report](2026-10-15-final/README.md) and earlier runs below remain historical results.

This benchmark compares SocketJack's custom `BinarySerializer.EnableCompression` enabled and disabled over real TCP and reliable-UDP sockets. It measures delivered throughput, application round-trip latency, and total socket data including dictionary registration. The formatter is SocketJack's custom SB format.

Raw results and analysis for the October 15, 2026 run are in [2026-10-15](2026-10-15/). The measured application DLL is the same for both modes; the runtime property is the only on/off change.

The adaptive optimization follow-up uses [2026-10-15-optimized](2026-10-15-optimized/). The original dated result files remain unchanged.

## Method

- Ten paired rounds alternate off/on and on/off ordering. Workload order rotates between rounds. Every trial starts fresh server and client processes, sockets, serializers, and repositories.
- Each payload is prepared deterministically outside the timers. The server checks every delivered byte against its own independently generated fixtures, rejects duplicates, and acknowledges each object. Throughput ends after all application receipts and sends complete, rather than after enqueueing.
- Both endpoints keep SafeMode and compatibility negotiation enabled. Application types are explicitly allowed for anonymous benchmark messages. Outer GZip/Deflate, the older pattern cache, P2P, chunked sending, FPS throttling, and bandwidth limits are disabled identically in both modes. Reliable UDP retains its default Adaptive profile, independent delivery, and 1,200-byte datagrams.
- A transparent counting proxy records successful forwards once per direction, including SafeMode handshake bytes, dictionary updates, dictionary acknowledgements, application receipts, framing, reliable-UDP controls, and retransmissions. Counts exclude OS TCP acknowledgements, IP/TCP/UDP headers, link-layer overhead, and the final harness shutdown notification. They are **TCP stream / UDP datagram bytes**, not a packet capture of NIC traffic.
- After each timed phase, reliable UDP uses a server queue-drain barrier and both transports wait for 160 ms of quiet counters. This captures trailing control traffic. These bytes and the small reliable-UDP barrier exchange are included in the phase totals; drain time is separately recorded and excluded from useful-payload throughput.
- Setup byte totals include the handshake and initial quiet drain; setup timing measures `Connect` only. Complete connection totals use the ending cumulative counter, including setup, all three phases, initial cache learning, and any traffic in the tiny gaps between phase snapshots.
- The main test uses two application endpoints on IPv4 loopback, with the proxy in the client process. A separate fanout experiment counts one active uploader and three idle clients across four proxies. Fanout timing is auxiliary because its endpoints share a process; its purpose is accounting for definitions sent to otherwise idle clients.

## Workloads and phases

| Workload | Payload | Pattern |
|---|---:|---|
| `repeated-1k` | 1 KiB | Same seeded random bytes; object sequence changes |
| `repeated-32k` | 32 KiB | Same seeded random bytes; object sequence changes |
| `delta-32k` | 32 KiB | Stable first 75%; fresh random final 25% on each object |
| `delta-blocks-32k` (TCP follow-up) | 32 KiB | Last 1 KiB of every 4 KiB block changes; 75% stable in distributed regions |
| `cold-first-32k` (TCP follow-up) | 32 KiB | Same delta pattern, with the first object measured separately before the remaining 63 cold objects |
| `sustained-delta-32k`, `sustained-delta-blocks-32k`, `sustained-random-32k` | 32 KiB | Same seeded patterns with 64 cold, 2,048 warm and 256 latency objects; six paired rounds |
| `random-32k` | 32 KiB | Independently seeded random bytes on every object |
| `runs-32k` | 32 KiB | Repeated `A` bytes with a changing random 64-byte prefix |
| `adaptive-32k` (optional) | 32 KiB | Fresh random → repeated random → random churn → return original → byte runs; one connection and cache |
| `spaced-32k` (optional) | 32 KiB | Cycle of 32 different random payloads; every payload returns after 31 unrelated objects |
| `marginal-32k` (optional) | 32 KiB | Fresh random bytes except a stable 512-byte high-entropy region around a scout position (1.6% stable) |

Each standard connection delivers 256 objects: 64 cold objects, 128 warm objects, and 64 latency objects. The two bulk phases use a window of eight objects; the latency phase uses stop-and-wait with the same workload payload and a small receipt. Cold results include first-use JIT and dictionary learning. Warm results reuse the cache established by the cold phase. The complete connection byte table still includes that cold cost. The p50/p95/p99 values are application RTTs, not one-way latency; bulk RTTs reflect an eight-object window. With only 64 latency samples per trial, p99 is close to the maximum and is descriptive. Workloads with different phase plans are identified above and in raw metadata; the latest report retains their results separately.

The report uses medians of ten runs and paired geometric mean ratios with deterministic 10,000-resample percentile 95% bootstrap intervals. An interval that crosses 1 does not establish a directional speed change. Client CPU figures include the proxy and do not represent combined CPU at both application endpoints. The desktop remained in normal use; builds and correctness tests were kept out of the timing series. Loopback measurements do not predict WAN or bandwidth-limited behavior.

The optional adaptive experiment uses six phases of 64 objects each: `random`, `repeat`, `churn`, `return`, `runs`, and `latency`. The first five use window eight; the final phase is stop-and-wait with byte runs. Returning the original data after 64 unrelated objects checks retained definitions, and the later runs check recovery from an incompressible stream. `spaced-32k` checks candidate reuse beyond a ten-object history: every high-entropy payload repeats only after 31 different payloads. It uses cold 64, warm 128, trained bulk 64, and latency 64: 320 objects total. The warm phase can still include substantial dictionary growth for the 32-object pool; the additional trained bulk phase permits a later throughput comparison. Every phase, including that learning cost, remains in the complete connection totals. `marginal-32k` uses the standard phases and checks that a small stable region does not continuously trigger expensive attempts on otherwise random data. These optional experiments run separately from the five unchanged main workloads. Their five paired rounds provide supplementary evidence, with less timing precision than the ten-pair main series.

The optimized harness also records approximate process-wide managed allocation during each timed phase, including the proxy, and cumulative candidate/encoding/bypass/probe counters after the drain. Counter reads happen outside the stopwatch. Allocated volume differs from retained cache memory; neither measures combined endpoint CPU or allocation.

## Reproduction

From the repository root, with .NET 8 and Python 3:

```powershell
dotnet build tools/BinaryCompression.Bench/BinaryCompression.Bench.csproj -c Release -p:BuildPackage=false
dotnet tools/BinaryCompression.Bench/bin/Release/net8.0/BinaryCompression.Bench.dll run docs/binary-compression-benchmark/2026-10-15/results.json 10
python tools/BinaryCompression.Bench/analyze.py docs/binary-compression-benchmark/2026-10-15/results.json docs/binary-compression-benchmark/2026-10-15/analysis

dotnet build tools/BinaryCompression.FanoutBench/BinaryCompression.FanoutBench.csproj -c Release -p:BuildPackage=false
dotnet tools/BinaryCompression.FanoutBench/bin/Release/net8.0/BinaryCompression.FanoutBench.dll docs/binary-compression-benchmark/2026-10-15/fanout.json 5
```

Keep builds and other benchmarks separate from the timing runs. The raw files preserve DLL hashes, runtime/OS details, complete per-trial measurements, and the byte-counting scope. `source-manifest.json` records the relevant source hashes. Pilot files are diagnostics and are excluded from the final analysis.

The optimized experiment retains the main workload and timing settings. Its additional commands are:

```powershell
dotnet tools/BinaryCompression.Bench/bin/Release/net8.0/BinaryCompression.Bench.dll run docs/binary-compression-benchmark/2026-10-15-optimized/results.json 10
python tools/BinaryCompression.Bench/analyze.py docs/binary-compression-benchmark/2026-10-15-optimized/results.json docs/binary-compression-benchmark/2026-10-15-optimized/analysis
dotnet tools/BinaryCompression.Bench/bin/Release/net8.0/BinaryCompression.Bench.dll run docs/binary-compression-benchmark/2026-10-15-optimized/adaptive.json 5 tcp,reliable-udp adaptive-32k,spaced-32k,marginal-32k
python tools/BinaryCompression.Bench/analyze.py docs/binary-compression-benchmark/2026-10-15-optimized/adaptive.json docs/binary-compression-benchmark/2026-10-15-optimized/adaptive-analysis
python tools/BinaryCompression.Bench/compare.py docs/binary-compression-benchmark/2026-10-15/analysis.json docs/binary-compression-benchmark/2026-10-15-optimized/analysis.json docs/binary-compression-benchmark/2026-10-15-optimized/comparison
dotnet tools/BinaryCompression.FanoutBench/bin/Release/net8.0/BinaryCompression.FanoutBench.dll docs/binary-compression-benchmark/2026-10-15-optimized/fanout.json 5
```
