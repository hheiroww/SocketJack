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

## Replication and failure checks

The session suite separately connects 2,000 logical peers across 100 rooms, verifies room isolation and per-peer snapshot budgets, and streams hundreds of kilobytes of late-join state through a small bounded queue. Real UDP/TCP integration checks owned input, authoritative state, late joining, visibility changes, custom messages and owner cleanup after disconnect.

The UDP impairment proxy drops data and acknowledgments and reorders packets. Tests verify ordered, duplicate-free reliable delivery, independent channels, sequence wrap, source/session rejection, bounded reordering, handshake validation, admission, timeout and callback limits. TCP tests cover partial and oversized frames, slow handshakes, queue recovery, admission exceptions, recursive pumping and mismatched configurations.

The Unity component sources compile against real Unity 2022.3.17f1 reference assemblies. A local Play Mode acceptance attempt was blocked because the installed Editor had no valid license. No Unity gameplay, IL2CPP, mobile, console or browser-player acceptance is claimed by these results.

## Sizing a game

Keep simulation authoritative, partition visibility into rooms, and measure bytes and CPU per visible object at the game's tick and snapshot rates. Start with multiple room/server processes for thousands of total users. A large mutually visible world requires additional spatial interest filtering, packet aggregation and/or sharding. This release does not supply hosted orchestration, matchmaking, NAT traversal, prediction, rollback or an encrypted game transport. See the [package guide](../SocketJack.Unity/README.md) for the integration contract and limits.
