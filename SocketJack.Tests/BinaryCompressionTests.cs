using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Net;
using SocketJack.Serialization;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace SocketJack.Tests;

[TestClass]
public sealed class BinaryCompressionTests {
    public sealed class Message {
        public int Number { get; set; }
        public byte[] Data { get; set; }
    }

    static byte[] RandomBytes(int count, int seed) { var data = new byte[count]; new Random(seed).NextBytes(data); return data; }
    static NetworkOptions Options(bool enabled = true, bool outerCompression = false, bool binary = true) {
        var options = new NetworkOptions {
            UsePeerToPeer = false, UseCompression = outerCompression, EnablePatternCache = false,
            Logging = false, AutoReconnect = false
        };
        if (binary) options.Serializer = new BinarySerializer { EnableCompression = enabled };
        options.Whitelist.Add(typeof(Message));
        options.Authorization.AnonymousMessageTypes.Add(typeof(Message));
        return options;
    }
    static int Port() {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
    static Task Until(Func<bool> condition, string message) => Until(condition, () => message);
    static async Task Until(Func<bool> condition, Func<string> message) {
        var timer = Stopwatch.StartNew();
        while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(8)) await Task.Delay(20);
        Assert.IsTrue(condition(), message());
    }
    static bool Contains(byte[] haystack, byte[] needle) {
        for (int start = 0; start <= haystack.Length - needle.Length; start++)
            if (haystack.AsSpan(start, needle.Length).SequenceEqual(needle)) return true;
        return false;
    }
    static byte[] Decode(byte[] frame, int maximum, params BinaryCompressionDefinition[] definitions) =>
        BinaryCompressionCodec.Decode(frame, maximum, id => definitions.FirstOrDefault(d => d.Id == id)?.Data, out _);

    [TestMethod]
    public void RepeatedRunsAndNonAdjacentSegmentsRoundTripAndShrink() {
        byte[] run = Enumerable.Repeat((byte)'A', 32768).ToArray();
        byte[] segment = RandomBytes(256, 11);
        byte[] repeated = Enumerable.Range(0, 64).SelectMany(_ => segment).ToArray();
        foreach (var data in new[] { run, repeated }) {
            byte[] encoded = BinaryCompressionCodec.Encode(data, Array.Empty<BinaryCompressionDefinition>(), new HashSet<ulong>());
            Assert.IsTrue(BinaryCompressionCodec.HasMagic(encoded));
            Assert.IsTrue(encoded.Length < data.Length / 4, $"Expected useful compression, got {encoded.Length}/{data.Length} bytes.");
            CollectionAssert.AreEqual(data, Decode(encoded, data.Length));
        }
    }

    [TestMethod]
    public void IncompressibleAndTinyMessagesKeepOriginalBytes() {
        foreach (var data in new[] { Array.Empty<byte>(), new byte[] { 1, 2, 3 }, RandomBytes(4096, 12) }) {
            byte[] encoded = BinaryCompressionCodec.Encode(data, Array.Empty<BinaryCompressionDefinition>(), new HashSet<ulong>());
            CollectionAssert.AreEqual(data, encoded);
            Assert.IsFalse(BinaryCompressionCodec.HasMagic(encoded));
        }
    }

    [TestMethod]
    public void DictionaryIsSelfContainedUntilAcknowledgedThenUsesShortReferences() {
        byte[] segment = RandomBytes(1024, 13);
        var definition = new BinaryCompressionDefinition(BinaryCompressionCodec.Hash(segment), segment);
        byte[] data = RandomBytes(20, 14).Concat(segment).Concat(RandomBytes(20, 15)).ToArray();
        // A definition that would cost more than it saves must keep the original message.
        byte[] first = BinaryCompressionCodec.Encode(data, new[] { definition }, new HashSet<ulong>());
        if (BinaryCompressionCodec.HasMagic(first)) CollectionAssert.AreEqual(data, Decode(first, data.Length));
        else CollectionAssert.AreEqual(data, first);
        byte[] acknowledged = BinaryCompressionCodec.Encode(data, new[] { definition }, new HashSet<ulong> { definition.Id });
        Assert.IsTrue(BinaryCompressionCodec.HasMagic(acknowledged));
        Assert.IsTrue(acknowledged.Length < data.Length / 4);
        CollectionAssert.AreEqual(data, Decode(acknowledged, data.Length, definition));
        Assert.ThrowsException<InvalidDataException>(() => Decode(acknowledged, data.Length));
        byte[] update = BinaryCompressionCodec.Update(new[] { definition });
        var imported = BinaryCompressionCodec.ReadUpdate(update, 4096);
        Assert.AreEqual(definition.Id, imported.Single().Id);
        CollectionAssert.AreEqual(segment, imported.Single().Data);
        CollectionAssert.AreEqual(new[] { definition.Id }, BinaryCompressionCodec.ReadAcknowledgement(BinaryCompressionCodec.Acknowledge(new[] { definition.Id })));
    }

    [TestMethod]
    public void DictionarySlicesAndCopyTailsRoundTripWithoutExpandingFrames() {
        byte[] segment = RandomBytes(1024, 61);
        var definition = new BinaryCompressionDefinition(BinaryCompressionCodec.Hash(segment), segment);
        var known = new HashSet<ulong> { definition.Id };
        foreach (int offset in new[] { 0, 1, 7, 15, 31, 63, 127, 511, 960 }) {
            foreach (int tail in new[] { 0, 1, 3, 7, 15, 31 }) {
                byte[] slice = segment.AsSpan(offset).ToArray();
                byte[] data = RandomBytes(17, 62).Concat(slice).Concat(slice).Concat(RandomBytes(tail, 63)).ToArray();
                foreach (var acknowledged in new[] { new HashSet<ulong>(), known }) {
                    byte[] encoded = BinaryCompressionCodec.Encode(data, new[] { definition }, acknowledged);
                    Assert.IsTrue(encoded.Length <= data.Length, "All frame and inline-definition costs must fit the original bytes.");
                    CollectionAssert.AreEqual(data, Decode(encoded, data.Length, definition), $"Slice offset {offset}, tail length {tail}.");
                    if (acknowledged.Count == 0)
                        CollectionAssert.AreEqual(data, Decode(encoded, data.Length), "Unacknowledged dictionary frames must be self-contained.");
                }
            }
        }
    }

    [TestMethod]
    public void ChangedPrefixesAndSuffixesDoNotEnterTheLearnedDictionary() {
        using var cache = new BinaryCompressionCache();
        byte[] common = RandomBytes(16384, 16);
        byte[] first = RandomBytes(193, 17).Concat(common).Concat(RandomBytes(197, 18)).ToArray();
        byte[] second = RandomBytes(311, 19).Concat(common).Concat(RandomBytes(401, 20)).ToArray();
        Assert.AreEqual(0, cache.Observe(first).Length);
        var definitions = cache.Observe(second);
        Assert.IsTrue(definitions.Sum(d => d.Data.Length) > 1024, "Unchanged data should be learned even when a preceding field changes length.");
        foreach (var definition in definitions) Assert.IsTrue(Contains(common, definition.Data), "Changed bytes must stay outside an exact shared definition.");
        byte[] encoded = BinaryCompressionCodec.Encode(second, definitions, new HashSet<ulong>(definitions.Select(d => d.Id)));
        Assert.IsTrue(encoded.Length < second.Length / 2);
        CollectionAssert.AreEqual(second, Decode(encoded, second.Length, definitions));
    }

    [TestMethod]
    public void DynamicLearningRetainsUsefulCandidatesBeyondTenMessages() {
        byte[] data = RandomBytes(8192, 21);
        using var cache = new BinaryCompressionCache();
        cache.Observe(data);
        for (int i = 0; i < 64; i++) cache.Observe(RandomBytes(2048, 100 + i));
        var definitions = cache.Observe(data);
        Assert.IsTrue(definitions.Length > 0, "Candidate retention must depend on memory and reuse rather than an arbitrary object count.");
        Assert.IsTrue(cache.CandidateCount > 0);
        foreach (var definition in definitions) Assert.IsTrue(Contains(data, definition.Data));
        byte[] encoded = BinaryCompressionCodec.Encode(data, definitions, new HashSet<ulong>(definitions.Select(d => d.Id)));
        Assert.IsTrue(encoded.Length < data.Length / 2);
        CollectionAssert.AreEqual(data, Decode(encoded, data.Length, definitions));
    }

    [TestMethod]
    public void ColdRandomTrafficUsesOnlyBoundedScoutsUntilRepetitionIsObserved() {
        using var cache = new BinaryCompressionCache();
        var known = new HashSet<ulong>();
        for (int i = 0; i < 256; i++) {
            byte[] raw = RandomBytes(32768, 21000 + i);
            Assert.AreSame(raw, cache.Encode(raw, known, out var added));
            Assert.AreEqual(0, added.Length);
        }
        Assert.AreEqual(0L, cache.EncodingAttempts, "Cold random data should not enter the general encoder.");
        Assert.AreEqual(0L, cache.LearningPasses, "Unproven cold data should not allocate candidates for every initial probe.");
        Assert.AreEqual(0, cache.CandidateCount);
        Assert.IsTrue(cache.RetainedBytes < 32768, "Random traffic should retain only the bounded scout and repository metadata.");
    }

    [TestMethod]
    public void RotatingScoutsLearnStableDataBetweenChangingFixedSamples() {
        using var cache = new BinaryCompressionCache();
        var known = new HashSet<ulong>();
        byte[] common = RandomBytes(32768, 22000), last = null;
        var learned = new Dictionary<ulong, BinaryCompressionDefinition>();
        for (int i = 0; i < 144; i++) {
            byte[] raw = (byte[])common.Clone();
            for (int sample = 1; sample <= 8; sample++) {
                int position = (raw.Length - 64) * sample / 9;
                RandomBytes(64, 23000 + i * 8 + sample).CopyTo(raw, position);
            }
            last = cache.Encode(raw, known, out var added);
            CollectionAssert.AreEqual(raw, Decode(last, raw.Length, learned.Values.Concat(added).ToArray()));
            foreach (var definition in added) { learned[definition.Id] = definition; known.Add(definition.Id); }
            if (added.Length != 0) cache.RequestProbe();
            if (i < 127) Assert.AreEqual(0L, cache.LearningPasses, "Fixed scout changes must exercise the alternate periodic grid.");
        }
        Assert.IsTrue(learned.Count > 0, "Periodic rotating scouts must wake exact learning for repetition outside the fixed samples.");
        Assert.IsTrue(last.Length < common.Length / 2, "The changed sample regions must remain literals while stable spans are reused.");
    }

    [DataTestMethod]
    [DataRow(0)][DataRow(1)][DataRow(2)][DataRow(3)][DataRow(4)][DataRow(5)][DataRow(6)][DataRow(7)]
    public void TruncatedDictionaryIdsRemainProtocolErrors(int idBytes) {
        byte[] frame = Frame(w => { Number(w, 64); Number(w, 0); w.Write((byte)2); w.Write(new byte[idBytes]); });
        Assert.ThrowsException<InvalidDataException>(() => Decode(frame, 1024));
    }

    [TestMethod]
    public void ConfirmedLearningSuppliesAPlanBeforeTheFirstReferencedMessage() {
        using var cache = new BinaryCompressionCache();
        byte[] raw = RandomBytes(32768, 24000);
        var known = new HashSet<ulong>();
        for (int i = 0; i < 3; i++) {
            cache.Encode(raw, known, out var added);
            foreach (var definition in added) known.Add(definition.Id);
        }
        Assert.IsTrue(known.Count > 0);
        Assert.IsTrue(cache.PlanCount > 0, "Learning already knows the exact positions of confirmed chunks.");
        long hits = cache.PlanHits;
        byte[] encoded = cache.Encode(raw, known, out _);
        Assert.IsTrue(encoded.Length < raw.Length / 4);
        Assert.AreEqual(hits + 1, cache.PlanHits, "The first referenced message must reuse its observed layout.");
        CollectionAssert.AreEqual(raw, Decode(encoded, raw.Length, cache.Definitions));
    }

    [DataTestMethod]
    [DataRow(1, true)][DataRow(17, true)][DataRow(-1, true)][DataRow(1, false)][DataRow(17, false)]
    public void HeaderAndTailLengthChangesReuseVerifiedPlans(int change, bool leading) {
        using var cache = new BinaryCompressionCache();
        byte[] raw = RandomBytes(32768, 25000);
        var known = new HashSet<ulong>();
        for (int i = 0; i < 5; i++) {
            cache.Encode(raw, known, out var added);
            foreach (var definition in added) known.Add(definition.Id);
        }
        byte[] changed;
        if (change < 0) changed = raw.Skip(-change).ToArray();
        else changed = leading ? RandomBytes(change, 25001).Concat(raw).ToArray() : raw.Concat(RandomBytes(change, 25001)).ToArray();
        long hits = cache.PlanHits;
        byte[] encoded = cache.Encode(changed, known, out _);
        Assert.IsTrue(encoded.Length < changed.Length / 4);
        Assert.AreEqual(hits + 1, cache.PlanHits, "Small framing/field-length changes must not force a general dictionary scan.");
        CollectionAssert.AreEqual(changed, Decode(encoded, changed.Length, cache.Definitions));
    }

    [TestMethod]
    public void IncompressibleTrafficBacksOffFullEncodingAndRecoversWhenContentChanges() {
        using var cache = new BinaryCompressionCache();
        var known = new HashSet<ulong>();
        const int randomMessages = 128;
        for (int i = 0; i < randomMessages; i++) {
            byte[] raw = RandomBytes(32768, 1000 + i);
            byte[] encoded = cache.Encode(raw, known, out var added);
            Assert.AreSame(raw, encoded, "An incompressible message should reuse its original array without an encoded copy.");
            Assert.AreEqual(0, added.Length, "Unique random data must not publish dictionary definitions.");
        }
        Assert.IsTrue(cache.EncodingAttempts < randomMessages / 4, "Repeated misses should reduce expensive encoding attempts.");
        Assert.IsTrue(cache.BypassedMessages > randomMessages * 3 / 4, "Most incompressible messages should take the bounded scout path.");
        Assert.IsTrue(cache.ProbeInterval > 1);

        byte[] runs = Enumerable.Repeat((byte)'A', 32768).ToArray();
        byte[] compressedRun = cache.Encode(runs, known, out var runDefinitions);
        Assert.IsTrue(compressedRun.Length < runs.Length / 4, "A compressible run must wake encoding immediately after an incompressible stretch.");
        CollectionAssert.AreEqual(runs, Decode(compressedRun, runs.Length));
        Assert.AreEqual(0, runDefinitions.Length, "Local back-references should avoid unnecessary global registrations for a byte run.");

        byte[] repeated = RandomBytes(32768, 2000);
        byte[] last = repeated;
        var learned = new Dictionary<ulong, BinaryCompressionDefinition>();
        for (int i = 0; i < 8; i++) {
            last = cache.Encode(repeated, known, out var added);
            CollectionAssert.AreEqual(repeated, Decode(last, repeated.Length, learned.Values.Concat(added).ToArray()));
            foreach (var definition in added) { learned[definition.Id] = definition; known.Add(definition.Id); }
            if (added.Length != 0) cache.RequestProbe();
        }
        Assert.IsTrue(learned.Count > 0, "Repeated random content must wake dictionary learning after backoff.");
        Assert.IsTrue(last.Length < repeated.Length / 4, "Backoff must not permanently suppress compression of a new repeated payload.");
        Assert.IsTrue(cache.DictionaryCount > 0);

        long attemptsBeforeRandom = cache.EncodingAttempts;
        int definitionsBeforeRandom = cache.DictionaryCount;
        for (int i = 0; i < randomMessages; i++) {
            byte[] raw = RandomBytes(32768, 3000 + i);
            Assert.AreSame(raw, cache.Encode(raw, known, out var added), "A populated dictionary must not force encoding of unrelated random content.");
            Assert.AreEqual(0, added.Length);
        }
        Assert.IsTrue(cache.EncodingAttempts - attemptsBeforeRandom < randomMessages / 4, "Encoding must back off again when a learned stream becomes incompressible.");
        Assert.AreEqual(definitionsBeforeRandom, cache.DictionaryCount);
    }

    [DataTestMethod]
    [DataRow("stable-scout-block")]
    [DataRow("small-repeated-header")]
    public void MarginallyCompressibleTrafficStillBacksOffFullEncoding(string workload) {
        using var cache = new BinaryCompressionCache();
        var known = new HashSet<ulong>();
        var learned = new Dictionary<ulong, BinaryCompressionDefinition>();
        byte[] stable = RandomBytes(64, 4000);
        const int count = 128, length = 32768;
        long rawBytes = 0, encodedBytes = 0;
        for (int i = 0; i < count; i++) {
            byte[] raw = RandomBytes(length, 4100 + i);
            if (workload == "stable-scout-block")
                stable.CopyTo(raw, (length - stable.Length) * 4 / 9);
            else
                Array.Fill(raw, (byte)'H', 0, 256);
            byte[] encoded = cache.Encode(raw, known, out var added);
            CollectionAssert.AreEqual(raw, Decode(encoded, raw.Length, learned.Values.Concat(added).ToArray()));
            Assert.IsTrue(encoded.Length <= raw.Length);
            rawBytes += raw.Length; encodedBytes += encoded.Length;
            if (added.Length != 0) {
                // Include registrations and acknowledgements in the effort-versus-savings fixture.
                encodedBytes += BinaryCompressionCodec.Update(added).Length;
                encodedBytes += BinaryCompressionCodec.Acknowledge(added.Select(d => d.Id)).Length;
            }
            foreach (var definition in added) { learned[definition.Id] = definition; known.Add(definition.Id); }
            if (added.Length != 0) cache.RequestProbe();
        }
        Assert.IsTrue(encodedBytes >= rawBytes * 98 / 100, "This workload must offer less than two percent total-byte savings.");
        Assert.IsTrue(cache.EncodingAttempts < count / 4, $"A stable sample or tiny header must not force full encoding of every mostly random object; observed {cache.EncodingAttempts}/{count} attempts.");
        Assert.IsTrue(cache.ProbeInterval > 1, "Marginal savings should preserve adaptive backoff.");
    }

    [DataTestMethod]
    [DataRow(24576, 8)]
    [DataRow(28672, 132)]
    public void UsefulDictionaryContinuesLearningWhenAnUnchangedPrefixGainsAStableSuffix(int prefixLength, int transitionCount) {
        using var cache = new BinaryCompressionCache();
        var known = new HashSet<ulong>();
        var learned = new Dictionary<ulong, BinaryCompressionDefinition>();
        const int length = 32768;
        byte[] prefix = RandomBytes(prefixLength, 5000);
        byte[] suffix = RandomBytes(length - prefixLength, 5001);
        var suffixDefinitions = new List<BinaryCompressionDefinition>();

        byte[] Transfer(byte[] raw, bool observeSuffix, out long sentBytes) {
            byte[] encoded = cache.Encode(raw, known, out var added);
            CollectionAssert.AreEqual(raw, Decode(encoded, raw.Length, learned.Values.Concat(added).ToArray()));
            sentBytes = encoded.Length;
            if (added.Length != 0) {
                sentBytes += BinaryCompressionCodec.Update(added).Length;
                sentBytes += BinaryCompressionCodec.Acknowledge(added.Select(d => d.Id)).Length;
            }
            foreach (var definition in added) {
                learned[definition.Id] = definition; known.Add(definition.Id);
                if (observeSuffix && Contains(suffix, definition.Data)) suffixDefinitions.Add(definition);
            }
            if (added.Length != 0) cache.RequestProbe();
            Assert.IsTrue(cache.RetainedBytes <= 4L * 1024 * 1024);
            Assert.IsTrue(cache.RetainedBytes <= BinaryCompressionMemory.Limit);
            return encoded;
        }

        byte[] baseline = null;
        for (int i = 0; i < 16; i++) {
            byte[] raw = prefix.Concat(RandomBytes(length - prefixLength, 5100 + i)).ToArray();
            BitConverter.GetBytes(i).CopyTo(raw, 0);
            baseline = Transfer(raw, false, out _);
        }
        string trained = $"Prefix={prefixLength}, baseline={baseline.Length}/{length} bytes, dictionary={cache.DictionaryCount}, attempts={cache.EncodingAttempts}, probe interval={cache.ProbeInterval}.";
        Assert.IsTrue(baseline.Length < length / 2, "The prefix must already produce useful compression before the suffix becomes stable. " + trained);
        Assert.IsTrue(baseline.Length > length / 8, "The changing random suffix must remain outside the trained dictionary. " + trained);

        byte[] final = null;
        long transitionBytes = 0;
        for (int i = 0; i < transitionCount; i++) {
            byte[] raw = prefix.Concat(suffix).ToArray();
            BitConverter.GetBytes(100 + i).CopyTo(raw, 0);
            final = Transfer(raw, true, out long sentBytes);
            transitionBytes += sentBytes;
        }
        string result = $"Prefix={prefixLength}, baseline={baseline.Length}, final={final.Length}/{length} bytes, transition={transitionBytes}/{transitionCount * (long)baseline.Length} bytes including definitions and ACKs, suffix definitions={suffixDefinitions.Count}, dictionary={cache.DictionaryCount}, attempts={cache.EncodingAttempts}, probe interval={cache.ProbeInterval}.";
        Assert.IsTrue(suffixDefinitions.Count > 0, "A useful existing dictionary must not prevent new stable suffix segments from being learned. " + result);
        Assert.IsTrue(final.Length < length / 8, $"The newly stable suffix should join the compressed dictionary within {transitionCount} objects. " + result);
        Assert.IsTrue(final.Length < baseline.Length / 2, "Learning the stable suffix should at least halve the trained frame. " + result);
        if (transitionCount == 8)
            Assert.IsTrue(transitionBytes < transitionCount * (long)baseline.Length, "Suffix learning must recover its data, registration, and acknowledgement costs across this transition. " + result);
    }

    static BinaryCompressionDefinition[] PlanDefinitions(int count, int seed) => Enumerable.Range(0, count)
        .Select(i => {
            byte[] bytes = RandomBytes(1024, seed + i);
            return new BinaryCompressionDefinition(BinaryCompressionCodec.Hash(bytes), bytes);
        }).ToArray();

    static byte[] PlanPayload(BinaryCompressionDefinition[] definitions, int number, int seed) {
        byte[] raw = new byte[8 + definitions.Sum(d => d.Data.Length) + 128];
        BitConverter.GetBytes(number).CopyTo(raw, 0);
        BitConverter.GetBytes(~number).CopyTo(raw, 4);
        int offset = 8;
        foreach (var definition in definitions) { definition.Data.CopyTo(raw, offset); offset += definition.Data.Length; }
        RandomBytes(128, seed).CopyTo(raw, offset);
        return raw;
    }

    [TestMethod]
    public void CachedDictionaryEncodingKeepsChangingLiteralsAndVerifiesReferencedBytes() {
        using var cache = new BinaryCompressionCache();
        var definitions = PlanDefinitions(16, 6000);
        foreach (var definition in definitions) Assert.IsTrue(cache.Import(definition));
        var known = new HashSet<ulong>(definitions.Select(d => d.Id));
        for (int i = 0; i < 8; i++) {
            byte[] raw = PlanPayload(definitions, i, 6100 + i);
            byte[] encoded = cache.Encode(raw, known, out _);
            Assert.IsTrue(encoded.Length < raw.Length / 8);
            CollectionAssert.AreEqual(raw, Decode(encoded, raw.Length, definitions), "Changing literals must be read from the current payload rather than a cached previous object.");
        }
        Assert.IsTrue(cache.PlanCount > 0, "The fixture must create a dictionary encoding plan.");
        Assert.IsTrue(cache.PlanHits >= 7, $"Changing literals should reuse the cached plan; hits={cache.PlanHits}.");

        byte[] changedReference = PlanPayload(definitions, 99, 6200);
        changedReference[2500] ^= 0xa5;
        long hitsBeforeMutation = cache.PlanHits;
        byte[] changedFrame = cache.Encode(changedReference, known, out _);
        CollectionAssert.AreEqual(changedReference, Decode(changedFrame, changedReference.Length, definitions),
            "A changed byte in a previously referenced region must become a literal or trigger a fresh encoding pass.");
        Assert.IsTrue(cache.PlanHits > hitsBeforeMutation, "The changed-reference fixture must exercise byte verification inside a cached plan.");

        byte[] next = PlanPayload(definitions, 100, 6201);
        known.Remove(definitions[8].Id);
        byte[] partiallyAcknowledged = cache.Encode(next, known, out _);
        Assert.IsTrue(BinaryCompressionCodec.HasMagic(partiallyAcknowledged), "One missing acknowledgement should still allow a profitable self-contained inline registration.");
        CollectionAssert.AreEqual(next, Decode(partiallyAcknowledged, next.Length, definitions.Where(d => d.Id != definitions[8].Id).ToArray()),
            "An unacknowledged definition must be included even when an earlier cached encoding could omit it.");

        known.Clear();
        byte[] unacknowledged = cache.Encode(next, known, out _);
        CollectionAssert.AreEqual(next, Decode(unacknowledged, next.Length), "Without any acknowledgements, cached encoding must remain independently decodable or use the raw payload.");
        Assert.IsTrue(unacknowledged.Length <= next.Length, "Inline definition costs must still fit the complete frame.");
    }

    [TestMethod]
    public void MultipleDictionaryEncodingsStayBoundedAndReleaseTheirCacheMemory() {
        const long budget = 768 * 1024;
        using var cache = new BinaryCompressionCache(budget);
        var templates = Enumerable.Range(0, 32).Select(i => PlanDefinitions(8, 7000 + i * 100)).ToArray();
        var definitions = templates.SelectMany(d => d).ToArray();
        foreach (var definition in definitions) Assert.IsTrue(cache.Import(definition));
        var known = new HashSet<ulong>(definitions.Select(d => d.Id));
        for (int cycle = 0; cycle < 3; cycle++) {
            for (int i = 0; i < templates.Length; i++) {
                byte[] raw = PlanPayload(templates[i], cycle * templates.Length + i, 11000 + cycle * templates.Length + i);
                byte[] encoded = cache.Encode(raw, known, out _);
                Assert.IsTrue(encoded.Length < raw.Length / 4, $"Template {i}, cycle {cycle} should keep useful dictionary compression.");
                CollectionAssert.AreEqual(raw, Decode(encoded, raw.Length, definitions));
                Assert.IsTrue(cache.RetainedBytes <= budget, "Dictionary state and cached encoding metadata must share the repository budget.");
            }
        }
        Assert.IsTrue(cache.PlanCount >= templates.Length, $"The cache should retain the 32 active templates within its budget; plans={cache.PlanCount}.");
        Assert.IsTrue(cache.PlanHits >= templates.Length, $"Cycling multiple templates should reuse their encoding plans; hits={cache.PlanHits}.");
        cache.Dispose();
        Assert.AreEqual(0L, cache.RetainedBytes);
        Assert.AreEqual(0, cache.DictionaryCount);
        Assert.AreEqual(0, cache.PlanCount);
    }

    [TestMethod]
    public void CacheOwnsItsDataAndHonorsSmallRetentionBudget() {
        const long budget = 32768;
        using var cache = new BinaryCompressionCache(budget);
        byte[] original = RandomBytes(4096, 22), beforeMutation = original.ToArray();
        cache.Observe(original);
        Array.Fill(original, (byte)0);
        var definitions = cache.Observe(beforeMutation);
        Assert.IsTrue(definitions.Length > 0, "Observation history must retain the observed bytes, independent of caller mutation.");
        foreach (var definition in definitions) Assert.IsTrue(Contains(beforeMutation, definition.Data));
        for (int i = 0; i < 100; i++) {
            cache.Observe(RandomBytes(8192, 300 + i));
            Assert.IsTrue(cache.RetainedBytes <= budget, "History, definitions and indexing metadata must all fit the budget.");
        }
        long snapshotBytes = cache.Snapshot(1024).Sum(d => d.Data.Length);
        Assert.IsTrue(snapshotBytes <= 1024);
        cache.Dispose();
        Assert.AreEqual(0L, cache.RetainedBytes);
        Assert.AreEqual(0, cache.DictionaryCount);
    }

    static void Number(BinaryWriter writer, ulong value) {
        while (value >= 128) { writer.Write((byte)(value | 128)); value >>= 7; }
        writer.Write((byte)value);
    }
    static byte[] Frame(Action<BinaryWriter> body, byte command = 1) {
        using var memory = new MemoryStream(); using var writer = new BinaryWriter(memory);
        writer.Write(new byte[] { (byte)'S', (byte)'B', (byte)'C', 1, command });
        body(writer); return memory.ToArray();
    }

    [TestMethod]
    public void MalformedReferencesLengthsAndExpansionAreRejected() {
        byte[] missing = Frame(w => { Number(w, 64); Number(w, 0); w.Write((byte)2); w.Write(123UL); Number(w, 0); Number(w, 64); });
        byte[] invalidBackReference = Frame(w => { Number(w, 64); Number(w, 0); w.Write((byte)1); Number(w, 1); Number(w, 64); });
        byte[] invalidLiteral = Frame(w => { Number(w, 1); Number(w, 0); w.Write((byte)0); Number(w, 2); w.Write(new byte[] { 1, 2 }); });
        byte[] zeroToken = Frame(w => { Number(w, 1); Number(w, 0); w.Write((byte)0); Number(w, 0); });
        byte[] invalidVarint = Frame(w => w.Write(Enumerable.Repeat((byte)255, 12).ToArray()));
        foreach (var bad in new[] { missing, invalidBackReference, invalidLiteral, zeroToken, invalidVarint })
            Assert.ThrowsException<InvalidDataException>(() => Decode(bad, 1024));
        byte[] raw = Enumerable.Repeat((byte)42, 8192).ToArray();
        byte[] valid = BinaryCompressionCodec.Encode(raw, Array.Empty<BinaryCompressionDefinition>(), new HashSet<ulong>());
        Assert.ThrowsException<InvalidDataException>(() => Decode(valid, raw.Length - 1));
        Assert.ThrowsException<InvalidDataException>(() => Decode(valid.Take(valid.Length - 1).ToArray(), raw.Length));
        Assert.ThrowsException<InvalidDataException>(() => Decode(valid.Concat(new byte[] { 0 }).ToArray(), raw.Length));
        byte[] spoofed = Frame(w => { Number(w, 1); w.Write(1UL); Number(w, 64); w.Write(RandomBytes(64, 400)); }, 2);
        Assert.ThrowsException<InvalidDataException>(() => BinaryCompressionCodec.ReadUpdate(spoofed, 4096));
    }

    [TestMethod]
    public void SerializerDefaultsOnButStandaloneSerializationStaysStatelessAndCanBeDisabled() {
        var enabled = new BinarySerializer(); var disabled = new BinarySerializer { EnableCompression = false };
        Assert.IsTrue(enabled.EnableCompression);
        Assert.IsTrue(enabled.CompressionMemoryLimit > 0);
        byte[] value = RandomBytes(2048, 23);
        for (int i = 0; i < 12; i++) CollectionAssert.AreEqual(disabled.Serialize(value), enabled.Serialize(value));
        Assert.AreEqual(0L, enabled.CompressionCacheBytes);
        Assert.AreEqual(0, enabled.CompressionDictionaryCount);
    }

    [TestMethod]
    public void NegotiatedTinyNetworkMessagesBypassCompressionWithoutCreatingACache() {
        var options = Options();
        using var client = new SocketJack.Net.TcpClient(options);
        var connection = new NetworkConnection(client, null);
        SafeModeHandshake.Validate(SafeModeHandshake.Create(options), options, connection);
        connection.BinaryCompressionReady = true;
        Assert.IsTrue(BinaryNetworkCompression.Enabled(client, connection));
        var serializer = (BinarySerializer)options.Serializer;
        byte[] raw = serializer.Serialize(new Wrapper(new Message { Number = 7, Data = Array.Empty<byte>() }, client));
        Assert.IsTrue(raw.Length < 256, "This fixture must exercise the tiny-network-message fast path.");
        for (int i = 0; i < 64; i++) Assert.AreSame(raw, BinaryNetworkCompression.Encode(client, connection, raw));
        Assert.AreEqual(0L, serializer.CompressionCacheBytes, "Small receipts should not create adaptive cache state.");
        Assert.AreEqual(0, serializer.CompressionDictionaryCount);
    }

    [TestMethod]
    public void NegotiationRequiresBothBinaryPeersAndOldManifestDoesNotOptIn() {
        var enabled = Options(); var disabled = Options(false);
        Assert.IsTrue(WireContracts.Compare(WireContracts.Create(enabled), WireContracts.Create(enabled)).BinaryCompression);
        Assert.IsFalse(WireContracts.Compare(WireContracts.Create(enabled), WireContracts.Create(disabled)).BinaryCompression);
        Assert.IsFalse(WireContracts.Compare(WireContracts.Create(disabled), WireContracts.Create(enabled)).BinaryCompression);
        var json = Options(binary: false);
        Assert.IsFalse(WireContracts.Compare(WireContracts.Create(json), WireContracts.Create(json)).BinaryCompression);
        var legacy = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(WireContracts.Create(enabled)))!.AsObject();
        legacy.Remove("BinaryCompression");
        byte[] claim = Encoding.ASCII.GetBytes("SJSM2" + legacy.ToJsonString());
        using var client = new SocketJack.Net.TcpClient(enabled);
        var connection = new NetworkConnection(client, null);
        SafeModeHandshake.Validate(claim, enabled, connection);
        Assert.IsTrue(connection.SafeModeVerified);
        Assert.IsFalse(connection.Compatibility.BinaryCompression);
        byte[] raw = enabled.Serializer.Serialize(new Wrapper(new Message { Number = 1, Data = RandomBytes(1000, 24) }, client));
        CollectionAssert.AreEqual(raw, BinaryNetworkCompression.Encode(client, connection, raw));
        Assert.AreEqual(0L, ((BinarySerializer)enabled.Serializer).CompressionCacheBytes);
        using var jsonClient = new SocketJack.Net.TcpClient(json);
        var jsonConnection = new NetworkConnection(jsonClient, null);
        SafeModeHandshake.Validate(SafeModeHandshake.Create(json), json, jsonConnection);
        byte[] jsonBytes = json.Serializer.Serialize(new Wrapper(new Message { Number = 2, Data = RandomBytes(1000, 27) }, jsonClient));
        CollectionAssert.AreEqual(jsonBytes, BinaryNetworkCompression.Encode(jsonClient, jsonConnection, jsonBytes));
    }

    [DataTestMethod][DataRow(false)][DataRow(true)]
    public async Task TcpPropagatesDefinitionsToIdleClientsAndNewHandshakeThenRoundTrips(bool outerCompression) {
        int port = Port(); var serverOptions = Options(outerCompression: outerCompression);
        using var server = new SocketJack.Net.TcpServer(serverOptions, port);
        using var first = new SocketJack.Net.TcpClient(Options(outerCompression: outerCompression));
        using var idle = new SocketJack.Net.TcpClient(Options(outerCompression: outerCompression));
        var errors = new ConcurrentQueue<string>();
        server.OnError += e => errors.Enqueue(e.Exception.Message);
        first.OnError += e => errors.Enqueue(e.Exception.Message);
        idle.OnError += e => errors.Enqueue(e.Exception.Message);
        var received = new ConcurrentDictionary<int, byte[]>();
        var inbound = new ConcurrentDictionary<int, byte[]>();
        server.RegisterCallback<Message>(e => inbound[e.Object.Number] = e.Object.Data);
        first.RegisterCallback<Message>(e => received[e.Object.Number] = e.Object.Data);
        Assert.IsTrue(server.Listen()); Assert.IsTrue(await first.Connect("127.0.0.1", port));
        await Until(() => server.Clients.Count == 1, "The first client did not complete its handshake.");
        var firstConnection = server.Clients.Values.Single();
        Assert.IsTrue(await idle.Connect("127.0.0.1", port));
        await Until(() => server.Clients.Count == 2, "The idle client did not complete its handshake.");
        Assert.IsTrue(first.Connection.Compatibility.BinaryCompression);
        byte[] data = RandomBytes(16384, 25);
        for (int i = 0; i < 3; i++) server.Send(firstConnection, new Message { Number = i, Data = data });
        await Until(() => received.Count == 3, "Training messages were not delivered: " + string.Join("; ", errors));
        foreach (var item in received.Values) CollectionAssert.AreEqual(data, item);
        var serverSerializer = (BinarySerializer)serverOptions.Serializer;
        var idleSerializer = (BinarySerializer)idle.Options.Serializer;
        await Until(() => serverSerializer.CompressionDictionaryCount > 0 && idleSerializer.CompressionDictionaryCount > 0,
            "A dictionary learned while sending to one client must propagate to an idle client: " + string.Join("; ", errors));
        var expected = serverSerializer.GetCompressionCache(server).Snapshot(6000);
        Assert.IsTrue(expected.Length > 0);
        await Until(() => expected.All(d => idleSerializer.GetCompressionCache(idle).TryGet(d.Id, out _)), "The idle client did not receive the shared definitions.");
        long lastBytes = firstConnection.TotalBytesSent;
        int unchanged = 0;
        for (int i = 0; i < 30 && unchanged < 3; i++) {
            await Task.Delay(50);
            long current = firstConnection.TotalBytesSent;
            unchanged = current == lastBytes ? unchanged + 1 : 0; lastBytes = current;
        }
        long before = firstConnection.TotalBytesSent;
        server.Send(firstConnection, new Message { Number = 777, Data = data });
        await Until(() => received.ContainsKey(777), "The cached object did not arrive.");
        long wireBytes = firstConnection.TotalBytesSent - before;
        Console.WriteLine($"Binary dictionary TCP (outer compression={outerCompression}): {data.Length} payload bytes, {wireBytes} wire bytes after training.");
        Assert.IsTrue(wireBytes > 0 && wireBytes < data.Length / 2, $"The trained network path sent {wireBytes} bytes for a {data.Length}-byte object.");
        CollectionAssert.AreEqual(data, received[777]);
        using var late = new SocketJack.Net.TcpClient(Options(outerCompression: outerCompression));
        var lateReceived = new TaskCompletionSource<Message>(TaskCreationOptions.RunContinuationsAsynchronously);
        late.RegisterCallback<Message>(e => lateReceived.TrySetResult(e.Object));
        Assert.IsTrue(await late.Connect("127.0.0.1", port));
        var lateSerializer = (BinarySerializer)late.Options.Serializer;
        await Until(() => expected.All(d => lateSerializer.GetCompressionCache(late).TryGet(d.Id, out _)), "A newly connecting client did not receive the handshake dictionary snapshot.");
        server.SendBroadcast(new Message { Number = 99, Data = data });
        Message actual = await lateReceived.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.AreEqual(99, actual.Number); CollectionAssert.AreEqual(data, actual.Data);
        byte[] clientData = RandomBytes(8192, 28);
        for (int i = 0; i < 3; i++) first.Send(new Message { Number = 200 + i, Data = clientData });
        await Until(() => inbound.Count == 3, "Objects sent by the client were not delivered.");
        foreach (var item in inbound.Values) CollectionAssert.AreEqual(clientData, item);
        var firstCache = ((BinarySerializer)first.Options.Serializer).GetCompressionCache(first);
        var clientDefinitions = firstCache.Snapshot(60000).Where(d => Contains(clientData, d.Data)).ToArray();
        Assert.IsTrue(clientDefinitions.Length > 0, "The sending client did not learn its repeated segments.");
        await Until(() => clientDefinitions.All(d => idleSerializer.GetCompressionCache(idle).TryGet(d.Id, out _)),
            "Definitions learned by one client must propagate through the server to another idle client.");
        Assert.IsTrue(serverSerializer.CompressionCacheBytes <= serverSerializer.CompressionMemoryLimit);
        Assert.IsTrue(first.Connected && idle.Connected && late.Connected, string.Join("; ", errors));
    }

    [DataTestMethod][DataRow(true, false)][DataRow(false, true)][DataRow(false, false)]
    public async Task DisabledBinaryCompressionNegotiatesUncompressedRoundTrips(bool serverEnabled, bool clientEnabled) {
        int port = Port(); using var server = new SocketJack.Net.TcpServer(Options(serverEnabled), port);
        using var client = new SocketJack.Net.TcpClient(Options(clientEnabled));
        var received = new TaskCompletionSource<Message>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.RegisterCallback<Message>(e => received.TrySetResult(e.Object));
        Assert.IsTrue(server.Listen()); Assert.IsTrue(await client.Connect("127.0.0.1", port));
        Assert.IsFalse(client.Connection.Compatibility.BinaryCompression);
        byte[] data = Enumerable.Repeat((byte)65, 8192).ToArray();
        for (int i = 0; i < 3; i++) client.Send(new Message { Number = i, Data = data });
        var actual = await received.Task.WaitAsync(TimeSpan.FromSeconds(8));
        CollectionAssert.AreEqual(data, actual.Data);
        Assert.AreEqual(0L, ((BinarySerializer)client.Options.Serializer).CompressionCacheBytes);
        Assert.AreEqual(0L, ((BinarySerializer)server.Options.Serializer).CompressionCacheBytes);
    }

    [TestMethod]
    public async Task ConcurrentTcpSendsKeepAllObjectsIntact() {
        int port = Port(); using var server = new SocketJack.Net.TcpServer(Options(), port);
        using var client = new SocketJack.Net.TcpClient(Options());
        const int count = 40; byte[] common = RandomBytes(16384, 26);
        var got = new ConcurrentDictionary<int, byte[]>();
        server.RegisterCallback<Message>(e => got[e.Object.Number] = e.Object.Data);
        Assert.IsTrue(server.Listen()); Assert.IsTrue(await client.Connect("127.0.0.1", port));
        await Task.WhenAll(Enumerable.Range(0, count).Select(i => Task.Run(() => client.Send(new Message { Number = i, Data = common }))));
        await Until(() => got.Count == count, $"Only {got.Count}/{count} concurrent messages arrived.");
        foreach (var value in got.Values) CollectionAssert.AreEqual(common, value);
        Assert.IsTrue(client.Connected);
        Assert.IsTrue(((BinarySerializer)client.Options.Serializer).CompressionDictionaryCount > 0);
    }

    static async Task ExchangeTransport(ISocket server, ISocket client, Func<bool> listen,
        Func<Task<bool>> connect, Action<Message> send) {
        var echoed = new ConcurrentDictionary<int, byte[]>();
        var received = new ConcurrentDictionary<int, byte[]>();
        var errors = new ConcurrentQueue<string>();
        if (server is NetworkBase serverNetwork) serverNetwork.OnError += e => errors.Enqueue(e.Exception.ToString());
        if (client is NetworkBase clientNetwork) clientNetwork.OnError += e => errors.Enqueue(e.Exception.ToString());
        server.RegisterCallback<Message>(e => {
            received[e.Object.Number] = e.Object.Data;
            if (server is SocketJack.Net.TcpServer tcp) tcp.Send(e.Connection, e.Object);
            else server.Send(e.Connection, e.Object);
        });
        client.RegisterCallback<Message>(e => echoed[e.Object.Number] = e.Object.Data);
        Assert.IsTrue(listen(), "Server listen failed: " + string.Join("; ", errors));
        Assert.IsTrue(await connect(), "Client connect failed: " + string.Join("; ", errors));
        Assert.IsTrue(client.Connection.SafeModeVerified);
        Assert.IsTrue(client.Connection.Compatibility.BinaryCompression);
        byte[] data = RandomBytes(12288, 29);
        for (int i = 0; i < 4; i++) {
            send(new Message { Number = i, Data = data });
            int number = i;
            await Until(() => echoed.ContainsKey(number), () => $"{client.Name}: binary object {number} did not make a round trip. Server received={received.ContainsKey(number)}, client connected={client.Connected}. " + string.Join("; ", errors));
            CollectionAssert.AreEqual(data, echoed[number]);
        }
        Assert.IsTrue(((BinarySerializer)client.Options.Serializer).CompressionDictionaryCount > 0);
        Assert.IsTrue(((BinarySerializer)server.Options.Serializer).CompressionDictionaryCount > 0);
        Assert.IsTrue(client.Connected);
    }

    [DataTestMethod]
    [DataRow("websocket")][DataRow("mutable")][DataRow("udp")]
    [DataRow("reliable-ordered")][DataRow("reliable-independent")]
    public async Task BinaryCompressionRoundTripsAcrossNetworkTransports(string transport) {
        int port = Port(); var serverOptions = Options(); var clientOptions = Options();
        if (transport == "websocket") {
            using var server = new SocketJack.Net.WebSockets.WebSocketServer(port) { Options = serverOptions };
            using var client = new SocketJack.Net.WebSockets.WebSocketClient(clientOptions);
            await ExchangeTransport(server, client, server.Listen, () => client.Connect("127.0.0.1", port), client.Send);
        } else if (transport == "mutable") {
            using var server = new MutableTcpServer(serverOptions, port);
            server.EnabledProtocols.Add(MutableTcpProtocols.SocketJack);
            using var client = new SocketJack.Net.TcpClient(clientOptions);
            await ExchangeTransport(server, client, server.Listen, () => client.Connect("127.0.0.1", port), client.Send);
        } else {
            if (transport.StartsWith("reliable", StringComparison.Ordinal)) {
                serverOptions.UdpMode = clientOptions.UdpMode = UdpMode.UDP_Reliable;
                serverOptions.UdpReliable.Ordering = clientOptions.UdpReliable.Ordering = transport == "reliable-ordered";
            }
            using var server = new SocketJack.Net.UdpServer(serverOptions, port);
            using var client = new SocketJack.Net.UdpClient(clientOptions);
            await ExchangeTransport(server, client, server.Listen, () => client.Connect("127.0.0.1", port), client.Send);
        }
    }
}
