# UDP_Reliable and binary serializer optimization — September 30, 2026

This report measures the optimized custom `SB` v1 serializer and reliable UDP wire v3 on Windows IPv4 loopback. The acceptance requirement remains at least 10% more delivered bulk throughput **and** at least 10% less p95 application request/reply latency than **both** SocketJack TCP and a minimal tuned TCP implementation. The lower bounds of the 95% confidence intervals must also clear 10%.

**Speed acceptance: FAIL for every tested UDP profile with both serializers.** No configuration meets both targets against both TCP baselines. Loopback measurements alone also cannot establish LAN/Internet superiority.

## Delivered network performance

Means of ten runs; latency is the mean of each run's p95 application RTT. Each run delivers and hashes 256 MiB before the separate 256-byte echo phase.

| Serializer | Mode | Datagram | Useful MiB/s | p95 RTT (ms) | UDP retries (sum) | Speed acceptance |
|---|---|---:|---:|---:|---:|---|
| binary | tcp | - | 122.44 | 5.001 | - | Baseline |
| binary | raw-tcp | - | 72.34 | 4.384 | - | Baseline |
| binary | adaptive | 1200 | 51.60 | 4.674 | 0 | **FAIL** |
| binary | fast-lan | 1200 | 49.99 | 4.777 | 0 | **FAIL** |
| binary | fast-lan-32k | 32768 | 124.04 | 4.898 | 0 | **FAIL** |
| json | tcp | - | 84.95 | 4.571 | - | Baseline |
| json | raw-tcp | - | 47.22 | 4.269 | - | Baseline |
| json | adaptive | 1200 | 38.83 | 4.536 | 11 | **FAIL** |
| json | fast-lan | 1200 | 38.26 | 4.556 | 4 | **FAIL** |
| json | fast-lan-32k | 32768 | 83.92 | 4.432 | 0 | **FAIL** |

Binary FastLan/32 KiB reached **124.04 MiB/s**, compared with **122.44 MiB/s** for SocketJack TCP. Its mean paired throughput gain was only **1.47% (95% CI -0.86% to +3.89%)**, and its p95 reduction was **1.65% (-7.44% to +8.47%)**. Against minimal TCP it won throughput but lost latency. The throughput interval already excludes the required +10%; more samples would not resolve the failure as mere uncertainty.

All 100 measured transfers verified useful byte count and SHA-256. Binary UDP had zero retries across its 30 runs; JSON Adaptive had 11, JSON FastLan/1200 had 4, and JSON FastLan/32 KiB had zero. Binary and JSON network series ran sequentially, so their cross-serializer averages are descriptive rather than a paired causal estimate.

### Confidence intervals and pass/fail

Positive percentages mean improvement. Every cell is the mean paired relative improvement followed by its 95% interval. Passing requires both interval lower bounds to be at least +10%.

| Serializer | UDP profile | TCP baseline | Throughput improvement | p95 reduction | Result |
|---|---|---|---:|---:|---|
| binary | adaptive | tcp | -57.8% [-58.9, -56.1] | +6.0% [-0.6, +12.1] | FAIL |
| binary | adaptive | raw-tcp | -28.7% [-30.1, -27.3] | -7.0% [-13.5, -1.5] | FAIL |
| binary | fast-lan | tcp | -59.1% [-60.2, -57.8] | +4.2% [-0.6, +9.2] | FAIL |
| binary | fast-lan | raw-tcp | -30.9% [-32.1, -29.5] | -9.4% [-15.4, -2.7] | FAIL |
| binary | fast-lan-32k | tcp | +1.5% [-0.9, +3.9] | +1.7% [-7.4, +8.5] | FAIL |
| binary | fast-lan-32k | raw-tcp | +71.5% [+68.1, +74.5] | -12.6% [-24.5, -2.4] | FAIL |
| json | adaptive | tcp | -54.2% [-55.8, -52.8] | -0.2% [-5.6, +6.6] | FAIL |
| json | adaptive | raw-tcp | -17.8% [-19.2, -16.3] | -6.3% [-9.0, -3.7] | FAIL |
| json | fast-lan | tcp | -54.8% [-56.8, -52.9] | -0.9% [-7.5, +7.0] | FAIL |
| json | fast-lan | raw-tcp | -18.9% [-20.7, -16.8] | -6.9% [-10.8, -3.0] | FAIL |
| json | fast-lan-32k | tcp | -0.9% [-5.1, +3.4] | +2.1% [-2.1, +8.1] | FAIL |
| json | fast-lan-32k | raw-tcp | +77.9% [+73.3, +83.3] | -4.1% [-7.0, -0.9] | FAIL |

## Formatter comparison

Means across ten rotating runs. These payload sizes refer to the byte-array property inside the typed message; encoded sizes include the whole SocketJack wrapper. Time units are microseconds per operation.

| Payload | Codec | Encoded bytes | Encode (us) | Raw decode (us) | Decode + unwrap (us) | Encode allocated bytes |
|---:|---|---:|---:|---:|---:|---:|
| 256 | binary | 398 | 0.915 | 1.148 | 1290.0 | 592 |
| 256 | binary-before | 398 | 4.142 | 2.347 | 1288.5 | 2673 |
| 256 | json | 510 | 2.441 | 4.701 | 1337.2 | 1560 |
| 32768 | binary | 32912 | 7.513 | 5.911 | 1236.9 | 33228 |
| 32768 | binary-before | 32912 | 19.232 | 6.373 | 1252.0 | 133226 |
| 32768 | json | 43860 | 43.325 | 37.895 | 1357.1 | 133837 |
| 262144 | binary | 262288 | 167.101 | 127.559 | 1435.1 | 262653 |
| 262144 | binary-before | 262288 | 165.819 | 44.775 | 1425.0 | 1051641 |
| 262144 | json | 349784 | 287.596 | 542.179 | 3001.1 | 1072074 |

For 256-byte payloads, binary encoding is **4.53x** the original codec (95% CI **3.88-4.86x**) and **2.67x** SocketJack JSON (**1.97-3.60x**). Raw decoding is **2.04x** the original and **4.10x** JSON. For 32 KiB, encoding is **2.56x** the original (**2.09-3.13x**) and **5.77x** JSON (**4.86-7.00x**). Encoding allocations fall about **78%** for small messages and **75%** for 32 KiB/256 KiB messages versus the original codec.

Binary encoded sizes are **21.96% smaller** than JSON for 256-byte payloads and approximately **25% smaller** for the large payloads. The optimization itself preserves the original binary encoding size; the bandwidth advantage comes from binary raw arrays versus JSON base64.

Full decode plus unwrap remains around **1.2-1.4 ms** for the small/32 KiB cases because it includes whitelist/graph/assembly verification and object construction. Faster raw codec operations do not imply the same speedup in delivery callbacks.

### Investigating the large-payload regression

The primary 256 KiB microbenchmark did **not** demonstrate faster encoding versus the original (0.99x, 95% CI 0.86-1.23x), and raw decoding measured 127.56 us versus 44.77 us. These unfavorable measurements are retained above and in the raw data.

A separate diagnostic used the same core DLL, 200 warmups, full garbage collection before each phase, and **2,000 operations per phase** over ten rotating rounds. In-phase collection time remained included. This separates raw codec work from the heap state left by the short preceding encode/unwrap phases. It found:

| 256 KiB codec | Encode (us) | Decode (us) | Gen2 collections per 2,000 encodes | Gen2 collections per 2,000 decodes |
|---|---:|---:|---:|---:|
| binary-before | 285.91 | 72.14 | 645.5 | 161.1 |
| binary | 72.14 | 73.10 | 161.3 | 161.1 |
| json | 284.78 | 451.10 | 468.1 | 282.3 |

- Diagnostic encode speedup against binary-before: **3.96x**, 95% CI **3.64-4.22x**.
- Diagnostic encode speedup against json: **3.95x**, 95% CI **3.46-4.54x**.
- Diagnostic decode speedup against binary-before: **0.99x**, 95% CI **0.91-1.04x**.
- Diagnostic decode speedup against json: **6.17x**, 95% CI **5.62-6.95x**.

The short-run large-object result is sensitive to allocation/collection state. The longer diagnostic supports reduced encoding work and allocations, while large raw decoding remains approximately unchanged from the original. It does **not** justify a blanket decoder speedup claim or replace the primary network acceptance results. Both diagnostic and primary measurements are preserved.

## Datagram byte overhead

Mean bulk-only sender datagram bytes plus received control/reply datagram bytes, divided by useful payload. These counters exclude UDP/IP/link headers and do not establish an on-wire TCP comparison.

| Serializer | UDP mode | Bulk sent bytes | Bulk received bytes | Over useful payload |
|---|---|---:|---:|---:|
| binary | adaptive | 272156920 | 410498 | 1.54% |
| binary | fast-lan | 272156920 | 410418 | 1.54% |
| binary | fast-lan-32k | 268739736 | 249625 | 0.21% |
| json | adaptive | 362979895 | 527707 | 35.42% |
| json | fast-lan | 362979065 | 532465 | 35.42% |
| json | fast-lan-32k | 358363328 | 302313 | 33.61% |

## What changed and why

- Replaced the binary formatter's `MemoryStream`/`BinaryWriter`/`BinaryReader` hot path with span encoding/decoding. Small encodes start in 512 stack bytes. Larger encodes use pooled temporary blocks of at most 32 KiB; raw byte arrays are copied once into the exact-size result. Local property getters and UTF-8 names are cached. Strings decode directly from spans. The custom `SB` v1 bytes remain compatible; this is not `BinaryFormatter`.
- Separated the **32 KiB individual I/O buffer cap** from aggregate kernel queue capacity. The previous `BufferSize / MTU / 2` calculation allowed only one packet in flight at 32 KiB. `SocketQueueBytes` now requests a bounded 1 MiB aggregate socket queue; both endpoints negotiate flight capacity. The default ceilings become 436 packets at 1,200 bytes or 16 at 32 KiB. The OS queue holds multiple datagrams; object/reassembly budgets remain separate.
- Added flight credits to the endpoint-bound handshake. Reliable **wire v3 is required at both endpoints**, without fallback to older reliable versions or plain UDP. Compact DATA headers and per-object packet-index reuse are unchanged; no GUID is introduced.
- Coalesced scheduler wakeups without throwing/catching `SemaphoreFullException`, applied a real ACK deadline, removed per-ACK bitmap allocation, reused active-message storage, and reduced per-packet maintenance work. Adaptive pacing and loss recovery remain enabled by default. FastLan remains an explicit controlled-network profile.
- Sized stream frames to whole transport payloads. At a 32 KiB MTU, each bounded chunk fits one INLINE frame and avoids a second fragment and Open/Grant exchange. Stream chunks still wait for transport acceptance; cross-chunk pipelining and resume are not implemented.
- Corrected synchronous reply admission: an encoder already in use no longer causes a reply to fail despite available queue space. Pending synchronous references are count-bounded; encoded payloads retain byte/count backpressure, and exhaustion still raises `OnError`.
- Reused the type already resolved by the shared inbound decoder, avoiding one repeated lookup while preserving `Wrapper` graph, authorization and assembly checks. Allowed the inert nested `PeerAction` enum used in initial identity messages. Neither change permits unauthenticated redirects or caches authorization results.

The profile also showed material cost in assembly fingerprint verification, type resolution and callback dispatch. Raw decoding time and full `Deserialize(...).Unwrap(...)` time are therefore reported separately. Full object delivery includes work outside this codec; a faster encoder alone cannot remove those costs. The sampled trace was diagnostic and preceded the final shared-decoder change, so its timings are not a final performance comparison.

## Correctness and build results

| Check | Result |
|---|---|
| Reliable UDP Release suite on the measured core DLL | **35 passed, 2 failed, 37 total** |
| Binary v1 byte compatibility with saved original codec | Passed; boundary integers/floats, Unicode across 32 KiB boundaries, arrays, JSON fallback, large raw bytes |
| Concurrent serializer use and malformed input rejection | Passed |
| Actual UDP sockets through seeded impairment proxy | Passed; 1%/5% random loss, burst loss, duplication, reordering and delay; binary/JSON, ordered/independent delivery |
| Backpressure, cancellation, stalled sessions, reconnect, stale/invalid frames | Passed |
| Direct `ISocket` replies, broadcasts, compression, ordered caching, ordinary UDP regression | Passed |
| Stream acceptance, SHA-256, cancellation, no implicit file overwrite | Passed |
| File larger than 4 GiB | Passed: **4,295,032,833 bytes**, sender/receiver/disk SHA-256 match |
| .NET Standard 2.1 Release build | Passed |
| WPF linked-source Release builds | Passed for `net8.0-windows7.0` and `net10.0-windows7.0`; one existing unused-field warning |
| Separate current SafeMode/security suite | 36 passed, 2 failed, 38 total; see below |
| Two-machine network comparison | **Unverified** |
| Fair TCP-versus-UDP performance with network-level impairment | **Unverified** |

The two reliable-suite failures are the binary and JSON variants of `PeerRedirectAndInterfaceReplyRemainReliable`. The workspace's current receive policy requires authenticated TLS for peer redirects, which plaintext reliable UDP cannot provide. The older tests expect unauthenticated routing. Direct replies were separately verified with both serializers. These failures are retained and reported; the suite is **not** called fully passing, and the routing policy was not weakened to make it pass.

The separate security suite's remaining failures are `ArbitraryHttpBodyCannotInvokeCallbackBeforeRequestGate` (connection close instead of the expected HTTP response) and `ReflectedAdminEndpointCannotExecuteClrMethod` (the reflection fixture expects a removed endpoint). They concern concurrent HTTP/admin security changes, not UDP packet delivery. The binary dictionary-fallback case now passes.

The large-file test measured 176.16 seconds for transfer plus disk verification, with peak sampled managed heap **21,559,224 bytes (20.6 MiB)**. Whole-test duration was 180.10 seconds including generation. This sampled managed heap is not total process/kernel memory. The SHA-256 was:

```text
7771ca9460665383d6c6f5cf418cdec3f61e62173edb315fadc5016b17b647b2
```

## Measurement method and limits

- Machine: `DESKTOP-KSSU21A`, Xeon W-2140B at 3.20 GHz, 8 cores/16 logical processors, approximately 16 GiB RAM, Windows 11 Pro 10.0.22631, .NET 8.0.31. Tests use two separate processes on IPv4 loopback. Existing desktop applications remained running. No builds or correctness tests ran alongside the final timing series.
- The active Intel AX210 Wi-Fi interface advertised 1.2 Gbps. That is **not measured network throughput**, and this loopback test did not use Wi-Fi.
- Each serializer/transport combination has ten measured runs. Transport order rotates each round. Each fresh client/server pair warms up with 8 MiB and 100 echoes before measuring 256 MiB bulk and 1,000 sequential 256-byte typed-message echoes.
- Bulk input is 1,024 distinct seeded-random 256 KiB payloads, prepared before warm-up/timing. Eight application messages are outstanding for every transport. The receiver verifies unique indexes, useful byte count and ordered SHA-256 using at most eight pending payloads. Its elapsed time determines useful throughput. The sender verifies the returned digest. Every RTT sample verifies its echoed sequence and bytes.
- Compression and pattern caching are disabled for the primary measurements. Reliable delivery uses `Ordering=false` and the default four callback workers. Compression/caching have correctness coverage; enabled-configuration speed acceptance is not measured here.
- All modes use the same selected SocketJack serializer and payload. SafeMode remains enabled; the benchmark assembly is pinned and its message type explicitly allowed. TCP uses `NoDelay=true`. Minimal TCP uses a four-byte frame length, a scatter/gather send with partial-write handling, and the same serializer, `Wrapper` validation and receiver work. It is one tuned managed TCP baseline, not a claim of the best possible TCP implementation.
- The System.Text.Json comparison uses SocketJack's existing `SocketJack.Serialization.Json.JsonSerializer`, including its converters/options. It does not claim comparison against source-generated or hand-written JSON with different SocketJack semantics.
- Serializer-only measurements rotate the original binary codec, optimized binary codec and JSON over ten runs per 256-byte, 32 KiB and 256 KiB payload. They use a prebuilt SocketJack wrapper, 20 warmups, then respectively 200/100/50 operations per measured phase. Encode, raw decode and decode-plus-unwrap are distinct measurements. `binary-before` is a saved copy of the original custom codec, not an old transport DLL.
- Transport confidence intervals use 20,000 paired bootstrap samples of within-round relative improvement. Serializer speedup intervals use paired ratios of mean operation times, also 20,000 draws. Both use seed 7301 and percentile 95% intervals. Results apply to this workload/configuration; unrelated DTO shapes can behave differently.
- Raw measurements include p50/p95/p99, CPU, allocations, peak process working set, UDP retransmissions and application-layer datagram byte counters. Sender CPU/allocations cover bulk, digest verification and the latency run; receiver CPU/allocations cover bulk only. Peak working sets include startup/warmup, and sender memory includes the prepared 256 MiB fixture. They must not be interpreted as equivalent bulk-only intervals.
- UDP wire counters count transport datagram bytes, excluding UDP/IP/link headers. They are not packet-capture measurements, and no TCP wire-byte comparison is claimed. Retransmissions are transport retries, not measured physical packet loss. A clean loopback path can still drop/delay datagrams under scheduling pressure.
- The impairment proxy establishes UDP correctness. It cannot impose equivalent network-level impairment on TCP. No LAN/Internet superiority or impaired-path TCP superiority is established.

The measured core DLL SHA-256 is:

```text
3FA84278B92335272661F3E74C8F01897F0F302AE5AA648EB30B0D1AD9AA49D2
```

The reliable correctness run used the same core hash. The frozen benchmark assembly hash is `2E6888D89DDDA6162ED44DBAB058CBEB2518D91AC029FAAB32CB6FB48C113C6C`.

Earlier partial tuning measurements, the timed-payload-generation diagnostic, and historical v2 runs are excluded from acceptance. Different security code and benchmark preparation prevent a controlled old-v2/new-v3 transport speedup claim.

## Reproduction and evidence

From the repository root, with .NET 8 and Python available:

```powershell
$buildFlags = @('-c', 'Release', '-p:BuildPackage=false', '-p:GeneratePackageOnBuild=false', '-p:DocumentationFile=bin/Release/SocketJack.udp.xml')
dotnet build tools/UdpReliable.Bench/UdpReliable.Bench.csproj @buildFlags
dotnet test SocketJack.UdpReliable.Tests/SocketJack.UdpReliable.Tests.csproj @buildFlags --logger 'trx;LogFileName=udp-final.trx'
dotnet build SocketJack.Windows/SocketJack.WPF.csproj @buildFlags

# Run after builds/tests have finished. The full test suite requires about 8 GiB free disk space.
$bench = 'tools/UdpReliable.Bench/bin/Release/net8.0/UdpReliable.Bench.dll'
Remove-Item Env:SJ_DIAGNOSTICS,Env:SJ_BENCH_MODES -ErrorAction SilentlyContinue
$env:SJ_SERIALIZER = 'binary'
dotnet $bench run artifacts/udp-reliable/binary.json 10 256
$env:SJ_SERIALIZER = 'json'
dotnet $bench run artifacts/udp-reliable/json.json 10 256
Remove-Item Env:SJ_SERIALIZER
dotnet $bench serializers artifacts/udp-reliable/serializers.json
python tools/UdpReliable.Bench/analyze.py artifacts/udp-reliable/binary.json
python tools/UdpReliable.Bench/analyze.py artifacts/udp-reliable/json.json
python tools/UdpReliable.Bench/analyze-serializers.py artifacts/udp-reliable/serializers.json
```

For a real-network follow-up, use matching binaries/options and `server <mode> <port>` on one host, `client <mode> <host> <port> <run-index> 256` on the other. Repeat alternating rounds and the same statistical test. Use identical network-level impairment for TCP and UDP; 32 KiB datagrams require deliberate path/fragmentation testing.

Raw evidence: [binary network runs](udp-reliable-results/optimization/binary-256MiB-10runs.json), [binary confidence intervals](udp-reliable-results/optimization/binary-256MiB-10runs-analysis.json), [JSON network runs](udp-reliable-results/optimization/json-256MiB-10runs.json), [JSON confidence intervals](udp-reliable-results/optimization/json-256MiB-10runs-analysis.json), [formatter measurements](udp-reliable-results/optimization/serializers-final.json), [formatter confidence intervals](udp-reliable-results/optimization/serializers-final-analysis.json), [longer GC diagnostic](udp-reliable-results/optimization/codec-gc-diagnostics.json), [diagnostic analysis](udp-reliable-results/optimization/codec-gc-diagnostics-analysis.json).

Validation: [reliable suite TRX](udp-reliable-results/optimization/final-all.trx), [readable test log](udp-reliable-results/optimization/final-all-tests.log), [security suite TRX](udp-reliable-results/optimization/security-typed-unwrap.trx), [WPF build log](udp-reliable-results/optimization/wpf-final.log), [machine/network inventory](udp-reliable-results/optimization/machine.json), [binary hashes](udp-reliable-results/optimization/binary-hashes.json), [measurement audit](udp-reliable-results/optimization/measurement-audit.json).

Harness: [network benchmark](../tools/UdpReliable.Bench/Program.cs), [formatter comparison](../tools/UdpReliable.Bench/SerializerBench.cs), [original codec fixture](../tools/UdpReliable.Bench/BeforeBinarySerializer.cs), [GC diagnostic](../tools/UdpReliable.Bench/CodecDiagnostics.cs), [optimization tests](../SocketJack.UdpReliable.Tests/OptimizationTests.cs). Run the supplemental diagnostic with `dotnet $bench codec-diagnostics artifacts/udp-reliable/codec-gc-diagnostics.json`. The diagnostic was added after the frozen network/primary formatter runs, without modifying the core DLL.

