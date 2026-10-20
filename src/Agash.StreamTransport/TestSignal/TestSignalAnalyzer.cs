using System.Buffers;
using System.Numerics.Tensors;
using System.Runtime.InteropServices;
using Agash.StreamTransport.Media;

namespace Agash.StreamTransport.TestSignal;

/// <summary>What a <see cref="TestSignalAnalyzer"/> has measured.</summary>
/// <param name="Pairs">Flashes matched with their clicks.</param>
/// <param name="MeanOffset">Mean of click minus flash at the outputs: positive when audio lags video.</param>
/// <param name="MinOffset">The smallest offset.</param>
/// <param name="MaxOffset">The largest offset.</param>
/// <param name="Frames">Video frames whose barcode was read.</param>
/// <param name="MeanLatency">Mean of the receiver's wall time minus the barcode's, at the video output.</param>
/// <param name="MinLatency">The smallest latency.</param>
/// <param name="MaxLatency">The largest latency.</param>
/// <param name="Unreadable">
/// Video frames with no barcode, or one further than <see cref="TestSignalAnalyzer.MaxLatency"/> from the
/// receiver's clock: a picture that is not the test signal, or a clock that is not synchronised.
/// </param>
public readonly record struct TestSignalMeasurement(
    int Pairs,
    TimeSpan MeanOffset,
    TimeSpan MinOffset,
    TimeSpan MaxOffset,
    int Frames,
    TimeSpan MeanLatency,
    TimeSpan MinLatency,
    TimeSpan MaxLatency,
    int Unreadable
)
{
    /// <inheritdoc/>
    public override string ToString() =>
        FormattableString.Invariant(
            $"A/V offset {MeanOffset.TotalMilliseconds:0.0} ms ({MinOffset.TotalMilliseconds:0.0} to {MaxOffset.TotalMilliseconds:0.0}) over {Pairs} clicks; latency {MeanLatency.TotalMilliseconds:0.0} ms ({MinLatency.TotalMilliseconds:0.0} to {MaxLatency.TotalMilliseconds:0.0}) over {Frames} frames{(Unreadable > 0 ? $", {Unreadable} frames without a readable barcode" : "")}"
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
    /// <summary>
    /// The largest latency a barcode is believed for; one further from the receiver's clock is counted as
    /// unreadable rather than averaged in.
    /// </summary>
    public static readonly TimeSpan MaxLatency = TimeSpan.FromSeconds(10);

    private readonly TimeProvider _time;
    private readonly MediaClock _clock;
    private readonly Lock _gate = new();
    private readonly List<MediaTime> _flashes = [];
    private readonly List<MediaTime> _clicks = [];
    private readonly List<TimeSpan> _latencies = [];
    private bool _flashing;
    private bool _clicking;
    private long _silentSamples = long.MaxValue;
    private int _unreadable;

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

    /// <summary>Forgets what has been measured, to measure the steady state after a session settles.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _flashes.Clear();
            _clicks.Clear();
            _latencies.Clear();
            _unreadable = 0;
        }
    }

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
                _latencies.Count > 0 ? _latencies.Max() : TimeSpan.Zero,
                _unreadable
            );
        }
    }

    private static TimeSpan Mean(List<TimeSpan> values) =>
        values.Count == 0
            ? TimeSpan.Zero
            : TimeSpan.FromTicks((long)values.Average(static v => v.Ticks));

    private void OnVideo(in VideoFrame frame)
    {
        if (
            frame.Storage.Kind != VideoStorageKind.Cpu
            || frame.Format.PixelFormat is not (PixelFormat.Nv12 or PixelFormat.I420)
        )
        {
            return;
        }

        _ = frame.Storage.TryGetValue(out CpuImage image);
        ObserveVideo(
            frame.GetPlane(0),
            image.Planes[0].Stride,
            frame.Format.CodedSize,
            _clock.Now,
            _time.GetUtcNow()
        );
    }

    private void OnAudio(in AudioFrame frame)
    {
        MediaTime now = _clock.Now;
        AudioFormat format = frame.Format;
        if (format.SampleFormat == SampleFormat.F32)
        {
            ObserveAudio(
                MemoryMarshal.Cast<byte, float>(frame.Samples),
                format.Channels,
                format.SampleRate,
                now
            );
            return;
        }

        int count = frame.SampleCount * format.Channels;
        float[] converted = ArrayPool<float>.Shared.Rent(count);
        try
        {
            Span<float> samples = converted.AsSpan(0, count);
            TensorPrimitives.ConvertChecked(
                MemoryMarshal.Cast<byte, short>(frame.Samples)[..count],
                samples
            );
            TensorPrimitives.Multiply(samples, 1f / 32768, samples);
            ObserveAudio(samples, format.Channels, format.SampleRate, now);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(converted);
        }
    }

    /// <summary>
    /// Notes one video picture shown at <paramref name="at"/>: a flash when its centre is white, and its
    /// barcode's latency when <paramref name="received"/> says when it reached this machine.
    /// </summary>
    /// <param name="luma">The picture's luma plane.</param>
    /// <param name="stride">The bytes from one luma row to the next.</param>
    /// <param name="size">The picture's size.</param>
    /// <param name="at">When the picture was shown: its arrival, or its presentation time in a recording.</param>
    /// <param name="received">
    /// The wall time it reached this machine, for the latency; null for a recording, which has none.
    /// </param>
    public void ObserveVideo(
        ReadOnlySpan<byte> luma,
        int stride,
        VideoSize size,
        MediaTime at,
        DateTimeOffset? received
    )
    {
        TimeSpan? latency =
            received is { } now
            && TestSignalGenerator.TryReadBarcode(luma, stride, size, now, out DateTimeOffset sent)
            && (now - sent).Duration() <= MaxLatency
                ? now - sent
                : null;

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
            if (latency is { } value)
            {
                _latencies.Add(value);
            }
            else if (received is not null)
            {
                _unreadable++;
            }

            if (flash && !_flashing)
            {
                _flashes.Add(at);
            }

            _flashing = flash;
        }
    }

    /// <summary>Notes audio starting at <paramref name="at"/>: a click where it turns loud after silence.</summary>
    /// <param name="samples">Interleaved samples in [-1, 1].</param>
    /// <param name="channels">The channels interleaved; the first is read.</param>
    /// <param name="sampleRate">The sample rate.</param>
    /// <param name="at">When the first sample plays: its arrival, or its presentation time in a recording.</param>
    public void ObserveAudio(
        ReadOnlySpan<float> samples,
        int channels,
        int sampleRate,
        MediaTime at
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        int count = samples.Length / channels;
        int onset = -1;
        for (int i = 0; i < count; i++)
        {
            bool loud = Math.Abs(samples[i * channels]) > 0.1f;
            if (loud && !_clicking && _silentSamples > sampleRate / 10 && onset < 0)
            {
                onset = i;
            }

            _clicking = loud || (_clicking && _silentSamples < sampleRate / 100);
            _silentSamples = loud ? 0 : _silentSamples + 1;
        }

        if (onset >= 0)
        {
            lock (_gate)
            {
                _clicks.Add(at + new ClockRate(sampleRate).ToTimeSpan(onset));
            }
        }
    }

    private sealed class VideoTap(TestSignalAnalyzer analyzer, IVideoSink? inner) : IVideoSink
    {
        public VideoConstraints Constraints { get; } =
            inner?.Constraints ?? VideoConstraints.Cpu(PixelFormat.Nv12, PixelFormat.I420);

        public TimeSpan? OutputLatency => inner?.OutputLatency;

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

        public TimeSpan? OutputLatency => inner?.OutputLatency;

        public void OnFrame(in AudioFrame frame)
        {
            analyzer.OnAudio(in frame);
            inner?.OnFrame(in frame);
        }
    }
}
