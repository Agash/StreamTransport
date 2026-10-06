using Agash.StreamTransport.Codecs.FFmpeg;
using Agash.StreamTransport.Media;
using Agash.StreamTransport.Tests;
using Agash.StreamTransport.WebRtc.DependencyInjection;
using Agash.StreamTransport.WebRtc.Transport;
using Microsoft.Extensions.DependencyInjection;
using Windows.Win32.Graphics.Dxgi.Common;

namespace Agash.StreamTransport.Windows.Tests;

/// <summary>
/// Transparency between GPU textures across a session in AV1: the colour and alpha packed side by side
/// by a shader, AV1 coded (in software where the GPU has no AV1 encoder, read back for it), decoded on
/// the GPU and unpacked by a shader into the sink's texture.
/// </summary>
[TestClass]
[OSCondition(OperatingSystems.Windows)]
public sealed class GpuAlphaSessionTests
{
    private const int Width = 320;
    private const int Height = 240;

    [TestMethod]
    [Timeout(90_000)]
    public async Task Av1SideBySide_FromTextureToTexture_KeepsTheAlpha()
    {
        using var gpu = TestGpu.Open();
        byte[] pixels = new byte[Width * Height * 4];
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int i = ((y * Width) + x) * 4;
                (pixels[i], pixels[i + 1], pixels[i + 2]) = (40, 160, 200);
                pixels[i + 3] = (byte)(x * 255 / (Width - 1));
            }
        }

        nint texture = gpu.Upload(DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, Width, Height, pixels);
        ServiceCollection services = new();
        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(
            new ConsoleLogs(Microsoft.Extensions.Logging.LogLevel.Warning)
        );
        services.AddStreamTransport().AddStreamTransportWebRtc().AddFFmpegCodecs();
        services.AddWindowsMedia();
        await using ServiceProvider provider = services.BuildServiceProvider();
        IMediaSessionFactory factory = provider.GetRequiredService<IMediaSessionFactory>();
        MediaSessionOptions options = new()
        {
            VideoCodecs = [VideoCodecId.AV1],
            Alpha = AlphaLayout.PackSideBySide,
            Transport = new WebRtcTransportOptions { IncludeLoopbackCandidates = true },
        };
        TextureSink sink = new(gpu.Adapter, target: 10);
        (LoopbackSignaling offer, LoopbackSignaling answer) = LoopbackSignaling.Pair();
        await using (offer)
        await using (answer)
        {
            using TextureSource source = new(texture, gpu.Adapter);
            await using IMediaSession sender = factory.Create(
                offer,
                MediaSessionRole.Offerer,
                new MediaEndpoints { VideoSource = source },
                options
            );
            await using IMediaSession receiver = factory.Create(
                answer,
                MediaSessionRole.Answerer,
                new MediaEndpoints { VideoSink = sink },
                options with
                {
                    Alpha = AlphaLayout.None,
                }
            );

            await receiver.StartAsync();
            await sender.StartAsync();
            await Task.WhenAll(sender.Connected, receiver.Connected)
                .WaitAsync(TimeSpan.FromSeconds(20));
            await sink.Reached.WaitAsync(TimeSpan.FromSeconds(60));
        }

        using VideoFrameLease last = sink.Last!;
        Assert.IsTrue(
            last.Frame.Storage.TryGetValue(out D3D12Image image),
            "the sink got a texture"
        );
        byte[] received = gpu.Download(image, 1)[0];
        int row = Height / 2;
        foreach (int x in (int[])[Width / 4, Width / 2, Width * 3 / 4])
        {
            Assert.AreEqual(
                x * 255.0 / (Width - 1),
                received[(((row * Width) + x) * 4) + 3],
                16.0,
                $"alpha at {x}"
            );
        }
    }

    // One texture of colour over an alpha ramp, pushed as a frame every 33 ms.
    private sealed class TextureSource(nint texture, GpuIdentity adapter)
        : IVideoSource,
            IDisposable
    {
        private readonly CancellationTokenSource _stop = new();

        public IDisposable Connect(IVideoFrameConsumer consumer, VideoConstraints constraints)
        {
            Task loop = RunAsync(consumer, _stop.Token);
            return new Stopper(_stop, loop);
        }

        public void Dispose() => _stop.Dispose();

        private async Task RunAsync(IVideoFrameConsumer consumer, CancellationToken cancellation)
        {
            using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(33));
            try
            {
                while (await timer.WaitForNextTickAsync(cancellation))
                {
                    consumer.OnFrame(
                        new VideoFrame(
                            new VideoStorage(new D3D12Image(texture, 0, adapter)),
                            new VideoFormat(PixelFormat.Bgra, Width, Height),
                            MediaTimestamp.Captured(MediaClock.System.Now),
                            color: VideoColor.Srgb,
                            retainer: Unowned.Instance
                        )
                    );
                }
            }
            catch (OperationCanceledException)
            {
                // Deliberately not logged: cancellation is how the source stops.
            }
        }

        private sealed class Stopper(CancellationTokenSource stop, Task loop) : IDisposable
        {
            public void Dispose()
            {
                stop.Cancel();
                loop.GetAwaiter().GetResult();
            }
        }
    }

    // Takes BGRA textures on the test GPU and keeps the latest.
    private sealed class TextureSink(GpuIdentity adapter, int target) : IVideoSink
    {
        private readonly Lock _gate = new();
        private readonly TaskCompletionSource _reached = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private VideoFrameLease? _last;
        private int _frames;

        public VideoConstraints Constraints { get; } =
            new([VideoStorageKind.D3D12], [PixelFormat.Bgra], adapter);

        public Task Reached => _reached.Task;

        public VideoFrameLease? Last
        {
            get
            {
                lock (_gate)
                {
                    VideoFrameLease? last = _last;
                    _last = null;
                    return last;
                }
            }
        }

        public void OnFrame(in VideoFrame frame)
        {
            VideoFrameLease kept = frame.Retain();
            lock (_gate)
            {
                _last?.Dispose();
                _last = kept;
            }

            if (Interlocked.Increment(ref _frames) >= target)
            {
                _reached.TrySetResult();
            }
        }
    }

    // The test owns the texture for the whole test, so a kept frame needs no reference.
    private sealed class Unowned : IVideoFrameRetainer
    {
        public static Unowned Instance { get; } = new();

        public VideoFrameLease Retain(in VideoFrame frame) => new Borrowed(in frame);

        private sealed class Borrowed(in VideoFrame frame)
            : VideoFrameLease(
                frame.Storage,
                frame.Format,
                frame.Timestamp,
                frame.Color,
                frame.Orientation,
                frame.Duration
            )
        {
            protected override void Release() { }
        }
    }
}
