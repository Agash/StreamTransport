using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace Agash.StreamTransport.Codecs;

/// <summary>
/// A compute context that runs on the <see cref="VulkanDevice"/> FFmpeg created (the mpv model: FFmpeg owns
/// the device, our compute borrows it). It wraps FFmpeg's <c>VkInstance</c>/<c>VkPhysicalDevice</c>/<c>VkDevice</c>
/// with Vortice, finds a compute queue family, and owns only what it creates (queue handle + command pool) -
/// never the device, which belongs to FFmpeg. Used to run the Linux alpha pack/unpack compute over the
/// decoder's Vulkan images. See <c>docs/notes/linux-gpu-zerocopy-plan.md</c>.
/// </summary>
internal sealed unsafe class VulkanComputeContext : IDisposable
{
    private readonly VkInstanceApi _instanceApi;
    private readonly VkQueue _queue;
    private readonly VkCommandPool _commandPool;
    private bool _disposed;

    /// <summary>Try to build a compute context on FFmpeg's Vulkan device. Returns false when unavailable.</summary>
    public static bool TryCreate(out VulkanComputeContext? context)
    {
        try
        {
            context = new VulkanComputeContext();
            return true;
        }
        catch (Exception)
        {
            context = null;
            return false;
        }
    }

    public VulkanComputeContext()
    {
        if (!VulkanDevice.IsAvailable())
        {
            throw new NotSupportedException("No FFmpeg Vulkan device is available.");
        }

        vkInitialize().CheckResult();

        var instance = new VkInstance(VulkanDevice.Instance);
        var physicalDevice = new VkPhysicalDevice(VulkanDevice.PhysicalDevice);
        Device = new VkDevice(VulkanDevice.Device);

        _instanceApi = GetApi(instance);
        Api = GetApi(instance, Device);

        ComputeQueueFamily = FindComputeQueueFamily(physicalDevice);
        Api.vkGetDeviceQueue(ComputeQueueFamily, 0, out _queue);

        VkCommandPoolCreateInfo poolInfo = new()
        {
            flags = VkCommandPoolCreateFlags.ResetCommandBuffer,
            queueFamilyIndex = ComputeQueueFamily,
        };
        Api.vkCreateCommandPool(&poolInfo, null, out _commandPool).CheckResult();
    }

    /// <summary>The borrowed FFmpeg <c>VkDevice</c>. Not owned - never destroyed here.</summary>
    public VkDevice Device { get; }

    /// <summary>Device-level Vulkan function table for the borrowed device.</summary>
    public VkDeviceApi Api { get; }

    /// <summary>The compute queue family index used.</summary>
    public uint ComputeQueueFamily { get; }

    private uint FindComputeQueueFamily(VkPhysicalDevice physicalDevice)
    {
        _instanceApi.vkGetPhysicalDeviceQueueFamilyProperties(physicalDevice, out uint count);
        Span<VkQueueFamilyProperties> families = stackalloc VkQueueFamilyProperties[(int)count];
        _instanceApi.vkGetPhysicalDeviceQueueFamilyProperties(physicalDevice, families);

        // The first compute-capable family. On desktop GPUs this is the universal graphics+compute family that
        // FFmpeg always creates a queue on, so vkGetDeviceQueue(family, 0) is valid.
        for (uint i = 0; i < count; i++)
        {
            if ((families[(int)i].queueFlags & VkQueueFlags.Compute) != 0)
            {
                return i;
            }
        }

        throw new NotSupportedException("FFmpeg's Vulkan device has no compute queue family.");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Destroy only what we created. The instance/physical/logical device are owned by FFmpeg (VulkanDevice)
        // and intentionally left alone.
        if (Device.IsNotNull && _commandPool.IsNotNull)
        {
            Api.vkDestroyCommandPool(_commandPool, null);
        }
    }
}
