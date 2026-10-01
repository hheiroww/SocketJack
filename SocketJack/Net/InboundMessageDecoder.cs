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
                var wrapper = socket.Options.Serializer.Deserialize(bytes) ?? throw InboundMessageSecurity.Denied();
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
        private sealed class Entry {
            internal long Count;
            internal DateTime Started = DateTime.UtcNow;
            internal Dictionary<long, byte[]> Parts = new Dictionary<long, byte[]>();
            internal int Bytes;
        }
        private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private long bytes;
        internal byte[] Add(Segment segment, int maximum) {
            lock (entries) {
                foreach (string expired in entries.Where(p => DateTime.UtcNow - p.Value.Started > TimeSpan.FromSeconds(30)).Select(p => p.Key).ToArray()) {
                    bytes -= entries[expired].Bytes; entries.Remove(expired);
                }
                if (segment == null || string.IsNullOrEmpty(segment.SID) || segment.SID.Length > 128 ||
                    segment.Count < 1 || segment.Count > 65536 || segment.Index < 1 || segment.Index > segment.Count ||
                    segment.Data == null || segment.Data.Length > ((long)maximum + 2) / 3 * 4) throw InboundMessageSecurity.Denied();
                byte[] data = Convert.FromBase64String(segment.Data);
                if (data.Length == 0 || bytes + data.Length > maximum) throw InboundMessageSecurity.Denied();
                if (!entries.TryGetValue(segment.SID, out var entry)) {
                    if (entries.Count >= 8) throw InboundMessageSecurity.Denied();
                    entries.Add(segment.SID, entry = new Entry { Count = segment.Count });
                }
                if (entry.Count != segment.Count || entry.Parts.ContainsKey(segment.Index)) throw InboundMessageSecurity.Denied();
                entry.Parts.Add(segment.Index, data); entry.Bytes += data.Length; bytes += data.Length;
                if (entry.Parts.Count != entry.Count) return null;
                var output = new byte[entry.Bytes]; int offset = 0;
                for (long index = 1; index <= entry.Count; index++) {
                    var part = entry.Parts[index]; Buffer.BlockCopy(part, 0, output, offset, part.Length); offset += part.Length;
                }
                entries.Remove(segment.SID); bytes -= entry.Bytes;
                return output;
            }
        }
        internal void Clear() { lock (entries) { entries.Clear(); bytes = 0; } }
    }
}
