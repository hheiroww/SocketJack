# Unity multiplayer capacity verification

## Release two checkpoint

The 2026.11.04.0 candidate was retested on Windows/.NET 8.0.32 with 2,000 real loopback peers and 20 reliable-unordered echo rounds. Both UDP and TCP delivered all **40,000** replies, with zero rejected sends or disconnects. UDP measured 4,998 round trips/s, 438.51 ms p95 burst RTT and 54.89 MiB peak process working set; TCP measured 8,516 round trips/s, 262.32 ms p95 burst RTT and 51.94 MiB. These are saturated single-process burst results while the machine ran other work, not gameplay latency or a WAN/player-capacity guarantee.

Release two adds spatial visibility, bounded per-peer work, measured transport metrics, opt-in adaptive precision and linked-server services to help partition larger deployments. Native Unity 6/2022.3 checks cover gameplay/lifecycle and real 2D physics. Actual Unity 2022.3 WebGL/IL2CPP checks cover direct and relayed gameplay WebSockets with large reconstructed values. Native accounts/chat checks use real built Unity players on both Editors. The earlier results below remain historical evidence for their stated source versions; the current setup/validation boundaries are in [ReleaseTwo.md](Unity-ReleaseTwo.md).

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

## Topology, authority and Inspector extension

The 2026.11.02.3 source adds separate topology, transport and authority settings. Its actual Unity configuration probe checked all four movement modes and verified that Rigidbody mass, drag, angular drag, gravity, interpolation and collider-material friction remained unchanged. The movement controller defaults to `ForceMode.Force`, so native mass affects applied movement forces.

Two additional 20-second actual-player scenarios passed with zero hard corrections:

| Configuration | Measured client RTT p95 | Largest client visual-frame movement |
| --- | --- | --- |
| Dedicated Server / Client, server authority | 16.92 / 9.12 ms | approximately 0.060 m |
| Host / Peer, owner authority | 9.12 / 9.14 ms | 0.059 / 0.059 m |

The owner-authority peers each sent 100 canonical owner snapshots and the host applied 200, demonstrating that the authority switch changes canonical-state behavior. Dedicated configurations default to server authority. Hosts and peers in this mode still communicate through the host; this is not a full-mesh or hosted relay service.

Release builds omit debug symbols and machine-specific CodeView paths. The resulting clean runtime DLL was rebuilt into the actual player and retested for 20 seconds with a dedicated server and two headless clients: zero hard corrections, p95 RTT 16.86/16.76 ms, and mean position error 0.211/0.191 m. The raw runtime DLL and the DLL in that tested player have identical SHA-256 digests. The Asset Store export uses that same verified binary and rejects absolute machine/debug paths.

The managed suite now includes five owner-authority checks and 22 offline UPnP tests. Those cover ownership/room validation, stale state, dedicated-server write rejection, gateway restrictions, mapping conflicts, finite leases, cancellation, renewal and verified cleanup. Automated player runs explicitly disabled UPnP; no real router mapping or Internet reachability is claimed. The Asset Store archive separately passed strict file/path/hash checks and real Unity fresh-import compilation; that is not Asset Store submission approval.

## Sizing a game

Keep authoritative input validation, partition visibility into rooms, and measure bytes and CPU per visible object at the game's tick and snapshot rates. Start with multiple room/server processes for thousands of total users. A large mutually visible world requires additional spatial interest filtering, packet aggregation and/or sharding. This release supplies local physics prediction with sparse authoritative repair; it does not supply hosted orchestration, matchmaking, NAT traversal, collision rollback or an encrypted game transport. See the [package guide](../SocketJack.Unity/README.md) for the integration contract and limits.
