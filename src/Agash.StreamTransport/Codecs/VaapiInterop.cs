using System.Runtime.InteropServices;

namespace Agash.StreamTransport.Codecs;

/// <summary>
/// Minimal libva P/Invoke for the operations FFmpeg does not surface: a GPU surface-to-surface copy
/// (<c>vaCopy</c>) and surface sync. Used by the zero-copy republish to copy a decoded VAAPI surface into a
/// presentation surface we own and export to PipeWire - no CPU readback. libva is the same shared library the
/// VAAPI device already loaded (see <see cref="VaapiDevice"/>); calls target its <c>VADisplay</c>.
/// </summary>
/// <remarks>Linux/VAAPI only; <c>vaCopy</c> requires libva ≥ 1.12 and driver support (probe before relying on it).</remarks>
internal static unsafe class VaapiInterop
{
    private const string LibVa = "libva.so.2";

    /// <summary>The kind of object a <see cref="VACopyObject"/> refers to (va.h <c>VACopyObjectType</c>).</summary>
    public enum VACopyObjectType
    {
        Surface = 0,
        Buffer = 1,
    }

    /// <summary>
    /// va.h <c>VACopyObject</c>: a tagged handle (24 bytes) - object type, then a union of
    /// <c>VASurfaceID</c>/<c>VABufferID</c> (both 32-bit), then 16 reserved bytes.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct VACopyObject
    {
        public int ObjType;     // VACopyObjectType
        public uint Id;         // union { VASurfaceID surface_id; VABufferID buffer_id; }
        public uint Reserved0;
        public uint Reserved1;
        public uint Reserved2;
        public uint Reserved3;

        public static VACopyObject Surface(uint surfaceId) =>
            new() { ObjType = (int)VACopyObjectType.Surface, Id = surfaceId };
    }

    /// <summary>
    /// GPU copy of one VA object to another (<c>VAStatus vaCopy(VADisplay, VACopyObject* dst, VACopyObject* src,
    /// VACopyOption)</c>). <paramref name="option"/> packs the sync/mode bits; 0 = default (sync separately).
    /// Returns <c>VA_STATUS_SUCCESS</c> (0) on success.
    /// </summary>
    [DllImport(LibVa, EntryPoint = "vaCopy")]
    public static extern int vaCopy(nint display, VACopyObject* dst, VACopyObject* src, uint option);

    /// <summary>Blocks until all pending operations on <paramref name="surface"/> complete (<c>vaSyncSurface</c>).</summary>
    [DllImport(LibVa, EntryPoint = "vaSyncSurface")]
    public static extern int vaSyncSurface(nint display, uint surface);

    // - VA-API VPP (video post-processing): a GPU surface->surface copy that lets us target a specific dst
    //   surface (one we own and exported to PipeWire), unlike scale_vaapi which owns its output pool. radeonsi
    //   does not implement vaCopy, but it implements the VideoProc entrypoint, so this is the GPU copy path. -

    /// <summary>va.h <c>VAProfileNone</c> (no codec profile - for the VPP entrypoint).</summary>
    public const int VAProfileNone = -1;

    /// <summary>va.h <c>VAEntrypointVideoProc</c> (the video post-processing entrypoint).</summary>
    public const int VAEntrypointVideoProc = 10;

    /// <summary>va.h <c>VAProcPipelineParameterBufferType</c>.</summary>
    public const int VAProcPipelineParameterBufferType = 41;

    /// <summary>va.h <c>VA_PROGRESSIVE</c> picture-structure flag for the context.</summary>
    public const int VA_PROGRESSIVE = 1;

    /// <summary>
    /// Total byte size of <c>VAProcPipelineParameterBuffer</c> (va_vpp.h, measured on the target ABI). For a
    /// plain whole-surface copy only the leading <c>VASurfaceID surface</c> (the source, at offset 0) is set;
    /// every other field is left zero/NULL, which the driver treats as "whole surface, default colour".
    /// </summary>
    public const int ProcPipelineParameterBufferSize = 224;

    [DllImport(LibVa, EntryPoint = "vaCreateConfig")]
    public static extern int vaCreateConfig(nint display, int profile, int entrypoint, void* attribs, int numAttribs, out uint configId);

    [DllImport(LibVa, EntryPoint = "vaDestroyConfig")]
    public static extern int vaDestroyConfig(nint display, uint configId);

    [DllImport(LibVa, EntryPoint = "vaCreateContext")]
    public static extern int vaCreateContext(nint display, uint configId, int pictureWidth, int pictureHeight, int flag, uint* renderTargets, int numRenderTargets, out uint contextId);

    [DllImport(LibVa, EntryPoint = "vaDestroyContext")]
    public static extern int vaDestroyContext(nint display, uint contextId);

    [DllImport(LibVa, EntryPoint = "vaCreateBuffer")]
    public static extern int vaCreateBuffer(nint display, uint contextId, int type, uint size, uint numElements, void* data, out uint bufferId);

    [DllImport(LibVa, EntryPoint = "vaDestroyBuffer")]
    public static extern int vaDestroyBuffer(nint display, uint bufferId);

    [DllImport(LibVa, EntryPoint = "vaBeginPicture")]
    public static extern int vaBeginPicture(nint display, uint contextId, uint renderTarget);

    [DllImport(LibVa, EntryPoint = "vaRenderPicture")]
    public static extern int vaRenderPicture(nint display, uint contextId, uint* buffers, int numBuffers);

    [DllImport(LibVa, EntryPoint = "vaEndPicture")]
    public static extern int vaEndPicture(nint display, uint contextId);
}
