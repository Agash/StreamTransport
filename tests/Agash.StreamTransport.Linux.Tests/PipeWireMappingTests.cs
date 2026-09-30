using Agash.StreamTransport.Linux.PipeWire;
using Agash.StreamTransport.Media;
using PipeWire.NET.Media;
using PipeWireFormat = PipeWire.NET.Media.PixelFormat;
using PixelFormat = Agash.StreamTransport.Media.PixelFormat;

namespace Agash.StreamTransport.Linux.Tests;

[TestClass]
public sealed class PipeWireMappingTests
{
    private static readonly MediaTime Now = new(1_000_000_000_000);

    [TestMethod]
    public void Timestamp_OnTheMonotonicClock_IsTheCaptureTime()
    {
        long pts = Now.Nanoseconds - 20_000_000;
        Assert.AreEqual(
            MediaTimestamp.Captured(new MediaTime(pts)),
            PipeWireMapping.Timestamp(pts, 5, Now)
        );
    }

    [TestMethod]
    public void Timestamp_OnAClockOfItsOwn_FallsBackToTheQueuedCycle()
    {
        long queued = Now.Nanoseconds - 1_000_000;
        Assert.AreEqual(
            MediaTimestamp.Observed(new MediaTime(queued)),
            PipeWireMapping.Timestamp(40_000_000, queued, Now),
            "a running time from zero is not a monotonic time"
        );
        Assert.AreEqual(MediaTimestamp.Observed(Now), PipeWireMapping.Timestamp(null, null, Now));
    }

    [TestMethod]
    public void Formats_RoundTripWhatBothSidesHave()
    {
        PixelFormat[] formats =
        [
            PixelFormat.Bgra,
            PixelFormat.Rgba,
            PixelFormat.Nv12,
            PixelFormat.I420,
        ];
        foreach (PixelFormat format in formats)
        {
            Assert.AreEqual(
                format,
                PipeWireMapping.ToMedia(PipeWireMapping.ToPipeWire(format)!.Value)
            );
        }

        Assert.AreEqual(PixelFormat.Bgra, PipeWireMapping.ToMedia(PipeWireFormat.Bgrx));
        Assert.IsNull(PipeWireMapping.ToMedia(PipeWireFormat.Yuyv));
        Assert.IsNull(PipeWireMapping.ToPipeWire(PixelFormat.P010));
    }

    [TestMethod]
    public void Color_UnstatedRgbIsFullRangeIdentity()
    {
        VideoColor rgb = PipeWireMapping.ToMedia(VideoColorInfo.Unknown, PixelFormat.Bgra);
        Assert.AreEqual(ColorMatrix.Identity, rgb.Matrix);
        Assert.AreEqual(ColorRange.Full, rgb.Range);

        VideoColor yuv = PipeWireMapping.ToMedia(
            new VideoColorInfo(
                VideoColorRange.Limited_16_235,
                VideoColorMatrix.Bt709,
                VideoTransferFunction.Bt709,
                VideoColorPrimaries.Bt709,
                VideoChromaSite.HCosited
            ),
            PixelFormat.Nv12
        );
        Assert.AreEqual(VideoColor.Bt709, yuv);
    }

    [TestMethod]
    public void Color_RoundTripsThroughWhatAProducerDeclares()
    {
        VideoColor[] colours =
        [
            VideoColor.Bt709,
            VideoColor.Srgb,
            new(
                ColorMatrix.Bt2020,
                ColorRange.Limited,
                ColorPrimaries.Bt2020,
                TransferFunction.Pq,
                ChromaSiting.TopLeft
            ),
            new(
                ColorMatrix.Bt601,
                ColorRange.Full,
                ColorPrimaries.Bt601,
                TransferFunction.Bt709,
                ChromaSiting.Center
            ),
        ];
        foreach (VideoColor colour in colours)
        {
            PixelFormat format =
                colour.Matrix == ColorMatrix.Identity ? PixelFormat.Bgra : PixelFormat.Nv12;
            Assert.AreEqual(
                colour,
                PipeWireMapping.ToMedia(PipeWireMapping.ToPipeWire(colour), format)
            );
        }
    }
}
