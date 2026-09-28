using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using SocketJack.Extensions;

namespace SocketJack {
    /// <summary>
    /// Segments add support transfering objects above the network interface card's maximum transmission unit.
    /// </summary>
    public class Segment {

        public static ConcurrentDictionary<string, List<Segment>> Cache = new ConcurrentDictionary<string, List<Segment>>();

        public static bool SegmentComplete(Segment segment) {
            if (Cache.ContainsKey(segment.SID)) {
                return Cache[segment.SID].Count == segment.Count;
            } else {
                return false;
            }
        }

        public static byte[] Rebuild(Segment segment) {
            byte[] Combined = new byte[] { };
            var orderedSegments = Cache[segment.SID].OrderBy(s => s.Index).ToList();
            for (int i = 0, loopTo = orderedSegments.Count - 1; i <= loopTo; i++) {
                var s = orderedSegments[i];
                byte[] Data = Convert.FromBase64String(s.Data);
                Combined = ByteExtensions.Concat(new[] { Combined, Data });
            }
            Cache.Remove(segment.SID);
            return Combined;
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
