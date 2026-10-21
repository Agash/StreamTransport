using System.Diagnostics;
using System.Runtime.InteropServices;
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
    public unsafe void Nv12ToRgba_ReadBackIntoPageAlignedMemory()
    {
        // Mapped memory, as a PipeWire memfd buffer is: the GPU copies into it where it imports host
        // memory, with no staging and no CPU copy.
        const int size = Width * Height * 4;
        void* memory = NativeMemory.AlignedAlloc(size, 4096);
        PooledDmaBuf input = Nv12();
        try
        {
            TestContext.WriteLine(
                $"host memory import: {(TestDmaBufs.Engine.HostImportAlignment is { } a ? $"aligned to {a}" : "not available")}"
            );
            using ReadBack read = new((nint)memory, size);
            (double ms, long bytes) = Measure(input, read);
            Report("NV12 to RGBA, read back into page-aligned memory", ms, bytes);
            Assert.AreEqual(0, bytes / Frames, "a read-back allocates nothing per frame");
        }
        finally
        {
            input.Release();
            NativeMemory.AlignedFree(memory);
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

    private (double Ms, long Bytes) Measure(
        PooledDmaBuf input,
        IVideoFrameConsumer consumer,
        int frames = Frames
    )
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
        for (int i = 0; i < frames; i++)
        {
            processor.Process(in frame, consumer);
        }

        // Disposing waits for the last submission, so the time covers the GPU work, not its queueing.
        processor.Dispose();
        double ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds / frames;
        return (ms, GC.GetAllocatedBytesForCurrentThread() - allocated);
    }

    // What allocates on a path: the runtime's allocation samples (about one per 100 KB) by type, over
    // enough frames for a few hundred bytes a frame to show.
    [TestMethod]
    public void Nv12ToRgba_ReadBack_AllocatingTypes()
    {
        PooledDmaBuf input = Nv12();
        try
        {
            using ReadBack read = new(new byte[Width * Height * 4]);
            using AllocationSampler sampler = new();
            _ = Measure(input, read, frames: 20_000);
            foreach ((string type, int samples) in sampler.Top(12))
            {
                TestContext.WriteLine($"allocates {type}: {samples} samples");
            }
        }
        finally
        {
            input.Release();
        }
    }

    private sealed class AllocationSampler : System.Diagnostics.Tracing.EventListener
    {
        private readonly Dictionary<string, int> _types = [];
        private readonly Lock _gate = new();

        protected override void OnEventSourceCreated(System.Diagnostics.Tracing.EventSource source)
        {
            if (source.Name == "Microsoft-Windows-DotNETRuntime")
            {
                EnableEvents(
                    source,
                    System.Diagnostics.Tracing.EventLevel.Verbose,
                    (System.Diagnostics.Tracing.EventKeywords)0x1
                );
            }
        }

        protected override void OnEventWritten(
            System.Diagnostics.Tracing.EventWrittenEventArgs data
        )
        {
            if (
                data.EventName?.StartsWith("GCAllocationTick", StringComparison.Ordinal) != true
                || data.PayloadNames is null
            )
            {
                return;
            }

            int at = data.PayloadNames.IndexOf("TypeName");
            if (at >= 0 && data.Payload?[at] is string type)
            {
                lock (_gate)
                {
                    _types[type] = _types.GetValueOrDefault(type) + 1;
                }
            }
        }

        public IEnumerable<(string Type, int Samples)> Top(int count)
        {
            lock (_gate)
            {
                return
                [
                    .. _types
                        .OrderByDescending(t => t.Value)
                        .Take(count)
                        .Select(t => (t.Key, t.Value)),
                ];
            }
        }
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

    // Reads each converted frame back into memory, as a sink does for a consumer reading memory: an array,
    // or native memory at an address.
    private sealed unsafe class ReadBack : IVideoFrameConsumer, IDisposable
    {
        private readonly VulkanTransfer.Readback _readback = new();
        private readonly byte[]? _array;
        private readonly nint _address;
        private readonly int _length;

        public ReadBack(byte[] destination) => _array = destination;

        public ReadBack(nint address, int length)
        {
            _address = address;
            _length = length;
        }

        public void OnFrame(in VideoFrame frame)
        {
            _ = frame.Storage.TryGetValue(out DmaBufImage image);
            Span<byte> destination = _array ?? new Span<byte>((void*)_address, _length);
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
