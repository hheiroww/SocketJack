# Why UDP_Reliable is slow

Diagnosed September 26, 2026, using the production v2 core binary, isolated experimental builds, transport timing counters, and a sampled .NET server trace. All measurements here are two-process IPv4 loopback diagnostics. They do not replace the [ten-round TCP acceptance results](UDP_Reliable-results.md).

The main problem is a restricted transfer pipeline combined with substantial per-datagram scheduling and acknowledgment work. `Ordering = false` removes waiting for earlier completed objects; it does not remove packet-window limits, reservation handshakes, ACK waiting, or application callback work.

## 1. The 32 KiB setting was applied to two different capacities

[UdpReliableTransport.cs](../SocketJack/Net/UdpReliableTransport.cs), lines 52–53 and 207, sets both OS socket queues to `BufferSize`, then calculates:

```csharp
FlightLimit = Math.Min(MaximumFlightPackets, Math.Max(1, BufferSize / Mtu / 2));
```

With a 32,768-byte buffer, this allows only **13 bulk packets at 1,200-byte datagrams**, and **one bulk packet at 32-KiB datagrams**. The configured `MaximumFlightPackets = 512` is therefore never reached. At 32 KiB, bulk transfer behaves as stop-and-wait: send a fragment, wait for its ACK, then send the next fragment. Having eight objects queued does not expand this shared peer window.

I applied the requested maximum individual buffer size to the entire OS receive queue as well. Those are different capacities. The implementation needs separate controls for individual I/O buffers, aggregate queued receive capacity, and receiver-advertised credits. A bounded set of buffers can each remain at or below 32 KiB.

An experiment allowing two large packets without expanding receive capacity made performance worse and introduced retries. Merely increasing the flight limit is insufficient.

## 2. ACK batching is being undone by the scheduler

The receive path intends to acknowledge after six packets at the default flight limit. But `Pump()` flushes **every pending ACK on every scheduler pass**, without a batching deadline (`UdpReliableTransport.cs`, lines 321 and 409).

The instrumented default-datagram run observed:

- **237,600 DATA packets** received, including warm-up.
- **93,342 ACK packets** sent: one ACK per **2.55 DATA packets**, rather than approximately six on this clean path.
- 122,311 receiver pump calls; 70,060 send batches contained just one frame.
- Sender socket-send awaits accumulated about **4.88 seconds**, and sender scheduler waits accumulated **4.67 seconds** across the whole workload.

These durations include asynchronous waits and scheduling delays; they are not CPU-time measurements and must not be added across concurrent loops. They nevertheless show that shrinking headers does not eliminate the much larger cost of handling hundreds of thousands of separate datagrams and ACKs.

## 3. Ordinary wake-ups throw exceptions repeatedly

`Wake()` calls `SemaphoreSlim.Release()` on a semaphore whose maximum count is one, then catches `SemaphoreFullException` if a notification is already pending (`UdpReliableTransport.cs`, line 73).

First-chance exception counters recorded **80,436 client + 60,018 server = 140,454 SemaphoreFullExceptions** for one default FastLan workload. This includes startup, warm-up, bulk, latency samples, and shutdown. These are caught implementation exceptions, not lost packets.

An isolated variant checks for an already-pending notification before releasing, retaining the catch for the remaining race. In its instrumented default FastLan run, the count fell to **zero** on both endpoints. Clean diagnostic comparisons showed a modest throughput improvement; this alone did not resolve the larger pipeline limitation.

## 4. Large objects and stream chunks add acknowledgment barriers

Every fragmented object requires an `Open → Grant` exchange before data transmission (`UdpReliableTransport.cs`, line 429). The timing probe counted **1,056 grants** for the 8-MiB warm-up plus 256-MiB measured bulk, with approximately **0.437 ms average grant delay** in the 32-KiB run. Up to eight object reservations can overlap, so these delays cannot simply be summed as elapsed transfer time.

The stream sender has a stricter barrier: it reads a chunk, awaits that chunk's transport acceptance, and only then reads/sends the next ([UdpReliableTransfers.cs](../SocketJack/Net/UdpReliableTransfers.cs), lines 83–89). It does not maintain a multi-chunk pipeline. Its maximum stream frame is 32,768 bytes while the transport's fragment payload ceiling is 32,736 bytes at the largest datagram size. Consequently, a full stream chunk still takes **two fragments plus Open/Grant**, with a small trailing fragment. The object benchmark does not measure this additional file/stream-specific limitation.

## 5. Loss can produce conspicuous pauses

The retransmission timeout has a **30 ms minimum**, with exponential backoff (`UdpReliableTransport.cs`, line 411). Fast recovery needs three later packet indexes (`line 334`). A one-packet window cannot produce those later ACKs, so a lost large datagram generally waits for the retransmission timer. This explains a potential pause under loss; it is not the explanation for clean runs with zero retries.

The idle scheduler also has a 2 ms timeout, but the timing probe argues against blaming that timer alone. At 32 KiB the sender recorded **14,482 signaled wake-ups and 181 timeout wake-ups**. Most waits ended on a signal. It accumulated **5.10 seconds waiting**, compared with **0.387 seconds serializing** across the instrumented workload.

## Other application-specific contributors

- **Peer redirects are still forced into the ordered lane.** The namespace test in [UdpReliableIntegration.cs](../SocketJack/Net/UdpReliableIntegration.cs), line 33, treats every `SocketJack.Net.P2P` object as an ordered control message. That includes `PeerRedirect` data envelopes, even when `Ordering = false`. A missing redirected object can therefore hold up subsequent redirected objects. This is a source-confirmed behavior; the benchmark disables peer-to-peer and does not quantify its impact.
- **WPF callbacks synchronously enter the UI thread.** [NetworkBase.cs](../SocketJack/Net/NetworkBase.cs), line 103, uses `Dispatcher.Invoke`. A busy UI can block delivery workers despite independent transport delivery. This does not explain the console benchmark, which uses the .NET Standard library.
- **Type resolution repeats work.** [Wrapper.cs](../SocketJack/Serialization/Wrapper.cs), lines 35 and 69–101, gives each new wrapper its own type cache and enumerates assembly types on a miss. The existing static resolved-type cache is not used by this main lookup path. A sampled server trace captured about **648 ms of inclusive delivery-stack time** under type resolution, versus **128 ms under binary serializer frames**. These are sampled thread-stack durations, not CPU percentages. Much of the remaining delivery-stack time was inside the benchmark's shared hash/accounting callback or its lock. This wrapper overhead is shared with TCP, so it does not independently explain the transport gap.

## Controlled diagnostic comparisons

Three alternating rounds per configuration, 256 MiB each, identical serializer and receiver work, diagnostics counters disabled during these comparisons. All 18 transfers passed the existing byte-count and SHA-256 checks. Values are arithmetic means of per-run delivered throughput.

| Configuration | Default 1,200-byte datagrams | 32-KiB datagrams | Retries at 32 KiB |
|---|---:|---:|---:|
| Production baseline | 34.60 MiB/s | 71.98 MiB/s | 0 |
| Coalesce redundant wake-ups | 39.66 MiB/s | 79.18 MiB/s | 0 |
| Coalesce wake-ups and allow at least two packets in flight | 37.11 MiB/s | 64.40 MiB/s | 15 |

Three diagnostic rounds do not establish either TCP acceptance target or general latency improvements. In particular, default-datagram p95 latency did not improve consistently with the wake-up change. The effects need the full acceptance workload after production fixes.

An early experimental build reused a cached binary under a different variant label. Binary hashes caught this; that attempt is excluded from the final window comparison. The final variants have separate intermediate/output directories and distinct hashes. One UDP bind failed before measurement; allocating a UDP port instead of a TCP port resolved that test-harness problem, and only missing rows were resumed.

## Fix priority

1. Separate the 32-KiB per-buffer limit from socket queue capacity and receiver credits; keep individual buffers bounded and implement an actual pipeline that respects available receiver capacity.
2. Coalesce wake notifications and implement a real ACK batching deadline, with immediate ACKs for the loss/recovery cases that need them.
3. Pipeline bounded stream chunks and make a maximum-size chunk fit its intended datagram payload. Preserve acceptance, byte counts, SHA-256, cancellation, and memory accounting.
4. Distinguish ordered identity/control messages from independent peer-redirect payloads. Make UI dispatch behavior explicit for WPF callers.
5. Cache type/schema reflection across messages, then profile again before considering another formatter change.

The production transport source was not changed during this diagnosis. The normal Release build was restored and its core SHA-256 remains `b3f428253e90635ae2cdd5f57b1273d16cb9c02f16f77e4ee4df6f1dc4fff24d`, matching the earlier validated library. Only opt-in benchmark diagnostics and investigation tools were added.

## Evidence and reproduction

- [Summary, source hash, and production binary hash](udp-reliable-results/diagnosis/summary.json)
- [Final isolated-build comparisons](udp-reliable-results/diagnosis/ab-diagnostics.json)
- [Timing-probe measurements](udp-reliable-results/diagnosis/timed-diagnostics.json) and per-process [default sender counters](udp-reliable-results/diagnosis/timings/52320.json), [default receiver counters](udp-reliable-results/diagnosis/timings/87232.json), [32-KiB sender counters](udp-reliable-results/diagnosis/timings/82976.json)
- [Baseline client exceptions](udp-reliable-results/diagnosis/exceptions/fast-lan-client-85148.json), [baseline server exceptions](udp-reliable-results/diagnosis/exceptions/fast-lan-server-14640.json), and [coalesced client exceptions](udp-reliable-results/diagnosis/exceptions-wake-coalesced/fast-lan-client-24548.json)
- [Sampled stack analysis and limitations](udp-reliable-results/diagnosis/sampled-stack-analysis.json). The full `.nettrace` and Speedscope files remain in `artifacts/udp-reliable/diagnosis/`.

From the repository root:

```powershell
pwsh -File tools/UdpReliable.Bench/build-diagnostics.ps1
python tools/UdpReliable.Bench/diagnose.py --clean-only
python tools/UdpReliable.Bench/diagnose.py --timed

# First-chance exception counting is optional and changes timing; do not enable it for acceptance.
$env:SJ_DIAGNOSTICS = "$PWD/artifacts/udp-reliable/diagnosis/exceptions"
dotnet tools/UdpReliable.Bench/bin/Release/net8.0/UdpReliable.Bench.dll run artifacts/udp-reliable/diagnosis/profiled-baseline.json 1 256
Remove-Item Env:SJ_DIAGNOSTICS

# Optional sampled stack trace; the tool is installed locally in artifacts.
dotnet tool install dotnet-trace --tool-path artifacts/udp-reliable/tools
python tools/UdpReliable.Bench/trace-server.py
```

The snapshot build script checks the production transport source hash before compiling experimental sources. Regenerate/review those sources if the transport has changed. No two-machine or equal network-level TCP/UDP impairment comparison was performed here.
