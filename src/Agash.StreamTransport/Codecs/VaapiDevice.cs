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

        // No DRM render node means no GPU here (e.g. a headless CI runner). Creating a VAAPI device in that
        // case makes FFmpeg call into its bundled libva implib stub, which assert()/aborts the whole process
        // when the real libva-drm is absent - so gate on a render node existing before any VAAPI codepath.
        // (Loading the library by name is not enough to tell: the implib stub loads fine and only aborts on
        // first use.) A machine with a render node has a real VAAPI driver + libva, as verified on hardware.
        if (!HasDrmRenderNode())
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

    // True when a DRM render node exists (a GPU is present). A headless CI runner has none, so VAAPI - and
    // FFmpeg's abort-on-missing-libva implib stub - is never touched there.
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
}
