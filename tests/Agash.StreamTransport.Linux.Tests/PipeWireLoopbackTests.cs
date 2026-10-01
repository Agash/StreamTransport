using System.Runtime.InteropServices;
using Agash.StreamTransport.Linux.PipeWire;
using Agash.StreamTransport.Linux.Vulkan;
using Agash.StreamTransport.Media;
using PipeWire.NET;
using PixelFormat = Agash.StreamTransport.Media.PixelFormat;

namespace Agash.StreamTransport.Linux.Tests;

// Sinks published into the running PipeWire daemon and read back by sources that target them.
[TestClass]
[OSCondition(OperatingSystems.Linux)]
public sealed class PipeWireLoopbackTests
{
    private const int Width = 64;
    private const int Height = 32;

    [TestMethod]
    [TestCategory("Integration")]
    [Timeout(30_000)]
    [DataRow(PixelFormat.Bgra)]
    [DataRow(PixelFormat.Nv12)]
    public async Task VideoSinkToSource_CarriesThePicture(PixelFormat format)
    {
        await using PipeWireContext context = await StartAsync();
        byte[] pixels = new byte[PlaneLayout.PackedSize(format, new VideoSize(Width, Height))];
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)(i * 7 % 251);
        }

        using PipeWireVideoSink sink = new(
            context,
            $"streamtransport-test-{Guid.NewGuid():N}",
            new PipeWireVideoSinkOptions { AutoConnect = false }
        );
        Publish(sink, format, pixels);
        uint node = await sink.WaitForNodeIdAsync().WaitAsync(TimeSpan.FromSeconds(5));

        using PipeWireVideoSource source = new(
            context,
            new PipeWireVideoSourceOptions
            {
                TargetNodeId = node,
                PreferredSize = new VideoSize(Width, Height),
            }
        );
        Keeper keeper = new();
        using IDisposable connection = source.Connect(keeper, VideoConstraints.Cpu(format));
        using (PeriodicTimer frames = new(TimeSpan.FromMilliseconds(20)))
        using (CancellationTokenSource expiry = new(TimeSpan.FromSeconds(15)))
        {
            while (!keeper.Kept.IsCompleted && await frames.WaitForNextTickAsync(expiry.Token))
            {
                Publish(sink, format, pixels);
            }
        }

        using VideoFrameLease kept = await keeper.Kept;
        Assert.AreEqual(new VideoFormat(format, Width, Height), kept.Frame.Format);
        CollectionAssert.AreEqual(pixels, Packed(kept.Frame, format));
    }

    [TestMethod]
    [TestCategory("Integration")]
    [Timeout(30_000)]
    [DataRow(PixelFormat.Bgra)]
    [DataRow(PixelFormat.Nv12)]
    public async Task GpuVideoSinkToSource_StaysOnTheGpu(PixelFormat format)
    {
        await using PipeWireContext context = await StartAsync();
        bool nv12 = format == PixelFormat.Nv12;
        byte[][] planes = nv12
            ? [new byte[Width * Height], new byte[Width * Height / 2]]
            : [new byte[Width * Height * 4]];
        foreach (byte[] plane in planes)
        {
            for (int i = 0; i < plane.Length; i++)
            {
                plane[i] = (byte)(i * 7 % 251);
            }
        }

        VideoColor color = nv12 ? VideoColor.Bt709 : VideoColor.Srgb;
        PooledDmaBuf picture = TestDmaBufs.Upload(format, Width, Height, planes);
        try
        {
            using PipeWireVideoSink sink = new(
                context,
                $"streamtransport-gpu-test-{Guid.NewGuid():N}"
            );
            sink.OnFrame(TestDmaBufs.Frame(picture, format, Width, Height, color));
            uint node = await sink.WaitForNodeIdAsync().WaitAsync(TimeSpan.FromSeconds(5));

            using PipeWireVideoSource source = new(
                context,
                new PipeWireVideoSourceOptions
                {
                    TargetNodeId = node,
                    PreferredSize = new VideoSize(Width, Height),
                }
            );
            Keeper keeper = new();
            using IDisposable connection = source.Connect(
                keeper,
                new VideoConstraints([VideoStorageKind.DmaBuf], [format])
            );
            using (PeriodicTimer frames = new(TimeSpan.FromMilliseconds(20)))
            using (CancellationTokenSource expiry = new(TimeSpan.FromSeconds(15)))
            {
                while (!keeper.Kept.IsCompleted && await frames.WaitForNextTickAsync(expiry.Token))
                {
                    sink.OnFrame(TestDmaBufs.Frame(picture, format, Width, Height, color));
                }
            }

            using VideoFrameLease kept = await keeper.Kept;
            Assert.IsTrue(
                kept.Frame.Storage.TryGetValue(out DmaBufImage image),
                $"the frame crossed on the GPU; it arrived as {kept.Frame.Storage.Kind}"
            );
            Assert.AreEqual(format, kept.Frame.Format.PixelFormat);
            byte[][] read = TestDmaBufs.Download(image, format, Width, Height);
            for (int plane = 0; plane < planes.Length; plane++)
            {
                CollectionAssert.AreEqual(planes[plane], read[plane], $"plane {plane}");
            }
        }
        finally
        {
            picture.Release();
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    [Timeout(30_000)]
    public async Task GpuVideoSinkToMemorySource_IsReadBack()
    {
        await using PipeWireContext context = await StartAsync();
        byte[] pixels = new byte[Width * Height * 4];
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)(i * 5 % 253);
        }

        PooledDmaBuf picture = TestDmaBufs.Upload(PixelFormat.Bgra, Width, Height, pixels);
        try
        {
            using PipeWireVideoSink sink = new(
                context,
                $"streamtransport-readback-{Guid.NewGuid():N}"
            );
            sink.OnFrame(
                TestDmaBufs.Frame(picture, PixelFormat.Bgra, Width, Height, VideoColor.Srgb)
            );
            uint node = await sink.WaitForNodeIdAsync().WaitAsync(TimeSpan.FromSeconds(5));

            using PipeWireVideoSource source = new(
                context,
                new PipeWireVideoSourceOptions
                {
                    TargetNodeId = node,
                    PreferredSize = new VideoSize(Width, Height),
                }
            );
            Keeper keeper = new();
            using IDisposable connection = source.Connect(
                keeper,
                VideoConstraints.Cpu(PixelFormat.Bgra)
            );
            using (PeriodicTimer frames = new(TimeSpan.FromMilliseconds(20)))
            using (CancellationTokenSource expiry = new(TimeSpan.FromSeconds(15)))
            {
                while (!keeper.Kept.IsCompleted && await frames.WaitForNextTickAsync(expiry.Token))
                {
                    sink.OnFrame(
                        TestDmaBufs.Frame(picture, PixelFormat.Bgra, Width, Height, VideoColor.Srgb)
                    );
                }
            }

            using VideoFrameLease kept = await keeper.Kept;
            Assert.AreEqual(
                VideoStorageKind.Cpu,
                kept.Frame.Storage.Kind,
                "the consumer took memory"
            );
            CollectionAssert.AreEqual(pixels, Packed(kept.Frame, PixelFormat.Bgra));
        }
        finally
        {
            picture.Release();
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    [Timeout(30_000)]
    public async Task AudioSinkToSource_CarriesTheToneWithCaptureTimes()
    {
        await using PipeWireContext context = await StartAsync();
        using PipeWireAudioSink sink = new(
            context,
            new PipeWireAudioOptions
            {
                AutoConnect = false,
                NodeName = $"streamtransport-test-{Guid.NewGuid():N}",
            }
        );
        uint node = await sink.WaitForNodeIdAsync().WaitAsync(TimeSpan.FromSeconds(5));
        using PipeWireAudioSource source = new(
            context,
            new PipeWireAudioOptions { TargetNodeId = node }
        );
        Heard heard = new(samples: 48_000 / 4);
        using IDisposable connection = source.Connect(heard);

        using (PeriodicTimer period = new(TimeSpan.FromMilliseconds(10)))
        using (CancellationTokenSource expiry = new(TimeSpan.FromSeconds(15)))
        {
            for (
                int chunk = 0;
                !heard.Done.IsCompleted && await period.WaitForNextTickAsync(expiry.Token);
                chunk++
            )
            {
                sink.OnFrame(Tone(chunk));
            }
        }

        await heard.Done;
        Assert.IsGreaterThan(0.1, heard.Rms, "the tone came back");
        Assert.IsNotNull(sink.OutputLatency, "a running sink knows its latency");
        Assert.IsLessThan(TimeSpan.FromSeconds(1), sink.OutputLatency.Value);
        Assert.AreEqual(TimestampKind.Capture, heard.Kind);
        Assert.IsTrue(heard.Rising, "capture times rise");
        TimeSpan offset = MediaClock.System.Now - heard.Last;
        Assert.IsLessThan(
            TimeSpan.FromSeconds(1),
            offset.Duration(),
            $"The last capture is {offset} from now."
        );
    }

    private static async Task<PipeWireContext> StartAsync()
    {
        PipeWireContext context = new();
        try
        {
            await context.StartAsync();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            await context.DisposeAsync();
            Assert.Inconclusive($"No PipeWire daemon: {exception.Message}");
        }

        return context;
    }

    private static void Publish(PipeWireVideoSink sink, PixelFormat format, byte[] pixels) =>
        sink.OnFrame(
            new VideoFrame(
                new VideoStorage(
                    new CpuImage(PlaneLayout.Packed(format, new VideoSize(Width, Height)))
                ),
                new VideoFormat(format, Width, Height),
                MediaTimestamp.Captured(MediaClock.System.Now),
                pixels
            )
        );

    // A frame's planes, rows packed tightly, as they were published.
    private static byte[] Packed(VideoFrame frame, PixelFormat format)
    {
        var packed = PlaneLayout.Packed(format, new VideoSize(Width, Height));
        byte[] bytes = new byte[PlaneLayout.PackedSize(format, new VideoSize(Width, Height))];
        Assert.IsTrue(frame.Storage.TryGetValue(out CpuImage image));
        for (int plane = 0; plane < packed.Count; plane++)
        {
            int stride = image.Planes[plane].Stride;
            int rowBytes = packed[plane].Stride;
            for (int row = 0; row < PlaneLayout.PlaneRows(format, plane, Height); row++)
            {
                frame
                    .GetPlane(plane)
                    .Slice(row * stride, rowBytes)
                    .CopyTo(bytes.AsSpan(packed[plane].Offset + (row * rowBytes)));
            }
        }

        return bytes;
    }

    private static AudioFrame Tone(int chunk)
    {
        float[] samples = new float[480 * 2];
        for (int s = 0; s < 480; s++)
        {
            float value = 0.25f * MathF.Sin(2 * MathF.PI * 1000 * ((chunk * 480) + s) / 48_000f);
            samples[s * 2] = value;
            samples[(s * 2) + 1] = value;
        }

        return new AudioFrame(
            MemoryMarshal.AsBytes(samples.AsSpan()),
            PipeWireAudioSource.Transport,
            MediaTimestamp.Captured(MediaClock.System.Now)
        );
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

    // Measures what it hears once the tone arrives.
    private sealed class Heard(int samples) : IAudioFrameConsumer
    {
        private readonly TaskCompletionSource _done = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private double _sum;
        private int _count;

        public Task Done => _done.Task;

        public double Rms { get; private set; }

        public TimestampKind Kind { get; private set; }

        public bool Rising { get; private set; } = true;

        public MediaTime Last { get; private set; }

        public void OnFrame(in AudioFrame frame)
        {
            Kind = frame.Timestamp.Kind;
            Rising &= frame.Timestamp.Time > Last;
            Last = frame.Timestamp.Time;
            foreach (float value in MemoryMarshal.Cast<byte, float>(frame.Samples))
            {
                if (value != 0 || _count > 0)
                {
                    _sum += value * value;
                    _count++;
                }
            }

            if (_count >= samples * 2)
            {
                Rms = Math.Sqrt(_sum / _count);
                _done.TrySetResult();
            }
        }
    }
}
