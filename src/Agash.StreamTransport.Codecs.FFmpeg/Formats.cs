using Agash.StreamTransport.Media;
using FF = FFmpeg.Interop;

namespace Agash.StreamTransport.Codecs.FFmpeg;

// Translations between the Media model and FFmpeg's names for the same things.
internal static class Formats
{
    // FFmpeg's id for a codec this package implements.
    public static FF.CodecId ToCodecId(VideoCodecId codec) =>
        codec == VideoCodecId.H264 ? FF.CodecId.H264
        : codec == VideoCodecId.H265 ? FF.CodecId.Hevc
        : codec == VideoCodecId.AV1 ? FF.CodecId.Av1
        : throw new ArgumentOutOfRangeException(
            nameof(codec),
            codec,
            "FFmpeg codecs here are H.264, H.265 and AV1."
        );

    public static FF.PixelFormat ToFFmpeg(PixelFormat format) =>
        format switch
        {
            PixelFormat.Nv12 => FF.PixelFormat.Nv12,
            PixelFormat.P010 => FF.PixelFormat.P010,
            PixelFormat.I420 => FF.PixelFormat.Yuv420P,
            PixelFormat.Bgra => FF.PixelFormat.Bgra,
            PixelFormat.Rgba => FF.PixelFormat.Rgba,
            PixelFormat.Yuva420 => Yuva420P,
            PixelFormat.Yuy2 => Yuyv422,
            PixelFormat.Uyvy => Uyvy422,
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
        };

    // The Media format FFmpeg's format is, if the Media model has it. BGR0 and RGB0 are BGRA and RGBA
    // with the fourth byte ignored, which is how encoders that take no alpha list them.
    public static PixelFormat? FromFFmpeg(FF.PixelFormat format) =>
        format == FF.PixelFormat.Nv12 ? PixelFormat.Nv12
        : format == FF.PixelFormat.P010 ? PixelFormat.P010
        : format == FF.PixelFormat.Yuv420P ? PixelFormat.I420
        : format == FF.PixelFormat.Bgra || format == Bgr0 ? PixelFormat.Bgra
        : format == FF.PixelFormat.Rgba || format == Rgb0 ? PixelFormat.Rgba
        : format == Yuva420P ? PixelFormat.Yuva420
        : format == Yuyv422 ? PixelFormat.Yuy2
        : format == Uyvy422 ? PixelFormat.Uyvy
        : null;

    public static FF.PixelFormat Bgr0 { get; } = FF.PixelFormat.Parse("bgr0");

    public static FF.PixelFormat Rgb0 { get; } = FF.PixelFormat.Parse("rgb0");

    public static FF.PixelFormat Yuva420P { get; } = FF.PixelFormat.Parse("yuva420p");

    public static FF.PixelFormat Yuyv422 { get; } = FF.PixelFormat.Parse("yuyv422");

    public static FF.PixelFormat Uyvy422 { get; } = FF.PixelFormat.Parse("uyvy422");

    // The DXGI format a Direct3D surface pool holds for a Media format.
    public static FF.PixelFormat SurfaceSoftwareFormat(PixelFormat format) =>
        format is PixelFormat.Bgra or PixelFormat.Rgba ? FF.PixelFormat.Bgra
        : format == PixelFormat.P010 ? FF.PixelFormat.P010
        : FF.PixelFormat.Nv12;

    public static FF.ColorRange ToFFmpeg(ColorRange range) =>
        range switch
        {
            ColorRange.Limited => FF.ColorRange.Limited,
            ColorRange.Full => FF.ColorRange.Full,
            _ => FF.ColorRange.Unspecified,
        };

    public static FF.ColorPrimaries ToFFmpeg(ColorPrimaries primaries) =>
        primaries switch
        {
            ColorPrimaries.Bt601 => FF.ColorPrimaries.Smpte170M,
            ColorPrimaries.Bt709 => FF.ColorPrimaries.Bt709,
            ColorPrimaries.Bt2020 => FF.ColorPrimaries.Bt2020,
            _ => FF.ColorPrimaries.Unspecified,
        };

    public static FF.ColorTransfer ToFFmpeg(TransferFunction transfer) =>
        transfer switch
        {
            TransferFunction.Bt709 => FF.ColorTransfer.Bt709,
            TransferFunction.Srgb => FF.ColorTransfer.Srgb,
            TransferFunction.Pq => FF.ColorTransfer.Pq,
            TransferFunction.Hlg => FF.ColorTransfer.Hlg,
            TransferFunction.Linear => FF.ColorTransfer.Linear,
            _ => FF.ColorTransfer.Unspecified,
        };

    public static FF.ColorSpace ToFFmpeg(ColorMatrix matrix) =>
        matrix switch
        {
            ColorMatrix.Bt601 => FF.ColorSpace.Smpte170M,
            ColorMatrix.Bt709 => FF.ColorSpace.Bt709,
            ColorMatrix.Bt2020 => FF.ColorSpace.Bt2020Ncl,
            ColorMatrix.Identity => FF.ColorSpace.Rgb,
            _ => FF.ColorSpace.Unspecified,
        };

    public static VideoColor FromFFmpeg(FF.Frame frame) =>
        new(
            frame.ColorSpace switch
            {
                FF.ColorSpace.Bt470Bg or FF.ColorSpace.Smpte170M => ColorMatrix.Bt601,
                FF.ColorSpace.Bt709 => ColorMatrix.Bt709,
                FF.ColorSpace.Bt2020Ncl or FF.ColorSpace.Bt2020Cl => ColorMatrix.Bt2020,
                FF.ColorSpace.Rgb => ColorMatrix.Identity,
                _ => ColorMatrix.Unspecified,
            },
            frame.ColorRange switch
            {
                FF.ColorRange.Limited => ColorRange.Limited,
                FF.ColorRange.Full => ColorRange.Full,
                _ => ColorRange.Unspecified,
            },
            frame.ColorPrimaries switch
            {
                FF.ColorPrimaries.Bt470Bg or FF.ColorPrimaries.Smpte170M => ColorPrimaries.Bt601,
                FF.ColorPrimaries.Bt709 => ColorPrimaries.Bt709,
                FF.ColorPrimaries.Bt2020 => ColorPrimaries.Bt2020,
                _ => ColorPrimaries.Unspecified,
            },
            frame.ColorTransfer switch
            {
                FF.ColorTransfer.Bt709 or FF.ColorTransfer.Smpte170M => TransferFunction.Bt709,
                FF.ColorTransfer.Srgb => TransferFunction.Srgb,
                FF.ColorTransfer.Pq => TransferFunction.Pq,
                FF.ColorTransfer.Hlg => TransferFunction.Hlg,
                FF.ColorTransfer.Linear => TransferFunction.Linear,
                _ => TransferFunction.Unspecified,
            }
        );
}
