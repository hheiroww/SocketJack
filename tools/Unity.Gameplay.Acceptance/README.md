# Native gameplay acceptance

This test-only fixture builds two actual Unity scenes and a native player. Separate server, two client and late-joining client processes exercise the public components over UDP and TCP: placed scene objects, member baseline readiness, server health changes, client door commands, Animator state and transient triggers, ownership callbacks and state authority, late-join state without replayed triggers, and server-directed scene transitions with lifecycle teardown and fresh identities. A final pool cycle verifies reuse of the same Unity instance with a new network ID, an unbound cached identity, reset health and baseline state, and the game-authored `OnNetworkReset` hook.

The adaptive pass observes Balanced and Full Precision profiles in every process, waits for the actual server policy transition, and checks a Full Precision transform update together with an exact numeric member roundtrip through the declared adaptive codec family.

Run `Build-And-Test.ps1` with an installed licensed Editor and its exact `-UnityVersion`, an accepted `-RuntimeDll` and `-ExpectedRuntimeSha256`, and an owned `-OutputDirectory` outside source. It refuses projects belonging to a different source/Editor and active owned players. A matching retained build can use `-SkipBuild`. Generated projects, players, receipts and machine paths remain outside source. Every process has a bounded test deadline; the script never kills a process or deletes outputs. All networking is loopback, with automatic router mapping disabled.

These sources are reproducible test tooling, excluded from NuGet and Asset Store product archives. Passing loopback acceptance does not measure Internet latency or establish production scaling capacity.
