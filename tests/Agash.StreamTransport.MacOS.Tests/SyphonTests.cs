using Agash.StreamTransport.MacOS.Metal;
using Agash.StreamTransport.MacOS.Syphon;
using Agash.StreamTransport.Media;

namespace Agash.StreamTransport.MacOS.Tests;

[TestClass]
public sealed class SyphonTests
{
    private const int Width = 64;
    private const int Height = 32;

    [TestMethod]
    [Timeout(30_000)]
    [DataRow(VideoStorageKind.IOSurface)]
    [DataRow(VideoStorageKind.Cpu)]
    public async Task SinkToSource_KeptFrameHoldsThePicture(VideoStorageKind storage)
    {
        byte[] pixels = new byte[Width * Height * 4];
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)(i * 7 % 251);
        }

        using SyphonVideoSink sink = new(
            $"StreamTransport test {Guid.NewGuid():N}",
            TestSurfaces.Device
        );
        using SyphonVideoSource source = new(sink.Description, TestSurfaces.Device);
        Keeper keeper = new();
        using IDisposable connection = source.Connect(keeper, sink.Constraints);
        PooledSurface surface = TestSurfaces.Upload(PixelFormat.Bgra, Width, Height, pixels);
        try
        {
            // Published at a frame rate, as a real server does, until the source has one; a frame is
            // a view for one call, so each is made for its call.
            using PeriodicTimer frames = new(TimeSpan.FromMilliseconds(10));
            using CancellationTokenSource expiry = new(TimeSpan.FromSeconds(10));
            do
            {
                if (storage == VideoStorageKind.IOSurface)
                {
                    sink.OnFrame(TestSurfaces.Frame(surface, PixelFormat.Bgra, Width, Height));
                }
                else
                {
                    sink.OnFrame(
                        new VideoFrame(
                            new VideoFormat(PixelFormat.Bgra, Width, Height),
                            MediaTimestamp.Captured(new MediaTime(1)),
                            pixels,
                            Width * 4
                        )
                    );
                }
            } while (!keeper.Kept.IsCompleted && await frames.WaitForNextTickAsync(expiry.Token));
        }
        finally
        {
            surface.Release();
        }

        using VideoFrameLease kept = await keeper.Kept.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(new VideoFormat(PixelFormat.Bgra, Width, Height), kept.Frame.Format);
        Assert.AreEqual(
            TimestampKind.Observation,
            kept.Frame.Timestamp.Kind,
            "Syphon carries no capture time"
        );
        Assert.IsTrue(kept.Frame.Storage.TryGetValue(out IOSurfaceImage image));
        CollectionAssert.AreEqual(
            pixels,
            TestSurfaces.Download(image, PixelFormat.Bgra, Width, Height)[0]
        );
    }

    [TestMethod]
    public void Sink_RefusesNv12()
    {
        using SyphonVideoSink sink = new(
            $"StreamTransport test {Guid.NewGuid():N}",
            TestSurfaces.Device
        );

        Assert.ThrowsExactly<ArgumentException>(() =>
            sink.OnFrame(
                new VideoFrame(
                    new VideoFormat(PixelFormat.Nv12, 2, 2),
                    MediaTimestamp.Captured(new MediaTime(1)),
                    new byte[4],
                    2,
                    new byte[2],
                    2
                )
            )
        );
    }

    // Keeps the first frame it is handed.
    private sealed class Keeper : IVideoFrameConsumer
    {
        private readonly TaskCompletionSource<VideoFrameLease> _kept = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public Task<VideoFrameLease> Kept => _kept.Task;

        public void OnFrame(in VideoFrame frame)
        {
            if (!_kept.Task.IsCompleted)
            {
                VideoFrameLease lease = frame.Retain();
                if (!_kept.TrySetResult(lease))
                {
                    lease.Dispose();
                }
            }
        }
    }
}
