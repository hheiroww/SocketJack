# Unity multiplayer capacity verification

The initial SocketJack.Unity release was checked on November 2, 2026 with .NET 8.0.32 on Windows. These tests establish connection and delivery correctness at the measured load. They do not establish a production game player count or acceptable realtime latency for 2,000 players.

## Reproduce

```sh
dotnet test SocketJack.Unity.Tests/SocketJack.Unity.Tests.csproj -c Release
dotnet run --project tools/Unity.Multiplayer.Bench -c Release -- 2000 20 Udp
dotnet run --project tools/Unity.Multiplayer.Bench -c Release -- 2000 20 Tcp
```

The harness hosts one server and 2,000 real socket clients in one process, connects in bounded bursts, and completes 20 rounds of 64-byte reliable echo messages. Each round waits for every reply. All clients run on the same thread as the server. This is a transport correctness and resource test, not a fixed-rate game simulation or a WAN test. The pump budget is at least 4,096 events; the low-level default of 256 must be raised for larger servers.

| Measurement | UDP | TCP |
| --- | ---: | ---: |
| Connected / unique peers | 2,000 / 2,000 | 2,000 / 2,000 |
| Reliable round trips completed | 40,000 | 40,000 |
| Rejected sends / disconnects | 0 / 0 | 0 / 0 |
| Connection setup | 1.26 s | 2.57 s |
| Traffic phase | 6.76 s | 4.15 s |
| Echo round trips/s | 5,915 | 9,629 |
| Round-trip latency, p50 / p95 | 273 / 354 ms | 176 / 256 ms |
| Peak process working set | 56.5 MiB | 47.7 MiB |

The transports were measured serially. The machine was also running other applications. Working set excludes kernel socket buffers and other processes; it is not a total memory requirement. In particular, the burst latencies above are unsuitable evidence for a low-latency 2,000-player action game. TCP's higher result here does not predict performance under WAN loss or establish a general transport recommendation.

After the wire-v2 reliable-unordered extension, a further 2,000-peer UDP run completed all 40,000 echo round trips with zero rejected sends or disconnects. It measured 5,250 round trips/s and 388 ms p95 latency while this machine also ran other work. The harness now defaults to `ReliableUnordered`; pass a fourth argument such as `ReliableOrdered` to compare a specific mode. This remains a transport capacity/correctness result, separate from the two-player physics latency below.

## Replication and failure checks

The session suite separately connects 2,000 logical peers across 100 rooms, verifies room isolation and per-peer snapshot budgets, and streams hundreds of kilobytes of late-join state through a small bounded queue. Real UDP/TCP integration checks owned input, authoritative state, late joining, visibility changes, custom messages and owner cleanup after disconnect.

The UDP impairment proxy drops data and acknowledgments and reorders packets. Tests verify ordered, duplicate-free reliable delivery, independent channels, sequence wrap, source/session rejection, bounded reordering, handshake validation, admission, timeout and callback limits. TCP tests cover partial and oversized frames, slow handshakes, queue recovery, admission exceptions, recursive pumping and mismatched configurations.

The Unity component sources compile against real Unity 2022.3.17f1 reference assemblies. After Editor activation, the [physics cube sample](../Examples/UnityPhysics/README.md) was built as a real Windows Unity player and exercised with a headless server and two visible clients. A 45-second scripted movement run observed both objects, local/relayed input and sparse keyframes on both clients. The same run reported:

| Physics-player measurement | Client 1 | Client 2 |
| --- | ---: | ---: |
| Hard corrections | 0 | 0 |
| Mean / peak position error | 0.230 / 1.439 m | 0.226 / 1.410 m |
| RTT p95 | 16.76 ms | 9.35 ms |
| RTT maximum | 136.65 ms | 140.48 ms |
| Largest sampled visual-frame movement | 0.825 m | 0.878 m |

These results passed the unchanged loopback acceptance limits. They do not establish zero rendering hitches or zero jerk in every scenario; isolated timing/visual-step spikes were recorded. Physics collisions can also produce legitimate abrupt velocity changes. The first live attempt exposed an inactive-Rigidbody baseline bug and failed the hard-snap check; activating and setting the body pose before baseline capture corrected that failure. No warmup samples or thresholds were removed to pass the rerun. No IL2CPP, mobile, console or browser-player acceptance is claimed.

The final source, including room/object revocation for queued physics relays, was rebuilt into a separate Windows player and retested with a real headless Unity server and two headless Unity clients. That 20-second client run also passed: zero hard corrections, p95 RTT 17.14/17.80 ms, maximum RTT 25.82/19.77 ms, mean position error 0.322/0.248 m, and peak error 1.691/1.166 m. Each client sent 99 owner inputs, received 99 relayed inputs and 196 keyframes. This final run exercises the same PhysX/game code without a rendered client window; the preceding visible run provides the rendering check.

## Sizing a game

Keep authoritative input validation, partition visibility into rooms, and measure bytes and CPU per visible object at the game's tick and snapshot rates. Start with multiple room/server processes for thousands of total users. A large mutually visible world requires additional spatial interest filtering, packet aggregation and/or sharding. This release supplies local physics prediction with sparse authoritative repair; it does not supply hosted orchestration, matchmaking, NAT traversal, collision rollback or an encrypted game transport. See the [package guide](../SocketJack.Unity/README.md) for the integration contract and limits.
