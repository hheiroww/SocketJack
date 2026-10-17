using SocketJack.Serialization;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace SocketJack.Net {
    // Controls never pass through CLR type activation or application callbacks.
    internal sealed class BinaryCompressionControl {
        internal readonly byte[] Frame;
        internal BinaryCompressionControl(byte[] frame) { Frame = frame; }
    }

    internal static class BinaryNetworkCompression {
        sealed class PeerState : IDisposable {
            internal HashSet<ulong> Known = new HashSet<ulong>();
            long reserved;
            internal int Retrying;
            bool disposed;
            internal PeerState() { if (BinaryCompressionMemory.TryReserve(2048)) reserved = 2048; }
            internal void Acknowledge(ulong id) {
                if (disposed || reserved == 0 || Known.Count >= 4096 || Known.Contains(id) || !BinaryCompressionMemory.TryReserve(128)) return;
                Known.Add(id); reserved += 128;
            }
            public void Dispose() { lock (this) { disposed = true; Known = new HashSet<ulong>(); BinaryCompressionMemory.Release(reserved); reserved = 0; } }
            ~PeerState() { Dispose(); }
        }
        static readonly ConditionalWeakTable<NetworkConnection, PeerState> peers = new ConditionalWeakTable<NetworkConnection, PeerState>();
        static PeerState Peer(NetworkConnection connection) => peers.GetValue(connection, key => {
            var state = new PeerState(); key.OnDisconnected += (_, __) => state.Dispose(); return state;
        });
        internal static bool Enabled(ISocket socket, NetworkConnection connection) =>
            socket?.Options.Serializer is BinarySerializer binary && binary.EnableCompression &&
            connection?.Compatibility?.BinaryCompression == true && connection.BinaryCompressionReady;

        internal static byte[] Encode(ISocket socket, NetworkConnection connection, byte[] raw) {
            // Receipts and other tiny objects cannot repay dictionary/encoder setup costs. Keep
            // them entirely out of the repository, rather than allocating peer state for them.
            if (raw == null || raw.Length < 256 || !Enabled(socket, connection) || BinaryCompressionCodec.HasMagic(raw)) return raw;
            var cache = ((BinarySerializer)socket.Options.Serializer).GetCompressionCache(socket, connection);
            var state = Peer(connection);
            BinaryCompressionDefinition[] added;
            byte[] encoded;
            lock (state) encoded = cache.Encode(raw, state.Known, out added);
            // Publishing may synchronously enter a transport send. Do it outside both the
            // repository and peer locks; definitions stay inline until their ACK is received.
            if (added.Length != 0) Publish(socket, connection, added);
            return encoded;
        }

        internal static byte[] Decode(ISocket socket, NetworkConnection connection, byte[] frame) {
            if (!BinaryCompressionCodec.HasMagic(frame)) return frame;
            if (!Enabled(socket, connection)) throw new InvalidDataException("Binary compression was not negotiated for this connection.");
            var cache = ((BinarySerializer)socket.Options.Serializer).GetCompressionCache(socket, connection);
            byte command = BinaryCompressionCodec.Command(frame);
            if (command == 3) {
                var ids = BinaryCompressionCodec.ReadAcknowledgement(frame);
                var state = Peer(connection);
                lock (state) {
                    int before = state.Known.Count;
                    foreach (ulong id in ids) if (cache.TryGet(id, out _)) state.Acknowledge(id);
                    if (state.Known.Count != before) cache.RequestProbe();
                }
                return null;
            }
            BinaryCompressionDefinition[] definitions;
            byte[] raw;
            if (command == 2) { raw = null; definitions = BinaryCompressionCodec.ReadUpdate(frame, socket.Options.MaximumBufferSize); }
            else raw = cache.Decode(frame, socket.Options.MaximumBufferSize, out definitions);
            if (definitions.Length == 0) return raw;
            var accepted = new List<ulong>();
            var imported = new List<BinaryCompressionDefinition>();
            var senderState = Peer(connection);
            bool learnedShortcut = false;
            foreach (var definition in definitions) {
                bool existing = cache.TryGet(definition.Id, out _);
                if (cache.Import(definition)) {
                    // A peer supplying an immutable definition already owns those bytes.
                    // This also lets a private reply use an uploaded shortcut immediately.
                    lock (senderState) {
                        int before = senderState.Known.Count;
                        senderState.Acknowledge(definition.Id);
                        learnedShortcut |= senderState.Known.Count != before;
                    }
                    accepted.Add(definition.Id);
                    if (!existing) imported.Add(definition);
                }
            }
            if (learnedShortcut) cache.RequestProbe();
            // Never omit a definition before the peer confirms it retained the bytes.
            if (accepted.Count != 0) SendControl(socket, connection, BinaryCompressionCodec.Acknowledge(accepted));
            if (imported.Count != 0) Publish(socket, connection, imported.ToArray(), connection);
            return raw;
        }

        internal static bool TryDeserialize(ISocket socket, NetworkConnection connection, byte[] frame, out Wrapper wrapper) {
            wrapper = null;
            if (!BinaryCompressionCodec.HasMagic(frame) || !(socket.Options.Serializer is BinarySerializer binary) ||
                !Enabled(socket, connection) || BinaryCompressionCodec.Command(frame) != 1) return false;
            var cache = binary.GetCompressionCache(socket, connection);
            var segments = ArrayPool<ReadOnlyMemory<byte>>.Shared.Rent(256);
            try {
                if (!cache.TryReadSegments(frame, socket.Options.MaximumBufferSize, segments, out int count, out int length)) return false;
                wrapper = binary.DeserializeSegments(segments, count, length);
                return true;
            } finally { ArrayPool<ReadOnlyMemory<byte>>.Shared.Return(segments, true); }
        }

        internal static byte[] Snapshot(ISocket socket, NetworkConnection connection = null) {
            if (!(socket.Options.Serializer is BinarySerializer binary) || !binary.EnableCompression) return null;
            if (connection == null && !binary.ShareCompressionDictionary) return null;
            var definitions = (connection == null ? binary.GetCompressionCache(socket) : binary.GetCompressionCache(socket, connection)).Snapshot(3000);
            if (definitions.Length == 0) return null;
            byte[] frame = BinaryCompressionCodec.Update(definitions);
            return frame.Length <= 4096 ? frame : null;
        }

        internal static void ImportSnapshot(ISocket socket, byte[] frame, NetworkConnection connection = null) {
            if (!(socket.Options.Serializer is BinarySerializer binary) || !binary.EnableCompression || frame.Length > 4096)
                throw new InvalidDataException("Invalid binary dictionary snapshot.");
            var definitions = BinaryCompressionCodec.ReadUpdate(frame, 4096);
            var cache = connection == null ? binary.GetCompressionCache(socket) : binary.GetCompressionCache(socket, connection);
            foreach (var definition in definitions) cache.Import(definition);
        }

        internal static void Synchronize(ISocket socket, NetworkConnection connection) {
            if (!(socket?.Options.Serializer is BinarySerializer binary) || !binary.EnableCompression || connection?.Compatibility?.BinaryCompression != true) return;
            connection.BinaryCompressionReady = true;
            var cache = binary.GetCompressionCache(socket, connection);
            SendDefinitions(socket, connection, cache.Definitions);
        }

        static IEnumerable<NetworkConnection> Connections(ISocket socket) {
            if (socket is TcpServer tcp) return tcp.Clients.Values;
            if (socket is WebSockets.WebSocketServer webSocket) return webSocket.Clients.Values;
            if (socket is UdpServer udp) return udp.CompressionConnections;
            return socket.Connection == null ? Array.Empty<NetworkConnection>() : new[] { socket.Connection };
        }

        static void Publish(ISocket socket, NetworkConnection source, BinaryCompressionDefinition[] definitions, NetworkConnection except = null) {
            var binary = (BinarySerializer)socket.Options.Serializer;
            if (binary.UsesPrivateCompression(source)) {
                if (source != except && Enabled(socket, source) && !source.Closed) SendDefinitions(socket, source, definitions);
                return;
            }
            foreach (var connection in Connections(socket)) {
                if (connection == except || !Enabled(socket, connection) || connection.Closed || binary.UsesPrivateCompression(connection)) continue;
                SendDefinitions(socket, connection, definitions);
            }
        }

        static void SendDefinitions(ISocket socket, NetworkConnection connection, BinaryCompressionDefinition[] definitions) {
                if (definitions.Length == 0) return;
                var batch = new List<BinaryCompressionDefinition>(); int size = 0;
                bool udp = socket is UdpClient || socket is UdpServer;
                bool reliable = udp && socket.Options.UdpMode == UdpMode.UDP_Reliable;
                // Streams and reliable UDP already fragment and retry admitted messages. Batch
                // registrations to avoid flooding cold connections with tiny updates and ACKs.
                int maximum = Math.Min(!udp || reliable ? 16384 : 2048, socket.Options.MaximumBufferSize);
                if (udp && !reliable) maximum = Math.Min(maximum, socket.Options.MaxDatagramSize - 64);
                foreach (var definition in definitions) {
                    if (definition.Data.Length + 32 > maximum) continue;
                    if (batch.Count != 0 && size + definition.Data.Length + 16 > maximum) {
                        SendControl(socket, connection, BinaryCompressionCodec.Update(batch.ToArray())); batch.Clear(); size = 0;
                    }
                    batch.Add(definition); size += definition.Data.Length + 16;
                }
                if (batch.Count != 0) SendControl(socket, connection, BinaryCompressionCodec.Update(batch.ToArray()));
                ScheduleRetry(socket, connection);
        }

        static void ScheduleRetry(ISocket socket, NetworkConnection connection) {
            // Only ordinary UDP requires periodic registration retries. Data frames remain independently
            // decodable until ACK, including unordered reliable UDP and concurrent TCP delivery.
            if (!(socket is UdpServer) && !(socket is UdpClient)) return;
            if (socket.Options.UdpMode == UdpMode.UDP_Reliable) return;
            var state = Peer(connection);
            if (Interlocked.CompareExchange(ref state.Retrying, 1, 0) != 0) return;
            _ = Retry(new WeakReference<ISocket>(socket), new WeakReference<NetworkConnection>(connection), state);
        }

        static async Task Retry(WeakReference<ISocket> socket, WeakReference<NetworkConnection> connection, PeerState state) {
            int delay = 500;
            try {
                while (true) {
                    await Task.Delay(delay).ConfigureAwait(false);
                    if (!Resend(socket, connection, state)) break;
                    delay = Math.Min(10000, delay * 2);
                }
            } catch { /* The normal transport reports send failures; closure ends retry work. */ }
            finally {
                Interlocked.Exchange(ref state.Retrying, 0);
                // A publication may race the transition to idle. Recheck after clearing the
                // flag so that either this check or the publishing thread starts its retry.
                if (Pending(socket, connection, state, out var owner, out var peer, out _)) ScheduleRetry(owner, peer);
            }
        }

        static bool Resend(WeakReference<ISocket> socketReference, WeakReference<NetworkConnection> connectionReference, PeerState state) {
            if (!Pending(socketReference, connectionReference, state, out var socket, out var connection, out var definitions)) return false;
            SendDefinitions(socket, connection, definitions);
            return true;
        }

        static bool Pending(WeakReference<ISocket> socketReference, WeakReference<NetworkConnection> connectionReference, PeerState state,
            out ISocket socket, out NetworkConnection connection, out BinaryCompressionDefinition[] definitions) {
            socket = null; connection = null; definitions = null;
            if (!socketReference.TryGetTarget(out socket) || !connectionReference.TryGetTarget(out connection) ||
                socket.isDisposed || connection.Closed || !Enabled(socket, connection)) return false;
            definitions = ((BinarySerializer)socket.Options.Serializer).GetCompressionCache(socket, connection).Definitions;
            lock (state) definitions = definitions.Where(d => !state.Known.Contains(d.Id)).ToArray();
            return definitions.Length != 0;
        }

        static void SendControl(ISocket socket, NetworkConnection connection, byte[] frame) {
            if (socket.isDisposed || connection.Closed || frame.Length > socket.Options.MaximumBufferSize) return;
            var control = new BinaryCompressionControl(frame);
            if (socket is WebSockets.WebSocketServer server) { _ = server.SendAsync(connection, control); return; }
            if (socket is WebSockets.WebSocketClient client) { client.Send(control); return; }
            if (socket is MutableTcpServer mutable && connection.Protocol == TcpProtocol.WebSocket) {
                mutable.WebSocket.SendObject(mutable, connection, control);
                return;
            }
            if (socket is UdpServer || socket is UdpClient) { connection.Send(control); return; }
            if (socket is NetworkBase network) network.Send(connection, control);
        }
    }
}
