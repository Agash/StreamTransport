using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Agash.StreamTransport.Codecs;

// Hand-defined mirror of FFmpeg's libavutil/hwcontext_drm.h structs, which FFmpeg.AutoGen 8.1.0 does not
// expose. A DRM-PRIME AVFrame carries a pointer to AVDRMFrameDescriptor in data[0]; these let us read the
// dmabuf objects (fds + modifier) and layers/planes (offset + pitch) of an exported VAAPI surface, and -
// in reverse - hand a dmabuf to FFmpeg to import as a VAAPI surface. Layout must match the C ABI exactly
// (LayoutKind.Sequential + natural alignment reproduces the C padding on 64-bit Linux); the field types are
// chosen to that end: ptrdiff_t -> nint, size_t -> nuint. Verified against a real Mesa VAAPI driver.
internal static class DrmPrime
{
    // AV_DRM_MAX_PLANES from hwcontext_drm.h.
    internal const int MaxPlanes = 4;

    // AV_PIX_FMT_DRM_PRIME / hwframe map flags (avutil). MAP_READ = read access for the consumer.
    internal const uint HwframeMapRead = 1;
    internal const uint HwframeMapWrite = 2;
}

[StructLayout(LayoutKind.Sequential)]
internal struct AVDRMPlaneDescriptor
{
    public int object_index;
    public nint offset;   // ptrdiff_t
    public nint pitch;    // ptrdiff_t
}

[InlineArray(DrmPrime.MaxPlanes)]
internal struct PlaneArray
{
    private AVDRMPlaneDescriptor _e0;
}

[StructLayout(LayoutKind.Sequential)]
internal struct AVDRMLayerDescriptor
{
    public uint format;
    public int nb_planes;
    public PlaneArray planes;
}

[StructLayout(LayoutKind.Sequential)]
internal struct AVDRMObjectDescriptor
{
    public int fd;
    public nuint size;            // size_t
    public ulong format_modifier; // uint64_t
}

[InlineArray(DrmPrime.MaxPlanes)]
internal struct ObjectArray
{
    private AVDRMObjectDescriptor _e0;
}

[InlineArray(DrmPrime.MaxPlanes)]
internal struct LayerArray
{
    private AVDRMLayerDescriptor _e0;
}

[StructLayout(LayoutKind.Sequential)]
internal struct AVDRMFrameDescriptor
{
    public int nb_objects;
    public ObjectArray objects;
    public int nb_layers;
    public LayerArray layers;
}
