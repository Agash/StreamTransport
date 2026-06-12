namespace Agash.StreamTransport.Codecs;

/// <summary>
/// A surface transform: side-by-side alpha pack/unpack (and the NV12 normalisation that feeds it) as a step
/// independent of the codec, keyed by the surface kind it runs on. The transform is the same byte contract on
/// every platform - colour|alpha laid out in a <c>2W x H</c> frame, alpha as <c>16..235</c> limited-range
/// luma, BT.709 limited - so a frame packed by one implementation unpacks correctly on any other.
///
/// GPU implementations run the transform on the native GPU surface with no readback and are the default on
/// every platform (D3D11 on Windows, Metal on macOS, Vulkan on Linux - all in the capture/publish consumer,
/// where the surface lives). <see cref="CpuSurfaceTransform"/> is the byte-exact reference oracle and the
/// genuine <b>last-resort</b> path, used only for frames that are already in CPU memory (synthetic / camera /
/// PipeWire-MemPtr); it is never the default for a GPU-surface source. (Mandate: handoff §0.)
/// </summary>
/// <summary>Packs a <c>W x H</c> BGRA frame into a <c>2W x H</c> colour|alpha frame for an opaque encoder.</summary>
internal interface IAlphaPacker
{
    VideoFrame PackAlpha(in VideoFrame colourBgra, long presentationTimeNs);
}

/// <summary>Splits a decoded <c>2W x H</c> colour|alpha frame back into a <c>W x H</c> BGRA frame.</summary>
internal interface IAlphaUnpacker
{
    VideoFrame UnpackAlpha(in VideoFrame packed, long presentationTimeNs);
}

/// <summary>
/// Converts a decoded NV12 surface to BGRA for an opaque publish - the colour-only counterpart to
/// <see cref="IAlphaUnpacker"/> (a hardware decoder emits NV12; consumers such as OBS want BGRA).
/// </summary>
internal interface INv12ToBgra
{
    VideoFrame Nv12ToBgra(in VideoFrame nv12, long presentationTimeNs);
}

// Notes on the shape: there is deliberately no combined "does both" interface. A
// transform implements only the role(s) it provides, because the real implementations are split that way -
// Windows D3D11 has a packer on the capture device and a separate unpacker on the decode device, while CPU
// and Metal happen to do both in one object. Keeping the roles separate matches every implementation instead
// of forcing a shape on the ones that only do one direction. The interfaces are not IDisposable; transforms
// that own GPU resources implement IDisposable concretely and are disposed by whoever owns their lifetime.

/// <summary>
/// CPU surface transform - the byte-exact reference and last-resort fallback for CPU-memory frames. Wraps the
/// <see cref="AlphaPacking"/> pack/unpack and the I420→NV12 normalisation a software decoder may require. Never
/// used for a GPU-surface source; those pack/unpack on the GPU in the capture/publish consumer.
/// </summary>
internal sealed class CpuSurfaceTransform : IAlphaPacker, IAlphaUnpacker
{
    public VideoFrame PackAlpha(in VideoFrame colourBgra, long presentationTimeNs)
    {
        byte[] packed = new byte[AlphaPacking.PackedNv12Length(colourBgra.Width, colourBgra.Height)];
        AlphaPacking.PackBgraToNv12(colourBgra.Pixels.Span, colourBgra.Width * 4, colourBgra.Width, colourBgra.Height, packed);
        return VideoFrame.FromPixels(packed, VideoPixelFormat.Nv12, colourBgra.Width * 2, colourBgra.Height, presentationTimeNs);
    }

    public VideoFrame UnpackAlpha(in VideoFrame packed, long presentationTimeNs)
    {
        int width = packed.Width;
        int height = packed.Height;

        // Normalise to NV12 first (a software decoder may emit I420) so the unpack reads the layout it expects.
        byte[] nv12 = packed.PixelFormat == VideoPixelFormat.Nv12 ? packed.Pixels.ToArray() : I420ToNv12(packed.Pixels.Span, width, height);
        byte[] bgra = new byte[(width / 2) * height * 4];
        AlphaPacking.UnpackNv12ToBgra(nv12, width, height, bgra);
        return VideoFrame.FromPixels(bgra, VideoPixelFormat.Bgra, width / 2, height, presentationTimeNs);
    }

    /// <summary>Normalise an opaque CPU frame to NV12 for the encoder (passthrough NV12, convert I420). Last-resort only.</summary>
    public VideoFrame ToEncoderNv12(in VideoFrame frame, long presentationTimeNs)
    {
        if (frame.PixelFormat == VideoPixelFormat.Nv12)
        {
            return frame;
        }

        if (frame.PixelFormat != VideoPixelFormat.I420)
        {
            throw new NotSupportedException($"Pixel format {frame.PixelFormat} is not supported for encode yet.");
        }

        byte[] nv12 = I420ToNv12(frame.Pixels.Span, frame.Width, frame.Height);
        return VideoFrame.FromPixels(nv12, VideoPixelFormat.Nv12, frame.Width, frame.Height, presentationTimeNs);
    }

    private static byte[] I420ToNv12(ReadOnlySpan<byte> i420, int width, int height)
    {
        byte[] nv12 = new byte[width * height * 3 / 2];
        i420[..(width * height)].CopyTo(nv12);
        int chroma = width * height / 4;
        int uOffset = width * height;
        int vOffset = uOffset + chroma;
        int uvOffset = width * height;
        for (int i = 0; i < chroma; i++)
        {
            nv12[uvOffset + (2 * i)] = i420[uOffset + i];
            nv12[uvOffset + (2 * i) + 1] = i420[vOffset + i];
        }

        return nv12;
    }
}
