using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

#if SOCKETJACK_UNITY
namespace SocketJack.Multiplayer.Serialization
#else
namespace SocketJack.Net
#endif
{
    /// <summary>Connection-scoped raw object reconstruction shared by stream and datagram receivers.
    /// Limits include all incomplete objects in this instance, not merely one object.</summary>
    internal sealed class ObjectRebuilder
    {
        private sealed class Entry
        {
            internal int Count, TotalLength, Bytes;
            internal string Signature;
            internal double Started;
            internal readonly Dictionary<int, byte[]> Parts = new Dictionary<int, byte[]>();
        }
        private sealed class Completed
        {
            internal int Count, TotalLength;
            internal string Signature;
            internal double At;
        }
        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly Dictionary<string, Completed> _completed = new Dictionary<string, Completed>(StringComparer.Ordinal);
        private readonly int _maxObjects, _maxParts, _completedLimit;
        private readonly double _timeout;
        private readonly bool _strictDuplicates;
        private readonly Func<double> _clock;
        private long _bytes;
        private double _nextPruneAt = double.PositiveInfinity;
        internal int Count { get { lock (_entries) return _entries.Count; } }
        internal long BufferedBytes { get { lock (_entries) return _bytes; } }

        internal ObjectRebuilder(int maxObjects = 8, int maxParts = 65536, double timeoutSeconds = 30,
            bool strictDuplicates = true, int completedLimit = 0, Func<double> clock = null)
        {
            if (maxObjects < 1 || maxParts < 1 || timeoutSeconds <= 0 || double.IsNaN(timeoutSeconds) || double.IsInfinity(timeoutSeconds) || completedLimit < 0)
                throw new ArgumentOutOfRangeException(nameof(maxObjects));
            _maxObjects = maxObjects; _maxParts = maxParts; _timeout = timeoutSeconds;
            _strictDuplicates = strictDuplicates; _completedLimit = completedLimit;
            _clock = clock ?? (() => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency);
        }

        // Indices are zero based. totalLength == -1 is the legacy stream format without a declared length.
        internal byte[] Add(string id, int count, int index, byte[] data, int maximum,
            int totalLength = -1, string signature = "")
        {
            lock (_entries)
            {
                double now = _clock(); PruneCore(now);
                if (string.IsNullOrEmpty(id) || id.Length > 128 || count < 1 || count > _maxParts || index < 0 || index >= count ||
                    data == null || data.Length == 0 || maximum < 1 || totalLength < -1 || totalLength > maximum ||
                    (totalLength >= 0 && (count > totalLength || data.Length > totalLength)))
                    throw new InvalidDataException("Invalid object fragment.");
                if (_completed.TryGetValue(id, out var completed))
                {
                    if (completed.Count != count || completed.TotalLength != totalLength || completed.Signature != signature)
                        throw new InvalidDataException("Completed object metadata changed.");
                    return null;
                }
                if (!_entries.TryGetValue(id, out var entry))
                {
                    if (_entries.Count >= _maxObjects) throw new InvalidDataException("Too many incomplete objects.");
                    entry = new Entry { Count = count, TotalLength = totalLength, Signature = signature, Started = now };
                }
                if (entry.Count != count || entry.TotalLength != totalLength || entry.Signature != signature)
                    throw new InvalidDataException("Object fragment metadata changed.");
                if (entry.Parts.TryGetValue(index, out var previous))
                {
                    if (_strictDuplicates || !Equal(previous, data)) throw new InvalidDataException("Invalid duplicate object fragment.");
                    return null;
                }
                if (_bytes + data.Length > maximum || (entry.TotalLength >= 0 && entry.Bytes + data.Length > entry.TotalLength))
                    throw new InvalidDataException("Object reconstruction byte limit exceeded.");
                if (!_entries.ContainsKey(id))
                {
                    _entries.Add(id, entry);
                    _nextPruneAt = Math.Min(_nextPruneAt, entry.Started + _timeout);
                }
                entry.Parts.Add(index, data); entry.Bytes += data.Length; _bytes += data.Length;
                if (entry.Parts.Count != entry.Count) return null;
                if (entry.TotalLength >= 0 && entry.Bytes != entry.TotalLength) throw new InvalidDataException("Object length mismatch.");
                var output = new byte[entry.Bytes]; int offset = 0;
                for (int partIndex = 0; partIndex < entry.Count; partIndex++)
                {
                    var part = entry.Parts[partIndex]; Buffer.BlockCopy(part, 0, output, offset, part.Length); offset += part.Length;
                }
                _entries.Remove(id); _bytes -= entry.Bytes;
                if (_completedLimit > 0)
                {
                    if (_completed.Count >= _completedLimit)
                    {
                        string oldest = null; double at = double.MaxValue;
                        foreach (var pair in _completed) if (pair.Value.At < at) { oldest = pair.Key; at = pair.Value.At; }
                        if (oldest != null) _completed.Remove(oldest);
                    }
                    _completed[id] = new Completed { Count = count, TotalLength = totalLength, Signature = signature, At = now };
                    _nextPruneAt = Math.Min(_nextPruneAt, now + _timeout);
                }
                return output;
            }
        }

        internal void Prune() { lock (_entries) PruneCore(_clock()); }
        private void PruneCore(double now)
        {
            if (now <= _nextPruneAt) return;
            var expired = new List<string>();
            foreach (var pair in _entries) if (now - pair.Value.Started > _timeout) expired.Add(pair.Key);
            foreach (string id in expired) { _bytes -= _entries[id].Bytes; _entries.Remove(id); }
            expired.Clear();
            foreach (var pair in _completed) if (now - pair.Value.At > _timeout) expired.Add(pair.Key);
            foreach (string id in expired) _completed.Remove(id);
            _nextPruneAt = double.PositiveInfinity;
            foreach (var entry in _entries.Values) _nextPruneAt = Math.Min(_nextPruneAt, entry.Started + _timeout);
            foreach (var completed in _completed.Values) _nextPruneAt = Math.Min(_nextPruneAt, completed.At + _timeout);
        }
        internal void Clear() { lock (_entries) { _entries.Clear(); _completed.Clear(); _bytes = 0; _nextPruneAt = double.PositiveInfinity; } }
        private static bool Equal(byte[] a, byte[] b)
        { if (a.Length != b.Length) return false; for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false; return true; }
    }
}
