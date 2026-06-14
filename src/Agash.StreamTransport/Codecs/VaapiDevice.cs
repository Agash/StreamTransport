using System.IO;
using System.Linq;
using FFmpeg.AutoGen;

namespace Agash.StreamTransport.Codecs;

/// <summary>
/// Owns a single process-wide VAAPI hardware device (one <c>VADisplay</c>) shared by every
/// <see cref="VaapiVideoEncoder"/> and <see cref="VaapiVideoDecoder"/>. Each consumer takes a counted
/// reference (<see cref="AcquireRef"/>) and releases it on dispose; the underlying device itself is created
/// once and intentionally never destroyed.
///
/// <para>Creating and destroying multiple independent VAAPI devices in one process is unstable on some
/// drivers (the Mesa <c>radeonsi</c> VA backend segfaults during teardown when several <c>VADisplay</c>
/// instances are opened and closed over a process's lifetime). Sharing one device that lives for the whole
/// process sidesteps that, and is also cheaper - opening a <c>VADisplay</c> is not free. The single base
/// reference is deliberately leaked at process exit rather than risking the driver's teardown path.</para>
/// </summary>
internal static unsafe class VaapiDevice
{
    private static readonly object s_gate = new();
    private static AVBufferRef* s_device;
    private static bool s_attempted;

    /// <summary>
    /// A new counted reference to the shared VAAPI device, creating it on first use. The caller owns the
    /// returned reference and must <c>av_buffer_unref</c> it on dispose. Throws when no VAAPI device exists.
    /// </summary>
    public static AVBufferRef* AcquireRef(string? renderNode = null)
    {
        lock (s_gate)
        {
            EnsureCreated(renderNode);
            if (s_device is null)
            {
                throw new NotSupportedException("No usable VAAPI device is available on this machine.");
            }

            return ffmpeg.av_buffer_ref(s_device);
        }
    }

    /// <summary>Whether a VAAPI device can be opened here (cached). Creating it keeps it for later reuse.</summary>
    public static bool IsAvailable(string? renderNode = null)
    {
        lock (s_gate)
        {
            EnsureCreated(renderNode);
            return s_device is not null;
        }
    }

    private static void EnsureCreated(string? renderNode)
    {
        if (s_attempted)
        {
            return;
        }

        s_attempted = true;

        // Only touch VAAPI when a real GPU (DRM render node) AND the real libva-drm library are both present.
        // FFmpeg's bundled libva *implib stub* loads by name even when the real library is absent and then
        // assert()/aborts the whole process on first use - so loading by name cannot tell them apart; we must
        // confirm the actual libva-drm.so.2 file exists at a system path before letting FFmpeg create a VAAPI
        // device. A headless CI runner has the render node but no libva-drm file, which previously aborted.
        if (!HasDrmRenderNode() || !RealLibVaDrmPresent())
        {
            return;
        }

        AVBufferRef* device = null;
        // Default to the first render node; a multi-GPU box can pass e.g. /dev/dri/renderD129. The node is
        // fixed by the first caller for the process (single shared device).
        int created = ffmpeg.av_hwdevice_ctx_create(&device, AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI, renderNode, null, 0);
        if (created >= 0 && device is not null)
        {
            s_device = device; // never unref'd: kept for the whole process (see class remarks).
        }
        else if (device is not null)
        {
            ffmpeg.av_buffer_unref(&device);
        }
    }

    // True when a DRM render node exists (a GPU is present).
    private static bool HasDrmRenderNode()
    {
        try
        {
            return Directory.Exists("/dev/dri")
                && Directory.EnumerateFileSystemEntries("/dev/dri", "renderD*").Any();
        }
        catch (Exception)
        {
            return false;
        }
    }

    // Standard install locations of the real libva DRM backend across the distros we target (Arch, Debian/
    // Ubuntu multiarch, Fedora, arm64). We check the file directly rather than loading by name so FFmpeg's
    // bundled implib stub (which loads by name regardless) is never mistaken for the real library.
    private static readonly string[] s_libVaDrmPaths =
    [
        "/usr/lib/libva-drm.so.2",
        "/usr/lib64/libva-drm.so.2",
        "/usr/lib/x86_64-linux-gnu/libva-drm.so.2",
        "/lib/x86_64-linux-gnu/libva-drm.so.2",
        "/usr/lib/aarch64-linux-gnu/libva-drm.so.2",
    ];

    private static bool RealLibVaDrmPresent() => s_libVaDrmPaths.Any(File.Exists);
}
