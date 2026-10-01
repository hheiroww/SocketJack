using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using SocketJack.Extensions;

namespace SocketJack {
    /// <summary>
    /// Segments add support transfering objects above the network interface card's maximum transmission unit.
    /// </summary>
    public class Segment {

        public static ConcurrentDictionary<string, List<Segment>> Cache = new ConcurrentDictionary<string, List<Segment>>();

        public static bool SegmentComplete(Segment segment) {
            return Cache.TryGetValue(segment.SID, out var segments) && segments.Count == segment.Count;
        }

        public static byte[] Rebuild(Segment segment) {
            if (segment == null || segment.Count < 1 || segment.Count > int.MaxValue ||
                !Cache.TryGetValue(segment.SID, out var source) || source.Count != segment.Count)
                throw new InvalidDataException("Incomplete segment object.");
            var ordered = new Segment[source.Count]; var lengths = new int[source.Count]; int total = 0;
            foreach (var part in source) {
                if (part == null || part.SID != segment.SID || part.Count != segment.Count || part.Index < 1 || part.Index > ordered.Length || ordered[part.Index - 1] != null)
                    throw new InvalidDataException("Invalid or duplicate segment index.");
                int index = (int)part.Index - 1;
                ordered[index] = part; lengths[index] = DecodedLength(part.Data); total = checked(total + lengths[index]);
            }
            // Exactly one final byte array; decode each fragment directly into its indexed slice.
            var combined = new byte[total]; int offset = 0;
            for (int i = 0; i < ordered.Length; i++) {
                if (!Convert.TryFromBase64String(ordered[i].Data, combined.AsSpan(offset, lengths[i]), out int written) || written != lengths[i])
                    throw new FormatException("Invalid base64 segment.");
                offset += written;
            }
            ((ICollection<KeyValuePair<string, List<Segment>>>)Cache).Remove(new KeyValuePair<string, List<Segment>>(segment.SID, source));
            return combined;
        }
        private static int DecodedLength(string data) {
            if (data == null) throw new FormatException("Missing segment data.");
            int count = data.Length, padding = 0;
            if (data.AsSpan().IndexOfAny(' ', '\t', '\r') >= 0 || data.AsSpan().IndexOf('\n') >= 0) {
                count = 0; char last = '\0', previous = '\0';
                foreach (char c in data) if (c != ' ' && c != '\t' && c != '\r' && c != '\n') { count++; previous = last; last = c; }
                if (last == '=') padding++; if (previous == '=') padding++;
            } else {
                if (count > 0 && data[count - 1] == '=') padding++;
                if (count > 1 && data[count - 2] == '=') padding++;
            }
            if (count % 4 != 0) throw new FormatException("Invalid base64 segment length.");
            return checked(count / 4 * 3 - padding);
        }

        public Segment() {

        }

        public Segment(string SID, byte[] Data, int Index, int Count) {
            this.SID = SID;
            // Segment payloads can contain compressed or otherwise arbitrary bytes.
            // UTF-8 string conversion is lossy for those values, so use a byte-safe
            // representation while the Segment itself is serialized as JSON.
            this.Data = Convert.ToBase64String(Data);
            this.Index = Index;
            this.Count = Count;
        }

        public string Data { get; set; }

        public long Index { get; set; } = 0L;

        public long Count { get; set; } = 0L;

        public string SID { get; set; }

    }
}
