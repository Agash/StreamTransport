using Agash.StreamTransport.Media;
using Agash.StreamTransport.Sessions;
using Microsoft.Extensions.DependencyInjection;

namespace Agash.StreamTransport.Tests;

/// <summary>
/// Transparency across a session: a BGRA source with an alpha ramp in, frames with alpha out, by
/// whichever way the two sides agree on.
/// </summary>
[TestClass]
public sealed class AlphaSessionTests
{
    [TestMethod]
    [Timeout(60_000)]
    public async Task SideBySide_CarriesTheAlpha()
    {
        (AlphaLayout negotiated, AlphaSink sink) = await RunAsync(
            "H264",
            AlphaLayout.PackSideBySide
        );

        Assert.AreEqual(AlphaLayout.PackSideBySide, negotiated);
        sink.AssertRamp();
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task LayerPreferred_UsesTheLayerWhereAnEncoderCodesIt_ElseSideBySide()
    {
        await using ServiceProvider services = MediaServices.Create();
        bool layered = services
            .GetRequiredService<MediaCodecRegistry>()
            .CanEncodeAlphaLayer(VideoCodecId.H265);

        (AlphaLayout negotiated, AlphaSink sink) = await RunAsync("H265", AlphaLayout.Layer);

        Assert.AreEqual(layered ? AlphaLayout.Layer : AlphaLayout.PackSideBySide, negotiated);
        sink.AssertRamp();
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task NoAlphaAsked_SendsOpaqueVideo()
    {
        (AlphaLayout negotiated, AlphaSink sink) = await RunAsync("H264", AlphaLayout.None);

        Assert.AreEqual(AlphaLayout.None, negotiated);
        Assert.AreEqual(255, sink.LeftAlpha, 1.0, "opaque video arrives opaque");
    }

    private static async Task<(AlphaLayout Negotiated, AlphaSink Sink)> RunAsync(
        string codec,
        AlphaLayout alpha
    )
    {
        await using ServiceProvider services = MediaServices.Create();
        IMediaSessionFactory factory = services.GetRequiredService<IMediaSessionFactory>();
        MediaSessionOptions options = MediaServices.Loopback(
            new MediaSessionOptions { VideoCodecs = [new VideoCodecId(codec)], Alpha = alpha }
        );
        AlphaSink sink = new(target: 15);
        (LoopbackSignaling offer, LoopbackSignaling answer) = LoopbackSignaling.Pair();
        await using (offer)
        await using (answer)
        {
            using AlphaSource source = new();
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
            await sink.Reached.WaitAsync(TimeSpan.FromSeconds(30));

            AlphaLayout negotiated = ((MediaSession)receiver).VideoAlpha;
            Assert.AreEqual(negotiated, ((MediaSession)sender).VideoAlpha);
            return (negotiated, sink);
        }
    }

    // BGRA frames of flat colour over an alpha ramp: transparent on the left, opaque on the right.
    private sealed class AlphaSource : IVideoSource, IDisposable
    {
        public const int Width = 320;
        public const int Height = 240;

        private readonly CancellationTokenSource _stop = new();

        public IDisposable Connect(IVideoFrameConsumer consumer, VideoConstraints constraints)
        {
            Task loop = RunAsync(consumer, _stop.Token);
            return new Stopper(_stop, loop);
        }

        public void Dispose() => _stop.Dispose();

        private static async Task RunAsync(
            IVideoFrameConsumer consumer,
            CancellationToken cancellationToken
        )
        {
            byte[] pixels = new byte[Width * Height * 4];
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    int i = ((y * Width) + x) * 4;
                    pixels[i] = 40;
                    pixels[i + 1] = 160;
                    pixels[i + 2] = 200;
                    pixels[i + 3] = (byte)(x * 255 / (Width - 1));
                }
            }

            using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(33));
            try
            {
                while (await timer.WaitForNextTickAsync(cancellationToken))
                {
                    consumer.OnFrame(
                        new VideoFrame(
                            new CpuImage(
                                PlaneLayout.Packed(PixelFormat.Bgra, new VideoSize(Width, Height))
                            ),
                            new VideoFormat(PixelFormat.Bgra, Width, Height),
                            MediaTimestamp.Captured(MediaClock.System.Now),
                            pixels,
                            VideoColor.Srgb
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

    // Takes BGRA in memory and reads the alpha a quarter in from each side of the middle row.
    private sealed class AlphaSink(int target) : IVideoSink
    {
        private readonly TaskCompletionSource _reached = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private int _frames;

        public VideoConstraints Constraints { get; } = VideoConstraints.Cpu(PixelFormat.Bgra);

        public Task Reached => _reached.Task;

        public double LeftAlpha { get; private set; }

        public double RightAlpha { get; private set; }

        public void OnFrame(in VideoFrame frame)
        {
            Assert.AreEqual(PixelFormat.Bgra, frame.Format.PixelFormat);
            _ = frame.Storage.TryGetValue(out CpuImage image);
            ReadOnlySpan<byte> row = frame.GetPlane(0)[
                (image.Planes[0].Stride * (frame.Format.VisibleRect.Height / 2))..
            ];
            int width = frame.Format.VisibleRect.Width;
            LeftAlpha = row[((width / 4) * 4) + 3];
            RightAlpha = row[((width * 3 / 4) * 4) + 3];
            if (Interlocked.Increment(ref _frames) >= target)
            {
                _reached.TrySetResult();
            }
        }

        // The ramp's values a quarter and three quarters across, within what lossy coding moves them.
        public void AssertRamp()
        {
            Assert.AreEqual(255.0 / 4, LeftAlpha, 16.0, "alpha a quarter across");
            Assert.AreEqual(255.0 * 3 / 4, RightAlpha, 16.0, "alpha three quarters across");
        }
    }
}
