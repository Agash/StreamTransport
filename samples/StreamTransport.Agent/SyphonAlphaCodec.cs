#if HAS_SYPHON
using System.Runtime.Versioning;
using Agash.StreamTransport;
using Agash.StreamTransport.Codecs;
using Metal;

// Disambiguate from the macOS framework bindings' `IOSurface` namespace.
using SyphonSurface = Syphon.NET.IOSurface;

namespace StreamTransport.Agent;

/// <summary>
/// macOS GPU side-by-side alpha pack/unpack for the Syphon zero-copy path, run as Metal compute kernels driven
/// directly through the Microsoft Metal bindings (<see cref="MetalSurfaceCompute"/>) - no native shim. The
/// transport-specific shaders (the 2W x H colour|alpha layout, BT.709 limited constants, 16..235 alpha) are the
/// embedded <c>.metal</c> kernels, precompiled into the app's default.metallib. Each call's result is a surface
/// owned by the underlying compute (valid until the next call of the same direction) - encode/publish it before
/// packing/unpacking the next frame. macOS-only.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class SyphonAlphaCodec : IDisposable, IAlphaPacker, IAlphaUnpacker
{
    // NV12 biplanar pixel formats a hardware HEVC decoder emits (VideoToolbox); both have an R8
    // luma plane (0) and an RG8 CbCr plane (1).
    private const uint Nv12VideoRange = 0x34323076; // '420v'
    private const uint Nv12FullRange = 0x34323066;  // '420f'

    private readonly MetalSurfaceCompute _pack = new("alpha_pack");
    private MetalSurfaceCompute? _unpackNv12;
    private MetalSurfaceCompute? _unpackBgra;
    private bool _disposed;

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

    // The SyphonSurface-typed Pack/Unpack below are the surface-native primitive (rich IOSurface in/out); the
    // VideoFrame PackAlpha/UnpackAlpha above are the production IAlphaPacker/IAlphaUnpacker wrappers over them.
    // SyphonSelfTest drives the primitive directly (layout verification, Handle, Width/Height).

    /// <summary>Pack a W x H BGRA surface into a 2W x H BGRA surface (left = colour, right = alpha-as-luma).</summary>
    public SyphonSurface Pack(SyphonSurface bgra)
    {
        nint output = _pack.Run(bgra.Width * 2, bgra.Height,
        [
            new MetalSurfaceCompute.Input(bgra.Handle, MTLPixelFormat.BGRA8Unorm, 0, bgra.Width, bgra.Height),
        ]);
        return new SyphonSurface(output);
    }

    /// <summary>Unpack a decoded 2W x H surface (NV12 from the hardware decoder, or BGRA) into W x H BGRA with alpha.</summary>
    public SyphonSurface Unpack(SyphonSurface packed)
    {
        int outW = packed.Width / 2;
        int outH = packed.Height;
        uint fourcc = (uint)packed.PixelFormat;
        if (fourcc == Nv12VideoRange || fourcc == Nv12FullRange)
        {
            _unpackNv12 ??= new MetalSurfaceCompute("alpha_unpack_nv12");
            nint output = _unpackNv12.Run(outW, outH,
            [
                new MetalSurfaceCompute.Input(packed.Handle, MTLPixelFormat.R8Unorm, 0, packed.Width, packed.Height),
                new MetalSurfaceCompute.Input(packed.Handle, MTLPixelFormat.RG8Unorm, 1, packed.Width / 2, packed.Height / 2),
            ]);
            return new SyphonSurface(output);
        }

        _unpackBgra ??= new MetalSurfaceCompute("alpha_unpack_bgra");
        nint bgraOut = _unpackBgra.Run(outW, outH,
        [
            new MetalSurfaceCompute.Input(packed.Handle, MTLPixelFormat.BGRA8Unorm, 0, packed.Width, packed.Height),
        ]);
        return new SyphonSurface(bgraOut);
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
