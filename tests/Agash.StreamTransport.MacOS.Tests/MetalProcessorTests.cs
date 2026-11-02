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
    public async Task BgraToNv12_MatchesTheBt709MatrixWithLeftSitedChroma()
    {
        byte[] pixels = Picture();
        using Processed run = await Start(
                Bgra(pixels),
                Description(PixelFormat.Bgra, Width),
                new VideoProcessing(Nv12Output())
            )
            .FinishAsync();
        VideoFrameLease output = run.Output;
        VideoProcessorInfo info = run.Info;

        Assert.AreEqual(new VideoSize(Width, Height), info.Output.Size);
        byte[][] planes = Download(output, PixelFormat.Nv12, Width);
        (Vector4 y, Vector4 cb, Vector4 cr) = ColorConversion.RgbToYCbCr(VideoColor.Bt709);
        for (int row = 0; row < Height; row += 2)
        {
            for (int x = 0; x < Width; x += 2)
            {
                Vector3 rgb = Rgb(pixels, x, row);
                // Chroma is left-sited: a [1 2 1] filter over the column before the block and the
                // block's two, which are alike, as its two rows are.
                Vector3 sited = (Rgb(pixels, Math.Max(x - 1, 0), row) + (3 * rgb)) / 4;
                Assert.AreEqual(
                    Apply(y, rgb) * 255,
                    planes[0][(row * Width) + x],
                    1.0,
                    $"Luma at {x},{row}."
                );
                int c = ((row / 2) * Width) + x;
                Assert.AreEqual(Apply(cb, sited) * 255, planes[1][c], 1.0, $"Cb at {x},{row}.");
                Assert.AreEqual(Apply(cr, sited) * 255, planes[1][c + 1], 1.0, $"Cr at {x},{row}.");
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
    public async Task AlphaPack_PutsAlphaInTheRightHalfLumaWithNeutralChroma()
    {
        byte[] pixels = Picture();
        using Processed run = await Start(
                Bgra(pixels),
                Description(PixelFormat.Bgra, Width),
                new VideoProcessing(Nv12Output(), Alpha: AlphaLayout.PackSideBySide)
            )
            .FinishAsync();
        VideoFrameLease output = run.Output;
        VideoProcessorInfo info = run.Info;

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
    public async Task AlphaPack_ToMemory_ReadsBackThePackedPicture()
    {
        byte[] pixels = Picture();
        using Processed run = await Start(
                Bgra(pixels),
                Description(PixelFormat.Bgra, Width),
                new VideoProcessing(
                    VideoConstraints.Cpu(PixelFormat.Nv12),
                    Alpha: AlphaLayout.PackSideBySide
                )
            )
            .FinishAsync();
        VideoFrameLease output = run.Output;
        VideoProcessorInfo info = run.Info;

        Assert.AreEqual(VideoStorageKind.Cpu, info.Output.Storage);
        VideoFrame frame = output.Frame;
        Assert.IsTrue(frame.Storage.TryGetValue(out CpuImage image));
        ReadOnlySpan<byte> luma = frame.GetPlane(0);
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
            }
        }
    }

    [TestMethod]
    [DataRow(AlphaLayout.PackSideBySide)]
    [DataRow(AlphaLayout.PackStacked)]
    public async Task PackThenUnpack_ReturnsAColourRampAndAlpha(AlphaLayout layout)
    {
        byte[] pixels = Ramp();
        using Processed run = await Start(
                Bgra(pixels),
                Description(PixelFormat.Bgra, Width),
                new VideoProcessing(Nv12Output(), Alpha: layout)
            )
            .FinishAsync();
        VideoFrameLease packed = run.Output;
        VideoSize packedSize = layout.PackedSize(new VideoSize(Width, Height));
        Assert.AreEqual(packedSize, packed.Frame.Format.CodedSize);

        using IVideoProcessor unpack = Factory.Create(
            Description(PixelFormat.Nv12, Width * 2) with
            {
                Size = packedSize,
            },
            new VideoProcessing(BgraOutput(), Alpha: layout.Unpacking())
        );
        Keeper keeper = new();
        unpack.Process(packed.Frame, keeper);
        using VideoFrameLease unpacked = await keeper.TakeAsync();

        Assert.AreEqual(new VideoSize(Width, Height), unpack.Info.Output.Size);
        byte[] back = Download(unpacked, PixelFormat.Bgra, Width)[0];
        // Inside the border, where neither shader clamps, a ramp comes back as it went.
        for (int y = 1; y < Height - 1; y++)
        {
            for (int x = 1; x < Width - 1; x++)
            {
                for (int k = 0; k < 4; k++)
                {
                    int i = (((y * Width) + x) * 4) + k;
                    Assert.AreEqual(pixels[i], back[i], 2.0, $"{"BGRA"[k]} at {x},{y}.");
                }
            }
        }

        Assert.AreEqual(VideoColor.Srgb, unpacked.Frame.Color);
    }

    [TestMethod]
    public async Task Nv12ToBgra_ExpandsVideoRangeGrey()
    {
        byte[] luma = [.. Enumerable.Repeat((byte)128, Width * Height)];
        byte[] chroma = [.. Enumerable.Repeat((byte)128, Width * Height / 2)];
        PooledSurface surface = TestSurfaces.Upload(PixelFormat.Nv12, Width, Height, luma, chroma);
        try
        {
            using Processed run = await Start(
                    TestSurfaces.Frame(surface, PixelFormat.Nv12, Width, Height, VideoColor.Bt709),
                    Description(PixelFormat.Nv12, Width),
                    new VideoProcessing(BgraOutput())
                )
                .FinishAsync();
            VideoFrameLease output = run.Output;
            VideoProcessorInfo info = run.Info;

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
    public async Task Upload_BgraInMemory_ArrivesUnchangedOnTheGpu()
    {
        byte[] pixels = Picture();
        var frame = VideoFrame.FromPlanes(
            new VideoFormat(PixelFormat.Bgra, Width, Height),
            MediaTimestamp.Captured(new MediaTime(42)),
            pixels,
            Width * 4,
            color: VideoColor.Srgb
        );
        using Processed run = await Start(
                in frame,
                Description(PixelFormat.Bgra, Width) with
                {
                    Storage = VideoStorageKind.Cpu,
                    Device = null,
                },
                new VideoProcessing(BgraOutput())
            )
            .FinishAsync();
        VideoFrameLease output = run.Output;
        VideoProcessorInfo info = run.Info;

        Assert.AreEqual(VideoStorageKind.IOSurface, info.Output.Storage);
        CollectionAssert.AreEqual(pixels, Download(output, PixelFormat.Bgra, Width)[0]);
        Assert.AreEqual(new MediaTime(42), output.Frame.Timestamp.Time);
    }

    [TestMethod]
    public async Task Upload_Nv12InMemory_IsConvertedOnTheGpu()
    {
        byte[] picture = [.. Enumerable.Repeat((byte)128, Width * Height * 3 / 2)];
        var frame = VideoFrame.FromPlanes(
            new VideoFormat(PixelFormat.Nv12, Width, Height),
            MediaTimestamp.Captured(new MediaTime(7)),
            picture.AsSpan(0, Width * Height),
            Width,
            picture.AsSpan(Width * Height),
            Width,
            color: VideoColor.Bt709
        );
        using Processed run = await Start(
                in frame,
                Description(PixelFormat.Nv12, Width) with
                {
                    Storage = VideoStorageKind.Cpu,
                    Device = null,
                },
                new VideoProcessing(BgraOutput())
            )
            .FinishAsync();
        VideoFrameLease output = run.Output;
        VideoProcessorInfo info = run.Info;

        Assert.AreEqual(PixelFormat.Bgra, info.Output.PixelFormat);
        CollectionAssert.AreEqual(
            new byte[] { 130, 130, 130, 255 },
            Download(output, PixelFormat.Bgra, Width)[0][..4]
        );
    }

    [TestMethod]
    public async Task KeptOutput_IsNotReusedByLaterFrames()
    {
        byte[] first = Picture();
        byte[] second = [.. first.Select(b => (byte)(255 - b))];
        using IVideoProcessor processor = Factory.Create(
            Description(PixelFormat.Bgra, Width),
            new VideoProcessing(Nv12Output())
        );
        Keeper keeper = new();
        Process(processor, first, keeper);
        using VideoFrameLease kept = await keeper.TakeAsync();
        byte[][] before = Download(kept, PixelFormat.Nv12, Width);

        Discarder discarder = new();
        for (int i = 0; i < 4; i++)
        {
            Process(processor, second, discarder);
        }

        await discarder.WaitForAsync(4);

        CollectionAssert.AreEqual(before[0], Download(kept, PixelFormat.Nv12, Width)[0]);
    }

    [TestMethod]
    public void QueryCapabilities_RefusesWhatItCannotDo()
    {
        VideoStreamDescription bgra = Description(PixelFormat.Bgra, Width);
        VideoConstraints nv12 = new([VideoStorageKind.IOSurface], [PixelFormat.Nv12]);

        Assert.AreEqual(
            VideoStorageKind.IOSurface,
            Factory
                .QueryCapabilities(
                    bgra with
                    {
                        Storage = VideoStorageKind.Cpu,
                    },
                    new VideoProcessing(nv12)
                )
                ?.Output.Storage,
            "frames in memory are uploaded for a consumer on the GPU"
        );
        Assert.IsNull(
            Factory.QueryCapabilities(
                bgra with
                {
                    Storage = VideoStorageKind.Cpu,
                },
                new VideoProcessing(VideoConstraints.Cpu(PixelFormat.Nv12))
            ),
            "memory to memory is a CPU processor's work"
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

    // Hands a surface's frame to a new processor; the surface goes back once the processor holds it.
    private static Pending Start(
        PooledSurface input,
        VideoStreamDescription description,
        VideoProcessing processing
    )
    {
        try
        {
            return Start(
                TestSurfaces.Frame(
                    input,
                    description.PixelFormat,
                    description.Size.Width,
                    Height,
                    VideoColor.Srgb
                ),
                description,
                processing
            );
        }
        finally
        {
            input.Release();
        }
    }

    private static Pending Start(
        in VideoFrame input,
        VideoStreamDescription description,
        VideoProcessing processing
    )
    {
        IVideoProcessor processor = Factory.Create(description, processing);
        Keeper keeper = new();
        processor.Process(in input, keeper);
        return new Pending(processor, keeper);
    }

    // A frame handed to a processor: its output, once the GPU work completes, and then the processor
    // goes.
    private sealed class Pending(IVideoProcessor processor, Keeper keeper)
    {
        public async Task<Processed> FinishAsync()
        {
            using (processor)
            {
                VideoFrameLease output = await keeper.TakeAsync();
                return new Processed(output, processor.Info);
            }
        }
    }

    // What a processor gave back, and what it said about its output.
    private sealed record Processed(VideoFrameLease Output, VideoProcessorInfo Info) : IDisposable
    {
        public void Dispose() => Output.Dispose();
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

    // Colour ramps in BGRA with an alpha pattern. Chroma sited and filtered the same way down and up
    // returns a linear ramp exactly, so a round trip that shifts or blurs the colour shows here.
    private static byte[] Ramp()
    {
        byte[] pixels = new byte[Width * Height * 4];
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int i = ((y * Width) + x) * 4;
                pixels[i] = (byte)(200 - x - y);
                pixels[i + 1] = (byte)(60 + (3 * y));
                pixels[i + 2] = (byte)(40 + (2 * x));
                pixels[i + 3] = (byte)(((x * 37) + (y * 5)) % 256);
            }
        }

        return pixels;
    }

    // Colour blocks of 2x2 pixels with varying alpha: luma and alpha come through exactly, and left-sited
    // chroma takes in the column before each block.
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

    // Keeps the frame it is handed; the processor hands it on when the GPU work completes.
    private sealed class Keeper : IVideoFrameConsumer
    {
        private readonly TaskCompletionSource<VideoFrameLease> _kept = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public void OnFrame(in VideoFrame frame) => _kept.SetResult(frame.Retain());

        public Task<VideoFrameLease> TakeAsync() => _kept.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    // Counts the frames it is handed.
    private sealed class Discarder : IVideoFrameConsumer
    {
        private readonly SemaphoreSlim _handed = new(0);

        public void OnFrame(in VideoFrame frame) => _handed.Release();

        public async Task WaitForAsync(int frames)
        {
            for (int i = 0; i < frames; i++)
            {
                Assert.IsTrue(
                    await _handed.WaitAsync(TimeSpan.FromSeconds(10)),
                    "a frame was never handed on"
                );
            }
        }
    }
}
