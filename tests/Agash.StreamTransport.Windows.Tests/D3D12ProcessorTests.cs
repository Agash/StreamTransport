using System.Numerics;
using Agash.StreamTransport.Codecs.FFmpeg;
using Agash.StreamTransport.Media;
using Agash.StreamTransport.Windows.Direct3D12;
using Windows.Win32.Graphics.Dxgi.Common;

namespace Agash.StreamTransport.Windows.Tests;

[TestClass]
[OSCondition(OperatingSystems.Windows)]
public sealed class D3D12ProcessorTests
{
    private const int Width = 64;
    private const int Height = 32;
    private static readonly D3D12VideoProcessorFactory Factory = new();

    [TestMethod]
    [DataRow(PixelFormat.Bgra)]
    [DataRow(PixelFormat.Rgba)]
    public void RgbToNv12_MatchesTheBt709VideoRangeMatrix(PixelFormat format)
    {
        using var gpu = TestGpu.Open();
        byte[] pixels = Picture(format);
        Kept output = Run(
            gpu,
            format,
            pixels,
            new VideoProcessing(Nv12Output(gpu)),
            out VideoProcessorInfo info
        );

        Assert.AreEqual(new VideoSize(Width, Height), info.Output.Size);
        byte[][] planes = gpu.Download(output.Image, 2);
        (Vector4 y, Vector4 cb, Vector4 cr) = ColorConversion.RgbToYCbCr(VideoColor.Bt709);
        for (int row = 0; row < Height; row += 2)
        {
            for (int x = 0; x < Width; x += 2)
            {
                // Pictures are flat over each 2x2 block, so the chroma average is the block's colour.
                Vector3 rgb = Rgb(pixels, format, x, row);
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

        Assert.AreEqual(VideoColor.Bt709, output.Color);
    }

    [TestMethod]
    public void AlphaPack_OddColourWidth_IsRefused()
    {
        using var gpu = TestGpu.Open();
        VideoStreamDescription odd = new(
            VideoStorageKind.D3D12,
            PixelFormat.Bgra,
            new VideoSize(1919, 1080),
            gpu.Adapter
        );

        // The packed frame is 3838 wide, even, but a 4:2:0 chroma block would straddle the halves.
        Assert.IsNull(
            Factory.QueryCapabilities(
                odd,
                new VideoProcessing(Nv12Output(gpu), Alpha: AlphaLayout.PackSideBySide)
            )
        );
    }

    [TestMethod]
    public void AlphaPack_PutsAlphaInTheRightHalfLumaWithNeutralChroma()
    {
        using var gpu = TestGpu.Open();
        byte[] pixels = Picture(PixelFormat.Bgra);
        Kept output = Run(
            gpu,
            PixelFormat.Bgra,
            pixels,
            new VideoProcessing(Nv12Output(gpu), Alpha: AlphaLayout.PackSideBySide),
            out VideoProcessorInfo info
        );

        Assert.AreEqual(new VideoSize(Width * 2, Height), info.Output.Size);
        byte[][] planes = gpu.Download(output.Image, 2);
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
    public void AlphaPack_ToMemory_ReadsBackThePackedPicture()
    {
        using var gpu = TestGpu.Open();
        byte[] pixels = Picture(PixelFormat.Bgra);
        Kept output = Run(
            gpu,
            PixelFormat.Bgra,
            pixels,
            new VideoProcessing(
                VideoConstraints.Cpu(PixelFormat.Nv12),
                Alpha: AlphaLayout.PackSideBySide
            ),
            out VideoProcessorInfo info
        );

        Assert.AreEqual(VideoStorageKind.Cpu, info.Output.Storage);
        Assert.AreEqual(new VideoSize(Width * 2, Height), info.Output.Size);
        VideoFrame frame = output.Lease.Frame;
        Assert.IsTrue(frame.Storage.TryGetValue(out CpuImage image));
        ReadOnlySpan<byte> luma = frame.GetPlane(0);
        ReadOnlySpan<byte> chroma = frame.GetPlane(1);
        for (int row = 0; row < Height; row += 2)
        {
            for (int x = 0; x < Width; x += 2)
            {
                double alpha = pixels[(((row * Width) + x) * 4) + 3];
                Assert.AreEqual(
                    16 + (219 * alpha / 255),
                    luma[(row * image.Planes[0].Stride) + Width + x],
                    1.0
                );
                Assert.AreEqual(128, chroma[((row / 2) * image.Planes[1].Stride) + Width + x], 1.0);
            }
        }

        output.Lease.Dispose();
    }

    [TestMethod]
    public void PackThenUnpack_ReturnsColourAndAlpha()
    {
        using var gpu = TestGpu.Open();
        byte[] pixels = Picture(PixelFormat.Bgra);
        Kept packed = Run(
            gpu,
            PixelFormat.Bgra,
            pixels,
            new VideoProcessing(Nv12Output(gpu), Alpha: AlphaLayout.PackSideBySide),
            out _
        );

        Kept unpacked = Run(
            gpu,
            packed,
            new VideoStreamDescription(
                VideoStorageKind.D3D12,
                PixelFormat.Nv12,
                new VideoSize(Width * 2, Height),
                gpu.Adapter
            ),
            new VideoProcessing(
                new VideoConstraints([VideoStorageKind.D3D12], [PixelFormat.Bgra], gpu.Adapter),
                Alpha: AlphaLayout.UnpackSideBySide
            ),
            out VideoProcessorInfo info
        );

        Assert.AreEqual(new VideoSize(Width, Height), info.Output.Size);
        byte[] back = gpu.Download(unpacked.Image, 1)[0];
        for (int i = 0; i < pixels.Length; i++)
        {
            Assert.AreEqual(pixels[i], back[i], 2.0, $"Byte {i} ({"BGRA"[i % 4]}).");
        }
    }

    [TestMethod]
    public void Nv12ToBgra_ExpandsVideoRangeGrey()
    {
        using var gpu = TestGpu.Open();
        byte[] luma = [.. Enumerable.Repeat((byte)128, Width * Height)];
        byte[] chroma = [.. Enumerable.Repeat((byte)128, Width * Height / 2)];
        nint texture = gpu.Upload(DXGI_FORMAT.DXGI_FORMAT_NV12, Width, Height, luma, chroma);

        Kept output = Run(
            gpu,
            texture,
            PixelFormat.Nv12,
            new VideoStreamDescription(
                VideoStorageKind.D3D12,
                PixelFormat.Nv12,
                new VideoSize(Width, Height),
                gpu.Adapter
            ),
            new VideoProcessing(
                new VideoConstraints(
                    [VideoStorageKind.D3D12],
                    [PixelFormat.Rgba, PixelFormat.Bgra],
                    gpu.Adapter
                )
            ),
            VideoColor.Bt709,
            out VideoProcessorInfo info
        );

        Assert.AreEqual(PixelFormat.Rgba, info.Output.PixelFormat, "the sink's first choice");
        byte[] rgba = gpu.Download(output.Image, 1)[0];
        CollectionAssert.AreEqual(new byte[] { 130, 130, 130, 255 }, rgba[..4]);
        Assert.AreEqual(VideoColor.Srgb, output.Color);
    }

    [TestMethod]
    public void QueryCapabilities_RefusesWhatItCannotDo()
    {
        VideoStreamDescription bgra = new(
            VideoStorageKind.D3D12,
            PixelFormat.Bgra,
            new VideoSize(64, 32)
        );
        VideoConstraints nv12 = new([VideoStorageKind.D3D12], [PixelFormat.Nv12]);

        Assert.IsNull(
            Factory.QueryCapabilities(
                bgra with
                {
                    Storage = VideoStorageKind.Cpu,
                },
                new VideoProcessing(nv12)
            )
        );
        Assert.AreEqual(
            VideoStorageKind.Cpu,
            Factory
                .QueryCapabilities(
                    bgra,
                    new VideoProcessing(VideoConstraints.Cpu(PixelFormat.Nv12))
                )
                ?.Output.Storage,
            "a consumer in memory gets the result read back"
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
                    PixelFormat = PixelFormat.Nv12,
                },
                new VideoProcessing(
                    new VideoConstraints([VideoStorageKind.D3D12], [PixelFormat.Bgra]),
                    new VideoSize(32, 16)
                )
            ),
            "NV12 to RGB does not scale"
        );
        Assert.AreEqual(
            new VideoSize(32, 16),
            Factory
                .QueryCapabilities(bgra, new VideoProcessing(nv12, new VideoSize(32, 16)))!
                .Output.Size,
            "RGB to NV12 scales"
        );
    }

    [TestMethod]
    public void CpuBgra_IsUploadedAsItIs()
    {
        using var gpu = TestGpu.Open();
        byte[] pixels = Picture(PixelFormat.Bgra);
        using IVideoProcessor processor = Factory.Create(
            new VideoStreamDescription(VideoStorageKind.Cpu, PixelFormat.Bgra, new(Width, Height)),
            new VideoProcessing(
                new VideoConstraints([VideoStorageKind.D3D12], [PixelFormat.Bgra], gpu.Adapter)
            )
        );
        Keeper keeper = new();

        processor.Process(
            new VideoFrame(
                new CpuImage(PlaneLayout.Packed(PixelFormat.Bgra, new(Width, Height))),
                new VideoFormat(PixelFormat.Bgra, Width, Height),
                MediaTimestamp.Captured(new MediaTime(42)),
                pixels,
                VideoColor.Srgb
            ),
            keeper
        );

        Kept output = keeper.Kept!;
        Assert.AreEqual(VideoStorageKind.D3D12, output.Lease.Frame.Storage.Kind);
        Assert.AreEqual(gpu.Adapter, output.Image.Adapter);
        CollectionAssert.AreEqual(pixels, gpu.Download(output.Image, 1)[0]);
        output.Lease.Dispose();
    }

    [TestMethod]
    public void Yuva420InMemory_ReachesAnRgbTextureThroughAChain()
    {
        using var gpu = TestGpu.Open();
        MediaCodecRegistry registry = new(
            [],
            [],
            [new FFmpegVideoProcessorFactory(), Factory],
            [],
            []
        );
        VideoSize size = new(Width, Height);
        byte[] yuva = new byte[PlaneLayout.PackedSize(PixelFormat.Yuva420, size)];
        var layout = PlaneLayout.Packed(PixelFormat.Yuva420, size);
        yuva.AsSpan(0, layout[1].Offset).Fill(128);
        yuva.AsSpan(layout[1].Offset, layout[3].Offset - layout[1].Offset).Fill(128);
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                yuva[layout[3].Offset + (y * Width) + x] = (byte)(x * 255 / (Width - 1));
            }
        }

        Assert.IsTrue(
            registry.TryCreateVideoProcessor(
                new VideoStreamDescription(VideoStorageKind.Cpu, PixelFormat.Yuva420, size),
                new VideoProcessing(
                    new VideoConstraints([VideoStorageKind.D3D12], [PixelFormat.Bgra], gpu.Adapter)
                ),
                out IVideoProcessor? chain
            )
        );
        using (chain)
        {
            Keeper keeper = new();
            chain.Process(
                new VideoFrame(
                    new CpuImage(layout),
                    new VideoFormat(PixelFormat.Yuva420, Width, Height),
                    MediaTimestamp.Captured(new MediaTime(42)),
                    yuva,
                    VideoColor.Bt709
                ),
                keeper
            );

            Kept output = keeper.Kept!;
            byte[] bgra = gpu.Download(output.Image, 1)[0];
            for (int x = 0; x < Width; x++)
            {
                Assert.AreEqual(x * 255 / (Width - 1), bgra[(4 * x) + 3], 2.0, $"alpha at {x}");
            }

            output.Lease.Dispose();
        }
    }

    private static VideoConstraints Nv12Output(TestGpu gpu) =>
        new([VideoStorageKind.D3D12], [PixelFormat.Nv12], gpu.Adapter);

    // Colour blocks of 2x2 pixels with varying alpha, so 4:2:0 chroma loses nothing.
    private static byte[] Picture(PixelFormat format)
    {
        byte[] pixels = new byte[Width * Height * 4];
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int bx = x / 2;
                int by = y / 2;
                byte r = (byte)(20 + (bx * 7 % 200));
                byte g = (byte)(30 + (by * 13 % 190));
                byte b = (byte)(40 + ((bx + by) * 11 % 180));
                byte a = (byte)((bx * 37 + by * 5) % 256);
                int i = ((y * Width) + x) * 4;
                (pixels[i], pixels[i + 1], pixels[i + 2]) =
                    format == PixelFormat.Bgra ? (b, g, r) : (r, g, b);
                pixels[i + 3] = a;
            }
        }

        return pixels;
    }

    private static Vector3 Rgb(byte[] pixels, PixelFormat format, int x, int y)
    {
        int i = ((y * Width) + x) * 4;
        return format == PixelFormat.Bgra
            ? new Vector3(pixels[i + 2], pixels[i + 1], pixels[i]) / 255
            : new Vector3(pixels[i], pixels[i + 1], pixels[i + 2]) / 255;
    }

    private static float Apply(Vector4 row, Vector3 rgb) =>
        Vector3.Dot(new Vector3(row.X, row.Y, row.Z), rgb) + row.W;

    private static Kept Run(
        TestGpu gpu,
        PixelFormat format,
        byte[] pixels,
        VideoProcessing processing,
        out VideoProcessorInfo info
    )
    {
        DXGI_FORMAT dxgi =
            format == PixelFormat.Bgra
                ? DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM
                : DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM;
        nint texture = gpu.Upload(dxgi, Width, Height, pixels);
        return Run(
            gpu,
            texture,
            format,
            new VideoStreamDescription(
                VideoStorageKind.D3D12,
                format,
                new VideoSize(Width, Height),
                gpu.Adapter
            ),
            processing,
            VideoColor.Srgb,
            out info
        );
    }

    private static Kept Run(
        TestGpu gpu,
        Kept input,
        VideoStreamDescription description,
        VideoProcessing processing,
        out VideoProcessorInfo info
    )
    {
        using IVideoProcessor processor = Factory.Create(description, processing);
        info = processor.Info;
        Keeper keeper = new();
        processor.Process(input.Lease.Frame, keeper);
        _ = gpu;
        return keeper.Kept!;
    }

    private static Kept Run(
        TestGpu gpu,
        nint texture,
        PixelFormat format,
        VideoStreamDescription description,
        VideoProcessing processing,
        VideoColor color,
        out VideoProcessorInfo info
    )
    {
        using IVideoProcessor processor = Factory.Create(description, processing);
        info = processor.Info;
        Keeper keeper = new();
        processor.Process(
            new VideoFrame(
                new VideoStorage(new D3D12Image(texture, 0, gpu.Adapter)),
                new VideoFormat(format, description.Size.Width, description.Size.Height),
                MediaTimestamp.Captured(new MediaTime(42)),
                color: color,
                retainer: new NotRetained()
            ),
            keeper
        );
        Assert.AreEqual(
            new MediaTime(42),
            keeper.Kept!.Lease.Frame.Timestamp.Time,
            "the timestamp carries through"
        );
        return keeper.Kept;
    }

    // A processed frame kept past its delivery, as a consumer that reads it later would.
    private sealed record Kept(VideoFrameLease Lease)
    {
        public D3D12Image Image =>
            Lease.Frame.Storage.TryGetValue(out D3D12Image image) ? image : default;

        public VideoColor Color => Lease.Frame.Color;
    }

    private sealed class Keeper : IVideoFrameConsumer
    {
        public Kept? Kept { get; private set; }

        public void OnFrame(in VideoFrame frame) => Kept = new Kept(frame.Retain());
    }

    // Test textures outlive the processing, so retaining them needs no reference.
    private sealed class NotRetained : IVideoFrameRetainer
    {
        public VideoFrameLease Retain(in VideoFrame frame) => new Borrowed(in frame);

        private sealed class Borrowed(in VideoFrame frame)
            : VideoFrameLease(
                frame.Storage,
                frame.Format,
                frame.Timestamp,
                frame.Color,
                frame.Orientation,
                frame.Duration
            )
        {
            protected override void Release() { }
        }
    }
}
