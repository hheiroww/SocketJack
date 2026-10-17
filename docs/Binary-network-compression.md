# Adaptive binary network compression

SocketJack's custom `BinarySerializer` enables adaptive network compression by default. Configure it on the serializer before connecting:

```csharp
var options = new NetworkOptions {
    Serializer = new BinarySerializer { EnableCompression = false }
};
```

Leave `EnableCompression` at `true` to use it. JSON and other serializers retain their existing behavior. This feature does not use .NET's `BinaryFormatter`.

Learning uses a bounded pool of content-defined binary chunks rather than a fixed number of previous objects. Candidates survive across more than ten objects when memory permits; inactivity and memory pressure retire candidates. Only exact matches seen in separate messages become dictionary definitions. Changes to a field or its length can therefore leave neighboring stable segments reusable. The current message also uses LZ back-references for repeated runs and repeated segments. A data frame uses the original SB1 bytes whenever its complete compressed representation, including inline definitions, would be larger.

An inexpensive content scout adapts work to the traffic. A cold stream starts with sampled words rather than invoking the general encoder on unproven random data. Recurrence wakes exact candidate observations. Every 64 eligible unproven messages, an alternate sample grid checks between the fixed samples; each grid is checked twice before moving. Random traffic therefore retains only bounded hashes until repetition warrants exact learning. Local repetition or shortcuts already owned by the peer enable encoding. Unsuccessful or marginal attempts then back off up to 64 messages. A persistent tiny repeated header or one stable sample cannot force continuous full scans. Network messages smaller than 256 bytes bypass cache creation and compression entirely. Definition indexes are reused and updated incrementally; useful local compression avoids unnecessary dictionary registrations. Chunk boundaries favor larger definitions within the existing 64–1024 byte wire limits, reducing reference and registration overhead. A verified encoding plan skips the local repetition scan, while every referenced span still receives an exact byte comparison. Stream dictionary updates use bounded 16 KiB batches to reduce cold connection control traffic.

Exact learning records the positions of confirmed chunks as a reusable plan, avoiding a general index build and another search of those same bytes. On a layout miss, up to four recent plans can try their original positions and a length shift of at most 64 bytes in either direction. This handles small leading field/header changes and appended bytes. Every reference is checked against the current message; changed or out-of-range spans remain literals. A bounded plan for the new length is retained when worthwhile. Unrelated layouts continue through the normal indexed encoder.

Learning also continues while an existing dictionary already compresses the message well. A stronger reuse signal requests confirmation observations promptly, and occasional background observations find smaller changes. A formerly random suffix can therefore enter the dictionary after becoming stable without discarding the already useful prefix definitions.

Repeated payload layouts can reuse cached encoding plans. Sampled anchors select a candidate plan; every referenced dictionary span is compared with the current payload before use, and current literal bytes are copied directly into one correctly sized output buffer. Changed regions become literals or trigger a fresh encoding pass. Plan storage shares the repository memory limit and is evicted by recency. Definitions require peer acknowledgement, or direct evidence that the peer supplied those immutable bytes, before their bytes can be omitted.

Dictionary/literal frames can be deserialized directly from bounded immutable segments, avoiding a separate full expanded-message allocation. Application byte arrays still receive their own storage. Inline definitions, overlapping LZ copies and heavily fragmented frames fall back to the normal bounded decoder. Both paths retain the same SB1 format, type whitelist and authorization checks.

For applications that intentionally exchange common data between users, the default `ShareCompressionDictionary = true` shares definitions between negotiated clients of the same socket. Dictionary definitions contain exact payload bytes, so only use sharing when every participating client may receive those bytes. A new shared connection receives a small snapshot in its compatibility handshake and the remaining definitions in bounded protocol updates after the handshake.

Websites and services whose clients exchange independent data should configure a private cache:

```csharp
var options = new NetworkOptions {
    Serializer = new BinarySerializer {
        EnableCompression = true,
        ShareCompressionDictionary = false
    }
};
```

Either endpoint can require isolation. When negotiated, each connection owns its candidates, definitions, encoding plans, and acknowledgement state. Its updates and handshake snapshot stay on that connection; shared updates exclude private peers. Closing a private connection releases its retained state. The negotiated choice stays fixed until reconnecting. A peer that enables compression without supporting the isolation requirement is rejected rather than silently sharing data; disable compression before reconnecting to that older peer.

Browser integrations must require private per-connection caches and explicit permission before persisting definitions. Typed HTTP request/response bodies and standalone `Serialize` calls retain SB1.

Externally hosted binary transports can use `BinarySerializer.CreateCompressionSession(maximumMessageBytes)` to create one private repository per connection. Its `Encode` and `Decode` methods return dictionary/ACK controls to send in order on that same connection; a null decode result denotes a control. The host must negotiate the protocol, bind it to the authenticated account, enforce message limits and dispose it on disconnect. `SerializeJson`/`DeserializeJson` preserve JSON-shaped contracts as actual SB1 fields without activating named CLR types. `EnableCompression` is captured when the private session is created. Session retention is included in the process-wide memory ceiling.

Definitions have immutable content IDs. Until a receiver acknowledges retaining a definition, a referencing data frame includes its bytes. Lost or reordered updates, acknowledgements, and data therefore cannot produce an undefined shortcut. Ordinary UDP retries unacknowledged registrations with backoff; reliable UDP uses its transport's retransmission and larger registration pages. Dictionary updates and acknowledgements never invoke application callbacks or register application types. Existing type allowlists and authorization still apply after decompression.

Both endpoints must advertise support in the compatibility handshake. If either serializer disables the property, or an older peer does not advertise it, traffic remains ordinary SB1. Strict legacy SafeMode handshakes and `SafeMode = false` do not enable this extension. Configure the property before connecting; reconnect to change the negotiated setting.

The process shares one admission budget for retained compression state across all serializers and connections: at most one sixteenth of the computer's physical RAM. Each repository also has a 4 MiB cap, includes conservative metadata costs, and observes at most 512 chunks of each full learning probe. Candidate retention adapts to available cache space; unused candidates expire after two minutes when the next learning probe runs. Acknowledged definitions remain immutable for the repository's lifetime; a private repository ends when its connection closes. At capacity, new definitions are declined and existing definitions and message-local compression remain usable. Windows and Linux discover physical RAM locally; an unknown memory limit disables dictionary retention. The native cache remains in memory. The consent-gated browser adapter also commits its private definitions to IndexedDB before acknowledging them.

`CompressionMemoryLimit`, `CompressionMemoryUsed`, `CompressionCacheBytes`, and `CompressionDictionaryCount` expose the shared budget, total accounted state, and this serializer's repository usage. The admission budget covers retained candidates, definitions, indexes, scout metadata, and peer acknowledgement metadata. Ordinary in-flight network buffers and temporary encoder/decoder workspaces are separate from retained cache state.

`CompressionCandidateCount`, `CompressionEncodingAttempts`, `CompressionBypassedMessages`, and `CompressionProbeInterval` expose the adaptive learner's current candidates, encoding attempts, direct-send count, and largest current probe interval. `CompressionLearningPasses` also counts exact observations that skip the encoder. `CompressionPlanCount` reports reusable layouts. `CompressionPlanHits` counts successful verified-plan encoding passes; one object can count twice when learning refines an initial encoding. It is a cache-work diagnostic, not a unique delivered-message count. These diagnostics aggregate this serializer's live caches, including isolated connections. The deprecated `CompressionHistoryLength` getter returns zero to indicate that no fixed object history remains.

Existing `NetworkOptions.UseCompression` can still apply GZip/Deflate around binary frames. Binary traffic bypasses the older whole-message pattern cache so `EnableCompression = false` also disables adaptive binary shortcuts. Small or already compressed data may save no bytes, and dictionary registration has an initial transfer cost. Throughput depends on workload and CPU; fewer bytes alone do not establish a speed gain.

Release coverage is in `SocketJack.Tests/BinaryCompressionTests.cs`, `BinaryCompressionDeliveryTests.cs`, `BinaryCompressionIsolationTests.cs`, and `BinaryCompressionUdpImpairmentTests.cs`, including incompressible backoff and recovery, recurrence after more than ten objects, bounded cache memory, profitable complete frames, real loopback transfers, shared/private peer isolation, definition/acknowledgement loss and reordering, and 32 KiB datagrams under 1% and 5% packet loss. Current build and test results accompany the optimization benchmark report.

One trained TCP fixture transferred a 16,384-byte payload in 930 wire bytes (94.3% reduction, including framing and control traffic). This is a measured repeated-data example, not a general throughput guarantee.

The [TCP optimization benchmark](binary-compression-benchmark/2026-10-15-tcp-direct-plans/README.md) measures speed, latency, and complete connection bytes with private per-client caches, including cold starts, partially changing objects, sustained traffic, initial dictionary learning and shared three-idle-client fanout. The report includes confidence intervals, adaptive reuse after more than ten objects, raw data, and reproduction commands. The [previous TCP and reliable-UDP report](binary-compression-benchmark/2026-10-15-final/README.md), [original report](binary-compression-benchmark/2026-10-15/README.md) and [first optimization](binary-compression-benchmark/2026-10-15-optimized/README.md) remain available as historical measurements.
