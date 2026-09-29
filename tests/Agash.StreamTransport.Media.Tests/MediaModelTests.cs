using Agash.StreamTransport.Media;

namespace Agash.StreamTransport.Media.Tests;

[TestClass]
public sealed class MediaTimeTests
{
    [TestMethod]
    public void FromTimestamp_NanosecondTicksPastThreeHours_DoesNotOverflow()
    {
        // Linux and macOS timestamps tick in nanoseconds; ticks * 1e9 overflowed a long after about
        // 2.56 hours of uptime.
        long threeHours = 3L * 3600 * 1_000_000_000;

        var time = MediaTime.FromTimestamp(threeHours, 1_000_000_000);

        Assert.AreEqual(threeHours, time.Nanoseconds);
    }

    [TestMethod]
    public void FromTimestamp_OddFrequency_IsExact()
    {
        // Integer-dividing the frequency first loses precision when it does not divide 1e9.
        var time = MediaTime.FromTimestamp(3_000_000, 3_000_000);

        Assert.AreEqual(1_000_000_000L, time.Nanoseconds);
    }

    [TestMethod]
    public void Subtraction_TwoTimes_IsTheSpanBetween()
    {
        MediaTime earlier = new(1_000_000_000);
        MediaTime later = earlier + TimeSpan.FromMilliseconds(33);

        Assert.AreEqual(TimeSpan.FromMilliseconds(33), later - earlier);
        Assert.IsTrue(earlier < later);
    }

    [TestMethod]
    public void ClockRate_TicksAndBack_RoundTrip()
    {
        ClockRate rtp = new(90_000);

        Assert.AreEqual(90_000 * 3, rtp.ToTicks(TimeSpan.FromSeconds(3)));
        Assert.AreEqual(2999, rtp.ToTicks(TimeSpan.FromMilliseconds(33.333333)));
        Assert.AreEqual(TimeSpan.FromMilliseconds(10), new ClockRate(48_000).ToTimeSpan(480));
    }
}

[TestClass]
public sealed class VideoStorageTests
{
    [TestMethod]
    public void Switch_EveryCase_MatchesWithoutAllocating()
    {
        VideoStorage[] storages =
        [
            new CpuImage(PlaneLayout.Packed(PixelFormat.Nv12, new VideoSize(64, 64))),
            new D3D11Image(1, 0, GpuIdentity.FromAdapterLuid(7)),
            new IOSurfaceImage(2, GpuIdentity.FromMetalRegistryId(8)),
            new DmaBufImage(
                [new DmaBufPlane(3, 0, 64)],
                0x3231564E,
                0,
                GpuIdentity.FromDrmDevice(9)
            ),
        ];
        _ = Describe(storages[0]);

        long before = GC.GetAllocatedBytesForCurrentThread();
        int sum = 0;
        for (int i = 0; i < 1000; i++)
        {
            foreach (VideoStorage storage in storages)
            {
                sum += Describe(storage);
            }
        }

        Assert.AreEqual(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.AreEqual(1000 * (64 + 1 + 2 + 3), sum);
    }

    [TestMethod]
    public void Device_GpuStorages_NameTheirGpu()
    {
        VideoStorage gpu = new D3D11Image(1, 0, GpuIdentity.FromAdapterLuid(7));
        VideoStorage cpu = new CpuImage(PlaneLayout.Packed(PixelFormat.Bgra, new VideoSize(2, 2)));

        Assert.AreEqual(GpuIdentity.FromAdapterLuid(7), gpu.Device);
        Assert.IsNull(cpu.Device);
    }

    private static int Describe(VideoStorage storage) =>
        storage switch
        {
            CpuImage cpu => cpu.Planes[0].Stride,
            D3D11Image d3d11 => (int)d3d11.Texture,
            IOSurfaceImage surface => (int)surface.Surface,
            DmaBufImage dmaBuf => dmaBuf[0].Fd,
        };
}

[TestClass]
public sealed class VideoFrameTests
{
    [TestMethod]
    public void Retain_CpuFrameWithoutRetainer_CopiesIntoAnOwnedLease()
    {
        byte[] pixels = [.. Enumerable.Range(0, 16).Select(static i => (byte)i)];
        VideoFrameLease lease = Deliver(pixels);
        pixels.AsSpan().Clear();

        using (lease)
        {
            VideoFrame kept = lease.Frame;
            Assert.AreEqual(15, kept.CpuData[15]);
            Assert.AreEqual(PixelFormat.Bgra, kept.Format.PixelFormat);
        }

        Assert.IsTrue(lease.IsDisposed);
        _ = Assert.ThrowsExactly<ObjectDisposedException>(() => lease.Frame.Duration);
        lease.Dispose();
    }

    [TestMethod]
    public void Retain_GpuFrameWithoutRetainer_IsRefused() =>
        _ = Assert.ThrowsExactly<NotSupportedException>(static () =>
        {
            VideoFrame frame = new(
                new D3D11Image(1, 0, GpuIdentity.FromAdapterLuid(1)),
                new VideoFormat(PixelFormat.Bgra, 2, 2),
                MediaTimestamp.Captured(MediaTime.Zero)
            );
            _ = frame.Retain();
        });

    [TestMethod]
    public void Constraints_Accepts_ChecksStorageFormatAndDevice()
    {
        var gpu = GpuIdentity.FromDrmDevice(0xE280);
        VideoConstraints constraints = new(
            [VideoStorageKind.DmaBuf, VideoStorageKind.Cpu],
            [PixelFormat.Nv12],
            gpu
        );
        VideoStorage onGpu = new DmaBufImage([new DmaBufPlane(3, 0, 64)], 0, 0, gpu);
        VideoStorage elsewhere = new DmaBufImage(
            [new DmaBufPlane(3, 0, 64)],
            0,
            0,
            GpuIdentity.FromDrmDevice(1)
        );

        Assert.IsTrue(constraints.Accepts(onGpu, PixelFormat.Nv12));
        Assert.IsFalse(constraints.Accepts(elsewhere, PixelFormat.Nv12));
        Assert.IsFalse(constraints.Accepts(onGpu, PixelFormat.Bgra));
    }

    private static VideoFrameLease Deliver(byte[] pixels)
    {
        VideoFormat format = new(PixelFormat.Bgra, 2, 2);
        VideoFrame frame = new(
            new CpuImage(PlaneLayout.Packed(format.PixelFormat, format.CodedSize)),
            format,
            MediaTimestamp.Captured(new MediaTime(5)),
            pixels
        );
        return frame.Retain();
    }
}
