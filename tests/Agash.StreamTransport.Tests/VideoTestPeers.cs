using System.Diagnostics;
using Agash.StreamTransport;

namespace Agash.StreamTransport.Tests;

/// <summary>
/// A test <see cref="IVideoFrameSource"/> that emits an NV12 test pattern at ~30 fps, paced to roughly
/// real time so the encoder sees a realistic cadence.
/// </summary>
internal sealed class PatternVideoSource(int width, int height) : IVideoFrameSource
{
    private static readonly long s_frameTicks = Stopwatch.Frequency / 30;
    private readonly byte[] _nv12 = HardwareEncoderTestSupport.Nv12Pattern(width, height);
    private long _nextEmitTicks = Stopwatch.GetTimestamp();

    public bool TryGetFrame(out VideoFrame frame)
    {
        long now = Stopwatch.GetTimestamp();
        if (now < _nextEmitTicks)
        {
            frame = default;
            return false;
        }

        _nextEmitTicks = now + s_frameTicks;
        long presentationTimeNs = now * (1_000_000_000L / Stopwatch.Frequency);
        frame = VideoFrame.FromPixels(_nv12, VideoPixelFormat.Nv12, width, height, presentationTimeNs);
        return true;
    }
}

/// <summary>
/// A test <see cref="IVideoFrameSource"/> emitting an NV12 frame with a known luma structure - the top half
/// bright (<see cref="BrightY"/>), the bottom half dark (<see cref="DarkY"/>), neutral chroma - paced at
/// ~30 fps. A test asserts the structure survives encode/decode, catching black-output, plane-swap, and
/// colour/format wiring bugs end-to-end (the M-1 / W-4 class).
/// </summary>
internal sealed class StructuredVideoSource : IVideoFrameSource
{
    public const byte BrightY = 200;
    public const byte DarkY = 40;

    private static readonly long s_frameTicks = Stopwatch.Frequency / 30;
    private readonly int _width;
    private readonly int _height;
    private readonly byte[] _nv12;
    private long _nextEmitTicks = Stopwatch.GetTimestamp();

    public StructuredVideoSource(int width, int height)
    {
        _width = width;
        _height = height;
        _nv12 = new byte[width * height * 3 / 2];
        for (int y = 0; y < height; y++)
        {
            byte luma = y < height / 2 ? BrightY : DarkY;
            _nv12.AsSpan(y * width, width).Fill(luma);
        }

        _nv12.AsSpan(width * height).Fill(128); // neutral chroma -> grey, no clipping
    }

    public bool TryGetFrame(out VideoFrame frame)
    {
        long now = Stopwatch.GetTimestamp();
        if (now < _nextEmitTicks)
        {
            frame = default;
            return false;
        }

        _nextEmitTicks = now + s_frameTicks;
        long presentationTimeNs = now * (1_000_000_000L / Stopwatch.Frequency);
        frame = VideoFrame.FromPixels(_nv12, VideoPixelFormat.Nv12, _width, _height, presentationTimeNs);
        return true;
    }
}

/// <summary>A test <see cref="IVideoFrameSink"/> that counts decoded frames and signals when a target is reached.</summary>
internal sealed class CollectingVideoSink(int target) : IVideoFrameSink
{
    private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _gate = new();
    private byte[]? _lastPixels;
    private int _count;

    public int Count => Volatile.Read(ref _count);

    public int LastWidth { get; private set; }

    public int LastHeight { get; private set; }

    public VideoPixelFormat LastFormat { get; private set; }

    public Task Reached => _reached.Task;

    /// <summary>A copy of the most recent decoded frame's pixels (null until a frame with pixels arrives). Y plane first.</summary>
    public byte[]? SnapshotPixels()
    {
        // The stored array is only ever replaced wholesale in Submit (never mutated in place), so returning
        // the reference under the lock hands back an effectively immutable snapshot - no defensive copy is
        // needed. The one unavoidable copy is the ToArray in Submit: the decoder reuses its frame buffer once
        // Submit returns, and a Span/slice can't be held across the thread boundary the test reads it from.
        lock (_gate)
        {
            return _lastPixels;
        }
    }

    public void Submit(VideoFrame frame)
    {
        lock (_gate)
        {
            LastWidth = frame.Width;
            LastHeight = frame.Height;
            LastFormat = frame.PixelFormat;
            if (!frame.Pixels.IsEmpty)
            {
                _lastPixels = frame.Pixels.ToArray();
            }
        }

        if (Interlocked.Increment(ref _count) >= target)
        {
            _reached.TrySetResult();
        }
    }
}
