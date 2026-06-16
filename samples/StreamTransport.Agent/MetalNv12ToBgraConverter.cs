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
/// Converts a decoded NV12 IOSurface to BGRA on the GPU via a Metal compute kernel (<see cref="MetalSurfaceCompute"/>,
/// MS Metal bindings), so an opaque (non-alpha) decoded frame publishes with correct colour. A hardware HEVC
/// decoder (VideoToolbox) emits NV12 surfaces, so republishing one directly to Syphon would be miscoloured; this
/// is the macOS analog of the Windows <c>D3D11Nv12ToBgraConverter</c>. The colour math is the BT.709 limited->full
/// matrix shared with <c>alpha_unpack_nv12.metal</c> / the CPU <c>AlphaPacking</c> oracle. Surfaces are the
/// Microsoft <see cref="IOSurface"/> bindings directly; the returned surface is owned by the compute and valid
/// until the next call. macOS-only.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class MetalNv12ToBgraConverter : IDisposable, INv12ToBgra
{
    private readonly MetalSurfaceCompute _compute = new("nv12_to_bgra");
    private bool _disposed;

    private static IOSurface.IOSurface Wrap(nint handle) =>
        Runtime.GetINativeObject<IOSurface.IOSurface>(handle, owns: false)!;

    /// <summary>Convert a decoded NV12 surface frame to a BGRA surface frame for an opaque publish (zero-copy, Metal).</summary>
    public VideoFrame Nv12ToBgra(in VideoFrame nv12, long presentationTimeNs)
    {
        IOSurface.IOSurface result = Convert(Wrap(nv12.Surface));
        (int rw, int rh) = result.PixelSize();
        return VideoFrame.FromSurface(result.Handle.Handle, StreamInteropKind.Syphon, rw, rh, presentationTimeNs)
            with { PixelFormat = VideoPixelFormat.Bgra };
    }

    /// <summary>Convert a full-frame NV12 surface (Y plane 0, CbCr plane 1) to a <c>W x H</c> BGRA surface.</summary>
    public IOSurface.IOSurface Convert(IOSurface.IOSurface nv12)
    {
        (int w, int h) = nv12.PixelSize();
        (int cw, int ch, _) = nv12.PlaneInfo(1); // CbCr plane dims straight from the surface
        nint output = _compute.Run(w, h,
        [
            new MetalSurfaceCompute.Input(nv12.Handle.Handle, MTLPixelFormat.R8Unorm, 0, w, h),
            new MetalSurfaceCompute.Input(nv12.Handle.Handle, MTLPixelFormat.RG8Unorm, 1, cw, ch),
        ]);
        return Wrap(output);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _compute.Dispose();
    }
}
#endif
