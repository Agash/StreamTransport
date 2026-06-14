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
}
