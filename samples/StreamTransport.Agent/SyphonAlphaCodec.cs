#if HAS_SYPHON
using System.Runtime.Versioning;
using Agash.StreamTransport;
using Agash.StreamTransport.Codecs;
using Syphon.NET;

// Disambiguate from the macOS framework bindings' `IOSurface` namespace.
using SyphonSurface = Syphon.NET.IOSurface;

namespace StreamTransport.Agent;

/// <summary>
/// macOS GPU side-by-side alpha pack/unpack for the Syphon zero-copy path, built on Syphon.NET's
/// <see cref="SurfaceEffect"/> Metal helper. The transport-specific shaders (the 2W x H colour|alpha
/// layout, BT.709 limited constants, 16..235 alpha) live here as embedded <c>.metal</c> files; the
/// library only supplies the general "run a fragment shader over IOSurfaces" primitive. The result
/// of each call is a surface owned by the underlying effect, valid until the next call of the same
/// direction - encode/publish it before packing/unpacking the next frame. macOS-only.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class SyphonAlphaCodec : IDisposable, IAlphaPacker, IAlphaUnpacker
{
    // NV12 biplanar pixel formats a hardware HEVC decoder emits (VideoToolbox); both have an R8
    // luma plane (0) and an RG8 CbCr plane (1).
    private const uint Nv12VideoRange = 0x34323076; // '420v'
    private const uint Nv12FullRange = 0x34323066;  // '420f'

    private readonly SurfaceEffect _pack;
    private SurfaceEffect? _unpackNv12;
    private SurfaceEffect? _unpackBgra;
    private bool _disposed;

    public SyphonAlphaCodec() =>
        _pack = new SurfaceEffect(EmbeddedShader.Load("alpha_pack.metal"), "alpha_pack");

    /// <summary>Pack a W x H BGRA surface frame into a 2W x H colour|alpha surface frame (zero-copy, Metal).</summary>
    public VideoFrame PackAlpha(in VideoFrame colourBgra, long presentationTimeNs)
    {
        SyphonSurface packed = Pack(new SyphonSurface(colourBgra.Surface));
        return VideoFrame.FromSurface(packed.Handle, StreamInteropKind.Syphon, packed.Width, packed.Height, presentationTimeNs)
            with { PixelFormat = VideoPixelFormat.Bgra };
    }

    /// <summary>Split a decoded 2W x H colour|alpha surface frame back into a W x H BGRA surface frame (zero-copy, Metal).</summary>
    public VideoFrame UnpackAlpha(in VideoFrame packed, long presentationTimeNs)
    {
        SyphonSurface result = Unpack(new SyphonSurface(packed.Surface));
        return VideoFrame.FromSurface(result.Handle, StreamInteropKind.Syphon, result.Width, result.Height, presentationTimeNs)
            with { PixelFormat = VideoPixelFormat.Bgra };
    }

    // The SyphonSurface-typed Pack/Unpack below are the Syphon-native primitive (rich IOSurface in/out); the
    // VideoFrame PackAlpha/UnpackAlpha above are the production IAlphaPacker/IAlphaUnpacker wrappers over them
    // (same relationship as VideoToolboxVideoEncoder vs its backend). SyphonSelfTest drives the primitive
    // directly because it works in SyphonSurfaces (layout verification, Handle, Width/Height).

    /// <summary>Pack a W x H BGRA surface into a 2W x H BGRA surface (left = colour, right = alpha-as-luma).</summary>
    public SyphonSurface Pack(SyphonSurface bgra) =>
        _pack.Render(bgra.Width * 2, bgra.Height, [new SurfaceInput(bgra, MetalPixelFormat.Bgra8Unorm)]);

    /// <summary>Unpack a decoded 2W x H surface (NV12 from the hardware decoder, or BGRA) into W x H BGRA with alpha.</summary>
    public SyphonSurface Unpack(SyphonSurface packed)
    {
        uint fourcc = (uint)packed.PixelFormat;
        if (fourcc == Nv12VideoRange || fourcc == Nv12FullRange)
        {
            _unpackNv12 ??= new SurfaceEffect(EmbeddedShader.Load("alpha_unpack_nv12.metal"), "alpha_unpack_nv12");
            return _unpackNv12.Render(packed.Width / 2, packed.Height,
            [
                new SurfaceInput(packed, MetalPixelFormat.R8Unorm, 0),
                new SurfaceInput(packed, MetalPixelFormat.Rg8Unorm, 1),
            ]);
        }

        _unpackBgra ??= new SurfaceEffect(EmbeddedShader.Load("alpha_unpack_bgra.metal"), "alpha_unpack_bgra");
        return _unpackBgra.Render(packed.Width / 2, packed.Height, [new SurfaceInput(packed, MetalPixelFormat.Bgra8Unorm)]);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pack.Dispose();
        _unpackNv12?.Dispose();
        _unpackBgra?.Dispose();
    }
}
#endif
