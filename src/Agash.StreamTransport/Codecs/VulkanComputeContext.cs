using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace Agash.StreamTransport.Codecs;

/// <summary>
/// A Vulkan compute context - a device + compute queue + command pool, plus the compute helpers
/// (<see cref="CreateShaderModule"/>, <see cref="SubmitOneShot"/>, <see cref="FindMemoryType"/>,
/// <see cref="ExportableModifiers"/>) the GPU alpha pack/unpack path runs on. It comes in two construction
/// modes that share one surface:
/// <list type="bullet">
///   <item><b>Borrowing</b> (<see cref="TryCreate"/> / the parameterless constructor): wraps the
///   <see cref="VulkanDevice"/> FFmpeg already created (the mpv model - FFmpeg owns the device, our compute
///   borrows it), for running compute over the decoder's own Vulkan images. Owns only the queue handle +
///   command pool; never destroys the device.</item>
///   <item><b>Standalone</b> (<see cref="CreateStandalone"/>): creates its own instance + logical device with
///   the dmabuf external-memory extensions and no swapchain/surface, for the VAAPI/PipeWire path where frames
///   arrive and leave as DMA-BUF file descriptors (zero-copy). Owns and destroys everything it created.</item>
/// </list>
/// The dmabuf-specific surface (<see cref="ExportableModifiers"/> and dmabuf import/export in
/// <see cref="VulkanAlphaCodec"/>) only works on a standalone context, whose device enabled those extensions.
/// See <c>docs/notes/linux-gpu-zerocopy-plan.md</c>. The borrowing mode is platform-neutral (FFmpeg Vulkan
/// hwaccel exists on Windows and Linux); the standalone dmabuf surface is exercised only on Linux.
/// </summary>
public sealed unsafe class VulkanComputeContext : IDisposable
{
    /// <summary>DRM_FORMAT_MOD_LINEAR - the single-plane, untiled modifier assumed when none is negotiated (the avatar case).</summary>
    // HW-VERIFY: tiled producers expose a vendor modifier that must be read from PipeWire and matched here via
    // VK_EXT_image_drm_format_modifier; PipeWire.NET does not surface the modifier list yet.
    public const ulong DrmFormatModLinear = 0UL;

    private static readonly string[] s_dmaBufDeviceExtensions =
    [
        "VK_KHR_external_memory_fd",
        "VK_EXT_external_memory_dma_buf",
        "VK_EXT_image_drm_format_modifier",
        "VK_EXT_queue_family_foreign",
    ];

    private readonly bool _ownsDevice;
    private readonly VkInstance _instance;
    private readonly VkInstanceApi _instanceApi;
    private readonly VkQueue _queue;
    private readonly VkCommandPool _commandPool;
    private bool _disposed;

    /// <summary>Try to build a <b>borrowing</b> compute context on FFmpeg's Vulkan device. Returns false when unavailable.</summary>
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

    /// <summary>Build a <b>borrowing</b> compute context wrapping the FFmpeg-owned <see cref="VulkanDevice"/>.</summary>
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

        _ownsDevice = false;
        _instance = instance; // borrowed - never destroyed.
        PhysicalDevice = physicalDevice;
        _instanceApi = GetApi(instance);
        Api = GetApi(instance, Device);

        ComputeQueueFamily = FindBorrowedComputeQueueFamily(physicalDevice);
        Api.vkGetDeviceQueue(ComputeQueueFamily, 0, out _queue);

        VkCommandPoolCreateInfo poolInfo = new()
        {
            flags = VkCommandPoolCreateFlags.ResetCommandBuffer,
            queueFamilyIndex = ComputeQueueFamily,
        };
        Api.vkCreateCommandPool(&poolInfo, null, out _commandPool).CheckResult();
    }

    /// <summary>
    /// Build a <b>standalone</b> compute context: its own headless instance + logical device with the dmabuf
    /// external-memory extensions, for the VAAPI/PipeWire DMA-BUF path. The context owns and destroys it all.
    /// </summary>
    public static VulkanComputeContext CreateStandalone() => new(standalone: true);

    private VulkanComputeContext(bool standalone)
    {
        _ = standalone;
        _ownsDevice = true;

        vkInitialize().CheckResult();

        VkApplicationInfo appInfo = new() { apiVersion = VkVersion.Version_1_1 };
        VkInstanceCreateInfo instanceInfo = new() { pApplicationInfo = &appInfo };
        vkCreateInstance(&instanceInfo, out _instance).CheckResult();
        _instanceApi = GetApi(_instance);

        VkPhysicalDevice physicalDevice = PickDmaBufPhysicalDevice(out uint computeQueueFamily);
        PhysicalDevice = physicalDevice;
        ComputeQueueFamily = computeQueueFamily;

        float priority = 1.0f;
        VkDeviceQueueCreateInfo queueInfo = new()
        {
            queueFamilyIndex = computeQueueFamily,
            queueCount = 1,
            pQueuePriorities = &priority,
        };

        using var extensions = new VkStringArray(s_dmaBufDeviceExtensions);
        VkDeviceCreateInfo deviceInfo = new()
        {
            queueCreateInfoCount = 1,
            pQueueCreateInfos = &queueInfo,
            enabledExtensionCount = extensions.Length,
            ppEnabledExtensionNames = extensions,
        };
        _instanceApi.vkCreateDevice(physicalDevice, &deviceInfo, null, out VkDevice device).CheckResult();
        Device = device;
        Api = GetApi(_instance, device);

        Api.vkGetDeviceQueue(computeQueueFamily, 0, out _queue);

        VkCommandPoolCreateInfo poolInfo = new()
        {
            flags = VkCommandPoolCreateFlags.ResetCommandBuffer,
            queueFamilyIndex = computeQueueFamily,
        };
        Api.vkCreateCommandPool(&poolInfo, null, out _commandPool).CheckResult();
    }

    /// <summary>The Vulkan <c>VkDevice</c> compute runs on (borrowed from FFmpeg, or this context's own).</summary>
    public VkDevice Device { get; }

    /// <summary>Device-level Vulkan function table for <see cref="Device"/>.</summary>
    public VkDeviceApi Api { get; }

    /// <summary>The physical device backing <see cref="Device"/> (for memory-property and format queries).</summary>
    public VkPhysicalDevice PhysicalDevice { get; }

    /// <summary>The compute queue family index used.</summary>
    public uint ComputeQueueFamily { get; }

    private uint FindBorrowedComputeQueueFamily(VkPhysicalDevice physicalDevice)
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

    private VkPhysicalDevice PickDmaBufPhysicalDevice(out uint computeQueueFamily)
    {
        _instanceApi.vkEnumeratePhysicalDevices(out uint count).CheckResult();
        if (count == 0)
        {
            throw new InvalidOperationException("No Vulkan physical devices found.");
        }

        Span<VkPhysicalDevice> devices = stackalloc VkPhysicalDevice[(int)count];
        _instanceApi.vkEnumeratePhysicalDevices(devices).CheckResult();

        foreach (VkPhysicalDevice candidate in devices)
        {
            if (!HasRequiredExtensions(candidate))
            {
                continue;
            }

            if (TryFindComputeQueueFamily(candidate, out computeQueueFamily))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            "No Vulkan device supports compute + the dmabuf external-memory extensions " +
            $"({string.Join(", ", s_dmaBufDeviceExtensions)}).");
    }

    private bool HasRequiredExtensions(VkPhysicalDevice device)
    {
        _instanceApi.vkEnumerateDeviceExtensionProperties(device, out uint count).CheckResult();
        Span<VkExtensionProperties> props = stackalloc VkExtensionProperties[(int)count];
        _instanceApi.vkEnumerateDeviceExtensionProperties(device, props).CheckResult();

        var available = new HashSet<string>(StringComparer.Ordinal);
        foreach (ref readonly VkExtensionProperties p in props)
        {
            fixed (VkExtensionProperties* pp = &p)
            {
                available.Add(new string((sbyte*)pp->extensionName));
            }
        }

        foreach (string required in s_dmaBufDeviceExtensions)
        {
            if (!available.Contains(required))
            {
                return false;
            }
        }

        return true;
    }

    private bool TryFindComputeQueueFamily(VkPhysicalDevice device, out uint family)
    {
        _instanceApi.vkGetPhysicalDeviceQueueFamilyProperties(device, out uint count);
        Span<VkQueueFamilyProperties> families = stackalloc VkQueueFamilyProperties[(int)count];
        _instanceApi.vkGetPhysicalDeviceQueueFamilyProperties(device, families);

        for (uint i = 0; i < count; i++)
        {
            if ((families[(int)i].queueFlags & VkQueueFlags.Compute) != 0)
            {
                family = i;
                return true;
            }
        }

        family = 0;
        return false;
    }

    /// <summary>
    /// Create a shader module from an embedded precompiled SPIR-V blob. The GLSL <c>.comp</c> sources are
    /// compiled to <c>.spv</c> at build time (glslc), not at runtime - loading a runtime GLSL-&gt;SPIR-V
    /// compiler (shaderc) into the same process as the Mesa VAAPI driver crashes, because both export
    /// SPIRV-Tools symbols that the dynamic linker interposes (see the build target in the csproj).
    /// </summary>
    public VkShaderModule CreateShaderModule(string logicalName)
    {
        byte[] spirv = EmbeddedShader.LoadBytes(logicalName);
        if (spirv.Length < 20 || spirv.Length % 4 != 0)
        {
            throw new InvalidOperationException($"Embedded SPIR-V '{logicalName}' is not a valid module ({spirv.Length} bytes).");
        }

        fixed (byte* code = spirv)
        {
            VkShaderModuleCreateInfo info = new()
            {
                codeSize = (nuint)spirv.Length,
                pCode = (uint*)code,
            };
            Api.vkCreateShaderModule(&info, null, out VkShaderModule module).CheckResult();
            return module;
        }
    }

    /// <summary>
    /// The DRM format modifiers the device can export for <paramref name="format"/> as a single-plane image
    /// usable as a storage image (the alpha unpack output). Non-LINEAR (tiled) modifiers come first: a GL
    /// consumer's EGL import on radeonsi accepts the tiled AMD modifiers but not LINEAR, so we must publish a
    /// tiled one. LINEAR (if present) is kept last as a fallback. Empty if the device exposes none. Standalone
    /// (dmabuf) contexts only.
    /// </summary>
    public ulong[] ExportableModifiers(VkFormat format)
    {
        VkDrmFormatModifierPropertiesListEXT list = new();
        VkFormatProperties2 props = new() { pNext = &list };
        _instanceApi.vkGetPhysicalDeviceFormatProperties2(PhysicalDevice, format, &props);
        uint count = list.drmFormatModifierCount;
        if (count == 0)
        {
            return [];
        }

        var mods = new VkDrmFormatModifierPropertiesEXT[count];
        fixed (VkDrmFormatModifierPropertiesEXT* pm = mods)
        {
            list.pDrmFormatModifierProperties = pm;
            props.pNext = &list;
            _instanceApi.vkGetPhysicalDeviceFormatProperties2(PhysicalDevice, format, &props);
        }

        var tiled = new List<ulong>();
        var linear = new List<ulong>();
        foreach (VkDrmFormatModifierPropertiesEXT m in mods)
        {
            // Single-plane only (our BGRA output is one plane) and usable as a storage image (compute writes it).
            if (m.drmFormatModifierPlaneCount != 1)
            {
                continue;
            }

            if ((m.drmFormatModifierTilingFeatures & VkFormatFeatureFlags.StorageImage) == 0)
            {
                continue;
            }

            (m.drmFormatModifier == DrmFormatModLinear ? linear : tiled).Add(m.drmFormatModifier);
        }

        tiled.AddRange(linear);
        return [.. tiled];
    }

    /// <summary>
    /// Find a device-local memory type compatible with <paramref name="typeBits"/>. HW-VERIFY: for imported
    /// dmabuf the valid type bits come from <c>vkGetMemoryFdPropertiesKHR</c>; this picks the first match.
    /// </summary>
    public uint FindMemoryType(uint typeBits, VkMemoryPropertyFlags properties)
    {
        _instanceApi.vkGetPhysicalDeviceMemoryProperties(PhysicalDevice, out VkPhysicalDeviceMemoryProperties memProps);
        for (uint i = 0; i < memProps.memoryTypeCount; i++)
        {
            bool typeOk = (typeBits & (1u << (int)i)) != 0;
            bool propsOk = (memProps.memoryTypes[(int)i].propertyFlags & properties) == properties;
            if (typeOk && propsOk)
            {
                return i;
            }
        }

        throw new InvalidOperationException("No compatible Vulkan memory type.");
    }

    /// <summary>Allocate a one-shot command buffer, record via <paramref name="record"/>, submit and wait.</summary>
    public void SubmitOneShot(Action<VkCommandBuffer> record)
    {
        Api.vkAllocateCommandBuffer(_commandPool, out VkCommandBuffer cmd).CheckResult();

        VkCommandBufferBeginInfo beginInfo = new() { flags = VkCommandBufferUsageFlags.OneTimeSubmit };
        Api.vkBeginCommandBuffer(cmd, &beginInfo).CheckResult();
        record(cmd);
        Api.vkEndCommandBuffer(cmd).CheckResult();

        VkSubmitInfo submit = new() { commandBufferCount = 1, pCommandBuffers = &cmd };
        Api.vkQueueSubmit(_queue, 1, &submit, VkFence.Null).CheckResult();
        Api.vkQueueWaitIdle(_queue).CheckResult();

        Api.vkFreeCommandBuffers(_commandPool, 1, &cmd);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (Device.IsNotNull && _commandPool.IsNotNull)
        {
            if (_ownsDevice)
            {
                Api.vkDeviceWaitIdle().CheckResult();
            }

            // Destroy the command pool in both modes (we always create our own).
            Api.vkDestroyCommandPool(_commandPool, null);
        }

        // The borrowed device + instance belong to FFmpeg (VulkanDevice) and are intentionally left alone;
        // a standalone context created them and so destroys them.
        if (_ownsDevice)
        {
            if (Device.IsNotNull)
            {
                Api.vkDestroyDevice();
            }

            if (_instance.IsNotNull)
            {
                _instanceApi.vkDestroyInstance();
            }
        }
    }
}
