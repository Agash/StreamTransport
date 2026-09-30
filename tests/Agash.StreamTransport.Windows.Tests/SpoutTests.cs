using Agash.StreamTransport.Media;
using Agash.StreamTransport.Windows.Spout;
using Windows.Win32.Graphics.Dxgi.Common;

namespace Agash.StreamTransport.Windows.Tests;

[TestClass]
[OSCondition(OperatingSystems.Windows)]
public sealed class SpoutTests
{
    private const int Width = 64;
    private const int Height = 32;

    [TestMethod]
    [Timeout(30_000)]
    public async Task SinkToSource_KeptFrameHoldsThePicture()
    {
        using var gpu = TestGpu.Open();
        string name = $"StreamTransport test {Guid.NewGuid():N}";
        byte[] pixels = new byte[Width * Height * 4];
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)(i * 7 % 251);
        }

        nint texture = gpu.Upload(DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, Width, Height, pixels);
        using SpoutVideoSink sink = new(name, gpu.Adapter);
        using SpoutVideoSource source = new(
            new SpoutVideoSourceOptions { SenderName = name, Adapter = gpu.Adapter }
        );
        Keeper keeper = new();
        using IDisposable connection = source.Connect(keeper, sink.Constraints);

        // Published at a frame rate, as a real sender does, until the receiver has one; a frame is a
        // view for one call, so each is made for its call.
        using (PeriodicTimer frames = new(TimeSpan.FromMilliseconds(10)))
        using (CancellationTokenSource expiry = new(TimeSpan.FromSeconds(10)))
        {
            do
            {
                Publish(sink, texture, gpu.Adapter);
            } while (!keeper.Kept.IsCompleted && await frames.WaitForNextTickAsync(expiry.Token));
        }

        using VideoFrameLease kept = await keeper.Kept.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(new VideoFormat(PixelFormat.Bgra, Width, Height), kept.Frame.Format);
        Assert.AreEqual(
            TimestampKind.Observation,
            kept.Frame.Timestamp.Kind,
            "Spout carries no capture time"
        );
        Assert.IsTrue(kept.Frame.Storage.TryGetValue(out D3D12Image image));
        CollectionAssert.AreEqual(pixels, gpu.Download(image, 1)[0]);
    }

    [TestMethod]
    public void Sink_RefusesFramesOffItsGpu()
    {
        using var gpu = TestGpu.Open();
        using SpoutVideoSink sink = new($"StreamTransport test {Guid.NewGuid():N}", gpu.Adapter);

        Assert.ThrowsExactly<ArgumentException>(() =>
            sink.OnFrame(
                new VideoFrame(
                    new VideoStorage(
                        new CpuImage(PlaneLayout.Packed(PixelFormat.Bgra, new VideoSize(2, 2)))
                    ),
                    new VideoFormat(PixelFormat.Bgra, 2, 2),
                    MediaTimestamp.Captured(new MediaTime(1)),
                    new byte[16]
                )
            )
        );
    }

    private static void Publish(SpoutVideoSink sink, nint texture, GpuIdentity adapter) =>
        sink.OnFrame(
            new VideoFrame(
                new VideoStorage(new D3D12Image(texture, 0, adapter)),
                new VideoFormat(PixelFormat.Bgra, Width, Height),
                MediaTimestamp.Captured(new MediaTime(1))
            )
        );

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
