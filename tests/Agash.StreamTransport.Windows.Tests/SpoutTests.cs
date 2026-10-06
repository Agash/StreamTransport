using System.Numerics;
using Agash.StreamTransport.Media;
using Agash.StreamTransport.Windows.Spout;
using Windows.Win32.Graphics.Dxgi.Common;

namespace Agash.StreamTransport.Windows.Tests;

[TestClass]
[OSCondition(OperatingSystems.Windows)]
public sealed class SpoutTests
{
    private const int Width = 64;
    private const int Height = 32;

    [TestMethod]
    [Timeout(30_000)]
    public async Task SinkToSource_KeptFrameHoldsThePicture()
    {
        using var gpu = TestGpu.Open();
        string name = $"StreamTransport test {Guid.NewGuid():N}";
        byte[] pixels = new byte[Width * Height * 4];
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)(i * 7 % 251);
        }

        nint texture = gpu.Upload(DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, Width, Height, pixels);
        using SpoutVideoSink sink = new(name, gpu.Adapter);
        using SpoutVideoSource source = new(
            new SpoutVideoSourceOptions { SenderName = name, Adapter = gpu.Adapter }
        );
        Keeper keeper = new();
        using IDisposable connection = source.Connect(keeper, sink.Constraints);

        // Published at a frame rate, as a real sender does, until the receiver has one; a frame is a
        // view for one call, so each is made for its call.
        using (PeriodicTimer frames = new(TimeSpan.FromMilliseconds(10)))
        using (CancellationTokenSource expiry = new(TimeSpan.FromSeconds(10)))
        {
            do
            {
                Publish(sink, texture, gpu.Adapter);
            } while (!keeper.Kept.IsCompleted && await frames.WaitForNextTickAsync(expiry.Token));
        }

        using VideoFrameLease kept = await keeper.Kept.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(new VideoFormat(PixelFormat.Bgra, Width, Height), kept.Frame.Format);
        Assert.AreEqual(
            TimestampKind.Observation,
            kept.Frame.Timestamp.Kind,
            "Spout carries no capture time"
        );
        Assert.IsTrue(kept.Frame.Storage.TryGetValue(out D3D12Image image));
        CollectionAssert.AreEqual(pixels, gpu.Download(image, 1)[0]);
    }

    // A received frame is lent for GPU reads ordered before Spout's release, so the processor converts
    // the sender's texture in place, without a copy, and the picture comes out as published.
    [TestMethod]
    [Timeout(30_000)]
    public async Task SourceToProcessor_ReadsTheSendersTextureInPlace()
    {
        using var gpu = TestGpu.Open();
        string name = $"StreamTransport test {Guid.NewGuid():N}";
        // One colour, so every luma and chroma sample is that colour's.
        byte[] pixels = new byte[Width * Height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (40, 120, 200, 255);
        }

        nint texture = gpu.Upload(DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, Width, Height, pixels);
        using SpoutVideoSink sink = new(name, gpu.Adapter);
        using SpoutVideoSource source = new(
            new SpoutVideoSourceOptions { SenderName = name, Adapter = gpu.Adapter }
        );
        using IVideoProcessor processor = new Direct3D12.D3D12VideoProcessorFactory().Create(
            new VideoStreamDescription(
                VideoStorageKind.D3D12,
                PixelFormat.Bgra,
                new(Width, Height),
                gpu.Adapter
            ),
            new VideoProcessing(
                new VideoConstraints([VideoStorageKind.D3D12], [PixelFormat.Nv12], gpu.Adapter)
            )
        );
        Keeper keeper = new();
        Converter converter = new(processor, keeper);
        using IDisposable connection = source.Connect(
            converter,
            new VideoConstraints([VideoStorageKind.D3D12], [PixelFormat.Bgra], gpu.Adapter)
        );

        using (PeriodicTimer frames = new(TimeSpan.FromMilliseconds(10)))
        using (CancellationTokenSource expiry = new(TimeSpan.FromSeconds(10)))
        {
            do
            {
                Publish(sink, texture, gpu.Adapter);
            } while (!keeper.Kept.IsCompleted && await frames.WaitForNextTickAsync(expiry.Token));
        }

        using VideoFrameLease kept = await keeper.Kept.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(converter.Lent, "the frame did not name a release queue to read it in place");
        Assert.IsTrue(kept.Frame.Storage.TryGetValue(out D3D12Image output));
        byte[][] planes = gpu.Download(output, 2);
        (Vector4 y, Vector4 cb, Vector4 cr) = ColorConversion.RgbToYCbCr(VideoColor.Bt709);
        Vector3 rgb = new(200 / 255f, 120 / 255f, 40 / 255f);
        Assert.IsTrue(
            planes[0].All(l => Math.Abs(l - (Vector4.Dot(y, new(rgb, 1)) * 255)) <= 1),
            "luma"
        );
        for (int c = 0; c < planes[1].Length; c += 2)
        {
            Assert.AreEqual(Vector4.Dot(cb, new(rgb, 1)) * 255, planes[1][c], 1.0, "Cb");
            Assert.AreEqual(Vector4.Dot(cr, new(rgb, 1)) * 255, planes[1][c + 1], 1.0, "Cr");
        }
    }

    // The sink lends Spout's shared texture and the processor draws the frame straight into it: what a
    // receiver gets was never copied into the sink.
    [TestMethod]
    [Timeout(30_000)]
    public async Task ProcessorToSink_DrawsIntoTheSharedTexture()
    {
        using var gpu = TestGpu.Open();
        string name = $"StreamTransport test {Guid.NewGuid():N}";
        byte[] luma = [.. Enumerable.Repeat((byte)128, Width * Height)];
        byte[] chroma = [.. Enumerable.Repeat((byte)128, Width * Height / 2)];
        nint texture = gpu.Upload(DXGI_FORMAT.DXGI_FORMAT_NV12, Width, Height, luma, chroma);
        using SpoutVideoSink sink = new(name, gpu.Adapter);
        using IVideoProcessor processor = new Direct3D12.D3D12VideoProcessorFactory().Create(
            new VideoStreamDescription(
                VideoStorageKind.D3D12,
                PixelFormat.Nv12,
                new(Width, Height),
                gpu.Adapter
            ),
            new VideoProcessing(sink.Constraints)
        );
        VideoStreamDescription drawn = processor.Info.Output;
        using SpoutVideoSource source = new(
            new SpoutVideoSourceOptions { SenderName = name, Adapter = gpu.Adapter }
        );
        Keeper keeper = new();
        using IDisposable connection = source.Connect(keeper, sink.Constraints);

        using (PeriodicTimer frames = new(TimeSpan.FromMilliseconds(10)))
        using (CancellationTokenSource expiry = new(TimeSpan.FromSeconds(10)))
        {
            do
            {
                VideoFrame frame = new(
                    new VideoStorage(new D3D12Image(texture, 0, gpu.Adapter)),
                    new VideoFormat(PixelFormat.Nv12, Width, Height),
                    MediaTimestamp.Captured(new MediaTime(1)),
                    color: VideoColor.Bt709,
                    retainer: Owned.Retainer
                );
                Assert.IsTrue(
                    sink.TryRender(
                        new VideoFormat(drawn.PixelFormat, Width, Height),
                        new Drawing(processor, frame),
                        static (in VideoTarget target, scoped in Drawing drawing) =>
                            drawing.Processor.TryProcess(in drawing.Frame, in target)
                    ),
                    "the processor did not draw into the sink's texture"
                );
            } while (!keeper.Kept.IsCompleted && await frames.WaitForNextTickAsync(expiry.Token));
        }

        using VideoFrameLease kept = await keeper.Kept.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(kept.Frame.Storage.TryGetValue(out D3D12Image image));
        CollectionAssert.AreEqual(
            new byte[] { 130, 130, 130, 255 },
            gpu.Download(image, 1)[0][..4]
        );
    }

    [TestMethod]
    public void Sink_RefusesFramesOffItsGpu()
    {
        using var gpu = TestGpu.Open();
        using SpoutVideoSink sink = new($"StreamTransport test {Guid.NewGuid():N}", gpu.Adapter);

        Assert.ThrowsExactly<ArgumentException>(() =>
            sink.OnFrame(
                new VideoFrame(
                    new VideoStorage(
                        new CpuImage(PlaneLayout.Packed(PixelFormat.Bgra, new VideoSize(2, 2)))
                    ),
                    new VideoFormat(PixelFormat.Bgra, 2, 2),
                    MediaTimestamp.Captured(new MediaTime(1)),
                    new byte[16]
                )
            )
        );
    }

    private static void Publish(SpoutVideoSink sink, nint texture, GpuIdentity adapter) =>
        sink.OnFrame(
            new VideoFrame(
                new VideoStorage(new D3D12Image(texture, 0, adapter)),
                new VideoFormat(PixelFormat.Bgra, Width, Height),
                MediaTimestamp.Captured(new MediaTime(1))
            )
        );

    // Keeps a frame whose texture the test owns for the whole test: nothing to hold or release.
    private sealed class Owned(in VideoFrame frame)
        : VideoFrameLease(
            frame.Storage,
            frame.Format,
            frame.Timestamp,
            frame.Color,
            frame.Orientation,
            frame.Duration
        )
    {
        public static IVideoFrameRetainer Retainer { get; } = new Keeping();

        protected override void Release() { }

        private sealed class Keeping : IVideoFrameRetainer
        {
            public VideoFrameLease Retain(in VideoFrame frame) => new Owned(in frame);
        }
    }

    // A frame and the processor that draws it into the sink's surface.
    private readonly ref struct Drawing(IVideoProcessor processor, VideoFrame frame)
    {
        public readonly IVideoProcessor Processor = processor;

        public readonly VideoFrame Frame = frame;
    }

    // Hands each frame to a processor, noting whether the source lent it for reads in place.
    private sealed class Converter(IVideoProcessor processor, IVideoFrameConsumer next)
        : IVideoFrameConsumer
    {
        public bool Lent { get; private set; }

        public void OnFrame(in VideoFrame frame)
        {
            Lent = frame.Storage.TryGetValue(out D3D12Image image) && image.Sync.ReleaseQueue != 0;
            processor.Process(in frame, next);
        }
    }

    // Keeps the first frame it is handed.
    private sealed class Keeper : IVideoFrameConsumer
    {
        private readonly TaskCompletionSource<VideoFrameLease> _kept = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public Task<VideoFrameLease> Kept => _kept.Task;

        public void OnFrame(in VideoFrame frame)
        {
            if (!_kept.Task.IsCompleted)
            {
                VideoFrameLease lease = frame.Retain();
                if (!_kept.TrySetResult(lease))
                {
                    lease.Dispose();
                }
            }
        }
    }
}
