using System.Buffers.Binary;
using System.Collections.Immutable;
using Agash.StreamTransport.Media;

namespace Agash.StreamTransport.TestSignal;

/// <summary>
/// A generated video and audio signal for measuring a link: once a second, on the same media-clock
/// instant, the video shows a white square and the audio a 1 kHz click. Every frame also carries the wall
/// time it was made in a barcode along its top, so a receiver whose clock is synchronised (NTP on a LAN)
/// reads end-to-end latency off the picture. <see cref="TestSignalAnalyzer"/> reads both.
/// </summary>
/// <remarks>
/// Video and audio are computed from one origin: a frame's or a sample's capture time decides what it
/// shows, so the flash and the click are aligned exactly at the source and any offset a receiver sees was
/// added on the way.
/// </remarks>
public sealed class TestSignalGenerator
{
    /// <summary>The bits of wall time in the barcode: milliseconds since the Unix epoch, modulo 2^40.</summary>
    public const int BarcodeBits = 40;

    /// <summary>The click's frequency.</summary>
    public const double ClickHertz = 1000;

    /// <summary>The click's length.</summary>
    public static readonly TimeSpan ClickLength = TimeSpan.FromMilliseconds(40);

    /// <summary>A generator on a clock.</summary>
    /// <param name="timeProvider">The clock; the system's when null.</param>
    public TestSignalGenerator(TimeProvider? timeProvider = null)
    {
        TimeProvider = timeProvider ?? TimeProvider.System;
        Clock = new MediaClock(TimeProvider);
        Origin = Clock.Now;
        WallOrigin = TimeProvider.GetUtcNow();
    }

    /// <summary>The clock the signal runs on.</summary>
    public TimeProvider TimeProvider { get; }

    /// <summary>The media clock over it.</summary>
    public MediaClock Clock { get; }

    /// <summary>The media time the signal started at; flashes fall on whole seconds after it.</summary>
    public MediaTime Origin { get; }

    /// <summary>The wall time at <see cref="Origin"/>.</summary>
    public DateTimeOffset WallOrigin { get; }

    /// <summary>Whether a frame shown from <paramref name="time"/> for <paramref name="duration"/> holds a flash.</summary>
    /// <param name="time">The frame's capture time.</param>
    /// <param name="duration">How long it shows.</param>
    /// <returns>Whether the frame covers the start of a second.</returns>
    public bool IsFlash(MediaTime time, TimeSpan duration)
    {
        long since = (time - Origin).Ticks;
        long second = TimeSpan.TicksPerSecond;
        long into = ((since % second) + second) % second;
        return into < duration.Ticks;
    }

    /// <summary>The wall time a capture time corresponds to.</summary>
    /// <param name="time">A media time of this signal.</param>
    /// <returns>The wall time.</returns>
    public DateTimeOffset WallTime(MediaTime time) => WallOrigin + (time - Origin);

    /// <summary>Draws one NV12 picture.</summary>
    /// <param name="luma">The luma plane, <paramref name="size"/>.Width per row.</param>
    /// <param name="chroma">The interleaved chroma plane.</param>
    /// <param name="size">The picture size.</param>
    /// <param name="time">The frame's capture time.</param>
    /// <param name="duration">How long it shows.</param>
    public void Draw(Span<byte> luma, Span<byte> chroma, VideoSize size, MediaTime time, TimeSpan duration)
    {
        int w = size.Width;
        int h = size.Height;
        luma[..(w * h)].Fill(48);
        chroma[..(w * (h / 2))].Fill(128);

        // A bar sweeping across once a second shows motion.
        double phase = ((time - Origin).TotalSeconds % 1 + 1) % 1;
        int bar = (int)(phase * (w - (w / 16)));
        for (int y = h / 4; y < h; y++)
        {
            luma.Slice((y * w) + bar, w / 16).Fill(150);
        }

        if (IsFlash(time, duration))
        {
            for (int y = h / 4; y < h * 3 / 4; y++)
            {
                luma.Slice((y * w) + (w / 4), w / 2).Fill(235);
            }
        }

        WriteBarcode(luma, size, (ulong)WallTime(time).ToUnixTimeMilliseconds());
    }

    /// <summary>Fills interleaved float samples: silence, and the click at each whole second.</summary>
    /// <param name="samples">Interleaved samples, <paramref name="channels"/> per frame.</param>
    /// <param name="channels">Channels.</param>
    /// <param name="sampleRate">Samples per second.</param>
    /// <param name="firstSample">The first sample's index counted from <see cref="Origin"/>.</param>
    public static void Fill(Span<float> samples, int channels, int sampleRate, long firstSample)
    {
        long clickSamples = (long)(ClickLength.TotalSeconds * sampleRate);
        for (int i = 0; i < samples.Length / channels; i++)
        {
            long n = firstSample + i;
            long into = n % sampleRate;
            float value = into < clickSamples
                ? (float)(0.5 * Math.Sin(2 * Math.PI * ClickHertz * into / sampleRate))
                : 0f;
            samples.Slice(i * channels, channels).Fill(value);
        }
    }

    /// <summary>Reads the wall time a received picture's barcode carries.</summary>
    /// <param name="luma">The luma plane.</param>
    /// <param name="stride">Its row pitch.</param>
    /// <param name="size">The picture size.</param>
    /// <param name="near">A wall time near the one sent, to restore the bits above the barcode's.</param>
    /// <returns>The wall time.</returns>
    public static DateTimeOffset ReadBarcode(ReadOnlySpan<byte> luma, int stride, VideoSize size, DateTimeOffset near)
    {
        int cell = size.Width / BarcodeBits;
        int row = Math.Max(1, size.Height / 48) / 2;
        ulong bits = 0;
        for (int bit = 0; bit < BarcodeBits; bit++)
        {
            int sum = 0;
            int start = (bit * cell) + (cell / 4);
            for (int x = start; x < start + (cell / 2); x++)
            {
                sum += luma[(row * stride) + x];
            }

            if (sum / (cell / 2) > 128)
            {
                bits |= 1UL << (BarcodeBits - 1 - bit);
            }
        }

        // Restore the high bits from the receiver's own clock, choosing the closest candidate.
        long mask = (1L << BarcodeBits) - 1;
        long nearMs = near.ToUnixTimeMilliseconds();
        long candidate = (nearMs & ~mask) | (long)bits;
        if (candidate - nearMs > mask / 2)
        {
            candidate -= mask + 1;
        }
        else if (nearMs - candidate > mask / 2)
        {
            candidate += mask + 1;
        }

        return DateTimeOffset.FromUnixTimeMilliseconds(candidate);
    }

    private static void WriteBarcode(Span<byte> luma, VideoSize size, ulong milliseconds)
    {
        int w = size.Width;
        int cell = w / BarcodeBits;
        int rows = Math.Max(1, size.Height / 48);
        for (int bit = 0; bit < BarcodeBits; bit++)
        {
            byte value = ((milliseconds >> (BarcodeBits - 1 - bit)) & 1) != 0 ? (byte)235 : (byte)16;
            for (int y = 0; y < rows; y++)
            {
                luma.Slice((y * w) + (bit * cell), cell).Fill(value);
            }
        }
    }
}

/// <summary>The test signal's video as an input, named <c>test</c>.</summary>
/// <param name="generator">The signal; a shared one keeps video and audio aligned.</param>
public sealed class TestSignalVideoInputProvider(TestSignalGenerator generator) : IVideoInputProvider
{
    private static readonly ImmutableArray<VideoInputMode> Modes =
    [
        new(PixelFormat.Nv12, new VideoSize(1920, 1080), 60),
        new(PixelFormat.Nv12, new VideoSize(1920, 1080), 30),
        new(PixelFormat.Nv12, new VideoSize(1280, 720), 60),
        new(PixelFormat.Nv12, new VideoSize(1280, 720), 30),
        new(PixelFormat.Nv12, new VideoSize(640, 360), 30),
    ];

    /// <inheritdoc/>
    public string Name => "test";

    /// <inheritdoc/>
    public int Rank => 0;

    /// <inheritdoc/>
    public ValueTask<ImmutableArray<VideoInputInfo>> GetInputsAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult<ImmutableArray<VideoInputInfo>>(
            [new VideoInputInfo(Name, "signal", "Test signal", MediaInputKind.Generated, Modes)]
        );

    /// <inheritdoc/>
    public ValueTask<IVideoInput> OpenAsync(
        VideoInputInfo input,
        VideoInputRequest request,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        VideoInputMode mode = request.Choose(Modes) ?? Modes[3];
        return ValueTask.FromResult<IVideoInput>(new TestSignalVideoInput(generator, input, mode));
    }
}

/// <summary>The test signal's audio as an input, named <c>test</c>: 48 kHz stereo, 10 ms frames.</summary>
/// <param name="generator">The signal; a shared one keeps video and audio aligned.</param>
public sealed class TestSignalAudioInputProvider(TestSignalGenerator generator) : IAudioInputProvider
{
    /// <inheritdoc/>
    public string Name => "test";

    /// <inheritdoc/>
    public int Rank => 0;

    /// <inheritdoc/>
    public ValueTask<ImmutableArray<AudioInputInfo>> GetInputsAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult<ImmutableArray<AudioInputInfo>>(
            [new AudioInputInfo(Name, "signal", "Test signal", MediaInputKind.Generated)]
        );

    /// <inheritdoc/>
    public ValueTask<IAudioInput> OpenAsync(AudioInputInfo input, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IAudioInput>(new TestSignalAudioInput(generator, input));
}

// Pushes NV12 frames on the generator's clock; capture times are the frames' scheduled times.
internal sealed class TestSignalVideoInput : IVideoInput
{
    private readonly TestSignalGenerator _generator;
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _gate = new();
    private ImmutableArray<IVideoFrameConsumer> _consumers = [];
    private Task? _loop;

    public TestSignalVideoInput(TestSignalGenerator generator, VideoInputInfo info, VideoInputMode mode)
    {
        _generator = generator;
        Info = info;
        Mode = mode;
    }

    public VideoInputInfo Info { get; }

    public VideoInputMode? Mode { get; }

    public IDisposable Connect(IVideoFrameConsumer consumer, VideoConstraints constraints)
    {
        lock (_gate)
        {
            _consumers = _consumers.Add(consumer);
            _loop ??= RunAsync(_stop.Token);
        }

        return new Connection(this, consumer);
    }

    public void Dispose() => _stop.Cancel();

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        VideoInputMode mode = Mode!.Value;
        VideoSize size = mode.Size;
        var period = TimeSpan.FromSeconds(1 / mode.FrameRate);
        byte[] picture = new byte[size.Width * size.Height * 3 / 2];
        using PeriodicTimer timer = new(period, _generator.TimeProvider);
        long first = (long)Math.Ceiling((_generator.Clock.Now - _generator.Origin) / period);
        long index = first;
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                // Every frame that is due, so the schedule holds through a late tick.
                while (_generator.Origin + (period * index) <= _generator.Clock.Now)
                {
                    MediaTime time = _generator.Origin + (period * index);
                    index++;
                    Span<byte> luma = picture.AsSpan(0, size.Width * size.Height);
                    _generator.Draw(luma, picture.AsSpan(luma.Length), size, time, period);
                    VideoFrame frame = new(
                        new VideoFormat(PixelFormat.Nv12, size.Width, size.Height),
                        MediaTimestamp.Captured(time),
                        luma,
                        size.Width,
                        picture.AsSpan(luma.Length),
                        size.Width,
                        color: VideoColor.Bt709,
                        duration: period
                    );
                    foreach (IVideoFrameConsumer consumer in _consumers)
                    {
                        consumer.OnFrame(in frame);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Deliberately not logged: cancellation is how the signal stops.
        }
    }

    private sealed class Connection(TestSignalVideoInput input, IVideoFrameConsumer consumer) : IDisposable
    {
        public void Dispose()
        {
            lock (input._gate)
            {
                input._consumers = input._consumers.Remove(consumer);
            }
        }
    }
}

// Pushes 10 ms of 48 kHz stereo float audio per tick, sample-exact against the generator's origin.
internal sealed class TestSignalAudioInput(TestSignalGenerator generator, AudioInputInfo info) : IAudioInput
{
    private const int SampleRate = 48_000;
    private const int Channels = 2;
    private const int FrameSamples = SampleRate / 100;

    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _gate = new();
    private ImmutableArray<IAudioFrameConsumer> _consumers = [];
    private Task? _loop;

    public AudioInputInfo Info { get; } = info;

    public AudioFormat Format { get; } = new(SampleFormat.F32, SampleRate, Channels);

    public IDisposable Connect(IAudioFrameConsumer consumer)
    {
        lock (_gate)
        {
            _consumers = _consumers.Add(consumer);
            _loop ??= RunAsync(_stop.Token);
        }

        return new Connection(this, consumer);
    }

    public void Dispose() => _stop.Cancel();

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        float[] samples = new float[FrameSamples * Channels];
        byte[] bytes = new byte[samples.Length * sizeof(float)];
        ClockRate rate = new(SampleRate);
        using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(10), generator.TimeProvider);
        long next = rate.ToTicks(generator.Clock.Now - generator.Origin) / FrameSamples * FrameSamples;
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                long due = rate.ToTicks(generator.Clock.Now - generator.Origin);
                while (next + FrameSamples <= due)
                {
                    TestSignalGenerator.Fill(samples, Channels, SampleRate, next);
                    for (int i = 0; i < samples.Length; i++)
                    {
                        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4), samples[i]);
                    }

                    AudioFrame frame = new(
                        bytes,
                        Format,
                        MediaTimestamp.Captured(generator.Origin + rate.ToTimeSpan(next))
                    );
                    next += FrameSamples;
                    foreach (IAudioFrameConsumer consumer in _consumers)
                    {
                        consumer.OnFrame(in frame);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Deliberately not logged: cancellation is how the signal stops.
        }
    }

    private sealed class Connection(TestSignalAudioInput input, IAudioFrameConsumer consumer) : IDisposable
    {
        public void Dispose()
        {
            lock (input._gate)
            {
                input._consumers = input._consumers.Remove(consumer);
            }
        }
    }
}
