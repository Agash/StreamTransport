using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace Agash.StreamTransport.Codecs;

/// <summary>
/// Owns a single process-wide FFmpeg Vulkan hardware device (<c>AV_HWDEVICE_TYPE_VULKAN</c>) and exposes the
/// underlying Vulkan handles it created. We let FFmpeg create the Vulkan device rather than wrapping our own:
/// FFmpeg enables exactly the instance/device extensions and features its decode + dmabuf-interop paths need,
/// which is fragile to reproduce by hand. The decoder decodes into this device's frames pool (producing
/// <c>AVVkFrame</c>s), and the Vulkan compute side (alpha pack/unpack) borrows <see cref="Instance"/> /
/// <see cref="PhysicalDevice"/> / <see cref="Device"/> to run shaders on those images - the mpv model
/// (<c>hwdec_vulkan.c</c>), inverted: FFmpeg owns the device, our compute borrows it.
/// </summary>
/// <remarks>Compiles everywhere (FFmpeg.AutoGen); only functional on a host with a Vulkan driver.</remarks>
internal static unsafe class VulkanDevice
{
    private static readonly object s_gate = new();
    private static AVBufferRef* s_device;
    private static bool s_attempted;

    /// <summary>A new counted reference to the shared Vulkan device, creating it on first use. Throws when none.</summary>
    public static AVBufferRef* AcquireRef()
    {
        lock (s_gate)
        {
            EnsureCreated();
            if (s_device is null)
            {
                throw new NotSupportedException("No usable Vulkan device is available on this machine.");
            }

            return ffmpeg.av_buffer_ref(s_device);
        }
    }

    /// <summary>Whether an FFmpeg Vulkan device can be created here (cached).</summary>
    public static bool IsAvailable()
    {
        lock (s_gate)
        {
            EnsureCreated();
            return s_device is not null;
        }
    }

    /// <summary>The <c>VkInstance</c> FFmpeg created (0 until <see cref="IsAvailable"/> is true).</summary>
    public static nint Instance { get { lock (s_gate) { EnsureCreated(); return Head(out var h) ? h->inst : 0; } } }

    /// <summary>The <c>VkPhysicalDevice</c> FFmpeg selected.</summary>
    public static nint PhysicalDevice { get { lock (s_gate) { EnsureCreated(); return Head(out var h) ? h->phys_dev : 0; } } }

    /// <summary>The <c>VkDevice</c> FFmpeg created. Borrow it to run compute on the decoder's images.</summary>
    public static nint Device { get { lock (s_gate) { EnsureCreated(); return Head(out var h) ? h->act_dev : 0; } } }

    private static void EnsureCreated()
    {
        if (s_attempted)
        {
            return;
        }

        s_attempted = true;
        FfmpegLog.InstallIfRequested();

        AVBufferRef* device = null;
        int created = ffmpeg.av_hwdevice_ctx_create(&device, AVHWDeviceType.AV_HWDEVICE_TYPE_VULKAN, null, null, 0);
        if (created < 0 || device is null)
        {
            if (device is not null) ffmpeg.av_buffer_unref(&device);
            return;
        }

        s_device = device; // never unref'd: kept for the whole process (single shared device).
    }

    // The leading fields of the device's AVVulkanDeviceContext (the Vulkan handles FFmpeg created). Only those
    // are read, so we never depend on the exact size of the embedded VkPhysicalDeviceFeatures2 that follows.
    private static bool Head(out AVVulkanDeviceContextHead* head)
    {
        head = s_device is null ? null : (AVVulkanDeviceContextHead*)((AVHWDeviceContext*)s_device->data)->hwctx;
        return head is not null;
    }

    // Leading fields of libavutil's AVVulkanDeviceContext (hwcontext_vulkan.h). All pointer-sized on x64;
    // reading only these avoids needing the full struct (which embeds a large VkPhysicalDeviceFeatures2).
    [StructLayout(LayoutKind.Sequential)]
    private struct AVVulkanDeviceContextHead
    {
        public nint alloc;          // const VkAllocationCallbacks*
        public nint get_proc_addr;  // PFN_vkGetInstanceProcAddr
        public nint inst;           // VkInstance
        public nint phys_dev;       // VkPhysicalDevice
        public nint act_dev;        // VkDevice
    }
}
