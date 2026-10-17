using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace SocketJack.Serialization {
    /// <summary>Process-wide admission accounting for retained compression state, including peer metadata.</summary>
    internal static class BinaryCompressionMemory {
        static long retained;
        internal static long Limit { get; } = ReadPhysicalMemory() / 16;
        internal static long RetainedBytes => Interlocked.Read(ref retained);

        internal static bool TryReserve(long bytes) {
            if (bytes < 0) throw new ArgumentOutOfRangeException(nameof(bytes));
            while (true) {
                long before = Interlocked.Read(ref retained);
                if (bytes > Limit - before) return false;
                if (Interlocked.CompareExchange(ref retained, before + bytes, before) == before) return true;
            }
        }

        internal static void Release(long bytes) {
            if (bytes < 0) throw new ArgumentOutOfRangeException(nameof(bytes));
            while (true) {
                long before = Interlocked.Read(ref retained);
                if (bytes > before) throw new InvalidOperationException("Compression memory accounting underflow.");
                if (Interlocked.CompareExchange(ref retained, before - bytes, before) == before) return;
            }
        }

        static long ReadPhysicalMemory() {
            try {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
                    var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
                    if (GlobalMemoryStatusEx(ref status) && status.TotalPhysical <= long.MaxValue)
                        return (long)status.TotalPhysical;
                } else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) {
                    foreach (string line in File.ReadLines("/proc/meminfo")) {
                        if (!line.StartsWith("MemTotal:", StringComparison.Ordinal)) continue;
                        string[] fields = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (fields.Length >= 3 && fields[2] == "kB" &&
                            long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out long kb) &&
                            kb > 0 && kb <= long.MaxValue / 1024) return kb * 1024;
                    }
                }
            } catch { /* Unknown platforms have no adaptive repository rather than an unsafe guessed cap. */ }
            return 0;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MemoryStatus {
            internal uint Length, Load;
            internal ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile;
            internal ulong TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    }

    internal sealed class BinaryCompressionDefinition {
        internal ulong Id;
        internal byte[] Data;
        // Only the repository sets this on its owned, already-verified immutable storage. Avoid
        // rehashing the entire accumulated dictionary on every ordinary network message.
        internal bool Validated;
        internal BinaryCompressionDefinition() { }
        internal BinaryCompressionDefinition(ulong id, byte[] data) { Id = id; Data = data; }
    }

    /// <summary>
    /// Adaptive, byte-bounded differential repository. Candidates survive unrelated messages and
    /// expire by inactivity or memory pressure. A cheap scout wakes learning when content repeats;
    /// unsuccessful deep probes back off instead of scanning incompressible objects repeatedly.
    /// Acknowledged definitions remain immutable for the lifetime of the connection.
    /// </summary>
    internal sealed class BinaryCompressionCache : IDisposable {
        // Zero identifies adaptive retention to callers of the old diagnostic property.
        internal const int HistoryLength = 0;
        const long DefaultLimit = 4 * 1024 * 1024;
        const int BaseCost = 2048, CandidateCost = 256, DefinitionCost = 192, IndexSlotCost = 128;
        const int MaximumMessageChunks = 512, ScoutSlots = 1024, ScoutCost = ScoutSlots * 16 + 128;
        const int MaximumProbeInterval = 64;
        const int MaximumPlanBytes = 512 * 1024;
        const long CandidateLifetimeMilliseconds = 120000;
        static readonly ulong[] GearTable = CreateGearTable();
        readonly object sync = new object();
        readonly long maximumBytes;
        readonly Func<ulong, byte[]> decoderLookup;
        Dictionary<ulong, BinaryCompressionDefinition> definitions;
        Dictionary<ulong, Candidate> candidates;
        LinkedList<Candidate> recency;
        Dictionary<PlanKey, BinaryCompressionCodec.EncodingPlan> plans;
        LinkedList<BinaryCompressionCodec.EncodingPlan> planRecency;
        BinaryCompressionCodec.DictionaryIndex index;
        ulong[] scoutHashes;
        long[] scoutSequences;
        int candidateHighWater, probeInterval = MaximumProbeInterval, lastScoutEvidence, learningPasses, usefulMessagesSinceLearning, rotatingProbe;
        bool lastLocalRepetition, lastEncodingUseful;
        long sequence, retained, candidateBytes, nextProbe = MaximumProbeInterval, encodingAttempts, bypassedMessages, planBytes, planHits, observationPasses;
        ulong anchor1, anchor2, anchor3;
        int planKeyHighWater;
        bool disposed;

        readonly struct PlanKey : IEquatable<PlanKey> {
            readonly int length, anchor;
            readonly ulong hash;
            internal PlanKey(int length, int anchor, ulong hash) { this.length = length; this.anchor = anchor; this.hash = hash; }
            public bool Equals(PlanKey other) => length == other.length && anchor == other.anchor && hash == other.hash;
            public override bool Equals(object value) => value is PlanKey other && Equals(other);
            public override int GetHashCode() => unchecked((length * 397 ^ anchor) * 397 ^ (int)hash ^ (int)(hash >> 32));
        }

        sealed class Candidate {
            internal ulong Id;
            internal byte[] Data;
            internal long LastSeen, LastSequence;
            internal LinkedListNode<Candidate> Node;
        }

        internal BinaryCompressionCache() : this(DefaultLimit) { }
        internal BinaryCompressionCache(long maximumBytes) {
            decoderLookup = LookupDefinition;
            if (maximumBytes < 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
            this.maximumBytes = Math.Min(DefaultLimit, Math.Min(maximumBytes, BinaryCompressionMemory.Limit));
            if (this.maximumBytes >= BaseCost && BinaryCompressionMemory.TryReserve(BaseCost)) {
                retained = BaseCost;
                definitions = new Dictionary<ulong, BinaryCompressionDefinition>();
                candidates = new Dictionary<ulong, Candidate>();
                recency = new LinkedList<Candidate>();
                plans = new Dictionary<PlanKey, BinaryCompressionCodec.EncodingPlan>();
                planRecency = new LinkedList<BinaryCompressionCodec.EncodingPlan>();
            }
        }
        ~BinaryCompressionCache() { Dispose(false); }

        internal long RetainedBytes { get { lock (sync) return retained; } }
        internal int DictionaryCount { get { lock (sync) return definitions?.Count ?? 0; } }
        internal int CandidateCount { get { lock (sync) return candidates?.Count ?? 0; } }
        internal long EncodingAttempts { get { lock (sync) return encodingAttempts; } }
        internal long BypassedMessages { get { lock (sync) return bypassedMessages; } }
        internal int ProbeInterval { get { lock (sync) return probeInterval; } }
        internal int PlanCount { get { lock (sync) return planRecency?.Count ?? 0; } }
        internal long PlanHits { get { lock (sync) return planHits; } }
        internal long LearningPasses { get { lock (sync) return observationPasses; } }
        // A newly acknowledged shortcut can make the next object profitable even when all
        // earlier attempts included inline definitions and therefore fell back to raw bytes.
        internal void RequestProbe() { lock (sync) nextProbe = sequence + 1; }
        internal BinaryCompressionDefinition[] Definitions { get { lock (sync) return CopyDefinitions(int.MaxValue); } }

        internal BinaryCompressionDefinition[] Snapshot(int maximumBytes) {
            if (maximumBytes < 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
            lock (sync) return CopyDefinitions(maximumBytes);
        }

        BinaryCompressionDefinition[] CopyDefinitions(int limit) {
            if (disposed || definitions == null) return Array.Empty<BinaryCompressionDefinition>();
            var result = new List<BinaryCompressionDefinition>();
            long size = 0;
            foreach (var definition in definitions.Values) {
                long cost = definition.Data.Length + 16L;
                if (cost > limit - size) continue;
                result.Add(definition); size += cost;
            }
            return result.ToArray();
        }

        internal bool TryGet(ulong id, out byte[] bytes) {
            lock (sync) {
                if (!disposed && definitions != null && definitions.TryGetValue(id, out var definition)) { bytes = definition.Data; return true; }
                bytes = null; return false;
            }
        }

        // Hold the repository lock once for a frame instead of once for each dictionary token.
        internal byte[] Decode(byte[] frame, int maximumBytes, out BinaryCompressionDefinition[] inlineDefinitions) {
            lock (sync) return BinaryCompressionCodec.Decode(frame, maximumBytes, decoderLookup, out inlineDefinitions);
        }
        internal bool TryReadSegments(byte[] frame, int maximumBytes, ReadOnlyMemory<byte>[] segments, out int count, out int length) {
            lock (sync) return BinaryCompressionCodec.TryReadSegments(frame, maximumBytes, decoderLookup, segments, out count, out length);
        }
        byte[] LookupDefinition(ulong id) => !disposed && definitions != null && definitions.TryGetValue(id, out var definition) ? definition.Data : null;

        internal bool Import(BinaryCompressionDefinition definition) {
            if (!BinaryCompressionCodec.ValidDefinition(definition)) return false;
            lock (sync) return !disposed && retained != 0 && ImportCore(definition);
        }

        bool ImportCore(BinaryCompressionDefinition definition) {
            if (definitions.TryGetValue(definition.Id, out var present))
                return BinaryCompressionCodec.Equal(present.Data, definition.Data);
            if (definitions.Count >= BinaryCompressionCodec.MaximumDefinitions) return false;
            long cost = DefinitionCost + definition.Data.Length;
            if (!Charge(cost)) { EvictReclaimable(cost); if (!Charge(cost)) return false; }
            try {
                var owned = new BinaryCompressionDefinition(definition.Id, (byte[])definition.Data.Clone()) { Validated = true };
                definitions.Add(owned.Id, owned);
                if (index != null) index.Add(owned);
                return true;
            } catch { Credit(cost); throw; }
        }

        /// <summary>Fast network entry point; the immutable dictionary index is reused across objects.</summary>
        internal byte[] Encode(byte[] raw, ISet<ulong> knownIds, out BinaryCompressionDefinition[] added) {
            if (raw == null) throw new ArgumentNullException(nameof(raw));
            lock (sync) {
                added = Array.Empty<BinaryCompressionDefinition>();
                if (disposed || retained == 0 || raw.Length < 256) { bypassedMessages++; return raw; }
                sequence++;
                int evidence = Scout(raw);
                bool strongerScout = evidence >= lastScoutEvidence + 2;
                if (strongerScout && index != null) learningPasses = Math.Max(learningPasses, 2);
                // Existing savings must not hide newly repeating fields. Periodically compare
                // otherwise profitable objects too, with a second pass to confirm exact bytes.
                if (lastEncodingUseful && index != null && usefulMessagesSinceLearning >= 64)
                    learningPasses = Math.Max(learningPasses, 2);
                bool canReference = definitions.Count != 0 && knownIds != null && knownIds.Count != 0;
                // A verified plan already supplies a complete profitable encoding. Avoid the
                // local-LZ eligibility scan on this common path; changed spans remain literals.
                if (canReference && plans.Count != 0 && lastEncodingUseful && learningPasses == 0) {
                    byte[] planned = TryPlan(raw, knownIds);
                    if (planned != null) {
                        encodingAttempts++; usefulMessagesSinceLearning++;
                        lastScoutEvidence = evidence; lastLocalRepetition = false;
                        probeInterval = 1; nextProbe = sequence + 1;
                        return planned;
                    }
                }
                bool local = BinaryCompressionCodec.HasLocalRepetition(raw);
                bool signal = evidence >= 2 || local;
                bool wake = strongerScout || (local && !lastLocalRepetition);
                lastScoutEvidence = evidence; lastLocalRepetition = local;
                bool due = sequence >= nextProbe;
                // Do not JIT or run the general encoder merely to discover that a first
                // high-entropy object cannot shrink. Recurrence seeds and confirms exact
                // candidates; periodic rotating scouts discover less obvious repetition.
                // Dictionary references become useful after this peer owns a shortcut.
                if (!local && !canReference) {
                    if (due && !signal && learningPasses == 0) {
                        if (RotatingScout(raw) >= 2) { signal = true; learningPasses = 2; }
                        nextProbe = sequence + MaximumProbeInterval;
                    }
                    if (signal || learningPasses != 0) {
                        added = ObserveCore(raw);
                        if (learningPasses != 0) learningPasses--;
                        usefulMessagesSinceLearning = 0;
                        probeInterval = signal ? 1 : MaximumProbeInterval;
                        nextProbe = sequence + probeInterval;
                    }
                    bypassedMessages++;
                    return raw;
                }
                // Stable small fields inside otherwise fresh random data are not evidence of a
                // worthwhile codec pass. Failed attempts back off even when those fields recur.
                if (!due && !wake && learningPasses == 0 && !(signal && lastEncodingUseful) && encodingAttempts != 0) { bypassedMessages++; return raw; }
                encodingAttempts++;
                EnsureIndex();
                BinaryCompressionCodec.EncodingPlan learnedPlan = null;
                byte[] encoded = TryPlan(raw, knownIds);
                if (encoded == null) encoded = BinaryCompressionCodec.EncodeIndexed(raw, index, knownIds, out learnedPlan);
                bool useful = encoded.Length < raw.Length - raw.Length / 8;
                // Local LZ already handles runs without creating definitions and distributing bytes
                // that every client can reconstruct from this message alone. A useful dictionary
                // similarly avoids hashing and re-observing all its already learned chunks.
                if (!useful || learningPasses != 0) {
                    added = ObserveCore(raw);
                    if (learningPasses != 0) learningPasses--;
                    usefulMessagesSinceLearning = 0;
                    EnsureIndex();
                    if (added.Length != 0) encoded = BinaryCompressionCodec.EncodeIndexed(raw, index, knownIds, out learnedPlan);
                } else usefulMessagesSinceLearning++;
                if (learnedPlan != null) StorePlan(raw.Length, learnedPlan);
                lastEncodingUseful = encoded.Length < raw.Length - raw.Length / 8;
                // A newly recurring pattern gets one immediate confirmation pass: its first
                // observation seeds candidates, and the next can promote exact matching bytes.
                if (lastEncodingUseful || added.Length != 0 || wake) probeInterval = 1;
                else probeInterval = Math.Min(MaximumProbeInterval, probeInterval * 2);
                nextProbe = sequence + probeInterval;
                if (ReferenceEquals(encoded, raw)) bypassedMessages++;
                return encoded;
            }
        }

        int Scout(byte[] raw) {
            if (scoutHashes == null) {
                if (!Charge(ScoutCost)) return 0;
                scoutHashes = new ulong[ScoutSlots]; scoutSequences = new long[ScoutSlots];
            }
            int evidence = 0;
            // Samples avoid the changing object header. A matching hash is only a hint: all bytes
            // are compared again before a definition or compression reference can be emitted.
            int range = raw.Length - 64;
            for (int i = 1; i <= 8; i++) {
                int position = (int)((long)range * i / 9);
                ulong hash = ScoutHash(raw, position);
                if (i == 2) anchor1 = hash;
                else if (i == 4) anchor2 = hash;
                else if (i == 6) anchor3 = hash;
                int slot = (int)(hash & (ScoutSlots - 1));
                if (scoutHashes[slot] == hash && scoutSequences[slot] != sequence && scoutSequences[slot] != 0) evidence++;
                scoutHashes[slot] = hash; scoutSequences[slot] = sequence;
            }
            return evidence;
        }

        int RotatingScout(byte[] raw) {
            if (scoutHashes == null) return 0;
            int evidence = 0, range = raw.Length - 64;
            // Keep each alternate grid for two probes so repeats can confirm it, then move
            // between the fixed samples. This path retains hashes, never whole random objects.
            int grid = (rotatingProbe++ / 2) & 31;
            int shift = (int)((long)range * (grid * 2 + 1) / 64);
            for (int i = 1; i <= 8; i++) {
                int position = (int)(((long)range * i / 9 + shift) % range);
                ulong hash = ScoutHash(raw, position);
                int slot = (int)(hash & (ScoutSlots - 1));
                if (scoutHashes[slot] == hash && scoutSequences[slot] != sequence && scoutSequences[slot] != 0) evidence++;
                scoutHashes[slot] = hash; scoutSequences[slot] = sequence;
            }
            return evidence;
        }

        // These sampled words are hints only. Dictionary IDs still hash every byte, and
        // plans verify all referenced spans before emitting any shortcut.
        static ulong ScoutHash(byte[] raw, int position) {
            ulong a = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(position, 8));
            ulong b = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(position + 16, 8));
            ulong c = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(position + 32, 8));
            ulong d = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(position + 56, 8));
            ulong hash = a ^ ((b << 13) | (b >> 51)) ^ ((c << 29) | (c >> 35)) ^ ((d << 47) | (d >> 17));
            hash ^= hash >> 33; hash = unchecked(hash * 0xff51afd7ed558ccdUL);
            hash ^= hash >> 33; return hash;
        }

        PlanKey KeyFor(int length, int anchor) => new PlanKey(length, anchor, anchor == 0 ? anchor1 : anchor == 1 ? anchor2 : anchor3);
        static PlanKey KeyFor(BinaryCompressionCodec.EncodingPlan plan, int anchor) =>
            new PlanKey(plan.RawLength, anchor, anchor == 0 ? plan.Anchor1 : anchor == 1 ? plan.Anchor2 : plan.Anchor3);

        byte[] TryPlan(byte[] raw, ISet<ulong> knownIds) {
            BinaryCompressionCodec.EncodingPlan first = null, second = null;
            for (int anchor = 0; anchor < 3; anchor++) {
                if (!plans.TryGetValue(KeyFor(raw.Length, anchor), out var plan) || ReferenceEquals(plan, first) || ReferenceEquals(plan, second)) continue;
                if (first == null) first = plan; else second = plan;
                if (plan.Version != definitions.Count) { RemovePlan(plan); continue; }
                byte[] encoded = BinaryCompressionCodec.EncodePlan(raw, plan, knownIds);
                if (encoded == null) continue;
                planRecency.Remove(plan.Node); planRecency.AddLast(plan.Node); planHits++;
                return encoded;
            }
            return null;
        }

        void StorePlan(int rawLength, BinaryCompressionCodec.EncodingPlan plan) {
            long cost = 256L + plan.Segments.Length * 24L;
            long limit = Math.Min(MaximumPlanBytes, maximumBytes / 4);
            // High-water charging includes dictionary backing slots even after old keys are removed.
            int growth = Math.Max(0, plans.Count + 3 - planKeyHighWater);
            long indexCost = growth * IndexSlotCost;
            if (cost + indexCost > limit) return;
            while (planRecency.First != null && (planBytes + (long)planKeyHighWater * IndexSlotCost + cost + indexCost > limit || cost + indexCost > maximumBytes - retained))
                RemovePlan(planRecency.First.Value);
            if (planBytes + (long)planKeyHighWater * IndexSlotCost + cost + indexCost > limit) return;
            bool charged = Charge(cost + indexCost);
            if (!charged) { EvictReclaimable(cost + indexCost); charged = Charge(cost + indexCost); }
            if (!charged) return;
            planKeyHighWater += growth;
            plan.RawLength = rawLength; plan.Version = definitions.Count;
            plan.Anchor1 = anchor1; plan.Anchor2 = anchor2; plan.Anchor3 = anchor3; plan.Cost = cost;
            plan.Node = planRecency.AddLast(plan); planBytes += cost;
            for (int anchor = 0; anchor < 3; anchor++) plans[KeyFor(plan, anchor)] = plan;
        }

        void RemovePlan(BinaryCompressionCodec.EncodingPlan plan) {
            for (int anchor = 0; anchor < 3; anchor++) {
                var key = KeyFor(plan, anchor);
                if (plans.TryGetValue(key, out var owner) && ReferenceEquals(owner, plan)) plans.Remove(key);
            }
            planRecency.Remove(plan.Node); planBytes -= plan.Cost; Credit(plan.Cost);
        }

        void EnsureIndex() {
            if (index != null || definitions.Count == 0) return;
            if (!Charge(BinaryCompressionCodec.DictionaryIndex.RetainedCost)) return;
            try {
                index = new BinaryCompressionCodec.DictionaryIndex();
                foreach (var definition in definitions.Values) index.Add(definition);
            } catch { Credit(BinaryCompressionCodec.DictionaryIndex.RetainedCost); index = null; throw; }
        }

        // Kept as a deterministic learning entry point for callers explicitly requesting observation.
        internal BinaryCompressionDefinition[] Observe(byte[] raw) {
            if (raw == null) throw new ArgumentNullException(nameof(raw));
            lock (sync) {
                if (disposed || retained == 0) return Array.Empty<BinaryCompressionDefinition>();
                sequence++;
                return ObserveCore(raw);
            }
        }

        BinaryCompressionDefinition[] ObserveCore(byte[] raw) {
            observationPasses++;
            long now = (long)(System.Diagnostics.Stopwatch.GetTimestamp() * (1000.0 / System.Diagnostics.Stopwatch.Frequency));
            while (recency.First != null && now - recency.First.Value.LastSeen > CandidateLifetimeMilliseconds)
                RemoveCandidate(recency.First.Value);
            List<BinaryCompressionDefinition> added = null;
            int position = 0, chunks = 0;
            while (position < raw.Length && chunks++ < MaximumMessageChunks) {
                int length = ChunkLength(raw, position);
                if (length >= BinaryCompressionCodec.MinimumDefinitionLength) {
                    ulong id = BinaryCompressionCodec.Hash(raw, position, length);
                    if (!definitions.ContainsKey(id)) {
                        if (candidates.TryGetValue(id, out var candidate)) {
                            if (candidate.LastSequence != sequence && BinaryCompressionCodec.Equal(candidate.Data, raw, position, length)) {
                                candidate.LastSequence = sequence; candidate.LastSeen = now;
                                recency.Remove(candidate.Node); recency.AddLast(candidate.Node);
                                // Remove the candidate first so a tight repository can promote storage.
                                byte[] data = candidate.Data;
                                RemoveCandidate(candidate);
                                if (ImportCore(new BinaryCompressionDefinition(id, data))) {
                                    if (added == null) added = new List<BinaryCompressionDefinition>();
                                    added.Add(definitions[id]);
                                }
                            }
                        } else {
                            int peakGrowth = Math.Max(0, candidates.Count + 1 - candidateHighWater);
                            long cost = CandidateCost + length + peakGrowth * IndexSlotCost;
                            long target = Math.Max(0, maximumBytes / 2 - cost);
                            while (candidateBytes > target && recency.First != null) RemoveCandidate(recency.First.Value);
                            bool charged = Charge(cost);
                            if (!charged) { EvictReclaimable(cost); charged = Charge(cost); }
                            if (charged) {
                                var bytes = new byte[length]; Buffer.BlockCopy(raw, position, bytes, 0, length);
                                candidate = new Candidate { Id = id, Data = bytes, LastSequence = sequence, LastSeen = now };
                                candidate.Node = recency.AddLast(candidate); candidates.Add(id, candidate);
                                candidateBytes += CandidateCost + length; candidateHighWater += peakGrowth;
                            }
                        }
                    }
                }
                position += length;
            }
            return added == null ? Array.Empty<BinaryCompressionDefinition>() : added.ToArray();
        }

        static int ChunkLength(byte[] raw, int start) {
            int end = start + Math.Min(BinaryCompressionCodec.MaximumDefinitionLength, raw.Length - start);
            ulong gear = 0;
            for (int p = start; p < end; p++) {
                gear = unchecked((gear << 1) + GearTable[raw[p]]);
                int length = p - start + 1;
                if (length >= BinaryCompressionCodec.MinimumDefinitionLength && (gear & 1023UL) == 0) return length;
            }
            return end - start;
        }
        static ulong[] CreateGearTable() {
            var table = new ulong[256];
            for (int i = 0; i < table.Length; i++) {
                ulong x = (ulong)i + 0x9e3779b97f4a7c15UL;
                x = unchecked((x ^ (x >> 30)) * 0xbf58476d1ce4e5b9UL);
                x = unchecked((x ^ (x >> 27)) * 0x94d049bb133111ebUL);
                table[i] = x ^ (x >> 31);
            }
            return table;
        }

        void EvictReclaimable(long needed) {
            while (needed > maximumBytes - retained && recency.First != null) RemoveCandidate(recency.First.Value);
            while (needed > maximumBytes - retained && planRecency.First != null) RemovePlan(planRecency.First.Value);
        }
        void RemoveCandidate(Candidate candidate) {
            candidates.Remove(candidate.Id); recency.Remove(candidate.Node);
            long cost = CandidateCost + candidate.Data.Length;
            candidateBytes -= cost; Credit(cost);
            // Dictionary backing tables retain their high-water capacity until disposal. Its
            // separately charged slots cannot be reclaimed merely by removing candidate objects.
        }
        bool Charge(long bytes) {
            if (retained == 0 || bytes > maximumBytes - retained || !BinaryCompressionMemory.TryReserve(bytes)) return false;
            retained += bytes; return true;
        }
        void Credit(long bytes) { retained -= bytes; BinaryCompressionMemory.Release(bytes); }
        public void Dispose() { Dispose(true); GC.SuppressFinalize(this); }
        void Dispose(bool disposing) {
            lock (sync) {
                if (disposed) return;
                disposed = true;
                definitions = null; candidates = null; recency = null; plans = null; planRecency = null; index = null; scoutHashes = null; scoutSequences = null;
                long released = retained; retained = 0;
                if (released != 0) BinaryCompressionMemory.Release(released);
            }
        }
    }

    /// <summary>
    /// SBC/version 1: magic (S,B,C,1), command, then unsigned variable-length counts/lengths.
    /// Data: original length, inline definition list, and literal(0), overlapping LZ copy(1), or
    /// dictionary slice(2) tokens. Updates (2) contain definitions, acknowledgements (3) contain IDs.
    /// IDs are little-endian content hashes. Every reference is independently bounds checked.
    /// </summary>
    internal static class BinaryCompressionCodec {
        internal const int MinimumDefinitionLength = 64, MaximumDefinitionLength = 1024, MaximumDefinitions = 4096;
        const int HashSlots = 4096, DictionarySlots = 4096;

        internal static bool HasMagic(byte[] bytes) => bytes != null && bytes.Length >= 4 &&
            bytes[0] == 'S' && bytes[1] == 'B' && bytes[2] == 'C' && bytes[3] == 1;

        internal static byte Command(byte[] frame) {
            if (!HasMagic(frame) || frame.Length < 5 || frame[4] < 1 || frame[4] > 3)
                throw new InvalidDataException("Invalid binary compression frame.");
            return frame[4];
        }

        internal static byte[] Encode(byte[] raw, BinaryCompressionDefinition[] definitions, ISet<ulong> knownIds) {
            if (raw == null) throw new ArgumentNullException(nameof(raw));
            if (raw.Length < 16) return raw;
            definitions = definitions ?? Array.Empty<BinaryCompressionDefinition>();
            if (definitions.Length > MaximumDefinitions) throw new ArgumentException("Too many compression definitions.", nameof(definitions));
            var valid = new List<BinaryCompressionDefinition>(definitions.Length);
            var definitionIds = new Dictionary<ulong, BinaryCompressionDefinition>();
            foreach (var definition in definitions) {
                if (definition == null || definition.Data == null || definition.Data.Length < MinimumDefinitionLength ||
                    definition.Data.Length > MaximumDefinitionLength || (!definition.Validated && !ValidDefinition(definition)))
                    throw new ArgumentException("Invalid compression definition.", nameof(definitions));
                if (definitionIds.TryGetValue(definition.Id, out var present)) {
                    if (!Equal(present.Data, definition.Data)) throw new ArgumentException("Compression definition collision.", nameof(definitions));
                } else { definitionIds.Add(definition.Id, definition); valid.Add(definition); }
            }
            var dictionary = valid.Count == 0 ? null : new DictionaryIndex();
            if (dictionary != null) foreach (var definition in valid) dictionary.Add(definition);
            return EncodeIndexed(raw, dictionary, knownIds);
        }

        // Sample only distributed short regions, not the full object. This gates the stateless API;
        // network calls additionally retain scouts and periodically inspect a complete object.
        internal static bool HasLocalRepetition(byte[] raw) {
            if (raw.Length < 32) return false;
            int range = raw.Length - 32;
            for (int sample = 0; sample < 8; sample++) {
                int p = (int)((long)range * (sample + 1) / 9);
                uint key = Key(raw, p);
                for (int distance = 1; distance <= 16; distance++) {
                    if (Key(raw, p + distance) == key && MatchLength(raw, p, raw, p + distance, 16) >= 16) return true;
                }
                for (int distance = 32; distance <= 16384 && distance <= raw.Length - p - 16; distance <<= 1) {
                    if (Key(raw, p + distance) == key && MatchLength(raw, p, raw, p + distance, 16) >= 16) return true;
                }
            }
            return false;
        }

        internal static byte[] EncodeIndexed(byte[] raw, DictionaryIndex dictionary, ISet<ulong> knownIds) => EncodeIndexedCore(raw, dictionary, knownIds, false, out _);
        internal static byte[] EncodeIndexed(byte[] raw, DictionaryIndex dictionary, ISet<ulong> knownIds, out EncodingPlan plan) => EncodeIndexedCore(raw, dictionary, knownIds, true, out plan);
        static byte[] EncodeIndexedCore(byte[] raw, DictionaryIndex dictionary, ISet<ulong> knownIds, bool capturePlan, out EncodingPlan plan) {
            plan = null;
            if (raw.Length < 16) return raw;
            int[] last = ArrayPool<int>.Shared.Rent(HashSlots);
            Array.Fill(last, -1, 0, HashSlots);
            Dictionary<ulong, BinaryCompressionDefinition> referenced = null;
            List<PlanSegment> segments = null;
            int plannedBytes = 0;
            using (var tokens = new MemoryStream(Math.Min(raw.Length, 4096))) {
                try {
                    int p = 0, literal = 0, misses = 0;
                    while (p + 4 <= raw.Length) {
                        uint key = Key(raw, p);
                        int slot = Slot(key, HashSlots);
                        int previous = last[slot]; last[slot] = p;
                        int copyLength = 0, distance = 0;
                        if (previous >= 0 && previous < p && Key(raw, previous) == key) {
                            copyLength = MatchLength(raw, previous, raw, p, raw.Length - p);
                            distance = p - previous;
                        }
                        BinaryCompressionDefinition definition = null;
                        int offset = 0, dictionaryLength = 0;
                        dictionary?.Match(raw, p, key, out definition, out offset, out dictionaryLength);
                        int copySaving = copyLength - (1 + VarLength(distance) + VarLength(copyLength));
                        int dictionarySaving = dictionaryLength - (9 + VarLength(offset) + VarLength(dictionaryLength));
                        bool useDictionary = dictionarySaving > Math.Max(0, copySaving);
                        int length = useDictionary ? dictionaryLength : copySaving > 0 ? copyLength : 0;
                        if (length == 0) { p += 1 + Math.Min(7, misses++ >> 6); continue; }
                        Literal(tokens, raw, literal, p - literal);
                        if (useDictionary) {
                            if (capturePlan) {
                                if (segments == null) segments = new List<PlanSegment>();
                                if (segments.Count == 512) { capturePlan = false; segments = null; }
                                else { segments.Add(new PlanSegment(p, length, definition, offset)); plannedBytes += length; }
                            }
                            tokens.WriteByte(2); U64(tokens, definition.Id); Var(tokens, offset); Var(tokens, length);
                            if (knownIds == null || !knownIds.Contains(definition.Id)) {
                                if (referenced == null) referenced = new Dictionary<ulong, BinaryCompressionDefinition>();
                                referenced[definition.Id] = definition;
                            }
                        } else {
                            tokens.WriteByte(1); Var(tokens, distance); Var(tokens, length);
                        }
                        int end = p + length;
                        // A long match has already established repetition. Index a short prefix and
                        // its tail rather than touching every byte of the data being skipped.
                        for (int fill = p + 1; fill < Math.Min(end, p + 16) && fill + 4 <= raw.Length; fill++) last[Slot(Key(raw, fill), HashSlots)] = fill;
                        if (end >= 4 && end <= raw.Length) last[Slot(Key(raw, end - 4), HashSlots)] = end - 4;
                        p = end; literal = p; misses = 0;
                        if (tokens.Length >= raw.Length) return raw;
                    }
                    Literal(tokens, raw, literal, raw.Length - literal);
                    int referencedCount = referenced?.Count ?? 0;
                    long frameLength = 5L + VarLength(raw.Length) + VarLength(referencedCount) + tokens.Length;
                    if (referenced != null) foreach (var definition in referenced.Values) frameLength += 8L + VarLength(definition.Data.Length) + definition.Data.Length;
                    if (frameLength >= raw.Length) return raw;
                    if (capturePlan && segments != null && plannedBytes >= Math.Max(128, raw.Length / 8))
                        plan = new EncodingPlan(segments.ToArray());
                    using (var frame = new MemoryStream((int)frameLength)) {
                        Header(frame, 1); Var(frame, raw.Length); Var(frame, referencedCount);
                        if (referenced != null) foreach (var definition in referenced.Values) WriteDefinition(frame, definition);
                        tokens.Position = 0; tokens.CopyTo(frame); return frame.ToArray();
                    }
                } finally { ArrayPool<int>.Shared.Return(last); }
            }
        }

        internal readonly struct PlanSegment {
            internal readonly int Position, Length, Offset;
            internal readonly BinaryCompressionDefinition Definition;
            internal PlanSegment(int position, int length, BinaryCompressionDefinition definition, int offset) {
                Position = position; Length = length; Definition = definition; Offset = offset;
            }
        }
        internal sealed class EncodingPlan {
            internal readonly PlanSegment[] Segments;
            internal int RawLength, Version;
            internal ulong Anchor1, Anchor2, Anchor3;
            internal long Cost;
            internal LinkedListNode<EncodingPlan> Node;
            internal EncodingPlan(PlanSegment[] segments) { Segments = segments; }
        }

        // Anchors select candidates only. Every dictionary span is verified before it can be
        // referenced; changed bytes join the surrounding literals. Sizing precedes one allocation.
        internal static byte[] EncodePlan(byte[] raw, EncodingPlan plan, ISet<ulong> knownIds) {
            Span<int> matching = stackalloc int[512];
            int count = 0, cursor = 0, matchedBytes = 0;
            long tokenBytes = 0;
            Dictionary<ulong, BinaryCompressionDefinition> inline = null;
            for (int i = 0; i < plan.Segments.Length; i++) {
                var segment = plan.Segments[i];
                if (!raw.AsSpan(segment.Position, segment.Length).SequenceEqual(segment.Definition.Data.AsSpan(segment.Offset, segment.Length))) continue;
                matching[count++] = i; matchedBytes += segment.Length;
                int literal = segment.Position - cursor;
                if (literal != 0) tokenBytes += 1L + VarLength(literal) + literal;
                tokenBytes += 9L + VarLength(segment.Offset) + VarLength(segment.Length);
                cursor = segment.Position + segment.Length;
                if (knownIds == null || !knownIds.Contains(segment.Definition.Id)) {
                    if (inline == null) inline = new Dictionary<ulong, BinaryCompressionDefinition>();
                    inline[segment.Definition.Id] = segment.Definition;
                }
            }
            if (count == 0 || matchedBytes < Math.Max(128, raw.Length / 8)) return null;
            int tail = raw.Length - cursor;
            if (tail != 0) tokenBytes += 1L + VarLength(tail) + tail;
            int inlineCount = inline?.Count ?? 0;
            long length = 5L + VarLength(raw.Length) + VarLength(inlineCount) + tokenBytes;
            if (inline != null) foreach (var definition in inline.Values) length += 8L + VarLength(definition.Data.Length) + definition.Data.Length;
            if (length >= raw.Length - Math.Max(32, raw.Length / 8)) return null;
            var result = new byte[(int)length]; int written = 0;
            result[written++] = (byte)'S'; result[written++] = (byte)'B'; result[written++] = (byte)'C'; result[written++] = 1; result[written++] = 1;
            WriteVar(result, ref written, raw.Length); WriteVar(result, ref written, inlineCount);
            if (inline != null) foreach (var definition in inline.Values) {
                WriteId(result, ref written, definition.Id); WriteVar(result, ref written, definition.Data.Length);
                Buffer.BlockCopy(definition.Data, 0, result, written, definition.Data.Length); written += definition.Data.Length;
            }
            cursor = 0;
            for (int i = 0; i < count; i++) {
                var segment = plan.Segments[matching[i]];
                WriteLiteral(result, ref written, raw, cursor, segment.Position - cursor);
                result[written++] = 2; WriteId(result, ref written, segment.Definition.Id);
                WriteVar(result, ref written, segment.Offset); WriteVar(result, ref written, segment.Length);
                cursor = segment.Position + segment.Length;
            }
            WriteLiteral(result, ref written, raw, cursor, raw.Length - cursor);
            if (written != result.Length) throw new InvalidOperationException("Binary compression plan length mismatch.");
            return result;
        }
        static void WriteVar(byte[] output, ref int position, int value) {
            uint u = (uint)value;
            while (u >= 128) { output[position++] = (byte)(u | 128); u >>= 7; }
            output[position++] = (byte)u;
        }
        static void WriteId(byte[] output, ref int position, ulong value) {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(position, 8), value); position += 8;
        }
        static void WriteLiteral(byte[] output, ref int position, byte[] raw, int offset, int length) {
            if (length == 0) return;
            output[position++] = 0; WriteVar(output, ref position, length);
            Buffer.BlockCopy(raw, offset, output, position, length); position += length;
        }

        // Referenced dictionary bytes are immutable. Literal slices keep the current frame
        // alive until SB1 consumes it; byte fields are copied once into application ownership.
        // Inline definitions and overlapping LZ copies use the ordinary bounded decoder.
        internal static bool TryReadSegments(byte[] frame, int maximumBytes, Func<ulong, byte[]> lookup,
            ReadOnlyMemory<byte>[] segments, out int count, out int length) {
            count = length = 0;
            if (Command(frame) != 1) return false;
            var reader = new Reader(frame, 5);
            length = reader.Var();
            if (length > maximumBytes) throw new InvalidDataException("Expanded binary payload exceeds its configured limit.");
            if (reader.Var() != 0) return false;
            int written = 0;
            while (written < length) {
                if (count == segments.Length) return false;
                byte token = reader.Byte();
                if (token == 0) {
                    int size = reader.Var(); CheckLength(size, length - written);
                    segments[count++] = reader.Memory(size); written += size;
                } else if (token == 2) {
                    ulong id = reader.U64(); int offset = reader.Var(), size = reader.Var();
                    CheckLength(size, length - written);
                    byte[] data = lookup?.Invoke(id);
                    if (data == null || offset > data.Length || size > data.Length - offset)
                        throw new InvalidDataException("Missing or invalid binary compression definition.");
                    segments[count++] = data.AsMemory(offset, size); written += size;
                } else if (token == 1) return false;
                else throw new InvalidDataException("Unknown binary compression token.");
            }
            reader.End(); return true;
        }

        internal static byte[] Decode(byte[] frame, int maximumBytes, Func<ulong, byte[]> lookup,
            out BinaryCompressionDefinition[] definitions) {
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            if (maximumBytes < 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
            definitions = Array.Empty<BinaryCompressionDefinition>();
            if (!HasMagic(frame)) {
                if (frame.Length > maximumBytes) throw new InvalidDataException("Binary payload exceeds its configured limit.");
                return frame;
            }
            if (Command(frame) != 1) throw new InvalidDataException("Expected a binary compression data frame.");
            var reader = new Reader(frame, 5);
            int length = reader.Var();
            if (length > maximumBytes) throw new InvalidDataException("Expanded binary payload exceeds its configured limit.");
            var local = ReadDefinitions(ref reader, maximumBytes, lookup);
            Dictionary<ulong, byte[]> inline = null;
            if (local.Length != 0) {
                inline = new Dictionary<ulong, byte[]>(local.Length);
                foreach (var definition in local) inline.Add(definition.Id, definition.Data);
            }
            var output = new byte[length];
            int written = 0;
            while (written < length) {
                byte token = reader.Byte();
                if (token == 0) {
                    int count = reader.Var(); CheckLength(count, length - written);
                    reader.Copy(output, written, count); written += count;
                } else if (token == 1) {
                    int distance = reader.Var(), count = reader.Var(); CheckLength(count, length - written);
                    if (distance == 0 || distance > written) throw new InvalidDataException("Invalid binary copy distance.");
                    int copied = Math.Min(distance, count);
                    Buffer.BlockCopy(output, written - distance, output, written, copied);
                    while (copied < count) {
                        int amount = Math.Min(copied, count - copied);
                        Buffer.BlockCopy(output, written, output, written + copied, amount); copied += amount;
                    }
                    written += count;
                } else if (token == 2) {
                    ulong id = reader.U64(); int offset = reader.Var(), count = reader.Var();
                    CheckLength(count, length - written);
                    byte[] data = null;
                    if (inline == null || !inline.TryGetValue(id, out data)) data = lookup?.Invoke(id);
                    if (data == null || offset > data.Length || count > data.Length - offset)
                        throw new InvalidDataException("Missing or invalid binary compression definition.");
                    Buffer.BlockCopy(data, offset, output, written, count); written += count;
                } else throw new InvalidDataException("Unknown binary compression token.");
            }
            reader.End(); definitions = local; return output;
        }

        internal static byte[] Update(BinaryCompressionDefinition[] definitions) {
            if (definitions == null) throw new ArgumentNullException(nameof(definitions));
            if (definitions.Length > MaximumDefinitions) throw new ArgumentException("Too many compression definitions.", nameof(definitions));
            using (var frame = new MemoryStream()) {
                Header(frame, 2); Var(frame, definitions.Length);
                var ids = new HashSet<ulong>();
                foreach (var definition in definitions) {
                    if (!ValidDefinition(definition) || !ids.Add(definition.Id)) throw new ArgumentException("Invalid or duplicate compression definition.", nameof(definitions));
                    WriteDefinition(frame, definition);
                }
                return frame.ToArray();
            }
        }

        internal static BinaryCompressionDefinition[] ReadUpdate(byte[] frame, int maximumBytes = MaximumDefinitions * MaximumDefinitionLength) {
            if (maximumBytes < 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
            if (Command(frame) != 2) throw new InvalidDataException("Expected a binary compression update.");
            var reader = new Reader(frame, 5);
            var definitions = ReadDefinitions(ref reader, maximumBytes, null); reader.End(); return definitions;
        }

        internal static byte[] Acknowledge(IEnumerable<ulong> ids) {
            if (ids == null) throw new ArgumentNullException(nameof(ids));
            var distinct = new HashSet<ulong>();
            foreach (ulong id in ids) {
                if (distinct.Add(id) && distinct.Count > MaximumDefinitions) throw new ArgumentException("Too many compression acknowledgements.", nameof(ids));
            }
            using (var frame = new MemoryStream(10 + distinct.Count * 8)) {
                Header(frame, 3); Var(frame, distinct.Count);
                foreach (ulong id in distinct) U64(frame, id);
                return frame.ToArray();
            }
        }

        internal static ulong[] ReadAcknowledgement(byte[] frame) {
            if (Command(frame) != 3) throw new InvalidDataException("Expected a binary compression acknowledgement.");
            var reader = new Reader(frame, 5);
            int count = reader.Var();
            if (count > MaximumDefinitions || count > reader.Remaining / 8) throw new InvalidDataException("Invalid compression acknowledgement count.");
            var ids = new ulong[count]; var distinct = new HashSet<ulong>();
            for (int i = 0; i < count; i++) {
                ids[i] = reader.U64();
                if (!distinct.Add(ids[i])) throw new InvalidDataException("Duplicate compression acknowledgement.");
            }
            reader.End(); return ids;
        }

        static BinaryCompressionDefinition[] ReadDefinitions(ref Reader reader, int maximumBytes, Func<ulong, byte[]> lookup) {
            int count = reader.Var();
            if (count == 0) return Array.Empty<BinaryCompressionDefinition>();
            if (count > MaximumDefinitions || count > reader.Remaining / (9 + MinimumDefinitionLength))
                throw new InvalidDataException("Invalid binary compression definition count.");
            var result = new BinaryCompressionDefinition[count]; var ids = new HashSet<ulong>();
            long total = 0;
            for (int i = 0; i < count; i++) {
                ulong id = reader.U64(); int length = reader.Var();
                if (length < MinimumDefinitionLength || length > MaximumDefinitionLength || length > maximumBytes - total || length > reader.Remaining)
                    throw new InvalidDataException("Invalid binary compression definition length.");
                total += length;
                var data = new byte[length]; reader.Copy(data, 0, length);
                if (Hash(data) != id || !ids.Add(id)) throw new InvalidDataException("Invalid or duplicate binary compression definition ID.");
                byte[] present = lookup?.Invoke(id);
                if (present != null && !Equal(present, data)) throw new InvalidDataException("Binary compression definition collision.");
                result[i] = new BinaryCompressionDefinition(id, data);
            }
            return result;
        }

        static void CheckLength(int count, int remaining) {
            if (count <= 0 || count > remaining) throw new InvalidDataException("Invalid binary compression token length.");
        }
        internal static bool ValidDefinition(BinaryCompressionDefinition definition) => definition != null && definition.Data != null &&
            definition.Data.Length >= MinimumDefinitionLength && definition.Data.Length <= MaximumDefinitionLength && Hash(definition.Data) == definition.Id;
        internal static bool Equal(byte[] a, byte[] b) => a != null && b != null && Equal(a, b, 0, b.Length);
        internal static bool Equal(byte[] a, byte[] b, int offset, int length) {
            return a.Length == length && a.AsSpan().SequenceEqual(b.AsSpan(offset, length));
        }
        internal static ulong Hash(byte[] bytes) => Hash(bytes, 0, bytes.Length);
        internal static ulong Hash(byte[] bytes, int offset, int length) {
            ulong hash = 14695981039346656037UL;
            for (int i = 0; i < length; i++) hash = unchecked((hash ^ bytes[offset + i]) * 1099511628211UL);
            hash ^= hash >> 33; hash = unchecked(hash * 0xff51afd7ed558ccdUL);
            hash ^= hash >> 33; hash = unchecked(hash * 0xc4ceb9fe1a85ec53UL);
            return hash ^ (hash >> 33);
        }
        static uint Key(byte[] data, int p) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p, 4));
        static int Slot(uint key, int slots) => (int)(unchecked(key * 2654435761U) >> 20);
        static int MatchLength(byte[] a, int ap, byte[] b, int bp, int maximum) {
            if (a.AsSpan(ap, maximum).SequenceEqual(b.AsSpan(bp, maximum))) return maximum;
            int length = 0;
            while (length + 8 <= maximum &&
                System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(a.AsSpan(ap + length, 8)) ==
                System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(bp + length, 8))) length += 8;
            while (length < maximum && a[ap + length] == b[bp + length]) length++;
            return length;
        }
        static int VarLength(int value) { uint u = (uint)value; int n = 1; while (u >= 128) { u >>= 7; n++; } return n; }
        static void Var(Stream stream, int value) {
            uint u = (uint)value;
            while (u >= 128) { stream.WriteByte((byte)(u | 128)); u >>= 7; }
            stream.WriteByte((byte)u);
        }
        static void U64(Stream stream, ulong value) { for (int i = 0; i < 8; i++) { stream.WriteByte((byte)value); value >>= 8; } }
        static void Header(Stream stream, byte command) { stream.WriteByte((byte)'S'); stream.WriteByte((byte)'B'); stream.WriteByte((byte)'C'); stream.WriteByte(1); stream.WriteByte(command); }
        static void WriteDefinition(Stream stream, BinaryCompressionDefinition definition) {
            U64(stream, definition.Id); Var(stream, definition.Data.Length); stream.Write(definition.Data, 0, definition.Data.Length);
        }
        static void Literal(Stream stream, byte[] raw, int offset, int length) {
            if (length == 0) return;
            stream.WriteByte(0); Var(stream, length); stream.Write(raw, offset, length);
        }

        // Each hash bucket has two independent ways for definition starts and for slices. Insertion
        // replaces an old way in constant time, so changing traffic can populate the index forever
        // without rebuilding or retaining a list for every object. Every hit is byte-verified.
        internal sealed class DictionaryIndex {
            internal const int RetainedCost = DictionarySlots * (4 * 16 + 2 * 4) + 256;
            readonly Entry[] starts = new Entry[DictionarySlots * 2], slices = new Entry[DictionarySlots * 2];
            readonly int[] startWays = new int[DictionarySlots], sliceWays = new int[DictionarySlots];
            struct Entry { internal BinaryCompressionDefinition Definition; internal uint Key; internal int Offset; }
            internal void Add(BinaryCompressionDefinition definition) {
                AddEntry(starts, startWays, definition, 0);
                for (int offset = 16; offset + 4 <= definition.Data.Length; offset += 16)
                    AddEntry(slices, sliceWays, definition, offset);
            }
            static void AddEntry(Entry[] entries, int[] ways, BinaryCompressionDefinition definition, int offset) {
                uint key = Key(definition.Data, offset);
                int slot = Slot(key, DictionarySlots);
                int entry = slot * 2 + (ways[slot]++ & 1);
                entries[entry] = new Entry { Definition = definition, Key = key, Offset = offset };
            }
            internal bool HasSampleMatch(byte[] raw) {
                if (raw.Length < 32) return false;
                int range = raw.Length - 32;
                for (int sample = 0; sample < 8; sample++) {
                    int start = (int)((long)range * (sample + 1) / 9);
                    for (int step = 0; step < 16; step++) {
                        int p = start + step;
                        Match(raw, p, Key(raw, p), out _, out _, out int length);
                        if (length >= 16) return true;
                    }
                }
                return false;
            }
            internal void Match(byte[] raw, int position, uint key, out BinaryCompressionDefinition definition, out int offset, out int length) {
                definition = null; offset = length = 0;
                int slot = Slot(key, DictionarySlots) * 2;
                Check(starts[slot], raw, position, key, ref definition, ref offset, ref length);
                Check(starts[slot + 1], raw, position, key, ref definition, ref offset, ref length);
                Check(slices[slot], raw, position, key, ref definition, ref offset, ref length);
                Check(slices[slot + 1], raw, position, key, ref definition, ref offset, ref length);
            }
            static void Check(Entry entry, byte[] raw, int position, uint key,
                ref BinaryCompressionDefinition definition, ref int offset, ref int length) {
                if (entry.Definition == null || entry.Key != key) return;
                byte[] data = entry.Definition.Data;
                int found = MatchLength(data, entry.Offset, raw, position, Math.Min(data.Length - entry.Offset, raw.Length - position));
                if (found > length) { definition = entry.Definition; offset = entry.Offset; length = found; }
            }
        }

        struct Reader {
            readonly byte[] data;
            int position;
            internal Reader(byte[] data, int position) { this.data = data; this.position = position; }
            internal int Remaining => data.Length - position;
            internal byte Byte() {
                if (position >= data.Length) throw new InvalidDataException("Truncated binary compression frame.");
                return data[position++];
            }
            internal int Var() {
                uint value = 0;
                for (int shift = 0; shift <= 28; shift += 7) {
                    byte next = Byte();
                    if (shift == 28 && next > 7) throw new InvalidDataException("Binary compression integer overflow.");
                    value |= (uint)(next & 127) << shift;
                    if ((next & 128) == 0) return (int)value;
                }
                throw new InvalidDataException("Invalid binary compression integer.");
            }
            internal ulong U64() {
                if (data.Length - position < 8) throw new InvalidDataException("Truncated binary compression frame.");
                ulong value = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(position, 8));
                position += 8; return value;
            }
            internal void Copy(byte[] destination, int offset, int count) {
                if (count < 0 || count > Remaining) throw new InvalidDataException("Truncated binary compression data.");
                Buffer.BlockCopy(data, position, destination, offset, count); position += count;
            }
            internal ReadOnlyMemory<byte> Memory(int count) {
                if (count < 0 || count > Remaining) throw new InvalidDataException("Truncated binary compression data.");
                var result = data.AsMemory(position, count); position += count; return result;
            }
            internal void End() { if (Remaining != 0) throw new InvalidDataException("Trailing binary compression data."); }
        }
    }
}
