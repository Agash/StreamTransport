using System.Numerics;
using Agash.StreamTransport.MacOS.Metal;
using Agash.StreamTransport.Media;

namespace Agash.StreamTransport.MacOS.Tests;

[TestClass]
public sealed class MetalProcessorTests
{
    private const int Width = 64;
    private const int Height = 32;
    private static readonly MetalVideoProcessorFactory Factory = new();

    [TestMethod]
    public void BgraToNv12_MatchesTheBt709VideoRangeMatrix()
    {
        byte[] pixels = Picture();
        using VideoFrameLease output = Run(
            Bgra(pixels),
            Description(PixelFormat.Bgra, Width),
            new VideoProcessing(Nv12Output()),
            out VideoProcessorInfo info
        );

        Assert.AreEqual(new VideoSize(Width, Height), info.Output.Size);
        byte[][] planes = Download(output, PixelFormat.Nv12, Width);
        (Vector4 y, Vector4 cb, Vector4 cr) = ColorConversion.RgbToYCbCr(VideoColor.Bt709);
        for (int row = 0; row < Height; row += 2)
        {
            for (int x = 0; x < Width; x += 2)
            {
                // Pictures are flat over each 2x2 block, so the chroma average is the block's colour.
                Vector3 rgb = Rgb(pixels, x, row);
                Assert.AreEqual(
                    Apply(y, rgb) * 255,
                    planes[0][(row * Width) + x],
                    1.0,
                    $"Luma at {x},{row}."
                );
                int c = ((row / 2) * Width) + x;
                Assert.AreEqual(Apply(cb, rgb) * 255, planes[1][c], 1.0, $"Cb at {x},{row}.");
                Assert.AreEqual(Apply(cr, rgb) * 255, planes[1][c + 1], 1.0, $"Cr at {x},{row}.");
            }
        }

        Assert.AreEqual(VideoColor.Bt709, output.Frame.Color);
        Assert.AreEqual(
            new MediaTime(42),
            output.Frame.Timestamp.Time,
            "the timestamp carries through"
        );
    }

    [TestMethod]
    public void AlphaPack_PutsAlphaInTheRightHalfLumaWithNeutralChroma()
    {
        byte[] pixels = Picture();
        using VideoFrameLease output = Run(
            Bgra(pixels),
            Description(PixelFormat.Bgra, Width),
            new VideoProcessing(Nv12Output(), Alpha: AlphaLayout.PackSideBySide),
            out VideoProcessorInfo info
        );

        Assert.AreEqual(new VideoSize(Width * 2, Height), info.Output.Size);
        byte[][] planes = Download(output, PixelFormat.Nv12, Width * 2);
        for (int row = 0; row < Height; row += 2)
        {
            for (int x = 0; x < Width; x += 2)
            {
                double alpha = pixels[(((row * Width) + x) * 4) + 3];
                Assert.AreEqual(
                    16 + (219 * alpha / 255),
                    planes[0][(row * Width * 2) + Width + x],
                    1.0
                );
                int c = ((row / 2) * Width * 2) + Width + x;
                Assert.AreEqual(128, planes[1][c], 1.0);
                Assert.AreEqual(128, planes[1][c + 1], 1.0);
            }
        }
    }

    [TestMethod]
    public void PackThenUnpack_ReturnsColourAndAlpha()
    {
        byte[] pixels = Picture();
        using VideoFrameLease packed = Run(
            Bgra(pixels),
            Description(PixelFormat.Bgra, Width),
            new VideoProcessing(Nv12Output(), Alpha: AlphaLayout.PackSideBySide),
            out _
        );

        using IVideoProcessor unpack = Factory.Create(
            Description(PixelFormat.Nv12, Width * 2),
            new VideoProcessing(BgraOutput(), Alpha: AlphaLayout.UnpackSideBySide)
        );
        Keeper keeper = new();
        unpack.Process(packed.Frame, keeper);
        using VideoFrameLease unpacked = keeper.Kept!;

        Assert.AreEqual(new VideoSize(Width, Height), unpack.Info.Output.Size);
        byte[] back = Download(unpacked, PixelFormat.Bgra, Width)[0];
        for (int i = 0; i < pixels.Length; i++)
        {
            Assert.AreEqual(pixels[i], back[i], 2.0, $"Byte {i} ({"BGRA"[i % 4]}).");
        }

        Assert.AreEqual(VideoColor.Srgb, unpacked.Frame.Color);
    }

    [TestMethod]
    public void Nv12ToBgra_ExpandsVideoRangeGrey()
    {
        byte[] luma = [.. Enumerable.Repeat((byte)128, Width * Height)];
        byte[] chroma = [.. Enumerable.Repeat((byte)128, Width * Height / 2)];
        PooledSurface surface = TestSurfaces.Upload(PixelFormat.Nv12, Width, Height, luma, chroma);
        try
        {
            using VideoFrameLease output = Run(
                TestSurfaces.Frame(surface, PixelFormat.Nv12, Width, Height, VideoColor.Bt709),
                Description(PixelFormat.Nv12, Width),
                new VideoProcessing(BgraOutput()),
                out VideoProcessorInfo info
            );

            Assert.AreEqual(PixelFormat.Bgra, info.Output.PixelFormat);
            CollectionAssert.AreEqual(
                new byte[] { 130, 130, 130, 255 },
                Download(output, PixelFormat.Bgra, Width)[0][..4]
            );
        }
        finally
        {
            surface.Release();
        }
    }

    [TestMethod]
    public void KeptOutput_IsNotReusedByLaterFrames()
    {
        byte[] first = Picture();
        byte[] second = [.. first.Select(b => (byte)(255 - b))];
        using IVideoProcessor processor = Factory.Create(
            Description(PixelFormat.Bgra, Width),
            new VideoProcessing(Nv12Output())
        );
        Keeper keeper = new();
        Process(processor, first, keeper);
        using VideoFrameLease kept = keeper.Kept!;
        byte[][] before = Download(kept, PixelFormat.Nv12, Width);

        for (int i = 0; i < 4; i++)
        {
            Process(processor, second, new Discarder());
        }

        CollectionAssert.AreEqual(before[0], Download(kept, PixelFormat.Nv12, Width)[0]);
    }

    [TestMethod]
    public void QueryCapabilities_RefusesWhatItCannotDo()
    {
        VideoStreamDescription bgra = Description(PixelFormat.Bgra, Width);
        VideoConstraints nv12 = new([VideoStorageKind.IOSurface], [PixelFormat.Nv12]);

        Assert.IsNull(
            Factory.QueryCapabilities(
                bgra with
                {
                    Storage = VideoStorageKind.Cpu,
                },
                new VideoProcessing(nv12)
            )
        );
        Assert.IsNull(
            Factory.QueryCapabilities(
                bgra,
                new VideoProcessing(VideoConstraints.Cpu(PixelFormat.Nv12))
            )
        );
        Assert.IsNull(
            Factory.QueryCapabilities(
                bgra,
                new VideoProcessing(nv12, Alpha: AlphaLayout.UnpackSideBySide)
            )
        );
        Assert.IsNull(
            Factory.QueryCapabilities(
                bgra with
                {
                    Size = new VideoSize(63, 32),
                },
                new VideoProcessing(nv12)
            )
        );
        Assert.IsNull(
            Factory.QueryCapabilities(
                bgra with
                {
                    PixelFormat = PixelFormat.Rgba,
                },
                new VideoProcessing(nv12)
            ),
            "nothing on macOS makes RGBA IOSurfaces"
        );
        Assert.IsNull(
            Factory.QueryCapabilities(
                bgra with
                {
                    PixelFormat = PixelFormat.Nv12,
                },
                new VideoProcessing(BgraOutput(), new VideoSize(32, 16))
            ),
            "NV12 to BGRA does not scale"
        );
        Assert.AreEqual(
            new VideoSize(32, 16),
            Factory
                .QueryCapabilities(bgra, new VideoProcessing(nv12, new VideoSize(32, 16)))!
                .Output.Size,
            "BGRA to NV12 scales"
        );
    }

    private static VideoConstraints Nv12Output() =>
        new([VideoStorageKind.IOSurface], [PixelFormat.Nv12], TestSurfaces.Device);

    private static VideoConstraints BgraOutput() =>
        new([VideoStorageKind.IOSurface], [PixelFormat.Bgra], TestSurfaces.Device);

    private static VideoStreamDescription Description(PixelFormat format, int width) =>
        new(VideoStorageKind.IOSurface, format, new VideoSize(width, Height), TestSurfaces.Device);

    private static byte[][] Download(VideoFrameLease lease, PixelFormat format, int width)
    {
        Assert.IsTrue(lease.Frame.Storage.TryGetValue(out IOSurfaceImage image));
        return TestSurfaces.Download(image, format, width, Height);
    }

    // A BGRA surface of the picture, released when the returned frame's test is done with it.
    private static PooledSurface Bgra(byte[] pixels) =>
        TestSurfaces.Upload(PixelFormat.Bgra, Width, Height, pixels);

    private static VideoFrameLease Run(
        PooledSurface input,
        VideoStreamDescription description,
        VideoProcessing processing,
        out VideoProcessorInfo info
    )
    {
        try
        {
            return Run(
                TestSurfaces.Frame(
                    input,
                    description.PixelFormat,
                    description.Size.Width,
                    Height,
                    VideoColor.Srgb
                ),
                description,
                processing,
                out info
            );
        }
        finally
        {
            input.Release();
        }
    }

    private static VideoFrameLease Run(
        in VideoFrame input,
        VideoStreamDescription description,
        VideoProcessing processing,
        out VideoProcessorInfo info
    )
    {
        using IVideoProcessor processor = Factory.Create(description, processing);
        info = processor.Info;
        Keeper keeper = new();
        processor.Process(in input, keeper);
        return keeper.Kept!;
    }

    private static void Process(
        IVideoProcessor processor,
        byte[] pixels,
        IVideoFrameConsumer consumer
    )
    {
        PooledSurface input = Bgra(pixels);
        try
        {
            processor.Process(
                TestSurfaces.Frame(input, PixelFormat.Bgra, Width, Height, VideoColor.Srgb),
                consumer
            );
        }
        finally
        {
            input.Release();
        }
    }

    // Colour blocks of 2x2 pixels with varying alpha, so 4:2:0 chroma loses nothing.
    private static byte[] Picture()
    {
        byte[] pixels = new byte[Width * Height * 4];
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int bx = x / 2;
                int by = y / 2;
                int i = ((y * Width) + x) * 4;
                pixels[i] = (byte)(40 + ((bx + by) * 11 % 180));
                pixels[i + 1] = (byte)(30 + (by * 13 % 190));
                pixels[i + 2] = (byte)(20 + (bx * 7 % 200));
                pixels[i + 3] = (byte)(((bx * 37) + (by * 5)) % 256);
            }
        }

        return pixels;
    }

    private static Vector3 Rgb(byte[] pixels, int x, int y)
    {
        int i = ((y * Width) + x) * 4;
        return new Vector3(pixels[i + 2], pixels[i + 1], pixels[i]) / 255;
    }

    private static float Apply(Vector4 row, Vector3 rgb) =>
        Vector3.Dot(new Vector3(row.X, row.Y, row.Z), rgb) + row.W;

    private sealed class Keeper : IVideoFrameConsumer
    {
        public VideoFrameLease? Kept { get; private set; }

        public void OnFrame(in VideoFrame frame) => Kept = frame.Retain();
    }

    private sealed class Discarder : IVideoFrameConsumer
    {
        public void OnFrame(in VideoFrame frame) { }
    }
}
