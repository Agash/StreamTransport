using Agash.StreamTransport.Media;
using PipeWire.NET.Media;
using PipeWireColor = PipeWire.NET.Media.VideoColorInfo;
using PipeWireFormat = PipeWire.NET.Media.PixelFormat;
using PixelFormat = Agash.StreamTransport.Media.PixelFormat;

namespace Agash.StreamTransport.Linux.PipeWire;

// Formats, colours and times between PipeWire.NET and the media contracts.
internal static class PipeWireMapping
{
    // How far a producer's timestamp may be from now and still be taken as a time on the monotonic
    // clock. Producers that stamp with a clock of their own (a running time from zero) land far outside.
    private static readonly TimeSpan PlausibleSkew = TimeSpan.FromSeconds(5);

    public static PixelFormat? ToMedia(PipeWireFormat format) =>
        format switch
        {
            // The X formats carry no alpha; their fourth byte is read as opaque.
            PipeWireFormat.Bgra or PipeWireFormat.Bgrx => PixelFormat.Bgra,
            PipeWireFormat.Rgba or PipeWireFormat.Rgbx => PixelFormat.Rgba,
            PipeWireFormat.Nv12 => PixelFormat.Nv12,
            PipeWireFormat.Yuv420 => PixelFormat.I420,
            _ => null,
        };

    public static PipeWireFormat? ToPipeWire(PixelFormat format) =>
        format switch
        {
            PixelFormat.Bgra => PipeWireFormat.Bgra,
            PixelFormat.Rgba => PipeWireFormat.Rgba,
            PixelFormat.Nv12 => PipeWireFormat.Nv12,
            PixelFormat.I420 => PipeWireFormat.Yuv420,
            _ => null,
        };

    public static VideoColor ToMedia(PipeWireColor color, PixelFormat format)
    {
        bool rgb = format is PixelFormat.Bgra or PixelFormat.Rgba;
        return new VideoColor(
            color.Matrix switch
            {
                VideoColorMatrix.Rgb => ColorMatrix.Identity,
                VideoColorMatrix.Bt709 => ColorMatrix.Bt709,
                VideoColorMatrix.Bt601 => ColorMatrix.Bt601,
                VideoColorMatrix.Bt2020 => ColorMatrix.Bt2020,
                _ => rgb ? ColorMatrix.Identity : ColorMatrix.Unspecified,
            },
            color.Range switch
            {
                VideoColorRange.Full_0_255 => ColorRange.Full,
                VideoColorRange.Limited_16_235 => ColorRange.Limited,
                _ => rgb ? ColorRange.Full : ColorRange.Unspecified,
            },
            color.Primaries switch
            {
                VideoColorPrimaries.Bt601 => ColorPrimaries.Bt601,
                VideoColorPrimaries.Bt709 => ColorPrimaries.Bt709,
                VideoColorPrimaries.Bt2020 => ColorPrimaries.Bt2020,
                _ => ColorPrimaries.Unspecified,
            },
            color.Transfer switch
            {
                // BT.601 and BT.709 share one transfer curve.
                VideoTransferFunction.Bt709 or VideoTransferFunction.Bt601 =>
                    TransferFunction.Bt709,
                VideoTransferFunction.Srgb => TransferFunction.Srgb,
                VideoTransferFunction.Pq => TransferFunction.Pq,
                VideoTransferFunction.Hlg => TransferFunction.Hlg,
                VideoTransferFunction.Linear => TransferFunction.Linear,
                _ => TransferFunction.Unspecified,
            },
            color.ChromaSite switch
            {
                VideoChromaSite.None => ChromaSiting.Center,
                VideoChromaSite.HCosited => ChromaSiting.Left,
                VideoChromaSite.Cosited => ChromaSiting.TopLeft,
                _ => ChromaSiting.Unspecified,
            }
        );
    }

    public static PipeWireColor ToPipeWire(VideoColor color) =>
        new(
            color.Range switch
            {
                ColorRange.Full => VideoColorRange.Full_0_255,
                ColorRange.Limited => VideoColorRange.Limited_16_235,
                _ => VideoColorRange.Unknown,
            },
            color.Matrix switch
            {
                ColorMatrix.Identity => VideoColorMatrix.Rgb,
                ColorMatrix.Bt601 => VideoColorMatrix.Bt601,
                ColorMatrix.Bt709 => VideoColorMatrix.Bt709,
                ColorMatrix.Bt2020 => VideoColorMatrix.Bt2020,
                _ => VideoColorMatrix.Unknown,
            },
            color.Transfer switch
            {
                TransferFunction.Bt709 => VideoTransferFunction.Bt709,
                TransferFunction.Srgb => VideoTransferFunction.Srgb,
                TransferFunction.Pq => VideoTransferFunction.Pq,
                TransferFunction.Hlg => VideoTransferFunction.Hlg,
                TransferFunction.Linear => VideoTransferFunction.Linear,
                _ => VideoTransferFunction.Unknown,
            },
            color.Primaries switch
            {
                ColorPrimaries.Bt601 => VideoColorPrimaries.Bt601,
                ColorPrimaries.Bt709 => VideoColorPrimaries.Bt709,
                ColorPrimaries.Bt2020 => VideoColorPrimaries.Bt2020,
                _ => VideoColorPrimaries.Unknown,
            },
            color.ChromaSiting switch
            {
                ChromaSiting.Center => VideoChromaSite.None,
                ChromaSiting.Left => VideoChromaSite.HCosited,
                ChromaSiting.TopLeft => VideoChromaSite.Cosited,
                _ => VideoChromaSite.Unknown,
            }
        );

    /// <summary>
    /// When a buffer was made, on the media clock. PipeWire's graph runs on CLOCK_MONOTONIC, which is
    /// the media clock on Linux. A producer's own timestamp is its capture time when it is on that
    /// clock; otherwise the frame is stamped with the graph cycle it was queued in, which is when it
    /// was observed.
    /// </summary>
    public static MediaTimestamp Timestamp(long? presentation, long? queued, MediaTime now)
    {
        if (presentation is { } pts && Math.Abs(now.Nanoseconds - pts) < PlausibleSkew.Ticks * 100)
        {
            return MediaTimestamp.Captured(new MediaTime(pts));
        }

        return MediaTimestamp.Observed(queued is { } q ? new MediaTime(q) : now);
    }
}
