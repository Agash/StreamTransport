#if HAS_SYPHON
using System.Runtime.Versioning;
using Agash.StreamTransport;
using Agash.StreamTransport.Codecs;
using Metal;

// Disambiguate from the macOS framework bindings' `IOSurface` namespace.
using SyphonSurface = Syphon.NET.IOSurface;

namespace StreamTransport.Agent;

/// <summary>
/// Converts a decoded NV12 IOSurface to BGRA on the GPU via a Metal compute kernel (<see cref="MetalSurfaceCompute"/>,
/// MS Metal bindings), so an opaque (non-alpha) decoded frame publishes with correct colour. A hardware HEVC
/// decoder (VideoToolbox) emits NV12 surfaces, so republishing one directly to Syphon would be miscoloured; this
/// is the macOS analog of the Windows <c>D3D11Nv12ToBgraConverter</c>. The colour math is the BT.709 limited->full
/// matrix shared with <c>alpha_unpack_nv12.metal</c> / the CPU <c>AlphaPacking</c> oracle. The returned surface is
/// owned by the compute and valid until the next call. macOS-only.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class SyphonNv12ToBgraConverter : IDisposable, INv12ToBgra
{
    private readonly MetalSurfaceCompute _compute = new("nv12_to_bgra");
    private bool _disposed;

    /// <summary>Convert a decoded NV12 surface frame to a BGRA surface frame for an opaque publish (zero-copy, Metal).</summary>
    public VideoFrame Nv12ToBgra(in VideoFrame nv12, long presentationTimeNs)
    {
        SyphonSurface result = Convert(new SyphonSurface(nv12.Surface));
        return VideoFrame.FromSurface(result.Handle, StreamInteropKind.Syphon, result.Width, result.Height, presentationTimeNs)
            with { PixelFormat = VideoPixelFormat.Bgra };
    }

    /// <summary>Convert a full-frame NV12 surface (Y plane 0, CbCr plane 1) to a <c>W x H</c> BGRA surface.</summary>
    public SyphonSurface Convert(SyphonSurface nv12)
    {
        nint output = _compute.Run(nv12.Width, nv12.Height,
        [
            new MetalSurfaceCompute.Input(nv12.Handle, MTLPixelFormat.R8Unorm, 0, nv12.Width, nv12.Height),
            new MetalSurfaceCompute.Input(nv12.Handle, MTLPixelFormat.RG8Unorm, 1, nv12.Width / 2, nv12.Height / 2),
        ]);
        return new SyphonSurface(output);
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
