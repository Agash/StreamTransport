using System.Collections.Concurrent;
using System.Collections.Immutable;
using Agash.StreamTransport.Linux.V4l2;
using Agash.StreamTransport.Linux.Vulkan;
using Agash.StreamTransport.Media;

namespace Agash.StreamTransport.Linux.Tests;

/// <summary>
/// Captures from the kernel's virtual video driver (<c>modprobe vivid n_devs=2 multiplanar=1,2</c>),
/// which behaves as a camera does: driver buffers, monotonic timestamps, DMA-BUF export. Skipped when it
/// is not loaded.
/// </summary>
[TestClass]
[OSCondition(OperatingSystems.Linux)]
public sealed class V4l2InputTests
{
    [TestMethod]
    public void Layouts_MatchTheKernelAbi()
    {
        foreach ((string name, int size, int expected) in V4l2Native.Layouts)
        {
            Assert.AreEqual(expected, size, name);
        }
    }

    [TestMethod]
    public async Task GetInputs_ListsVividWithItsUncompressedModes()
    {
        ImmutableArray<VideoInputInfo> vivid = await Vivid();

        VideoInputInfo first = vivid[0];
        Assert.AreEqual("v4l2", first.Provider);
        Assert.AreEqual(MediaInputKind.Camera, first.Kind);
        Assert.IsTrue(first.Modes.Any(static m => m.PixelFormat == PixelFormat.Yuy2));
        Assert.IsTrue(first.Modes.Any(static m => m.PixelFormat == PixelFormat.Nv12));
        Assert.IsTrue(first.Modes.Any(static m => m.Size == new VideoSize(1280, 720)));
    }

    [TestMethod]
    [DataRow(PixelFormat.Yuy2, 0)]
    [DataRow(PixelFormat.Nv12, 0)]
    [DataRow(PixelFormat.I420, 0)]
    [DataRow(PixelFormat.Nv12, 1)]
    [DataRow(PixelFormat.Yuy2, 1)]
    [Timeout(20_000)]
    public async Task Capture_DeliversDriverBuffersWithCaptureTimes(PixelFormat format, int device)
    {
        ImmutableArray<VideoInputInfo> vivid = await Vivid();
        if (device >= vivid.Length)
        {
            Assert.Inconclusive("vivid has no second (multi-planar) device.");
        }

        var provider = new V4l2VideoInputProvider();
        using IVideoInput input = await provider.OpenAsync(
            vivid[device],
            new VideoInputRequest { Size = new VideoSize(1280, 720), FrameRate = 30, PixelFormat = format },
            CancellationToken.None
        );
        var consumer = new Recorder(frames: 12, keep: 3);

        using (input.Connect(consumer, VideoConstraints.Cpu(format)))
        {
            await consumer.Done.Task;
        }

        Assert.AreEqual(format, input.Mode!.Value.PixelFormat);
        Assert.AreEqual(new VideoSize(1280, 720), input.Mode.Value.Size);
        Assert.IsTrue(consumer.Frames.All(f => f.Format.PixelFormat == format));
        Assert.IsTrue(consumer.Frames.All(f => f.Kind == TimestampKind.Capture), "driver timestamps are capture times");
        MediaTime now = MediaClock.System.Now;
        Assert.IsTrue(consumer.Frames.All(f => now - f.Time < TimeSpan.FromSeconds(5)), "capture times are on the media clock");
        TimeSpan[] gaps = [.. consumer.Frames.Zip(consumer.Frames.Skip(1), static (a, b) => b.Time - a.Time)];
        Assert.IsTrue(gaps.All(static g => g > TimeSpan.Zero), "capture times increase");
        Assert.AreEqual(1000.0 / 30, gaps.Average(static g => g.TotalMilliseconds), 6.0, "frames come at the requested rate");
        Assert.IsTrue(consumer.Frames.All(static f => f.Bytes > 0 && f.Luma > 0), "the planes are the picture");
        foreach (VideoFrameLease kept in consumer.Kept)
        {
            Assert.AreEqual(format, kept.Frame.Format.PixelFormat);
            Assert.IsGreaterThan(0, kept.Frame.GetPlane(0).Length);
            kept.Dispose();
        }
    }

    [TestMethod]
    [Timeout(20_000)]
    public async Task Capture_HeldFramesHoldOnlyTheirBuffers()
    {
        ImmutableArray<VideoInputInfo> vivid = await Vivid();
        var provider = new V4l2VideoInputProvider();
        using IVideoInput input = await provider.OpenAsync(
            vivid[0],
            new VideoInputRequest { Size = new VideoSize(640, 480), PixelFormat = PixelFormat.Yuy2 },
            CancellationToken.None
        );

        // Keeping four of the six buffers leaves the capture running on the other two.
        var consumer = new Recorder(frames: 30, keep: 4);
        using (input.Connect(consumer, VideoConstraints.Cpu(PixelFormat.Yuy2)))
        {
            await consumer.Done.Task;
        }

        Assert.HasCount(30, consumer.Frames);
        foreach (VideoFrameLease kept in consumer.Kept)
        {
            kept.Dispose();
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Capture_SharedBuffers_ImportOnTheGpuAsTheMappedPictureReads()
    {
        ImmutableArray<VideoInputInfo> vivid = await Vivid();
        VulkanEngine engine;
        try
        {
            engine = VulkanEngine.For(null);
        }
        catch (InvalidOperationException)
        {
            Assert.Inconclusive("No Vulkan GPU imports DMA-BUFs here.");
            return;
        }

        var provider = new V4l2VideoInputProvider();
        using IVideoInput input = await provider.OpenAsync(
            vivid[0],
            new VideoInputRequest { Size = new VideoSize(640, 480), PixelFormat = PixelFormat.Nv12 },
            CancellationToken.None
        );
        var factory = new VulkanVideoProcessorFactory();
        VideoStreamDescription description = new(VideoStorageKind.DmaBuf, PixelFormat.Nv12, new VideoSize(640, 480), engine.Identity);
        using IVideoProcessor processor = factory.Create(
            description,
            new VideoProcessing(VideoConstraints.Cpu(PixelFormat.Bgra))
        );
        var gpu = new Converter(processor);

        using (input.Connect(gpu, new VideoConstraints([VideoStorageKind.DmaBuf], [PixelFormat.Nv12], engine.Identity)))
        {
            await gpu.Done.Task;
        }

        Assert.IsTrue(gpu.SawDmaBuf, "the consumer on the GPU was lent DMA-BUFs");
        Assert.IsNotNull(gpu.Result);
        Assert.AreEqual(PixelFormat.Bgra, gpu.Result.Frame.Format.PixelFormat);
        ReadOnlySpan<byte> pixels = gpu.Result.Frame.GetPlane(0);
        Assert.IsTrue(pixels.ToArray().Any(static b => b is > 16 and < 240), "the imported picture has content");
        gpu.Result.Dispose();
    }

    private static async Task<ImmutableArray<VideoInputInfo>> Vivid()
    {
        ImmutableArray<VideoInputInfo> inputs = await new V4l2VideoInputProvider().GetInputsAsync(CancellationToken.None);
        ImmutableArray<VideoInputInfo> vivid = [.. inputs.Where(static i => i.Name.StartsWith("vivid", StringComparison.Ordinal))];
        if (vivid.IsEmpty)
        {
            Assert.Inconclusive("The vivid driver is not loaded.");
        }

        return vivid;
    }

    private sealed record Seen(VideoFormat Format, MediaTime Time, TimestampKind Kind, int Bytes, byte Luma);

    // Records frames and keeps some of them.
    private sealed class Recorder(int frames, int keep) : IVideoFrameConsumer
    {
        public ConcurrentQueue<Seen> Seen { get; } = new();

        public List<Seen> Frames => [.. Seen];

        public List<VideoFrameLease> Kept { get; } = [];

        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void OnFrame(in VideoFrame frame)
        {
            if (Seen.Count >= frames)
            {
                return;
            }

            ReadOnlySpan<byte> plane = frame.GetPlane(0);
            Seen.Enqueue(new Seen(frame.Format, frame.Timestamp.Time, frame.Timestamp.Kind, plane.Length, plane[plane.Length / 2]));
            if (Kept.Count < keep)
            {
                Kept.Add(frame.Retain());
            }

            if (Seen.Count == frames)
            {
                Done.TrySetResult();
            }
        }
    }

    // Converts a few lent DMA-BUFs to BGRA in memory and keeps the last result.
    private sealed class Converter(IVideoProcessor processor) : IVideoFrameConsumer
    {
        private readonly Keeper _keeper = new();
        private int _count;

        public bool SawDmaBuf { get; private set; }

        public VideoFrameLease? Result => _keeper.Result;

        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void OnFrame(in VideoFrame frame)
        {
            if (Done.Task.IsCompleted)
            {
                return;
            }

            SawDmaBuf |= frame.Storage.Kind == VideoStorageKind.DmaBuf;
            try
            {
                processor.Process(in frame, _keeper);
            }
            catch (Exception exception)
            {
                Done.TrySetException(exception);
                return;
            }

            if (++_count == 5)
            {
                Done.TrySetResult();
            }
        }

        private sealed class Keeper : IVideoFrameConsumer
        {
            public VideoFrameLease? Result { get; private set; }

            public void OnFrame(in VideoFrame frame)
            {
                Result?.Dispose();
                Result = frame.Retain();
            }
        }
    }
}
