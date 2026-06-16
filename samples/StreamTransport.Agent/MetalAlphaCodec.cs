#if HAS_SYPHON
using System.Runtime.Versioning;
using Agash.StreamTransport;
using Agash.StreamTransport.Codecs;
using IOSurface;
using Metal;
using ObjCRuntime;
using Syphon.NET;

namespace StreamTransport.Agent;

/// <summary>
/// macOS GPU side-by-side alpha pack/unpack, run as Metal compute kernels driven directly through the Microsoft
/// Metal bindings (<see cref="MetalSurfaceCompute"/>) - no native shim. Surfaces are the Microsoft
/// <see cref="IOSurface"/> bindings directly. The transport-specific shaders (the 2W x H colour|alpha layout,
/// BT.709 limited constants, 16..235 alpha) are the embedded <c>.metal</c> kernels, precompiled into the app's
/// default.metallib. Each call's result is a surface owned by the underlying compute (valid until the next call
/// of the same direction) - encode/publish it before packing/unpacking the next frame. macOS-only.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class MetalAlphaCodec : IDisposable, IAlphaPacker, IAlphaUnpacker
{
    private readonly MetalSurfaceCompute _pack = new("alpha_pack");
    private MetalSurfaceCompute? _unpackNv12;
    private MetalSurfaceCompute? _unpackBgra;
    private bool _disposed;

    private static IOSurface.IOSurface Wrap(nint handle) =>
        Runtime.GetINativeObject<IOSurface.IOSurface>(handle, owns: false)!;

    /// <summary>Pack a W x H BGRA surface frame into a 2W x H colour|alpha surface frame (zero-copy, Metal).</summary>
    public VideoFrame PackAlpha(in VideoFrame colourBgra, long presentationTimeNs)
    {
        IOSurface.IOSurface packed = Pack(Wrap(colourBgra.Surface));
        (int pw, int ph) = packed.PixelSize();
        return VideoFrame.FromSurface(packed.Handle.Handle, StreamInteropKind.Syphon, pw, ph, presentationTimeNs)
            with { PixelFormat = VideoPixelFormat.Bgra };
    }

    /// <summary>Split a decoded 2W x H colour|alpha surface frame back into a W x H BGRA surface frame (zero-copy, Metal).</summary>
    public VideoFrame UnpackAlpha(in VideoFrame packed, long presentationTimeNs)
    {
        IOSurface.IOSurface result = Unpack(Wrap(packed.Surface));
        (int rw, int rh) = result.PixelSize();
        return VideoFrame.FromSurface(result.Handle.Handle, StreamInteropKind.Syphon, rw, rh, presentationTimeNs)
            with { PixelFormat = VideoPixelFormat.Bgra };
    }

    // The IOSurface-typed Pack/Unpack below are the surface-native primitive; the VideoFrame PackAlpha/UnpackAlpha
    // above are the production IAlphaPacker/IAlphaUnpacker wrappers over them. SyphonSelfTest drives the primitive
    // directly (layout verification, Handle, Width/Height).

    /// <summary>Pack a W x H BGRA surface into a 2W x H BGRA surface (left = colour, right = alpha-as-luma).</summary>
    public IOSurface.IOSurface Pack(IOSurface.IOSurface bgra)
    {
        (int w, int h) = bgra.PixelSize();
        nint output = _pack.Run(w * 2, h,
        [
            new MetalSurfaceCompute.Input(bgra.Handle.Handle, MTLPixelFormat.BGRA8Unorm, 0, w, h),
        ]);
        return Wrap(output);
    }

    /// <summary>Unpack a decoded 2W x H surface (NV12 from the hardware decoder, or BGRA) into W x H BGRA with alpha.</summary>
    public IOSurface.IOSurface Unpack(IOSurface.IOSurface packed)
    {
        (int packedW, int packedH) = packed.PixelSize();
        int outW = packedW / 2;
        if (packed.IsNv12())
        {
            (int cw, int ch, _) = packed.PlaneInfo(1); // CbCr plane dims straight from the surface
            _unpackNv12 ??= new MetalSurfaceCompute("alpha_unpack_nv12");
            nint output = _unpackNv12.Run(outW, packedH,
            [
                new MetalSurfaceCompute.Input(packed.Handle.Handle, MTLPixelFormat.R8Unorm, 0, packedW, packedH),
                new MetalSurfaceCompute.Input(packed.Handle.Handle, MTLPixelFormat.RG8Unorm, 1, cw, ch),
            ]);
            return Wrap(output);
        }

        _unpackBgra ??= new MetalSurfaceCompute("alpha_unpack_bgra");
        nint bgraOut = _unpackBgra.Run(outW, packedH,
        [
            new MetalSurfaceCompute.Input(packed.Handle.Handle, MTLPixelFormat.BGRA8Unorm, 0, packedW, packedH),
        ]);
        return Wrap(bgraOut);
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
