# UDP_Reliable measured results

## Subsequent release validation (2026.14.0)

The benchmark tables below preserve their original measurement snapshot. Subsequent release validation passed 111 core tests (one optional TypeScript compiler check skipped) and all 45 reliable-UDP/codec tests excluding the large-file test. The separate large-file test also passed: 4,295,032,833 bytes transferred in 206.82 seconds with matching SHA-256 and 26,756,272 bytes peak managed memory. These correctness checks do not replace or change the historical performance measurements or their TCP acceptance limits.


Recorded September 30, 2026. **No tested configuration met the combined TCP speed acceptance target.** The later encoder optimizations have separate measurements and do not retroactively change these transport results.

[Protocol guide](UDP_Reliable.md) · [Encoder testing README](encoder-testing/README.md) · [Main README](../README.md)

## Transport comparison: IPv4 loopback

Each serializer has ten measured runs per mode, with separate sender/receiver processes. Useful throughput is receiver-observed; p95 is application-level request/reply latency, not enqueue time. Values below are means of the ten runs.

| Serializer | Mode | Throughput (MiB/s) | p95 RTT (ms) |
|---|---|---:|---:|
| binary | tcp | 122.44 | 5.001 |
| binary | raw-tcp | 72.34 | 4.384 |
| binary | adaptive | 51.60 | 4.674 |
| binary | fast-lan | 49.99 | 4.777 |
| binary | fast-lan-32k | 124.04 | 4.898 |
| json | tcp | 84.95 | 4.571 |
| json | raw-tcp | 47.22 | 4.269 |
| json | adaptive | 38.83 | 4.536 |
| json | fast-lan | 38.26 | 4.556 |
| json | fast-lan-32k | 83.92 | 4.432 |

## Confidence intervals and acceptance

Passing requires both at least 10% more delivered throughput and at least 10% lower p95 latency against **both** TCP baselines, with both 95% interval lower bounds at or above +10%. Positive values favor UDP. Percentages are paired relative improvements; brackets show 95% intervals.

| Serializer | UDP profile | Baseline | Throughput improvement | p95 reduction | Result |
|---|---|---|---|---|---|
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

`fast-lan-32k` improved throughput relative to minimal TCP, but did not meet the combined latency/throughput requirement against both baselines. `Adaptive` and ordinary `FastLan` used 1,200-byte datagrams; `fast-lan-32k` used 32 KiB datagrams on loopback. Large datagrams may fragment on real networks.

## Later encoder, wrapper and segment measurements

The subsequent [encoder testing README](encoder-testing/README.md) includes binary/System.Text.Json comparisons, allocations, confidence intervals, correctness evidence and commands. For 32 KiB binary payloads:

| Operation | Before → after | Speedup |
|---|---:|---:|
| Encode | 5.64 → 4.51 µs | 1.25× |
| Raw decode | 5.67 → 4.27 µs | 1.33× |
| Wrap + encode | 592.29 → 491.08 µs | 1.21× |
| Decode + unwrap | 2002.75 → 1007.56 µs | 1.99× |

All four intervals cleared 1. Legacy 4 MiB segment reconstruction measured 955.89 → 8.82 ms (108.32×). This is a legacy helper result, not reliable UDP reassembly throughput. Small-message wrapping and further 256 KiB raw-codec improvements were inconclusive. The full network comparison has **not** been rerun on this later core DLL.

## Reliability and scope

The transport snapshot recorded 35 passed / 2 failed / 37 tests, including a >4 GiB byte-count/SHA-256 file test. The failures were both peer-redirect cases blocked by the current authentication policy. The later codec snapshot recorded 24/24 focused checks, 43/45 reliable checks excluding the large-file rerun, and 36/38 security checks. The remaining two security failures concern an HTTP connection-close expectation and a removed admin reflection endpoint. See the encoder README for exact test names. These are recorded snapshot outcomes, not a claim that the current whole-repository suite passes.

| Acceptance item | Status |
|---|---|
| Combined loopback speed targets against both TCP baselines | FAIL for every tested profile/serializer |
| Core delivery, impairment and codec coverage | Recorded passes; broader suite has the failures above |
| New encoder build transport speed targets | Not rerun |
| Two-machine LAN/Internet confirmation | Unverified |
| Fair, identical TCP/UDP network-level impairment | Unverified |

## Method and limitations

- Host: DESKTOP-KSSU21A, Xeon W-2140B, 8 cores/16 logical processors, about 16 GiB RAM, Windows 11 Pro 10.0.22631, .NET 8.0.31. Timing used loopback, not Wi-Fi. The advertised Wi-Fi rate is not measured throughput.
- Incompressible 256 MiB bulk is prepared before timing in 256 KiB typed-message payloads. Eight messages are outstanding. Receiver byte count, unique indexes and SHA-256 verify delivery. Separate latency runs use 1,000 sequential 256-byte typed-message echoes. Warmup is 8 MiB plus 100 echoes. Mode order rotates across ten runs.
- Compression/pattern caching are disabled, independent delivery is selected, and SafeMode remains enabled with approved DTOs. Each comparison uses the same serializer and receiver work. TCP uses NoDelay; minimal TCP uses length framing and the same serialization/wrapping validation.
- Confidence intervals use 20,000 paired bootstrap samples, seed 7301. Raw records include p50/p95/p99, CPU, allocations, working set and UDP retry/wire counters. Counters are not packet-capture loss measurements; process metrics cover different sender/receiver intervals and include fixture/warmup effects.
- Seeded UDP impairment tests establish delivery behavior, not fair impaired-path TCP speed. Loopback cannot establish LAN or Internet superiority. No fresh timing was performed for this documentation update.

Transport core SHA-256: `3FA84278B92335272661F3E74C8F01897F0F302AE5AA648EB30B0D1AD9AA49D2`. Later encoder core: `B137C0CBC9B5B2E23541DF7A1BC41B5A9C6509B7028A767988D5E5F2A77F36B5`. Keep these snapshots distinct.

## Reproduction and raw evidence

Run from the repository root. Rebuilt results describe the current source, not necessarily the frozen binaries above.

```powershell
$flags = @('-c','Release','-p:BuildPackage=false','-p:GeneratePackageOnBuild=false','-p:DocumentationFile=bin/Release/SocketJack.udp.xml')
dotnet build tools/UdpReliable.Bench/UdpReliable.Bench.csproj @flags
$bench = 'tools/UdpReliable.Bench/bin/Release/net8.0/UdpReliable.Bench.dll'
Remove-Item Env:SJ_DIAGNOSTICS,Env:SJ_BENCH_MODES -ErrorAction SilentlyContinue
$env:SJ_SERIALIZER = 'binary'
dotnet $bench run artifacts/udp-reliable/repeat-binary.json 10 256
$env:SJ_SERIALIZER = 'json'
dotnet $bench run artifacts/udp-reliable/repeat-json.json 10 256
Remove-Item Env:SJ_SERIALIZER
python tools/UdpReliable.Bench/analyze.py artifacts/udp-reliable/repeat-binary.json
python tools/UdpReliable.Bench/analyze.py artifacts/udp-reliable/repeat-json.json
```

- [Binary raw runs](udp-reliable-results/optimization/binary-256MiB-10runs.json) and [analysis](udp-reliable-results/optimization/binary-256MiB-10runs-analysis.json).
- [JSON raw runs](udp-reliable-results/optimization/json-256MiB-10runs.json) and [analysis](udp-reliable-results/optimization/json-256MiB-10runs-analysis.json).
- [Transport test TRX](udp-reliable-results/optimization/final-all.trx), [machine/network inventory](udp-reliable-results/optimization/machine.json), [binary hashes](udp-reliable-results/optimization/binary-hashes.json).
- [Network harness](../tools/UdpReliable.Bench/Program.cs), [statistical analysis](../tools/UdpReliable.Bench/analyze.py), [encoder test documentation](encoder-testing/README.md).
