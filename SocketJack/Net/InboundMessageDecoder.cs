using SocketJack.Net.P2P;
using SocketJack.Serialization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SocketJack.Net {
    internal static class InboundMessageDecoder {
        internal static object Read(ISocket socket, NetworkConnection connection, byte[] bytes, bool allowSegments = true, Type expectedType = null) {
            using (InboundMessageSecurity.Enter(socket, connection)) {
                SafeModeHandshake.RequireVerified(connection, socket.Options);
                if (bytes == null || bytes.Length > socket.Options.MaximumBufferSize) throw InboundMessageSecurity.Denied();
                Wrapper wrapper;
                if (!BinaryNetworkCompression.TryDeserialize(socket, connection, bytes, out wrapper)) {
                    bytes = BinaryNetworkCompression.Decode(socket, connection, bytes);
                    if (bytes == null) return null; // Dictionary updates and acknowledgements are protocol-only.
                    // WebSocket legacy redirects use an unwrapped, inert JSON envelope.
                    if (socket.Options.Serializer is Serialization.Json.JsonSerializer) {
                        using (var document = JsonDocument.Parse(bytes)) {
                            var root = document.RootElement;
                            if (root.ValueKind != JsonValueKind.Object) throw InboundMessageSecurity.Denied();
                            var names = new HashSet<string>(StringComparer.Ordinal);
                            foreach (var field in root.EnumerateObject()) if (!names.Add(field.Name)) throw InboundMessageSecurity.Denied();
                            if (root.TryGetProperty("Value", out _) && root.TryGetProperty("Recipient", out _)) {
                                if (expectedType != null && expectedType != typeof(PeerRedirect)) throw InboundMessageSecurity.Denied();
                                InboundMessageSecurity.RequireType(socket, connection, typeof(PeerRedirect));
                                return socket.Options.Serializer.DeserializeRedirect(socket, bytes);
                            }
                        }
                    }
                    wrapper = socket.Options.Serializer.Deserialize(bytes) ?? throw InboundMessageSecurity.Denied();
                }
                Type type = wrapper.GetValueType(socket);
                if (expectedType != null && type != expectedType) throw InboundMessageSecurity.Denied();
                InboundMessageSecurity.RequireType(socket, connection, type);
                if (type == typeof(Segment)) {
                    if (!allowSegments) throw InboundMessageSecurity.Denied();
                    return ReadSegment(socket, connection, (Segment)wrapper.Unwrap(type, socket));
                }
                // Keep DLL approval before authorization callbacks. Unwrap validates the graph
                // again after authorization and before setters; it avoids the extra identical
                // allowed-type fingerprint inside that validation sequence.
                return wrapper.Unwrap(type, socket);
            }
        }
        internal static object ReadSegment(ISocket socket, NetworkConnection connection, Segment segment) {
            if (connection == null) throw InboundMessageSecurity.Denied();
            byte[] rebuilt = connection.InboundSegments.Add(segment, socket.Options.MaximumBufferSize);
            return rebuilt == null ? null : Read(socket, connection, rebuilt, false);
        }
    }

    /// <summary>Per-connection, bounded legacy reassembly. A segment ID from another client never joins this buffer.</summary>
    internal sealed class InboundSegmentBuffer {
        private readonly ObjectRebuilder rebuilder = new ObjectRebuilder();
        internal byte[] Add(Segment segment, int maximum) {
            lock (rebuilder) {
                rebuilder.Prune();
                if (segment == null || string.IsNullOrEmpty(segment.SID) || segment.SID.Length > 128 ||
                    segment.Count < 1 || segment.Count > 65536 || segment.Index < 1 || segment.Index > segment.Count ||
                    segment.Data == null || segment.Data.Length > ((long)maximum + 2) / 3 * 4) throw InboundMessageSecurity.Denied();
                byte[] data = Convert.FromBase64String(segment.Data);
                try { return rebuilder.Add(segment.SID, (int)segment.Count, (int)segment.Index - 1, data, maximum); }
                catch (InvalidDataException) { throw InboundMessageSecurity.Denied(); }
            }
        }
        internal void Clear() { lock (rebuilder) rebuilder.Clear(); }
    }
}
