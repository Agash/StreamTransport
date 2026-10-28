using System.Collections.Immutable;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Agash.StreamTransport.Codecs.FFmpeg;
using Agash.StreamTransport.Codecs.Opus;
using Agash.StreamTransport.Media;
using Agash.StreamTransport.WebRtc.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using FF = FFmpeg.Interop;

namespace Agash.StreamTransport.Tests;

internal static class Natives
{
    // The FFmpeg 9 shared libraries eng/fetch-ffmpeg.ps1 puts under native/ffmpeg/<rid>, set when the
    // assembly loads: before anything, discovery included, can load FFmpeg from elsewhere.
    [ModuleInitializer]
    public static void Load()
    {
        string? directory = AppContext.BaseDirectory;
        while (
            directory is not null && !File.Exists(Path.Combine(directory, "StreamTransport.slnx"))
        )
        {
            directory = Path.GetDirectoryName(directory);
        }

        string natives = Path.Combine(
            directory ?? throw new InvalidOperationException("The repository root was not found."),
            "native",
            "ffmpeg",
            RuntimeInformation.RuntimeIdentifier
        );
        if (Directory.Exists(natives))
        {
            FF.FFmpegLibraries.SearchDirectory = natives;
        }
    }
}

// The container a host builds: sessions over WebRTC with the FFmpeg and Opus codecs.
internal static class MediaServices
{
    public static ServiceProvider Create(
        ILoggerFactory? loggers = null,
        IMeterFactory? meters = null
    )
    {
        ServiceCollection services = new();
        if (loggers is not null)
        {
            services.AddSingleton(loggers);
        }

        if (meters is not null)
        {
            services.AddSingleton(meters);
        }

        services.AddStreamTransport().AddStreamTransportWebRtc().AddFFmpegCodecs().AddOpusCodecs();
        return services.BuildServiceProvider();
    }

    // Both peers on one host connect over loopback.
    public static MediaSessionOptions Loopback(MediaSessionOptions options) =>
        options with
        {
            Transport = new WebRtcTransportOptions { IncludeLoopbackCandidates = true },
        };
}

// A moving gradient pushed at a steady rate from a timer, in the first CPU format the consumer takes,
// in the orientation given (a camera mounted sideways).
internal sealed class GradientSource(TimeSpan interval, VideoOrientation orientation = default)
    : IVideoSource
{
    public const int Width = 320;
    public const int Height = 240;

    public static VideoSize Size => new(Width, Height);

    public IDisposable Connect(IVideoFrameConsumer consumer, VideoConstraints constraints)
    {
        PixelFormat format = constraints.PixelFormats.FirstOrDefault(static f =>
            f is PixelFormat.Nv12 or PixelFormat.I420
        );
        if (format is not (PixelFormat.Nv12 or PixelFormat.I420))
        {
            format = PixelFormat.I420;
        }

        return new Pusher(consumer, format, interval, orientation);
    }

    public static byte Luma(int index, int x, int y) => (byte)(32 + ((x + y + (index * 3)) % 192));

    public static double MeanLuma(int index)
    {
        long sum = 0;
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                sum += Luma(index, x, y);
            }
        }

        return sum / (double)(Width * Height);
    }

    private sealed class Pusher : IDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly DeliveryGate _gate = new();

        public Pusher(
            IVideoFrameConsumer consumer,
            PixelFormat format,
            TimeSpan interval,
            VideoOrientation orientation
        ) => _ = Task.Run(() => RunAsync(consumer, format, interval, orientation, _gate, _stop));

        // The gate closes before this returns; the loop ends on the cancellation by itself.
        public void Dispose()
        {
            _gate.Close();
            _stop.Cancel();
        }

        private static async Task RunAsync(
            IVideoFrameConsumer consumer,
            PixelFormat format,
            TimeSpan interval,
            VideoOrientation orientation,
            DeliveryGate gate,
            CancellationTokenSource stop
        )
        {
            using CancellationTokenSource owned = stop;
            CancellationToken cancellationToken = stop.Token;
            using PeriodicTimer timer = new(interval);
            byte[] pixels = new byte[PlaneLayout.PackedSize(format, Size)];
            for (int index = 0; await timer.WaitForNextTickAsync(cancellationToken); index++)
            {
                for (int y = 0; y < Height; y++)
                {
                    for (int x = 0; x < Width; x++)
                    {
                        pixels[(y * Width) + x] = Luma(index, x, y);
                    }
                }

                pixels.AsSpan(Width * Height).Fill(128);
                using (gate.Enter(out bool open))
                {
                    if (!open)
                    {
                        return;
                    }

                    consumer.OnFrame(
                        new VideoFrame(
                            new CpuImage(PlaneLayout.Packed(format, Size)),
                            new VideoFormat(format, Width, Height),
                            MediaTimestamp.Captured(MediaClock.System.Now),
                            pixels,
                            VideoColor.Bt709,
                            orientation
                        )
                    );
                }
            }
        }
    }
}

// A 1 kHz tone pushed in 10 ms chunks from a timer, stereo at 48 kHz unless told otherwise, stamped with
// capture times or, when observed, with times it was only observed at.
internal sealed class ToneSource(int rate = 48_000, int channels = 2, bool observed = false)
    : IAudioSource
{
    public AudioFormat Format { get; } = new(SampleFormat.F32, rate, channels);

    public IDisposable Connect(IAudioFrameConsumer consumer) =>
        new Pusher(consumer, Format, observed);

    private sealed class Pusher : IDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly DeliveryGate _gate = new();

        public Pusher(IAudioFrameConsumer consumer, AudioFormat format, bool observed) =>
            _ = Task.Run(() => RunAsync(consumer, format, observed, _gate, _stop));

        // The gate closes before this returns; the loop ends on the cancellation by itself.
        public void Dispose()
        {
            _gate.Close();
            _stop.Cancel();
        }

        private static async Task RunAsync(
            IAudioFrameConsumer consumer,
            AudioFormat format,
            bool observed,
            DeliveryGate gate,
            CancellationTokenSource stop
        )
        {
            using CancellationTokenSource owned = stop;
            CancellationToken cancellationToken = stop.Token;
            int samples = format.SampleRate / 100;
            using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(10));
            float[] values = new float[samples * format.Channels];
            MediaTime start = MediaClock.System.Now;
            for (long chunk = 0; await timer.WaitForNextTickAsync(cancellationToken); chunk++)
            {
                for (int s = 0; s < samples; s++)
                {
                    float value =
                        0.5f
                        * MathF.Sin(
                            2
                                * MathF.PI
                                * 1000
                                * (((chunk * samples) + s) / (float)format.SampleRate)
                        );
                    values.AsSpan(s * format.Channels, format.Channels).Fill(value);
                }

                MediaTime at = start + new ClockRate(format.SampleRate).ToTimeSpan(chunk * samples);
                using (gate.Enter(out bool open))
                {
                    if (!open)
                    {
                        return;
                    }

                    consumer.OnFrame(
                        new AudioFrame(
                            MemoryMarshal.AsBytes(values.AsSpan()),
                            format,
                            observed ? MediaTimestamp.Observed(at) : MediaTimestamp.Captured(at)
                        )
                    );
                }
            }
        }
    }
}

// Keeps what arrives: the luma of each picture, and when it arrived.
internal sealed class RecordingVideoSink : IVideoSink
{
    private readonly Lock _gate = new();
    private readonly List<double> _lumas = [];
    private readonly TaskCompletionSource _reached = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private readonly int _target;

    public RecordingVideoSink(int target) => _target = target;

    public VideoConstraints Constraints { get; } =
        VideoConstraints.Cpu(PixelFormat.I420, PixelFormat.Nv12);

    public Task Reached => _reached.Task;

    public ImmutableArray<double> Lumas
    {
        get
        {
            lock (_gate)
            {
                return [.. _lumas];
            }
        }
    }

    public VideoFormat LastFormat { get; private set; }

    public VideoOrientation LastOrientation { get; private set; }

    public void OnFrame(in VideoFrame frame)
    {
        _ = frame.Storage.TryGetValue(out CpuImage image);
        int stride = image.Planes[0].Stride;
        ReadOnlySpan<byte> luma = frame.GetPlane(0);
        long sum = 0;
        for (int y = 0; y < frame.Format.VisibleRect.Height; y++)
        {
            foreach (byte value in luma.Slice(y * stride, frame.Format.VisibleRect.Width))
            {
                sum += value;
            }
        }

        lock (_gate)
        {
            LastFormat = frame.Format;
            LastOrientation = frame.Orientation;
            _lumas.Add(
                sum / (double)(frame.Format.VisibleRect.Width * frame.Format.VisibleRect.Height)
            );
            if (_lumas.Count >= _target)
            {
                _reached.TrySetResult();
            }
        }
    }
}

// Counts the audio that arrives and measures its level and pitch, taking 48 kHz stereo unless told
// otherwise.
internal sealed class RecordingAudioSink(int target, int rate = 48_000, int channels = 2)
    : IAudioSink
{
    private readonly TaskCompletionSource _reached = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private int _frames;
    private double _sumSquares;
    private long _samples;
    private long _crossings;
    private float _previous;

    public AudioConstraints Constraints { get; } = new([rate], [SampleFormat.F32], channels);

    public AudioFormat? LastFormat { get; private set; }

    public Task Reached => _reached.Task;

    public int Frames => Volatile.Read(ref _frames);

    public double Rms => _samples == 0 ? 0 : Math.Sqrt(_sumSquares / _samples);

    // The pitch from upward zero crossings of the first channel, at the rate the sink takes.
    public double Frequency =>
        _samples == 0 ? 0 : _crossings * (double)rate / (_samples / (double)channels);

    public void OnFrame(in AudioFrame frame)
    {
        LastFormat = frame.Format;
        ReadOnlySpan<float> values = MemoryMarshal.Cast<byte, float>(frame.Samples);
        for (int i = 0; i < values.Length; i++)
        {
            _sumSquares += values[i] * values[i];
            _samples++;
            if (i % frame.Format.Channels == 0)
            {
                if (_previous < 0 && values[i] >= 0)
                {
                    _crossings++;
                }

                _previous = values[i];
            }
        }

        if (Interlocked.Increment(ref _frames) >= target)
        {
            _reached.TrySetResult();
        }
    }
}
