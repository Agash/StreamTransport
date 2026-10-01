using Agash.StreamTransport.Media;

namespace Agash.StreamTransport.Codecs.FFmpeg.Tests;

/// <summary>
/// Side-by-side alpha in system memory: the same layout the GPU processors pack, alpha as video-range
/// luma beside the colour with neutral chroma.
/// </summary>
[TestClass]
public sealed class AlphaPackingTests
{
    private const int Width = 64;
    private const int Height = 32;

    private static readonly FFmpegVideoProcessorFactory Factory = new();

    [TestMethod]
    [DataRow(PixelFormat.Nv12)]
    [DataRow(PixelFormat.I420)]
    public void Pack_PutsAlphaInTheRightHalfLumaWithNeutralChroma(PixelFormat packed)
    {
        byte[] pixels = Picture();
        using IVideoProcessor processor = Factory.Create(
            Description(PixelFormat.Bgra, Width),
            new VideoProcessing(VideoConstraints.Cpu(packed), Alpha: AlphaLayout.PackSideBySide)
        );
        Kept kept = new();

        processor.Process(Bgra(pixels), kept);

        using VideoFrameLease lease = kept.Lease!;
        Assert.AreEqual(new VideoFormat(packed, Width * 2, Height), lease.Frame.Format);
        Assert.IsTrue(lease.Frame.Storage.TryGetValue(out CpuImage image));
        ReadOnlySpan<byte> luma = lease.Frame.GetPlane(0);
        for (int row = 0; row < Height; row++)
        {
            for (int x = 0; x < Width; x++)
            {
                double alpha = pixels[(((row * Width) + x) * 4) + 3];
                Assert.AreEqual(
                    16 + (219 * alpha / 255),
                    luma[(row * image.Planes[0].Stride) + Width + x],
                    1.0,
                    $"alpha at ({x}, {row}): row {string.Join(',', luma.Slice(row * image.Planes[0].Stride, Width * 2).ToArray())}"
                );
            }
        }

        ReadOnlySpan<byte> chroma = lease.Frame.GetPlane(1);
        int chromaRight = packed == PixelFormat.Nv12 ? Width : Width / 2;
        Assert.AreEqual(128, chroma[chromaRight]);
        Assert.AreEqual(128, chroma[(image.Planes[1].Stride * ((Height / 2) - 1)) + chromaRight]);
    }

    [TestMethod]
    [DataRow(PixelFormat.Bgra)]
    [DataRow(PixelFormat.Yuva420)]
    public void PackThenUnpack_ReturnsColourAndAlpha(PixelFormat unpacked)
    {
        byte[] pixels = Picture();
        using IVideoProcessor pack = Factory.Create(
            Description(PixelFormat.Bgra, Width),
            new VideoProcessing(
                VideoConstraints.Cpu(PixelFormat.Nv12),
                Alpha: AlphaLayout.PackSideBySide
            )
        );
        Kept packed = new();
        pack.Process(Bgra(pixels), packed);
        using VideoFrameLease packedLease = packed.Lease!;

        using IVideoProcessor unpack = Factory.Create(
            Description(PixelFormat.Nv12, Width * 2),
            new VideoProcessing(VideoConstraints.Cpu(unpacked), Alpha: AlphaLayout.UnpackSideBySide)
        );
        Kept back = new();
        unpack.Process(packedLease.Frame, back);

        using VideoFrameLease lease = back.Lease!;
        Assert.AreEqual(new VideoFormat(unpacked, Width, Height), lease.Frame.Format);
        Assert.IsTrue(lease.Frame.Storage.TryGetValue(out CpuImage image));
        if (unpacked == PixelFormat.Bgra)
        {
            ReadOnlySpan<byte> bgra = lease.Frame.GetPlane(0);
            for (int row = 0; row < Height; row++)
            {
                for (int i = 0; i < Width * 4; i++)
                {
                    // Colour goes through 4:2:0 and back; alpha only through the luma mapping.
                    double tolerance = i % 4 == 3 ? 1.0 : 12.0;
                    Assert.AreEqual(
                        pixels[(row * Width * 4) + i],
                        bgra[(row * image.Planes[0].Stride) + i],
                        tolerance,
                        $"byte {i} of row {row}"
                    );
                }
            }
        }
        else
        {
            ReadOnlySpan<byte> alpha = lease.Frame.GetPlane(3);
            for (int row = 0; row < Height; row++)
            {
                for (int x = 0; x < Width; x++)
                {
                    Assert.AreEqual(
                        pixels[(((row * Width) + x) * 4) + 3],
                        alpha[(row * image.Planes[3].Stride) + x],
                        1.0,
                        $"alpha at ({x}, {row})"
                    );
                }
            }
        }
    }

    [TestMethod]
    public void Pack_OfAFormatWithoutAlpha_IsRefused() =>
        Assert.IsNull(
            Factory.QueryCapabilities(
                Description(PixelFormat.I420, Width),
                new VideoProcessing(
                    VideoConstraints.Cpu(PixelFormat.Nv12),
                    Alpha: AlphaLayout.PackSideBySide
                )
            )
        );

    // Smooth colour, so 4:2:0 subsampling keeps it, over an alpha ramp from left to right.
    private static byte[] Picture()
    {
        byte[] pixels = new byte[Width * Height * 4];
        for (int row = 0; row < Height; row++)
        {
            for (int x = 0; x < Width; x++)
            {
                int i = ((row * Width) + x) * 4;
                pixels[i] = (byte)(60 + row);
                pixels[i + 1] = (byte)(90 + (x / 2));
                pixels[i + 2] = 150;
                pixels[i + 3] = (byte)(x * 255 / (Width - 1));
            }
        }

        return pixels;
    }

    private static VideoStreamDescription Description(PixelFormat format, int width) =>
        new(VideoStorageKind.Cpu, format, new VideoSize(width, Height));

    private static VideoFrame Bgra(byte[] pixels) =>
        new(
            new CpuImage(PlaneLayout.Packed(PixelFormat.Bgra, new VideoSize(Width, Height))),
            new VideoFormat(PixelFormat.Bgra, Width, Height),
            MediaTimestamp.Captured(new MediaTime(1)),
            pixels,
            VideoColor.Srgb
        );

    private sealed class Kept : IVideoFrameConsumer
    {
        public VideoFrameLease? Lease { get; private set; }

        public void OnFrame(in VideoFrame frame) => Lease = frame.Retain();
    }
}
