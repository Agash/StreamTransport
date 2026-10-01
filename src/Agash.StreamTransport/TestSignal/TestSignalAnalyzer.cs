using System.Buffers.Binary;
using Agash.StreamTransport.Media;

namespace Agash.StreamTransport.TestSignal;

/// <summary>What a <see cref="TestSignalAnalyzer"/> has measured.</summary>
/// <param name="Pairs">Flashes matched with their clicks.</param>
/// <param name="MeanOffset">Mean of click minus flash at the outputs: positive when audio lags video.</param>
/// <param name="MinOffset">The smallest offset.</param>
/// <param name="MaxOffset">The largest offset.</param>
/// <param name="Frames">Video frames read.</param>
/// <param name="MeanLatency">Mean of the receiver's wall time minus the barcode's, at the video output.</param>
/// <param name="MinLatency">The smallest latency.</param>
/// <param name="MaxLatency">The largest latency.</param>
public readonly record struct TestSignalMeasurement(
    int Pairs,
    TimeSpan MeanOffset,
    TimeSpan MinOffset,
    TimeSpan MaxOffset,
    int Frames,
    TimeSpan MeanLatency,
    TimeSpan MinLatency,
    TimeSpan MaxLatency
)
{
    /// <inheritdoc/>
    public override string ToString() =>
        FormattableString.Invariant(
            $"A/V offset {MeanOffset.TotalMilliseconds:0.0} ms ({MinOffset.TotalMilliseconds:0.0} to {MaxOffset.TotalMilliseconds:0.0}) over {Pairs} clicks; latency {MeanLatency.TotalMilliseconds:0.0} ms ({MinLatency.TotalMilliseconds:0.0} to {MaxLatency.TotalMilliseconds:0.0}) over {Frames} frames"
        );
}

/// <summary>
/// Measures a received test signal where it leaves the session: wrap the video and audio outputs with it
/// and it notes when each flash and each click is handed on, pairs them, and reads every frame's
/// barcode against this machine's wall clock. The wrapped outputs still get every frame.
/// </summary>
/// <remarks>
/// It reads CPU frames in NV12 or I420; give it an output that takes those (or none) to measure. The
/// times are when the session releases media to the outputs, so a device's own output buffering is not
/// part of the offset.
/// </remarks>
public sealed class TestSignalAnalyzer
{
    private readonly TimeProvider _time;
    private readonly MediaClock _clock;
    private readonly Lock _gate = new();
    private readonly List<MediaTime> _flashes = [];
    private readonly List<MediaTime> _clicks = [];
    private readonly List<TimeSpan> _latencies = [];
    private bool _flashing;
    private bool _clicking;
    private long _silentSamples = long.MaxValue;

    /// <summary>An analyzer on a clock.</summary>
    /// <param name="timeProvider">The clock; the system's when null.</param>
    public TestSignalAnalyzer(TimeProvider? timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        _clock = new MediaClock(_time);
    }

    /// <summary>A video output that measures, then hands frames to <paramref name="inner"/>.</summary>
    /// <param name="inner">The real output, or null to only measure.</param>
    /// <returns>The measuring output.</returns>
    public IVideoSink WrapVideo(IVideoSink? inner = null) => new VideoTap(this, inner);

    /// <summary>An audio output that measures, then hands frames to <paramref name="inner"/>.</summary>
    /// <param name="inner">The real output, or null to only measure.</param>
    /// <returns>The measuring output.</returns>
    public IAudioSink WrapAudio(IAudioSink? inner = null) => new AudioTap(this, inner);

    /// <summary>What has been measured so far.</summary>
    /// <returns>The measurement.</returns>
    public TestSignalMeasurement Measure()
    {
        lock (_gate)
        {
            List<TimeSpan> offsets = [];
            foreach (MediaTime flash in _flashes)
            {
                // The click within half a second of the flash is the same second's.
                MediaTime? click = _clicks
                    .Where(c => (c - flash).Duration() < TimeSpan.FromMilliseconds(500))
                    .Select(static c => (MediaTime?)c)
                    .FirstOrDefault();
                if (click is { } c)
                {
                    offsets.Add(c - flash);
                }
            }

            return new TestSignalMeasurement(
                offsets.Count,
                Mean(offsets),
                offsets.Count > 0 ? offsets.Min() : TimeSpan.Zero,
                offsets.Count > 0 ? offsets.Max() : TimeSpan.Zero,
                _latencies.Count,
                Mean(_latencies),
                _latencies.Count > 0 ? _latencies.Min() : TimeSpan.Zero,
                _latencies.Count > 0 ? _latencies.Max() : TimeSpan.Zero
            );
        }
    }

    private static TimeSpan Mean(List<TimeSpan> values) =>
        values.Count == 0 ? TimeSpan.Zero : TimeSpan.FromTicks((long)values.Average(static v => v.Ticks));

    private void OnVideo(in VideoFrame frame)
    {
        if (
            frame.Storage.Kind != VideoStorageKind.Cpu
            || frame.Format.PixelFormat is not (PixelFormat.Nv12 or PixelFormat.I420)
        )
        {
            return;
        }

        MediaTime now = _clock.Now;
        VideoSize size = frame.Format.CodedSize;
        ReadOnlySpan<byte> luma = frame.GetPlane(0);
        _ = frame.Storage.TryGetValue(out CpuImage image);
        int stride = image.Planes[0].Stride;
        DateTimeOffset sent = TestSignalGenerator.ReadBarcode(luma, stride, size, _time.GetUtcNow());

        // The centre of the frame is white in a flash and dark otherwise.
        long sum = 0;
        int samples = 0;
        for (int y = size.Height * 3 / 8; y < size.Height * 5 / 8; y += 4)
        {
            for (int x = size.Width * 3 / 8; x < size.Width * 5 / 8; x += 4)
            {
                sum += luma[(y * stride) + x];
                samples++;
            }
        }

        bool flash = samples > 0 && sum / samples > 180;
        lock (_gate)
        {
            _latencies.Add(_time.GetUtcNow() - sent);
            if (flash && !_flashing)
            {
                _flashes.Add(now);
            }

            _flashing = flash;
        }
    }

    private void OnAudio(in AudioFrame frame)
    {
        MediaTime now = _clock.Now;
        int channels = frame.Format.Channels;
        int count = frame.SampleCount;
        int onset = -1;
        for (int i = 0; i < count; i++)
        {
            float value = frame.Format.SampleFormat == SampleFormat.F32
                ? BinaryPrimitives.ReadSingleLittleEndian(frame.Samples[(i * channels * 4)..])
                : BinaryPrimitives.ReadInt16LittleEndian(frame.Samples[(i * channels * 2)..]) / 32768f;
            bool loud = Math.Abs(value) > 0.1f;
            if (loud && !_clicking && _silentSamples > frame.Format.SampleRate / 10 && onset < 0)
            {
                onset = i;
            }

            _clicking = loud || (_clicking && _silentSamples < frame.Format.SampleRate / 100);
            _silentSamples = loud ? 0 : _silentSamples + 1;
        }

        if (onset >= 0)
        {
            lock (_gate)
            {
                _clicks.Add(now + new ClockRate(frame.Format.SampleRate).ToTimeSpan(onset));
            }
        }
    }

    private sealed class VideoTap(TestSignalAnalyzer analyzer, IVideoSink? inner) : IVideoSink
    {
        public VideoConstraints Constraints { get; } =
            inner?.Constraints ?? VideoConstraints.Cpu(PixelFormat.Nv12, PixelFormat.I420);

        public void OnFrame(in VideoFrame frame)
        {
            analyzer.OnVideo(in frame);
            inner?.OnFrame(in frame);
        }
    }

    private sealed class AudioTap(TestSignalAnalyzer analyzer, IAudioSink? inner) : IAudioSink
    {
        public AudioConstraints Constraints { get; } =
            inner?.Constraints ?? new AudioConstraints([48_000], [SampleFormat.F32], 2);

        public void OnFrame(in AudioFrame frame)
        {
            analyzer.OnAudio(in frame);
            inner?.OnFrame(in frame);
        }
    }
}
