using System.Diagnostics;
using Agash.StreamTransport.Linux.Vulkan;
using Agash.StreamTransport.Media;

namespace Agash.StreamTransport.Linux.Tests;

/// <summary>
/// What a 1080p frame costs on this machine's GPU path, run as fast as it goes rather than at a camera's
/// rate: the conversion a consumer's format choice adds, and the read-back a consumer reading memory
/// adds to it. Reports per-frame time and allocation; asserts only that a read-back allocates nothing.
/// </summary>
[TestClass]
[OSCondition(OperatingSystems.Linux)]
[TestCategory("Benchmark")]
public sealed class VulkanCostTests
{
    private const int Width = 1920;
    private const int Height = 1080;
    private const int Warmup = 30;
    private const int Frames = 600;

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void Nv12ToRgba_OnTheGpu()
    {
        PooledDmaBuf input = Nv12();
        try
        {
            Discard discard = new();
            (double ms, long bytes) = Measure(input, discard);
            Report("NV12 to RGBA, GPU only", ms, bytes);
        }
        finally
        {
            input.Release();
        }
    }

    [TestMethod]
    public void Nv12ToRgba_ReadBackIntoMemory()
    {
        PooledDmaBuf input = Nv12();
        try
        {
            using ReadBack read = new(new byte[Width * Height * 4]);
            (double ms, long bytes) = Measure(input, read);
            Report("NV12 to RGBA, read back into memory", ms, bytes);
            Assert.AreEqual(0, bytes / Frames, "a read-back allocates nothing per frame");
        }
        finally
        {
            input.Release();
        }
    }

    [TestMethod]
    public void Nv12_ReadBackIntoMemory()
    {
        PooledDmaBuf input = Nv12();
        try
        {
            byte[] destination = new byte[(Width * Height) + (Width * Height / 2)];
            using VulkanTransfer.Readback readback = new();
            VideoFrame frame = TestDmaBufs.Frame(
                input,
                PixelFormat.Nv12,
                Width,
                Height,
                VideoColor.Bt709
            );
            Assert.IsTrue(frame.Storage.TryGetValue(out DmaBufImage image));
            for (int i = 0; i < Warmup; i++)
            {
                VulkanTransfer.Read(
                    TestDmaBufs.Engine,
                    readback,
                    in image,
                    PixelFormat.Nv12,
                    Width,
                    Height,
                    destination,
                    Width
                );
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread();
            long started = Stopwatch.GetTimestamp();
            for (int i = 0; i < Frames; i++)
            {
                VulkanTransfer.Read(
                    TestDmaBufs.Engine,
                    readback,
                    in image,
                    PixelFormat.Nv12,
                    Width,
                    Height,
                    destination,
                    Width
                );
            }

            double ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds / Frames;
            long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            Report("NV12, read back into memory without conversion", ms, bytes);
            Assert.AreEqual(0, bytes / Frames, "a read-back allocates nothing per frame");
        }
        finally
        {
            input.Release();
        }
    }

    private (double Ms, long Bytes) Measure(PooledDmaBuf input, IVideoFrameConsumer consumer)
    {
        VideoStreamDescription description = new(
            VideoStorageKind.DmaBuf,
            PixelFormat.Nv12,
            new VideoSize(Width, Height),
            TestDmaBufs.Engine.Identity
        );
        VideoProcessing processing = new(
            new VideoConstraints(
                [VideoStorageKind.DmaBuf],
                [PixelFormat.Rgba],
                TestDmaBufs.Engine.Identity
            )
        );
        IVideoProcessor processor = new VulkanVideoProcessorFactory().Create(
            description,
            processing
        );
        VideoFrame frame = TestDmaBufs.Frame(
            input,
            PixelFormat.Nv12,
            Width,
            Height,
            VideoColor.Bt709
        );
        for (int i = 0; i < Warmup; i++)
        {
            processor.Process(in frame, consumer);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();
        for (int i = 0; i < Frames; i++)
        {
            processor.Process(in frame, consumer);
        }

        // Disposing waits for the last submission, so the time covers the GPU work, not its queueing.
        processor.Dispose();
        double ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds / Frames;
        return (ms, GC.GetAllocatedBytesForCurrentThread() - allocated);
    }

    private void Report(string what, double ms, long bytes) =>
        TestContext.WriteLine(
            FormattableString.Invariant(
                $"{what}: {ms:0.000} ms a frame ({1000 / ms:0} fps), {bytes / (double)Frames:0} bytes allocated a frame"
            )
        );

    // A 1080p NV12 picture: a luma ramp, neutral chroma.
    private static PooledDmaBuf Nv12()
    {
        byte[] luma = new byte[Width * Height];
        for (int i = 0; i < luma.Length; i++)
        {
            luma[i] = (byte)(16 + (i % Width * 219 / Width));
        }

        byte[] chroma = new byte[Width * Height / 2];
        Array.Fill(chroma, (byte)128);
        return TestDmaBufs.Upload(PixelFormat.Nv12, Width, Height, luma, chroma);
    }

    private sealed class Discard : IVideoFrameConsumer
    {
        public void OnFrame(in VideoFrame frame) { }
    }

    // Reads each converted frame back into memory, as a sink does for a consumer reading memory.
    private sealed class ReadBack(byte[] destination) : IVideoFrameConsumer, IDisposable
    {
        private readonly VulkanTransfer.Readback _readback = new();

        public void OnFrame(in VideoFrame frame)
        {
            _ = frame.Storage.TryGetValue(out DmaBufImage image);
            VulkanTransfer.Read(
                TestDmaBufs.Engine,
                _readback,
                in image,
                PixelFormat.Rgba,
                Width,
                Height,
                destination,
                Width * 4
            );
        }

        public void Dispose() => _readback.Dispose();
    }
}
