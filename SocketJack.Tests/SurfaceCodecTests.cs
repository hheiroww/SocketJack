using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Media;

namespace SocketJack.Tests;

[TestClass]
public sealed class SurfaceCodecTests
{
    [TestMethod]
    public void PrioritizesViewportAndLatestChangedTilesWithoutSendingUnchangedPixels()
    {
        const int width = 96, height = 32;
        const int stride = width * 4 + 16;
        var frame = new byte[height * stride];
        var encoder = new SurfaceTileEncoder();
        encoder.Update(frame, width, height, stride, new SurfaceViewport(32, 0, 32, 32));
        Assert.AreEqual(32, Next(encoder).X);
        Assert.AreEqual(64, Next(encoder).X);
        Assert.AreEqual(0, Next(encoder).X);

        frame[0] = 1;
        frame[128] = 2;
        encoder.Update(frame, width, height, stride, new SurfaceViewport(32, 0, 32, 32));
        frame[128] = 3;
        encoder.Update(frame, width, height, stride, new SurfaceViewport(32, 0, 32, 32));
        Assert.AreEqual(2, encoder.PendingCount);
        var inView = Next(encoder);
        Assert.AreEqual(32, inView.X);
        Assert.AreEqual(3, inView.Pixels[0]);
        Assert.AreEqual(1, Next(encoder).Pixels[0]);
        encoder.Update(frame, width, height, stride);
        Assert.IsFalse(encoder.TryDequeue(out _));
    }

    [TestMethod]
    public void DropsStaleTilesAndRefreshesLossWithoutWholeFrameDependency()
    {
        var encoder = new SurfaceTileEncoder();
        var receiver = new SurfaceTileDecoder();
        var frame = new byte[64 * 32 * 4];
        encoder.Update(frame, 64, 32, 64 * 4);
        Assert.IsTrue(encoder.TryDequeue(out var lost));
        Assert.IsTrue(encoder.TryDequeue(out var first));
        Assert.IsTrue(receiver.TryApply(first, out _));
        frame[0] = 9;
        encoder.Update(frame, 64, 32, 64 * 4);
        Assert.IsTrue(encoder.TryDequeue(out var newer));
        Assert.IsTrue(receiver.TryApply(newer, out var updated));
        Assert.AreEqual(9, updated.Pixels[0]);
        Assert.IsTrue(receiver.TryApply(lost, out _));
        Assert.IsFalse(receiver.TryApply(first, out _));

        encoder.Refresh();
        while (encoder.TryDequeue(out var refresh))
            Assert.IsFalse(receiver.TryApply(refresh, out _));
        receiver.Reset();
        encoder.Refresh();
        Assert.IsTrue(encoder.TryDequeue(out var recovery));
        Assert.IsTrue(receiver.TryApply(recovery, out _));

        encoder.Update(new byte[4], 1, 1, 4);
        Assert.IsTrue(encoder.TryDequeue(out var resized));
        Assert.IsTrue(receiver.TryApply(resized, out var tiny));
        Assert.AreEqual(1, tiny.TileWidth);
        Assert.AreEqual(1, tiny.TileHeight);
        Assert.IsFalse(receiver.TryApply(newer, out _));
    }

    [TestMethod]
    public void CompressesFlatTilesAndPreservesRawPixelsForNoisyContent()
    {
        const int size = SurfaceTileEncoder.TileSize;
        var encoder = new SurfaceTileEncoder();
        var frame = new byte[size * size * 4];
        for (int i = 0; i < frame.Length; i += 4)
        {
            frame[i] = 127;
            frame[i + 3] = 255;
        }
        encoder.Update(frame, size, size, size * 4);
        Assert.IsTrue(encoder.TryDequeue(out var compressed));
        Assert.IsTrue(compressed.Length < 100);
        Assert.AreEqual((byte)'C', compressed[2]);
        CollectionAssert.AreEqual(frame, SurfaceTileDecoder.Decode(compressed).Pixels);

        new Random(735).NextBytes(frame);
        encoder.Update(frame, size, size, size * 4);
        Assert.IsTrue(encoder.TryDequeue(out var uncompressed));
        Assert.AreEqual(1, uncompressed[2]);
        CollectionAssert.AreEqual(frame, SurfaceTileDecoder.Decode(uncompressed).Pixels);
    }

    [TestMethod]
    public void RejectsTruncationExpansionOverrunAndUnboundedSurfaces()
    {
        var encoder = new SurfaceTileEncoder();
        encoder.Update(new byte[4096], 32, 32, 128);
        Assert.IsTrue(encoder.TryDequeue(out var packet));
        Assert.ThrowsException<InvalidDataException>(() => SurfaceTileDecoder.Decode(packet[..^1]));
        Assert.ThrowsException<InvalidDataException>(() => SurfaceTileDecoder.Decode([83, 66, 67, 1, 1, 255, 127, 0]));
        Assert.ThrowsException<InvalidDataException>(() => SurfaceTileDecoder.Decode(new byte[8193]));
        Assert.ThrowsException<InvalidDataException>(() => encoder.Update(new byte[1], 4096, 4096, 1));
        Assert.ThrowsException<ArgumentException>(() => encoder.Update(new byte[3], 1, 1, 4));
    }

    private static SurfaceTile Next(SurfaceTileEncoder encoder)
    {
        Assert.IsTrue(encoder.TryDequeue(out var packet));
        return SurfaceTileDecoder.Decode(packet);
    }
}
