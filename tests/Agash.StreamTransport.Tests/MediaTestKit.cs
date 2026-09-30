using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Agash.StreamTransport.Codecs.FFmpeg;
using Agash.StreamTransport.Codecs.Opus;
using Agash.StreamTransport.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using FF = FFmpeg.Interop;

namespace Agash.StreamTransport.Tests;

[TestClass]
public static class Natives
{
    // The FFmpeg 9 shared libraries eng/fetch-ffmpeg.ps1 puts under native/ffmpeg/<rid>.
    [AssemblyInitialize]
    public static void Load(TestContext context)
    {
        _ = context;
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
    public static ServiceProvider Create(ILoggerFactory? loggers = null)
    {
        ServiceCollection services = new();
        if (loggers is not null)
        {
            services.AddSingleton(loggers);
        }

        services.AddStreamTransport().AddFFmpegCodecs().AddOpusCodecs();
        return services.BuildServiceProvider();
    }

    // Both peers on one host connect over loopback.
    public static MediaSessionOptions Loopback(MediaSessionOptions options) =>
        options with
        {
            IncludeLoopbackCandidates = true,
        };
}

// A moving gradient pushed at a steady rate from a timer, in the first CPU format the consumer takes.
internal sealed class GradientSource(TimeSpan interval) : IVideoSource
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

        return new Pusher(consumer, format, interval);
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
        private readonly Task _loop;

        public Pusher(IVideoFrameConsumer consumer, PixelFormat format, TimeSpan interval) =>
            _loop = Task.Run(() => RunAsync(consumer, format, interval, _stop.Token));

        public void Dispose()
        {
            _stop.Cancel();
            try
            {
                _loop.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                // Deliberately not logged: stopping the loop cancels its wait.
            }

            _stop.Dispose();
        }

        private static async Task RunAsync(
            IVideoFrameConsumer consumer,
            PixelFormat format,
            TimeSpan interval,
            CancellationToken cancellationToken
        )
        {
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
                consumer.OnFrame(
                    new VideoFrame(
                        new CpuImage(PlaneLayout.Packed(format, Size)),
                        new VideoFormat(format, Width, Height),
                        MediaTimestamp.Captured(MediaClock.System.Now),
                        pixels,
                        VideoColor.Bt709
                    )
                );
            }
        }
    }
}

// A 1 kHz tone pushed in 10 ms chunks from a timer.
internal sealed class ToneSource : IAudioSource
{
    public AudioFormat Format { get; } = new(SampleFormat.F32, 48_000, 2);

    public IDisposable Connect(IAudioFrameConsumer consumer) => new Pusher(consumer, Format);

    private sealed class Pusher : IDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;

        public Pusher(IAudioFrameConsumer consumer, AudioFormat format) =>
            _loop = Task.Run(() => RunAsync(consumer, format, _stop.Token));

        public void Dispose()
        {
            _stop.Cancel();
            try
            {
                _loop.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                // Deliberately not logged: stopping the loop cancels its wait.
            }

            _stop.Dispose();
        }

        private static async Task RunAsync(
            IAudioFrameConsumer consumer,
            AudioFormat format,
            CancellationToken cancellationToken
        )
        {
            const int Samples = 480;
            using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(10));
            float[] values = new float[Samples * format.Channels];
            MediaTime start = MediaClock.System.Now;
            for (long chunk = 0; await timer.WaitForNextTickAsync(cancellationToken); chunk++)
            {
                for (int s = 0; s < Samples; s++)
                {
                    float value =
                        0.5f * MathF.Sin(2 * MathF.PI * 1000 * (((chunk * Samples) + s) / 48_000f));
                    values[(s * 2) + 0] = value;
                    values[(s * 2) + 1] = value;
                }

                MediaTime at = start + new ClockRate(format.SampleRate).ToTimeSpan(chunk * Samples);
                consumer.OnFrame(
                    new AudioFrame(
                        MemoryMarshal.AsBytes(values.AsSpan()),
                        format,
                        MediaTimestamp.Captured(at)
                    )
                );
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

// Counts the audio that arrives and measures its level.
internal sealed class RecordingAudioSink(int target) : IAudioSink
{
    private readonly TaskCompletionSource _reached = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private int _frames;
    private double _sumSquares;
    private long _samples;

    public AudioConstraints Constraints { get; } = new([48_000], [SampleFormat.F32], 2);

    public Task Reached => _reached.Task;

    public int Frames => Volatile.Read(ref _frames);

    public double Rms => _samples == 0 ? 0 : Math.Sqrt(_sumSquares / _samples);

    public void OnFrame(in AudioFrame frame)
    {
        foreach (float value in MemoryMarshal.Cast<byte, float>(frame.Samples))
        {
            _sumSquares += value * value;
            _samples++;
        }

        if (Interlocked.Increment(ref _frames) >= target)
        {
            _reached.TrySetResult();
        }
    }
}
