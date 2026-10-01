# UDP_Reliable

`UDP_Reliable` adds session-scoped reliable messages to SocketJack's UDP client and server. It provides indexed fragmentation, selective retransmission, duplicate suppression, independent delivery with optional ordered callbacks, receiver backpressure, and bounded stream/file transfer. Existing UDP remains the default.


## Typed messages

```csharp
using SocketJack.Net;
using SocketJack.Serialization;

public sealed class Update {
    public int Index { get; set; }
    public byte[] Data { get; set; }
}

NetworkOptions Options(string trustedMessageDllSha256) {
    var options = new NetworkOptions {
        Serializer = new BinarySerializer(), UsePeerToPeer = false,
        UdpReliable = new UdpReliableOptions { Ordering = false, BufferSize = 32768 }
    };
    options.VerifiedAssemblies.Add(typeof(Update).Assembly, trustedMessageDllSha256);
    options.Authorization.AnonymousMessageTypes.Add(typeof(Update));
    return options;
}
// Obtain this SHA-256 pin from your trusted build/release configuration.
var server = UDP_Reliable.CreateServer(9000, Options(trustedMessageDllSha256));
server.RegisterCallback<Update>(e => e.Connection.Send(e.Object));
server.OnError += e => Console.WriteLine(e.Exception);
server.Listen();

var client = UDP_Reliable.CreateClient(Options(trustedMessageDllSha256));
client.RegisterCallback<Update>(e => Console.WriteLine(e.Object.Index));
client.OnError += e => Console.WriteLine(e.Exception);
if (await client.Connect("127.0.0.1", 9000)) {
    await client.SendAsync(new Update { Index = 1, Data = new byte[1_000_000] });
}
```

Use matching serializers and compression settings on both endpoints; version/MTU negotiation does not negotiate application encoding. Register callbacks or whitelist your types on both endpoints before sending. Nested property types must also be allowed. `Send`, `SendTo`, `SendBroadcast`, `ISocket.Send`, and connection replies use the reliable engine when selected. SafeMode DLL verification and receive authorization still apply. The current authorization policy requires authenticated TLS for peer redirects; plaintext UDP cannot satisfy that policy. See [SafeMode](SAFEMODE.md).

Alternatively, set `NetworkOptions.UdpMode = UdpMode.UDP_Reliable` before constructing `UdpClient`/`UdpServer`. Factories set this property on the supplied options. Configure options before connecting/listening; do not mutate them during a session. Do not share mutable `NetworkOptions` instances between independently disposed sockets.

`SendAsync` and server `SendToAsync` wait for admission capacity and remote transport acceptance. Acceptance means the complete message is in the remote delivery queue, **not** that deserialization, an application callback, or durable storage succeeded. An application requiring that guarantee must reply explicitly.

Synchronous sends enqueue or raise `OnError` when admission or queue capacity is exhausted. Errors are never a reason to silently discard a committed ordered message: unrecoverable transport failure closes the session and fails pending sends. Cancellation before admission leaves the session usable; cancellation of a committed message terminates its session to prevent ordering gaps. Do not synchronously block a receive callback waiting for application-level replies on that same callback dispatcher.

Synchronous sends now wait behind an encoder already in use instead of failing simply because it is busy. Waiting synchronous references are separately capped by `MaximumQueuedMessages`; encoded outbound payload still obeys `SendQueueBytes` and the outgoing message count limit. If an admitted send is already blocked on byte capacity, another synchronous call reports exhaustion. Use `SendAsync` to wait for backpressure.

## Ordering, tuning, and limits

```csharp
var options = new NetworkOptions {
    UdpMode = UdpMode.UDP_Reliable,
    UdpReliable = new UdpReliableOptions {
        Profile = UdpReliableProfile.Adaptive,
        Ordering = true, // opt in only when callback ordering is required
        BufferSize = 32768,
        SocketQueueBytes = 1024 * 1024, // aggregate kernel queue, not an individual I/O buffer
        ReceiveWorkers = 4,
        DatagramSize = 1200,
        DeliveryTimeout = TimeSpan.FromSeconds(30)
    }
};
```

- `Ordering = true` delivers typed messages in send-admission order. Concurrent async callers are admitted serially; application scheduling still determines which caller arrives first.
- `Ordering = false` is the default: each complete object is decoded and dispatched without waiting for earlier objects or their callbacks. Up to `ReceiveWorkers` (default 4) independent callbacks run concurrently per peer; handlers must be thread-safe. Reassembly places arriving fragments directly at their indexed positions. Decoding starts when the whole encoded object arrives; incomplete compressed/serialized objects cannot be parsed safely. `Delivery` remains an alias for compatibility. Internal identity/routing updates retain an ordered lane. Pattern caching is bypassed in independent mode to avoid references to unavailable cache entries.
- `Adaptive` uses an RTT-based retransmission timeout, slow start/additive increase, multiplicative decrease, and bounded burst pacing. `FastLan` uses the negotiated flight window without adaptive congestion reduction. Both endpoints' packet limits and receive capacity constrain that window. Use FastLan only on controlled paths with sufficient capacity.
- There is no fixed bandwidth cap. Flow control, memory limits, socket capacity, and physical bandwidth still apply. Existing TCP bandwidth options do not pace this transport; use the reliable profile/window settings.
- Datagram size defaults to **1,200 bytes including the compact reliable header**. Both endpoints negotiate the smaller size. Datagram size cannot exceed `BufferSize`, which defaults to and is capped at **32 KiB**. Reusable I/O buffers and stream chunks retain that cap. `SocketQueueBytes` separately requests a bounded aggregate OS queue (default 1 MiB; the OS may round its allocation). This is shared queue capacity for multiple datagrams, not an individual packet/I/O buffer size. Increasing datagram size without knowing the path MTU can cause IP fragmentation and reduce reliability/throughput.
- Default budgets: 128 MiB outbound payload per peer, 128 MiB inbound payload per peer, 512 MiB aggregate inbound payload per transport, 1,024 queued messages, 8 active messages, a ceiling of 512 packets in flight, and 1,024 server peers. The handshake limits flight to the smaller peer allowance, using at most half the requested/available socket queue bytes divided by negotiated datagram size (minimum one), plus one small-message slot. Default queue capacity permits up to 436 packets at 1,200 bytes or 16 at 32 KiB. Aggregate socket contention across peers still causes congestion; Adaptive remains the default. These are payload budgets; protocol metadata, serialization, decompression, and application data use additional memory. At most one unadmitted serialized object is retained per peer.
- Materialized objects retain `MaximumBufferSize` (100 MiB by default), with the smaller negotiated peer message limit applied. Streams/files are chunked and can exceed that limit.
- Delivery or peer inactivity without progress times out after 30 seconds by default. Connection establishment also respects `ConnectionTimeout` (3 seconds by default). Small messages fit in a single reliable datagram without a separate reservation round trip; larger messages reserve receiver capacity before fragmentation.
- Built-in GZip2 and Deflate compression are supported with bounded decompression. Other `ICompression` implementations are rejected for reliable mode until a bounded decoder is available. Ordinary UDP/TCP compression behavior is unchanged.
- Reliable broadcasts are separate unicast deliveries to known peers, not reliable IP broadcast/multicast. Transport v3 uses IPv4 endpoints and does not migrate sessions across endpoint changes.

Statistics are available through `client.ReliableStatistics` and `UdpConnection.ReliableStatistics`: wire bytes, accepted payload bytes, retries, queued payload bytes, and smoothed RTT. A retry counter measures protocol retransmissions, which can include reordered/delayed packets rather than actual network loss.

## SocketJack binary serializer


Select `Serializer = new SocketJack.Serialization.BinarySerializer()` on **both** endpoints. It implements the shared `ISerializer` interface and works with SocketJack TCP, ordinary UDP (within its datagram limit), and reliable UDP. JSON remains the existing default so other applications do not silently change wire format.

The versioned `SB` binary format writes byte arrays directly, uses variable-length zigzag integers, and carries independent type/property metadata. It does not need ordered dictionary initialization. Decoding creates inert wrappers/maps; `Wrapper.Unwrap` enforces SocketJack's type whitelist before application objects are constructed. Lengths, nesting depth, tags, trailing data, and integer overflow are validated. Date/time and uncommon collection/converter values use self-contained JSON fallback; this is not a claim that every object type serializes faster. Cyclic object graphs are not supported.

The optimized codec keeps the exact `SB` v1 format. It uses span decoding, stack storage for small encodes, pooled temporary blocks no larger than 32 KiB, cached local property getters/UTF-8 names, and a single final copy of large byte arrays. It does not cache authorization decisions. DTO construction still passes through the current whitelist, graph policy, and SafeMode checks. JSON fallback dictionaries also decode their DTO fields correctly.


Both TCP baselines use the same selected serializer as UDP in each comparison. The harness supports binary and SocketJack's System.Text.Json serializer separately.

## Streams and files

```csharp
server.TransferRequested += request => {
    // Choose this path locally; do not use an untrusted request.Name as a path.
    request.AcceptFile(@"C:\Transfers\received.bin");
    // request.Completion yields the verified byte count and SHA-256.
};

var result = await client.SendFileAsync(@"C:\Transfers\source.bin",
    progress: new Progress<UdpTransferProgress>(p => Console.WriteLine(p.BytesTransferred)),
    cancellationToken: cancellationToken);
Console.WriteLine(result.Sha256);
```

Accept synchronously from `TransferRequested` with `Accept(Stream, leaveOpen: true)` or `AcceptFile(path)`. An unaccepted request is rejected. The corresponding server APIs take a `UdpConnection` first. `SendStreamAsync` accepts seekable or non-seekable sources; a seekable source supplies its remaining length. Sources remain caller-owned.

The receiver must authorize `UdpTransferRequest` under its local receive policy. Stream frames are sized to whole transport payloads: at a 32 KiB datagram size, a chunk fits in one INLINE packet rather than requiring Open/Grant and a second fragment. The sender still waits for each chunk's transport acceptance; cross-chunk pipelining/resume is not implemented.

Transfers use binary frames up to 32 KiB (13-byte prefix plus at most 32,755 payload bytes), 64-bit byte offsets, and at most 16 concurrent incoming/outgoing transfers per peer. They share network scheduling with typed messages, with a separate sequential stream dispatcher so disk writes cannot block independent object decoding. Transfer identifiers are 32-bit counters, separated by direction; no GUID is carried in reliable transfer frames. Progress reports bytes accepted by the transport; final completion additionally requires the receiver to validate byte count and SHA-256 and flush the destination. File helpers use a uniquely named `.partial` file and move it to the final destination only after verification. Existing files are never overwritten.

Transfer cancellation sends an abort and removes partial files when the receiver processes it. A session failure or 30 seconds without transfer progress also cleans up receiver state. Caller-owned streams may contain partial data after a failed/canceled transfer. There is no reconnect resume, durable replay, or crash-recovery guarantee.

## Wire and recovery model

The v3 header is `SJ` (2 bytes), version (1), opcode/lane (1), random numeric session ID (8), then variable-length unsigned integers for the object ID and opcode-specific fields. DATA omits the redundant object length already accepted by Open/Grant. A DATA header with small object/fragment IDs is **14 bytes**, unchanged from v2. Fragment payload sizing reserves the maximum 32-byte header so every frame stays within the negotiated datagram size.

Packet indexes use one byte for 0-127, two for 128-16,383, and more only as needed. They **reset to zero for each object**, and for each bounded stream chunk. Identity is `(endpoint, session, object, packet index)`: resetting packet IDs never makes an old object's packet valid for a new object. Object sequence numbers stay monotonic and variable-length; exhausting the sequence fails explicitly rather than wrapping into stale data. There is no fixed GUID field in the reliable protocol. SocketJack's existing application-level peer identities remain compatible.

Wire v3 adds flight-capacity negotiation to the endpoint-validated cookie handshake and requires v3 at both endpoints. There is no silent downgrade to v2, v1, or ordinary UDP. Unknown versions, truncated/overlong integers, stale sessions, invalid indexes and impossible acknowledgments are rejected. The application binary serializer remains `SB` v1, with unchanged byte encoding.

Handshake: Hello, a short-lived endpoint-bound HMAC cookie challenge, Confirm, then Ready. Both sides must select reliable mode. There is no fallback. Retired sessions and expired cookies are rejected; reconnect starts fresh state. Cookies validate endpoint reachability, **not peer identity or payload authenticity**. Use a trusted network or an authenticated encrypted tunnel for hostile networks.

Large-message Open/Grant reserves the full encoded message before data transmission. ACKs carry a cumulative fragment boundary plus a selective 64-fragment bitmap. ACKs are batched, gaps can trigger fast retransmission, and retransmission timers back off. Complete acknowledges remote queue acceptance; a lost Complete is recovered through a duplicate data/reservation probe. Completed sequence tracking suppresses repeat dispatch. All state is local to the session.

## Reproduction

From the repository root:

```powershell
dotnet test SocketJack.UdpReliable.Tests/SocketJack.UdpReliable.Tests.csproj -c Release -p:BuildPackage=false -p:GeneratePackageOnBuild=false -p:DocumentationFile=bin/Release/SocketJack.udp.xml --filter 'TestCategory!=LargeFile'
dotnet test SocketJack.UdpReliable.Tests/SocketJack.UdpReliable.Tests.csproj -c Release --no-build --filter 'TestCategory=LargeFile'
dotnet build tools/UdpReliable.Bench/UdpReliable.Bench.csproj -c Release -p:BuildPackage=false -p:GeneratePackageOnBuild=false -p:DocumentationFile=bin/Release/SocketJack.udp.xml
dotnet tools/UdpReliable.Bench/bin/Release/net8.0/UdpReliable.Bench.dll run artifacts/udp-reliable/results.json 10 256
python tools/UdpReliable.Bench/analyze.py artifacts/udp-reliable/results.json

$env:SJ_SERIALIZER = 'json'
dotnet tools/UdpReliable.Bench/bin/Release/net8.0/UdpReliable.Bench.dll run artifacts/udp-reliable/json-results.json 10 256
python tools/UdpReliable.Bench/analyze.py artifacts/udp-reliable/json-results.json
Remove-Item Env:SJ_SERIALIZER
dotnet tools/UdpReliable.Bench/bin/Release/net8.0/UdpReliable.Bench.dll serializers artifacts/udp-reliable/serializers.json
```

The large-file test temporarily needs about 8 GiB of free disk space. Tests exercise actual sockets through a seeded loss/duplication/reordering proxy. That proxy establishes correctness; it does not establish a fair impaired-network speed comparison against TCP.

For two machines, run `server <mode> <port>` on one host and `client <mode> <host> <port> <run-index> 256` on the other. Modes are `tcp`, `raw-tcp`, `adaptive`, `fast-lan`, and `fast-lan-32k`. The last explicitly uses 32 KiB datagrams on a controlled path; both other UDP profiles keep the 1,200-byte default. Apply identical network-level impairment to both protocols when comparing impaired-path performance. Do not infer real-network superiority from loopback measurements.
