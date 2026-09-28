using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SocketJack.Net {
    // Wire v2 uses compact numeric identifiers; see UdpReliableWire.
    // A fragment's (session,message,index) is its unique sequence. All numeric fields are little endian.
    internal sealed class UdpReliableTransport : IDisposable {
        internal const int Header = UdpReliableWire.MaximumHeader;
        internal enum Op : byte { Hello = 1, Cookie, Confirm, Ready, Open, Grant, Data, Ack, Complete, Close, Ping, Pong, Inline }
        internal sealed class Frame {
            internal Op Type; internal ulong Session; internal ulong Id; internal int A, B;
            internal byte[] Data; internal int Offset, Count; internal IPEndPoint Endpoint;
        }
        readonly Socket socket;
        readonly bool server;
        readonly NetworkOptions network;
        internal readonly UdpReliableOptions Options;
        readonly Action<UdpReliablePeer> connected;
        readonly Action<UdpReliablePeer, byte[], byte> received;
        readonly Action<UdpReliablePeer, Exception> failed;
        readonly ConcurrentDictionary<IPEndPoint, UdpReliablePeer> peers = new ConcurrentDictionary<IPEndPoint, UdpReliablePeer>();
        readonly ConcurrentDictionary<string, double> retired = new ConcurrentDictionary<string, double>();
        readonly ConcurrentQueue<Frame> controls = new ConcurrentQueue<Frame>();
        readonly SemaphoreSlim signal = new SemaphoreSlim(0, 1);
        readonly CancellationTokenSource stop = new CancellationTokenSource();
        readonly byte[] secret = new byte[32];
        int disposed, controlCount;
        long reserved;
        internal static double Now => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;
        internal int MaxMessage => (int)Math.Min(network.MaximumBufferSize, Math.Min(Options.ReceiveQueueBytes, int.MaxValue));
        internal UdpReliableTransport(Socket socket, bool server, NetworkOptions network,
            Action<UdpReliablePeer> connected, Action<UdpReliablePeer, byte[], byte> received, Action<UdpReliablePeer, Exception> failed) {
            this.socket = socket; this.server = server; this.network = network;
            Options = (network.UdpReliable ?? new UdpReliableOptions()).Snapshot();
            if (network.UseSsl) throw new NotSupportedException("UDP_Reliable does not implement TLS. UseSsl cannot be enabled.");
            if (network.UseCompression && !(network.CompressionAlgorithm is SocketJack.Compression.GZip2Compression) && !(network.CompressionAlgorithm is SocketJack.Compression.DeflateCompression))
                throw new NotSupportedException("UDP_Reliable requires GZip2 or Deflate for bounded decompression.");
            this.connected = connected; this.received = received; this.failed = failed;
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(secret);
            socket.ReceiveBufferSize = Options.BufferSize;
            socket.SendBufferSize = Options.BufferSize;
        }
        static ulong NewSession() {
            byte[] bytes = new byte[8];
            using (var rng = RandomNumberGenerator.Create()) {
                do { rng.GetBytes(bytes); } while (BinaryPrimitives.ReadUInt64LittleEndian(bytes) == 0);
            }
            return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        }
        internal bool IsServer => server;
        internal void Start() { _ = ReceiveLoop(); _ = SendLoop(); }
        internal async Task<UdpReliablePeer> Connect(IPEndPoint endpoint, CancellationToken token) {
            var peer = new UdpReliablePeer(this, endpoint, NewSession(), Options.DatagramSize, MaxMessage);
            peers[endpoint] = peer;
            using (token.Register(() => peer.Fail(new OperationCanceledException(token)))) {
                Wake();
                await peer.Ready.Task.ConfigureAwait(false);
                return peer;
            }
        }
        internal void Wake() { try { signal.Release(); } catch (SemaphoreFullException) { } }
        internal void Control(UdpReliablePeer p, Op op, ulong id = 0, int a = 0, int b = 0, byte[] body = null) {
            Enqueue(new Frame { Endpoint = p.Endpoint, Session = p.Session, Type = op, Id = id, A = a, B = b, Data = body, Count = body?.Length ?? 0 });
        }
        void Enqueue(Frame f) {
            if (Interlocked.Increment(ref controlCount) > 4096) { Interlocked.Decrement(ref controlCount); return; }
            controls.Enqueue(f); Wake();
        }
        internal bool Reserve(int bytes) {
            long count = Interlocked.Add(ref reserved, bytes);
            if (count <= Options.GlobalReceiveQueueBytes) return true;
            Interlocked.Add(ref reserved, -bytes); return false;
        }
        internal void Release(int bytes) => Interlocked.Add(ref reserved, -bytes);
        internal void Dispatch(UdpReliablePeer peer, byte[] data, byte kind) => received(peer, data, kind);
        internal void Failed(UdpReliablePeer peer, Exception error) {
            ((ICollection<KeyValuePair<IPEndPoint, UdpReliablePeer>>)peers).Remove(new KeyValuePair<IPEndPoint, UdpReliablePeer>(peer.Endpoint, peer));
            retired[peer.Endpoint + "|" + peer.Session] = Now + 90000;
            // Lifecycle handlers can take application locks; never run them under the protocol lock.
            _ = Task.Run(() => { peer.Transfers?.Dispose(); failed(peer, error); });
        }
        static ulong CookieEpoch => (ulong)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
        byte[] Cookie(IPEndPoint ep, ulong session, int mtu, int size, ulong epoch) {
            using (var hmac = new HMACSHA256(secret)) {
                return hmac.ComputeHash(Encoding.UTF8.GetBytes(ep + "|" + session + "|" + mtu + "|" + size + "|" + epoch)).Take(16).ToArray();
            }
        }
        async Task ReceiveLoop() {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(Options.BufferSize);
            using var operation = new UdpReliableSocketOperation(buffer, Options.BufferSize);
            try {
                while (!stop.IsCancellationRequested) {
                    int length;
                    try { length = await operation.Receive(socket).ConfigureAwait(false); }
                    catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset || ex.SocketErrorCode == SocketError.MessageSize) { continue; }
                    if (!UdpReliableWire.Read(buffer, length, out var op, out var session, out var id, out var a, out var b, out int header)) continue;
                    var ep = (IPEndPoint)operation.RemoteEndPoint;
                    if (server && op == Op.Hello && length == header && a >= 256 && a <= 32768 && b > 0) {
                        int mtu = Math.Min(a, Options.DatagramSize), size = Math.Min(b, MaxMessage);
                        ulong epoch = CookieEpoch;
                        Enqueue(new Frame { Endpoint = ep, Session = session, Type = Op.Cookie, Id = epoch, A = mtu, B = size, Data = Cookie(ep, session, mtu, size, epoch), Count = 16 });
                        continue;
                    }
                    if (server && op == Op.Confirm && length == header + 16 && a >= 256 && a <= Options.DatagramSize && b > 0 && b <= MaxMessage) {
                        ulong epoch = CookieEpoch;
                        if ((id != epoch && id != epoch - 1) || retired.ContainsKey(ep + "|" + session) || retired.Count >= (long)Options.MaximumPeers * 8) continue;
                        byte[] expected = Cookie(ep, session, a, b, id);
                        int difference = 0;
                        for (int i = 0; i < 16; i++) difference |= expected[i] ^ buffer[header + i];
                        if (difference != 0) continue;
                        if (peers.TryGetValue(ep, out var previous) && previous.Session != session)
                            previous.Fail(new IOException("Peer established a new UDP_Reliable session."), false);
                        if (!peers.TryGetValue(ep, out var existing)) {
                            if (peers.Count >= Options.MaximumPeers) continue;
                            var accepted = new UdpReliablePeer(this, ep, session, a, b);
                            accepted.MarkReady();
                            if (peers.TryAdd(ep, accepted)) {
                                try { connected(accepted); } catch (Exception ex) { accepted.Fail(ex); continue; }
                                existing = accepted;
                            }
                        }
                        if (existing != null && existing.Session == session) Control(existing, Op.Ready, a: a, b: b);
                        continue;
                    }
                    if (!peers.TryGetValue(ep, out var peer) || peer.Session != session) continue;
                    Interlocked.Add(ref peer.Statistics.received, length);
                    peer.Receive(op, id, a, b, buffer, header, length - header);
                }
            } catch (Exception ex) { if (!stop.IsCancellationRequested) foreach (var p in peers.Values) p.Fail(ex); }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }
        async Task SendLoop() {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(Options.BufferSize);
            using var operation = new UdpReliableSocketOperation(buffer, Options.BufferSize);
            var batch = new List<Frame>(256);
            try {
                while (!stop.IsCancellationRequested) {
                    batch.Clear();
                    while (batch.Count < 256 && controls.TryDequeue(out var control)) { Interlocked.Decrement(ref controlCount); batch.Add(control); }
                    double now = Now;
                    if (retired.Count > 0) foreach (var item in retired) if (item.Value < now) retired.TryRemove(item.Key, out _);
                    foreach (var peer in peers.Values) { peer.Transfers?.Expire(now); peer.Pump(now, batch); }
                    foreach (var frame in batch) {
                        int header = UdpReliableWire.Write(buffer, frame);
                        if (frame.Count > 0) Buffer.BlockCopy(frame.Data, frame.Offset, buffer, header, frame.Count);
                        try {
                            await operation.Send(socket, frame.Endpoint, header + frame.Count).ConfigureAwait(false);
                            if (peers.TryGetValue(frame.Endpoint, out var p)) Interlocked.Add(ref p.Statistics.sent, header + frame.Count);
                        } catch (SocketException ex) {
                            if (peers.TryGetValue(frame.Endpoint, out var p)) p.Fail(ex);
                        }
                    }
                    if (batch.Count == 0) await signal.WaitAsync(2, stop.Token).ConfigureAwait(false);
                }
            } catch (Exception ex) { if (!stop.IsCancellationRequested) foreach (var p in peers.Values) p.Fail(ex); }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }
        public void Dispose() {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            stop.Cancel();
            foreach (var peer in peers.Values) peer.Fail(new ObjectDisposedException("UDP_Reliable"));
            Wake(); // The owner closes the socket, releasing the outstanding receive operation.
        }
    }

    internal sealed class UdpReliablePeer {
        const ulong Independent = 1UL << 63;
        readonly UdpReliableTransport host;
        readonly object gate = new object();
        readonly Dictionary<ulong, Outgoing> outgoing = new Dictionary<ulong, Outgoing>();
        readonly LinkedList<Outgoing> sendOrder = new LinkedList<Outgoing>();
        readonly Dictionary<ulong, Incoming> incoming = new Dictionary<ulong, Incoming>();
        readonly SortedDictionary<ulong, Incoming> ordered = new SortedDictionary<ulong, Incoming>();
        readonly HashSet<ulong> completed = new HashSet<ulong>();
        readonly ConcurrentQueue<Incoming> deliveries = new ConcurrentQueue<Incoming>();
        readonly ConcurrentQueue<Incoming> independentDeliveries = new ConcurrentQueue<Incoming>();
        readonly ConcurrentQueue<Incoming> streamDeliveries = new ConcurrentQueue<Incoming>();
        readonly SemaphoreSlim independentSignal = new SemaphoreSlim(0);
        readonly SemaphoreSlim streamSignal = new SemaphoreSlim(0);
        readonly SemaphoreSlim deliverySignal = new SemaphoreSlim(0);
        readonly SemaphoreSlim admission = new SemaphoreSlim(1, 1);
        readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        TaskCompletionSource<bool> capacity = NewSignal();
        internal readonly TaskCompletionSource<bool> Ready = NewSignal();
        internal readonly UdpReliableStatistics Statistics = new UdpReliableStatistics();
        internal readonly IPEndPoint Endpoint;
        internal readonly ulong Session;
        internal UdpReliableTransfers Transfers;
        internal int Mtu, MaxMessage;
        internal bool IsReady { get; private set; }
        internal bool IsClosed { get; private set; }
        ulong nextOrdered = 1, nextIndependent = 1, deliverNext = 1, reserveNext = 1, doneOrdered, doneIndependent;
        int roundRobin, delivering;
        internal bool IsServer => host.IsServer;
        int FlightLimit => Math.Min(host.Options.MaximumFlightPackets, Math.Max(1, host.Options.BufferSize / Mtu / 2));
        long sendBytes, receiveBytes;
        double handshakeSent = -1000, created = UdpReliableTransport.Now, lastReceive = UdpReliableTransport.Now, lastPing;
        double srtt = 20, variation = 10, window = 10, threshold = double.MaxValue, nextPace, lastReduction;
        byte[] cookie;
        ulong cookieEpoch;
        int flight;
        static TaskCompletionSource<bool> NewSignal() => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        internal UdpReliablePeer(UdpReliableTransport host, IPEndPoint endpoint, ulong session, int mtu, int maxMessage) {
            this.host = host; Endpoint = endpoint; Session = session; Mtu = mtu; MaxMessage = maxMessage;
            if (host.Options.Profile == UdpReliableProfile.FastLan) window = FlightLimit;
            _ = DeliverLoop(deliveries, deliverySignal);
            _ = DeliverLoop(streamDeliveries, streamSignal);
            for (int i = 0; i < host.Options.ReceiveWorkers; i++) _ = DeliverLoop(independentDeliveries, independentSignal);
        }
        internal void MarkReady() { window = Math.Min(window, FlightLimit); IsReady = true; Ready.TrySetResult(true); }
        internal Task Send(byte[] data, byte kind, bool independent, bool wait, CancellationToken cancellation) => SendCore(_ => data, data.Length, kind, independent, wait, cancellation);
        // Preparation runs only after admission, serialized with indexing. Pattern-cache state cannot advance for a rejected send.
        internal async Task SendCore(Func<long, byte[]> prepare, int reservation, byte kind, bool independent, bool wait, CancellationToken cancellation, Func<int> measure = null) {
            Outgoing message;
            if (wait) await admission.WaitAsync(cancellation).ConfigureAwait(false);
            else if (!admission.Wait(0)) throw new IOException("UDP_Reliable send admission is busy. Use SendAsync for backpressure.");
            try {
            // Only one unadmitted serialized object can exist per peer, even with many awaiting callers.
            if (measure != null) reservation = measure();
            if (reservation < 0 || reservation > MaxMessage || reservation > host.Options.SendQueueBytes) throw new IOException("UDP_Reliable message exceeds the configured message or send budget.");
            while (true) {
                cancellation.ThrowIfCancellationRequested();
                Task available;
                lock (gate) {
                    if (IsClosed || !IsReady) throw new IOException("UDP_Reliable session is not connected.");
                    if (sendBytes + reservation <= host.Options.SendQueueBytes && outgoing.Count < host.Options.MaximumQueuedMessages) {
                        byte[] payload = prepare(Math.Min(MaxMessage, host.Options.SendQueueBytes - sendBytes));
                        if (payload.Length > MaxMessage || sendBytes + payload.Length > host.Options.SendQueueBytes) throw new IOException("UDP_Reliable encoded message exceeds the configured budget.");
                        ulong sequence = independent ? nextIndependent++ : nextOrdered++;
                        if (sequence >= Independent - 1) throw new IOException("UDP_Reliable sequence exhausted; reconnect.");
                        ulong id = independent ? sequence | Independent : sequence;
                        message = new Outgoing { Id = id, Data = payload, Kind = kind, Progress = UdpReliableTransport.Now };
                        int count = Math.Max(1, (payload.Length + Mtu - UdpReliableTransport.Header - 1) / (Mtu - UdpReliableTransport.Header));
                        message.Times = new double[count]; message.Attempts = new byte[count]; message.Acked = new bool[count];
                        outgoing.Add(id, message); message.Node = sendOrder.AddLast(message);
                        sendBytes += payload.Length; Interlocked.Exchange(ref Statistics.queued, sendBytes);
                        break;
                    }
                    if (!wait) throw new IOException("UDP_Reliable send queue is full. Use SendAsync for backpressure.");
                    available = capacity.Task;
                }
                var canceled = NewSignal();
                using (cancellation.Register(() => canceled.TrySetCanceled(cancellation))) {
                    await await Task.WhenAny(available, canceled.Task).ConfigureAwait(false);
                }
            }
            } finally { admission.Release(); }
            host.Wake();
            // Removing a committed ordered message would create a permanent gap. Canceling it terminates the session.
            using (cancellation.Register(() => Fail(new OperationCanceledException("A committed reliable send was canceled.", cancellation))))
                await message.Done.Task.ConfigureAwait(false);
        }
        internal void Receive(UdpReliableTransport.Op op, ulong id, int a, int b, byte[] data, int offset, int count) {
            lock (gate) {
                if (IsClosed) return;
                double now = UdpReliableTransport.Now;
                if (!IsReady) {
                    if (op == UdpReliableTransport.Op.Cookie && count == 16 && a >= 256 && a <= Mtu && b > 0 && b <= MaxMessage) {
                        Mtu = a; MaxMessage = b; cookieEpoch = id; cookie = new byte[16]; Buffer.BlockCopy(data, offset, cookie, 0, 16); handshakeSent = -1000; host.Wake();
                    } else if (op == UdpReliableTransport.Op.Ready && count == 0 && cookie != null && a == Mtu && b == MaxMessage) MarkReady();
                    return;
                }
                lastReceive = now;
                if (op == UdpReliableTransport.Op.Close && count == 0) { Fail(new IOException("Remote UDP_Reliable session closed."), false); return; }
                if (op == UdpReliableTransport.Op.Ping && count == 0) { host.Control(this, UdpReliableTransport.Op.Pong); return; }
                if (op == UdpReliableTransport.Op.Pong) return;
                if (op == UdpReliableTransport.Op.Inline) {
                    if (id == 0 || (id & ~Independent) == 0 || a != count || count > Mtu - UdpReliableTransport.Header || count > MaxMessage || b < 0 || b > 1) return;
                    if (AlreadyCompleted(id)) { host.Control(this, UdpReliableTransport.Op.Complete, id); return; }
                    ulong sequence = id & ~Independent, floor = (id & Independent) != 0 ? doneIndependent : doneOrdered;
                    if (sequence > floor + 128 || incoming.ContainsKey(id) || ((id & Independent) == 0 && id != reserveNext)) return;
                    if (incoming.Count + ordered.Count + delivering >= 128 || receiveBytes + count > host.Options.ReceiveQueueBytes || !host.Reserve(count)) return;
                    var small = new Incoming { Id = id, Kind = (byte)b, Data = new byte[count] };
                    Buffer.BlockCopy(data, offset, small.Data, 0, count); receiveBytes += count;
                    if ((id & Independent) == 0) reserveNext++;
                    CompleteIncoming(small); return;
                }
                if (op == UdpReliableTransport.Op.Open && count == 0) {
                    if (id == 0 || (id & ~Independent) == 0 || a < 0 || a > MaxMessage || b < 0 || b > 1) return;
                    if (AlreadyCompleted(id)) { host.Control(this, UdpReliableTransport.Op.Complete, id); return; }
                    ulong sequence = id & ~Independent, floor = (id & Independent) != 0 ? doneIndependent : doneOrdered;
                    if (sequence > floor + 128) return;
                    if (incoming.TryGetValue(id, out var existing)) {
                        if (existing.Data.Length == a && existing.Kind == b) host.Control(this, UdpReliableTransport.Op.Grant, id);
                        return;
                    }
                    // Reserve ordered messages in order; later messages cannot consume the space needed by a gap.
                    if ((id & Independent) == 0 && id != reserveNext) return;
                    if (incoming.Count + ordered.Count + delivering >= 128 || receiveBytes + a > host.Options.ReceiveQueueBytes || !host.Reserve(a)) return;
                    var input = new Incoming { Id = id, Kind = (byte)b, Data = new byte[a], Progress = now };
                    input.Fragments = new bool[Math.Max(1, (a + Mtu - UdpReliableTransport.Header - 1) / (Mtu - UdpReliableTransport.Header))];
                    incoming.Add(id, input); receiveBytes += a;
                    if ((id & Independent) == 0) reserveNext++;
                    host.Control(this, UdpReliableTransport.Op.Grant, id); return;
                }
                if (op == UdpReliableTransport.Op.Grant && count == 0 && outgoing.TryGetValue(id, out var granted)) { granted.Granted = true; host.Wake(); return; }
                if (op == UdpReliableTransport.Op.Data) {
                    if (AlreadyCompleted(id)) { host.Control(this, UdpReliableTransport.Op.Complete, id); return; }
                    if (!incoming.TryGetValue(id, out var input) || a < 0 || a >= input.Fragments.Length) return;
                    int position = a * (Mtu - UdpReliableTransport.Header), expected = Math.Min(Mtu - UdpReliableTransport.Header, input.Data.Length - position);
                    if (count != expected) return;
                    if (!input.Fragments[a]) {
                        Buffer.BlockCopy(data, offset, input.Data, position, count); input.Fragments[a] = true; input.Count++; input.Progress = now;
                        while (input.FirstMissing < input.Fragments.Length && input.Fragments[input.FirstMissing]) input.FirstMissing++;
                    }
                    input.AckPending = true; input.LastBlock = a / 64 * 64;
                    if (input.Count == input.Fragments.Length) {
                        incoming.Remove(id); CompleteIncoming(input);
                    } else if (++input.SinceAck >= Math.Min(16, Math.Max(1, FlightLimit / 2)) || a != input.FirstMissing - 1) Ack(input);
                    return;
                }
                if (op == UdpReliableTransport.Op.Ack && count == 8 && outgoing.TryGetValue(id, out var sent)) {
                    if (a < 0 || a > sent.Next || b < 0 || b >= sent.Times.Length || b % 64 != 0) return;
                    ulong bits = BinaryPrimitives.ReadUInt64LittleEndian(new ReadOnlySpan<byte>(data, offset, 8));
                    int highest = a - 1;
                    for (int i = sent.FirstUnacked; i < a; i++) Acknowledge(sent, i, now);
                    for (int i = 0; i < 64 && b + i < sent.Next; i++) if ((bits & (1UL << i)) != 0) { Acknowledge(sent, b + i, now); highest = Math.Max(highest, b + i); }
                    while (sent.FirstUnacked < sent.Next && sent.Acked[sent.FirstUnacked]) sent.FirstUnacked++;
                    // Three newly observed later packet indexes imply a likely gap; retransmit once before RTO.
                    if (highest > sent.HighestAck) {
                        sent.HighestAck = highest;
                        if (sent.FirstUnacked < sent.Next && highest >= sent.FirstUnacked + 3 && sent.Attempts[sent.FirstUnacked] == 1) sent.Times[sent.FirstUnacked] = -1;
                    }
                    host.Wake(); return;
                }
                if (op == UdpReliableTransport.Op.Complete && count == 0 && outgoing.TryGetValue(id, out var done) && done.Next == done.Times.Length) {
                    for (int i = 0; i < done.Next; i++) Acknowledge(done, i, now);
                    outgoing.Remove(id); sendOrder.Remove(done.Node); sendBytes -= done.Data.Length; Interlocked.Exchange(ref Statistics.queued, sendBytes);
                    Interlocked.Add(ref Statistics.payloadSent, done.Data.Length); done.Done.TrySetResult(true);
                    var previous = capacity; capacity = NewSignal(); previous.TrySetResult(true); host.Wake();
                }
            }
        }
        bool AlreadyCompleted(ulong id) => (id & ~Independent) <= (((id & Independent) == 0) ? doneOrdered : doneIndependent) || completed.Contains(id);
        void CompleteIncoming(Incoming input) {
            completed.Add(input.Id); AdvanceCompleted(input.Id);
            Interlocked.Add(ref Statistics.payloadReceived, input.Data.Length);
            if ((input.Id & Independent) != 0) QueueDelivery(input);
            else {
                ordered.Add(input.Id, input);
                while (ordered.TryGetValue(deliverNext, out var ready)) { ordered.Remove(deliverNext++); QueueDelivery(ready); }
            }
            host.Control(this, UdpReliableTransport.Op.Complete, input.Id);
        }
        void AdvanceCompleted(ulong id) {
            if ((id & Independent) == 0) { while (completed.Remove(doneOrdered + 1)) doneOrdered++; }
            else { while (completed.Remove((doneIndependent + 1) | Independent)) doneIndependent++; }
        }
        void QueueDelivery(Incoming message) {
            delivering++;
            var queue = message.Kind == 1 ? streamDeliveries : (message.Id & Independent) != 0 ? independentDeliveries : deliveries;
            var ready = message.Kind == 1 ? streamSignal : (message.Id & Independent) != 0 ? independentSignal : deliverySignal;
            queue.Enqueue(message); ready.Release();
        }
        async Task DeliverLoop(ConcurrentQueue<Incoming> queue, SemaphoreSlim ready) {
            try {
                while (!lifetime.IsCancellationRequested) {
                    await ready.WaitAsync(lifetime.Token).ConfigureAwait(false);
                    if (queue.TryDequeue(out var message)) {
                        try { host.Dispatch(this, message.Data, message.Kind); }
                        catch (Exception ex) { Fail(ex); }
                        finally { lock (gate) { delivering--; receiveBytes -= message.Data.Length; host.Release(message.Data.Length); } }
                    }
                }
            } catch (OperationCanceledException) { }
        }
        void Ack(Incoming input) {
            byte[] bits = new byte[8]; ulong value = 0;
            for (int i = 0; i < 64 && input.LastBlock + i < input.Fragments.Length; i++) if (input.Fragments[input.LastBlock + i]) value |= 1UL << i;
            BinaryPrimitives.WriteUInt64LittleEndian(bits, value);
            host.Control(this, UdpReliableTransport.Op.Ack, input.Id, input.FirstMissing, input.LastBlock, bits);
            input.AckPending = false; input.SinceAck = 0;
        }
        void Acknowledge(Outgoing message, int index, double now) {
            if (message.Acked[index]) return;
            message.Acked[index] = true; flight--; message.Progress = now;
            if (message.Attempts[index] == 1 && message.Times[index] > 0) {
                double sample = Math.Max(0.1, now - message.Times[index]);
                variation = .75 * variation + .25 * Math.Abs(srtt - sample); srtt = .875 * srtt + .125 * sample;
                Volatile.Write(ref Statistics.rtt, srtt);
            }
            if (host.Options.Profile == UdpReliableProfile.Adaptive) window = Math.Min(FlightLimit, window + (window < threshold ? 1 : 1 / window));
        }
        internal void Pump(double now, List<UdpReliableTransport.Frame> frames) {
            lock (gate) {
                if (IsClosed) return;
                if (!IsReady) {
                    if (now - created > 10000) { Fail(new TimeoutException("UDP_Reliable handshake timed out; verify both endpoints use the same mode/version.")); return; }
                    if (now - handshakeSent >= 200) { host.Control(this, cookie == null ? UdpReliableTransport.Op.Hello : UdpReliableTransport.Op.Confirm, id: cookieEpoch, a: Mtu, b: MaxMessage, body: cookie); handshakeSent = now; }
                    return;
                }
                double deadline = host.Options.DeliveryTimeout.TotalMilliseconds;
                if (now - lastReceive > deadline) { Fail(new TimeoutException("UDP_Reliable peer stopped responding.")); return; }
                if (now - lastPing >= Math.Min(1000, deadline / 3)) { host.Control(this, UdpReliableTransport.Op.Ping); lastPing = now; }
                foreach (var input in incoming.Values) {
                    if (now - input.Progress > deadline) { Fail(new TimeoutException("UDP_Reliable reassembly stalled.")); return; }
                    if (input.AckPending) Ack(input);
                }
                double rto = Math.Max(30, Math.Min(2000, srtt + Math.Max(1, 4 * variation)));
                int budget = 64;
                var active = sendOrder.Take(host.Options.MaximumConcurrentMessages).ToArray();
                int first = active.Length == 0 ? 0 : (roundRobin = (roundRobin + 1) % active.Length);
                // Admission/inline frames must follow lane order. Rotate only already reserved fragment work.
                foreach (var message in active) {
                    if (!message.Activated) { message.Activated = true; message.Progress = now; }
                    if (now - message.Progress > deadline) { Fail(new TimeoutException("UDP_Reliable delivery made no progress.")); return; }
                    if (message.Data.Length <= Mtu - UdpReliableTransport.Header) {
                        if (budget > 0 && (message.Next == 0 ? flight < (int)window + (message.Data.Length <= 1200 ? 1 : 0) : now - message.Times[0] >= rto * Math.Pow(2, Math.Min(5, message.Attempts[0] - 1)))) {
                            if (message.Next == 0) { flight++; message.Next = 1; }
                            else Interlocked.Increment(ref Statistics.retries);
                            frames.Add(new UdpReliableTransport.Frame { Endpoint = Endpoint, Session = Session, Type = UdpReliableTransport.Op.Inline,
                                Id = message.Id, A = message.Data.Length, B = message.Kind, Data = message.Data, Count = message.Data.Length });
                            message.Times[0] = now; message.Attempts[0] = (byte)Math.Min(255, message.Attempts[0] + 1); budget--;
                        }
                        continue;
                    }
                    if (!message.Granted) {
                        if (now - message.LastOpen >= rto) {
                            frames.Add(new UdpReliableTransport.Frame { Endpoint = Endpoint, Session = Session, Type = UdpReliableTransport.Op.Open,
                                Id = message.Id, A = message.Data.Length, B = message.Kind });
                            message.LastOpen = now;
                        }
                    }
                }
                for (int n = 0; n < active.Length; n++) {
                    var message = active[(first + n) % active.Length];
                    if (!message.Granted || message.Data.Length <= Mtu - UdpReliableTransport.Header) continue;
                    for (int i = message.FirstUnacked; i < message.Next && budget > 0; i++) {
                        if (message.Acked[i]) continue;
                        if (message.Times[i] >= 0 && now - message.Times[i] < rto * Math.Pow(2, Math.Min(5, message.Attempts[i] - 1))) continue;
                        if (host.Options.Profile == UdpReliableProfile.Adaptive && now - lastReduction >= srtt) { threshold = Math.Max(2, window / 2); window = threshold; lastReduction = now; }
                        Emit(message, i, now, frames); budget--; Interlocked.Increment(ref Statistics.retries);
                    }
                    while (message.Next < message.Times.Length && flight < (int)window && budget > 0) {
                        if (host.Options.Profile == UdpReliableProfile.Adaptive && now < nextPace) break;
                        Emit(message, message.Next++, now, frames); flight++; budget--;
                        if (host.Options.Profile == UdpReliableProfile.Adaptive && budget % 16 == 0) nextPace = now + Math.Min(1, srtt * 16 / window);
                    }
                    if (message.Next == message.Times.Length && message.FirstUnacked == message.Next && now - message.LastProbe >= rto) {
                        // A lost COMPLETE must not strand an otherwise fully acknowledged message.
                        host.Control(this, UdpReliableTransport.Op.Open, message.Id, message.Data.Length, message.Kind); message.LastProbe = now;
                    }
                }
            }
        }
        void Emit(Outgoing message, int index, double now, List<UdpReliableTransport.Frame> frames) {
            int position = index * (Mtu - UdpReliableTransport.Header);
            frames.Add(new UdpReliableTransport.Frame { Endpoint = Endpoint, Session = Session, Type = UdpReliableTransport.Op.Data, Id = message.Id,
                A = index, B = message.Data.Length, Data = message.Data, Offset = position, Count = Math.Min(Mtu - UdpReliableTransport.Header, message.Data.Length - position) });
            message.Times[index] = now; message.Attempts[index] = (byte)Math.Min(255, message.Attempts[index] + 1);
        }
        internal void Fail(Exception error, bool notify = true) {
            lock (gate) {
                if (IsClosed) return;
                IsClosed = true;
                if (notify && IsReady) host.Control(this, UdpReliableTransport.Op.Close);
                Ready.TrySetException(error);
                foreach (var message in outgoing.Values) message.Done.TrySetException(error);
                outgoing.Clear(); sendOrder.Clear(); sendBytes = 0; Interlocked.Exchange(ref Statistics.queued, 0);
                foreach (var input in incoming.Values.Concat(ordered.Values)) { receiveBytes -= input.Data.Length; host.Release(input.Data.Length); }
                incoming.Clear(); ordered.Clear();
                foreach (var queue in new[] { deliveries, independentDeliveries, streamDeliveries })
                    while (queue.TryDequeue(out var input)) { delivering--; receiveBytes -= input.Data.Length; host.Release(input.Data.Length); }
                capacity.TrySetResult(true); lifetime.Cancel();
            }
            host.Failed(this, error);
        }
        sealed class Outgoing {
            internal ulong Id; internal byte[] Data; internal byte Kind; internal bool Granted, Activated;
            internal LinkedListNode<Outgoing> Node;
            internal double[] Times; internal byte[] Attempts; internal bool[] Acked;
            internal int Next, FirstUnacked, HighestAck = -1; internal double Progress, LastOpen = -10000, LastProbe;
            internal readonly TaskCompletionSource<bool> Done = NewSignal();
        }
        sealed class Incoming {
            internal ulong Id; internal byte Kind; internal byte[] Data; internal bool[] Fragments;
            internal int Count, FirstMissing, LastBlock, SinceAck; internal bool AckPending; internal double Progress;
        }
    }
}
