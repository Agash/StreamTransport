using Agash.StreamTransport.Media;

namespace Agash.StreamTransport.Media.Tests;

/// <summary>
/// The left part of a 4:2:0 frame is the same memory at the same strides, narrower: the colour of a
/// side-by-side frame, kept without a copy.
/// </summary>
[TestClass]
public sealed class VideoFrameNarrowingTests
{
    private const int Width = 16;
    private const int Height = 4;

    [TestMethod]
    [DataRow(PixelFormat.Nv12)]
    [DataRow(PixelFormat.I420)]
    public void LeftOf_KeepsTheLeftPixelsOverTheSameMemory(PixelFormat format)
    {
        VideoSize size = new(Width, Height);
        byte[] pixels = new byte[PlaneLayout.PackedSize(format, size)];
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)i;
        }

        VideoFrame frame = new(
            new CpuImage(PlaneLayout.Packed(format, size)),
            new VideoFormat(format, Width, Height),
            MediaTimestamp.Captured(MediaTime.Zero),
            pixels
        );

        VideoFrame left = frame.LeftOf(Width / 2);

        Assert.AreEqual(new VideoFormat(format, Width / 2, Height), left.Format);
        Assert.IsTrue(left.Storage.TryGetValue(out CpuImage image));
        Assert.AreEqual(Width, image.Planes[0].Stride, "the stride stays the source's");
        for (int plane = 0; plane < left.PlaneCount; plane++)
        {
            Assert.IsTrue(
                left.GetPlane(plane).Overlaps(frame.GetPlane(plane)),
                $"plane {plane} is the source's memory"
            );
        }

        Assert.AreEqual(pixels[Width + 3], left.GetPlane(0)[image.Planes[0].Stride + 3]);
    }

    [TestMethod]
    [DataRow(PixelFormat.Nv12)]
    [DataRow(PixelFormat.I420)]
    public void TopOf_KeepsTheTopRowsOverTheSameMemory(PixelFormat format)
    {
        VideoFrame frame = Frame(format);

        VideoFrame top = frame.TopOf(Height / 2);

        Assert.AreEqual(new VideoFormat(format, Width, Height / 2), top.Format);
        Assert.IsTrue(top.Storage.TryGetValue(out CpuImage image));
        Assert.AreEqual(Width, image.Planes[0].Stride);
        for (int plane = 0; plane < top.PlaneCount; plane++)
        {
            Assert.IsTrue(top.GetPlane(plane).Overlaps(frame.GetPlane(plane)));
        }

        _ = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Frame(format).TopOf(3));
    }

    [TestMethod]
    public void PackedSizes_DoubleOneSide_AndColourSizeHalvesIt()
    {
        VideoSize colour = new(2560, 1440);

        Assert.AreEqual(new VideoSize(5120, 1440), AlphaLayout.PackSideBySide.PackedSize(colour));
        Assert.AreEqual(new VideoSize(2560, 2880), AlphaLayout.PackStacked.PackedSize(colour));
        Assert.AreEqual(colour, AlphaLayout.UnpackStacked.ColourSize(new VideoSize(2560, 2880)));
        Assert.AreEqual(colour, AlphaLayout.UnpackSideBySide.ColourSize(new VideoSize(5120, 1440)));
        Assert.AreEqual(AlphaLayout.UnpackStacked, AlphaLayout.PackStacked.Unpacking());
        Assert.IsTrue(AlphaLayout.PackStacked.IsPacked());
        Assert.IsFalse(AlphaLayout.Layer.IsPacked());
    }

    [TestMethod]
    public void LeftOf_OddWidthOrPackedFormat_Throws()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Frame(PixelFormat.Nv12).LeftOf(7));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            Frame(PixelFormat.Nv12).LeftOf(Width + 2)
        );
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            Frame(PixelFormat.Bgra).LeftOf(Width / 2)
        );
    }

    private static VideoFrame Frame(PixelFormat format)
    {
        VideoSize size = new(Width, Height);
        return new(
            new CpuImage(PlaneLayout.Packed(format, size)),
            new VideoFormat(format, Width, Height),
            MediaTimestamp.Captured(MediaTime.Zero),
            new byte[PlaneLayout.PackedSize(format, size)]
        );
    }
}
