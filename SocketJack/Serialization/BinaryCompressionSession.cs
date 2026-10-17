using System;
using System.Collections.Generic;
using System.IO;

namespace SocketJack.Serialization {
    /// <summary>One private SBC1 repository for an externally hosted, negotiated BinarySerializer connection. Send returned controls on the same connection; never share a session between clients.</summary>
    public sealed class BinaryCompressionSession : IDisposable {
        readonly object sync = new object();
        readonly BinaryCompressionCache cache = new BinaryCompressionCache();
        readonly HashSet<ulong> known = new HashSet<ulong>();
        readonly bool enabled;
        readonly int maximumBytes;
        long metadata;
        bool disposed;
        internal BinaryCompressionSession(bool enabled, int maximumBytes) {
            if (maximumBytes < 256) { cache.Dispose(); throw new ArgumentOutOfRangeException(nameof(maximumBytes)); }
            this.enabled = enabled; this.maximumBytes = maximumBytes;
            if (BinaryCompressionMemory.TryReserve(2048)) metadata = 2048;
        }
        /// <summary>Retained repository and peer metadata, charged to the process-wide physical RAM / 16 ceiling.</summary>
        public long RetainedBytes { get { lock (sync) return metadata + cache.RetainedBytes; } }
        /// <summary>Number of immutable binary pieces in this connection's dictionary.</summary>
        public int DictionaryCount => cache.DictionaryCount;
        /// <summary>Compress one SB1 payload. Send every returned registration before or with the payload; references remain self-contained until acknowledged.</summary>
        public byte[] Encode(byte[] message, out byte[][] controls) {
            lock (sync) {
                RequireOpen(); RequireMessage(message); controls = Array.Empty<byte[]>();
                if (!enabled) return message;
                byte[] result = cache.Encode(message, known, out var added);
                controls = Updates(added); return result;
            }
        }
        /// <summary>Decode data or process a control (null result). Send returned controls in order. No application types are activated.</summary>
        public byte[] Decode(byte[] frame, out byte[][] controls) {
            lock (sync) {
                RequireOpen();
                if (frame == null || frame.Length > maximumBytes) throw new InvalidDataException("Binary frame exceeds its limit.");
                controls = Array.Empty<byte[]>();
                byte[] raw;
                var output = new List<byte[]>();
                if (!BinaryCompressionCodec.HasMagic(frame)) raw = frame;
                else {
                    if (!enabled) throw new InvalidDataException("Compression was not enabled.");
                    byte command = BinaryCompressionCodec.Command(frame);
                    if (command == 3) {
                        foreach (ulong id in BinaryCompressionCodec.ReadAcknowledgement(frame)) if (cache.TryGet(id, out _)) Remember(id);
                        cache.RequestProbe(); return null;
                    }
                    BinaryCompressionDefinition[] definitions;
                    if (command == 2) { definitions = BinaryCompressionCodec.ReadUpdate(frame, maximumBytes); raw = null; }
                    else raw = cache.Decode(frame, maximumBytes, out definitions);
                    var accepted = new List<ulong>();
                    foreach (var definition in definitions) if (cache.Import(definition)) { accepted.Add(definition.Id); Remember(definition.Id); }
                    if (accepted.Count != 0) { output.Add(BinaryCompressionCodec.Acknowledge(accepted)); cache.RequestProbe(); }
                }
                if (raw != null) {
                    RequireMessage(raw);
                    if (enabled) output.AddRange(Updates(cache.ObserveIncoming(raw)));
                }
                controls = output.ToArray(); return raw;
            }
        }
        void Remember(ulong id) {
            if (metadata == 0 || known.Contains(id) || known.Count >= BinaryCompressionCodec.MaximumDefinitions || !BinaryCompressionMemory.TryReserve(128)) return;
            known.Add(id); metadata += 128;
        }
        byte[][] Updates(BinaryCompressionDefinition[] definitions) {
            if (definitions.Length == 0) return Array.Empty<byte[]>();
            var output = new List<byte[]>(); var page = new List<BinaryCompressionDefinition>(); int size = 8, limit = Math.Min(16384, maximumBytes);
            foreach (var definition in definitions) {
                int cost = definition.Data.Length + 16;
                if (cost + 8 > limit) continue;
                if (size + cost > limit) { output.Add(BinaryCompressionCodec.Update(page.ToArray())); page.Clear(); size = 8; }
                page.Add(definition); size += cost;
            }
            if (page.Count != 0) output.Add(BinaryCompressionCodec.Update(page.ToArray()));
            return output.ToArray();
        }
        void RequireOpen() { if (disposed) throw new ObjectDisposedException(nameof(BinaryCompressionSession)); }
        void RequireMessage(byte[] message) {
            if (message == null || message.Length > maximumBytes || message.Length < 4 || message[0] != 'S' || message[1] != 'B' || message[2] != 1)
                throw new InvalidDataException("Expected a bounded SB1 message.");
        }
        /// <summary>Release this connection's dictionary and acknowledgement metadata.</summary>
        public void Dispose() { lock (sync) { if (disposed) return; disposed = true; cache.Dispose(); known.Clear(); BinaryCompressionMemory.Release(metadata); metadata = 0; } GC.SuppressFinalize(this); }
        ~BinaryCompressionSession() { Dispose(); }
    }
}
