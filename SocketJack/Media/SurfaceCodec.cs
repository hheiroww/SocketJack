using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using SocketJack.Serialization;

namespace SocketJack.Media {
    public readonly struct SurfaceViewport {
        public SurfaceViewport(int x, int y, int width, int height) {
            X = x; Y = y; Width = width; Height = height;
        }
        public int X { get; }
        public int Y { get; }
        public int Width { get; }
        public int Height { get; }
    }

    public sealed class SurfaceTile {
        internal SurfaceTile(int width, int height, int x, int y, int tileWidth, int tileHeight,
            long generation, long sequence, byte[] pixels) {
            Width = width; Height = height; X = x; Y = y; TileWidth = tileWidth;
            TileHeight = tileHeight; Generation = generation; Sequence = sequence; Pixels = pixels;
        }
        public int Width { get; }
        public int Height { get; }
        public int X { get; }
        public int Y { get; }
        public int TileWidth { get; }
        public int TileHeight { get; }
        public long Generation { get; }
        public long Sequence { get; }
        /// <summary>Independently decodable, tightly packed BGRA pixels.</summary>
        public byte[] Pixels { get; }
    }

    /// <summary>Detects changes in BGRA surfaces and queues only the latest version of each tile.
    /// Use from a single producer or serialize calls externally. Transport pacing belongs to the caller.</summary>
    public sealed class SurfaceTileEncoder {
        public const int TileSize = 32, MaximumDimension = 4096, MaximumPixels = 8294400, MaximumPacketBytes = 8192;
        internal const int HeaderSize = 33, MaximumTileBytes = HeaderSize + TileSize * TileSize * 4;
        internal const long MaximumJsInteger = 9007199254740991L;
        readonly BinarySerializer formatter = new BinarySerializer();
        readonly SortedSet<(int Priority, long Order, int Index)> pending = new SortedSet<(int, long, int)>();
        Entry[] entries = Array.Empty<Entry>();
        int width, height, columns;
        long generation, sequence, order;
        SurfaceViewport? viewport;

        sealed class Entry {
            internal int X, Y, Width, Height;
            internal byte[] Pixels;
            internal long Sequence;
            internal (int Priority, long Order, int Index)? Queued;
        }

        public int PendingCount => pending.Count;

        public void Update(byte[] bgra, int width, int height, int stride, SurfaceViewport? viewport = null) {
            if (bgra == null) throw new ArgumentNullException(nameof(bgra));
            ValidateSurface(width, height);
            if (stride < width * 4 || (long)(height - 1) * stride + width * 4 > bgra.Length)
                throw new ArgumentException("Invalid BGRA stride or frame length.", nameof(stride));
            if (viewport.HasValue && (viewport.Value.Width < 0 || viewport.Value.Height < 0))
                throw new ArgumentOutOfRangeException(nameof(viewport));
            bool resized = this.width != width || this.height != height;
            bool viewportChanged = !Nullable.Equals(this.viewport, viewport);
            this.viewport = viewport;
            if (resized) {
                if (generation >= MaximumJsInteger) throw new InvalidOperationException("Surface generation limit reached.");
                this.width = width; this.height = height; generation++; sequence = 0; order = 0;
                columns = (width + TileSize - 1) / TileSize;
                entries = new Entry[columns * ((height + TileSize - 1) / TileSize)];
                pending.Clear();
            }
            for (int i = 0; i < entries.Length; i++) {
                var entry = entries[i];
                if (entry == null) {
                    int x = i % columns * TileSize, y = i / columns * TileSize;
                    entry = new Entry { X = x, Y = y, Width = Math.Min(TileSize, width - x), Height = Math.Min(TileSize, height - y) };
                    entry.Pixels = new byte[entry.Width * entry.Height * 4];
                    entries[i] = entry;
                }
                int rowBytes = entry.Width * 4;
                int offset = entry.Y * stride + entry.X * 4;
                bool changed = resized;
                for (int row = 0; row < entry.Height && !changed; row++)
                    changed = !bgra.AsSpan(offset + row * stride, rowBytes).SequenceEqual(entry.Pixels.AsSpan(row * rowBytes, rowBytes));
                if (changed) {
                    if (sequence >= MaximumJsInteger) throw new InvalidOperationException("Surface sequence limit reached.");
                    for (int row = 0; row < entry.Height; row++)
                        Buffer.BlockCopy(bgra, offset + row * stride, entry.Pixels, row * rowBytes, rowBytes);
                    entry.Sequence = ++sequence;
                    Queue(entry, i, refresh: false);
                } else if (viewportChanged && entry.Queued is { } queued && queued.Priority != 2) {
                    Queue(entry, i, refresh: false);
                }
            }
        }

        public void Refresh() {
            for (int i = 0; i < entries.Length; i++)
                if (!entries[i].Queued.HasValue) Queue(entries[i], i, refresh: true);
        }

        void Queue(Entry entry, int index, bool refresh) {
            if (entry.Queued is { } existing) pending.Remove(existing);
            int priority = refresh ? 2 : InViewport(entry) ? 0 : 1;
            var next = (priority, -++order, index);
            entry.Queued = next;
            pending.Add(next);
        }

        bool InViewport(Entry entry) {
            if (!viewport.HasValue) return true;
            var view = viewport.Value;
            return view.Width > 0 && view.Height > 0 && entry.X < (long)view.X + view.Width &&
                entry.Y < (long)view.Y + view.Height && view.X < entry.X + entry.Width && view.Y < entry.Y + entry.Height;
        }

        public bool TryDequeue(out byte[] packet) {
            packet = null;
            if (pending.Count == 0) return false;
            var next = pending.Min;
            var entry = entries[next.Index];
            byte[] bytes = new byte[HeaderSize + entry.Pixels.Length];
            bytes[0] = 1;
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(1), generation);
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(9), entry.Sequence);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(17), width);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(21), height);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(25), entry.X);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(29), entry.Y);
            entry.Pixels.CopyTo(bytes, HeaderSize);
            // An empty dictionary makes this SBC1 frame independent of every other tile.
            byte[] serialized = formatter.Serialize(bytes);
            packet = BinaryCompressionCodec.Encode(serialized, null, null);
            pending.Remove(next);
            entry.Queued = null;
            return true;
        }

        internal static void ValidateSurface(int width, int height) {
            if (width < 1 || height < 1 || width > MaximumDimension || height > MaximumDimension ||
                (long)width * height > MaximumPixels)
                throw new InvalidDataException("Surface dimensions exceed their limit.");
        }
    }

    /// <summary>Rejects stale tiles and old generations. Reset when switching to a new logical stream.</summary>
    public sealed class SurfaceTileDecoder {
        static readonly BinarySerializer Formatter = new BinarySerializer();
        readonly Dictionary<int, long> latest = new Dictionary<int, long>();
        long generation;
        int width, height;

        public void Reset() { generation = 0; width = height = 0; latest.Clear(); }

        public bool TryApply(byte[] packet, out SurfaceTile tile) {
            tile = Decode(packet);
            if (tile.Generation < generation) { tile = null; return false; }
            if (tile.Generation > generation) {
                latest.Clear(); generation = tile.Generation; width = tile.Width; height = tile.Height;
            }
            if (tile.Width != width || tile.Height != height)
                throw new InvalidDataException("Surface dimensions changed without a new generation.");
            int index = tile.Y * width + tile.X;
            if (latest.TryGetValue(index, out long previous) && tile.Sequence <= previous) {
                tile = null;
                return false;
            }
            latest[index] = tile.Sequence;
            return true;
        }

        public static SurfaceTile Decode(byte[] packet) {
            if (packet == null || packet.Length > SurfaceTileEncoder.MaximumPacketBytes)
                throw new InvalidDataException("Surface packet exceeds its limit.");
            byte[] serialized = BinaryCompressionCodec.Decode(packet, SurfaceTileEncoder.MaximumTileBytes + 6, null, out var definitions);
            if (definitions.Length != 0) throw new InvalidDataException("Surface packet contains a compression dictionary.");
            byte[] bytes = Formatter.DeserializeByteArray(serialized);
            if (bytes.Length < SurfaceTileEncoder.HeaderSize || bytes[0] != 1)
                throw new InvalidDataException("Unsupported surface tile version.");
            long generation = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(1));
            long sequence = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(9));
            int width = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(17));
            int height = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(21));
            int x = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(25));
            int y = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(29));
            SurfaceTileEncoder.ValidateSurface(width, height);
            const int size = SurfaceTileEncoder.TileSize;
            if (x < 0 || y < 0 || x >= width || y >= height || x % size != 0 || y % size != 0 ||
                generation < 1 || sequence < 1 || generation > SurfaceTileEncoder.MaximumJsInteger ||
                sequence > SurfaceTileEncoder.MaximumJsInteger)
                throw new InvalidDataException("Invalid surface tile coordinates or sequence.");
            int tileWidth = Math.Min(size, width - x), tileHeight = Math.Min(size, height - y);
            var pixels = bytes.AsSpan(SurfaceTileEncoder.HeaderSize);
            if (pixels.Length != tileWidth * tileHeight * 4) throw new InvalidDataException("Invalid surface payload length.");
            return new SurfaceTile(width, height, x, y, tileWidth, tileHeight, generation, sequence, pixels.ToArray());
        }
    }
}
