#if HAS_VULKAN
using System.Runtime.Versioning;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace StreamTransport.Agent;

/// <summary>
/// A minimal headless Vulkan compute device for the Linux GPU alpha pack/unpack path. It owns the instance,
/// physical/logical device (with the dmabuf external-memory extensions), a compute queue, a command pool and
/// a runtime GLSL-&gt;SPIR-V compiler (shaderc) - the cross-platform parallel to <c>D3D11Devices</c> on Windows.
/// It deliberately holds <b>no</b> swapchain or surface: frames come in and go out as DMA-BUF file descriptors
/// (zero-copy), so this never touches a window system.
///
/// <para><b>Hardware bring-up (not yet verified on a GPU).</b> This compiles against Vortice.Vulkan 3.2.3 and
/// is structurally complete, but the dmabuf import/export, DRM format-modifier negotiation and queue-family
/// ownership transfer can only be validated on a real Linux GPU (a dual-booted desktop or the RK3588). WSL has
/// no <c>/dev/dri</c>, so CI only compile-checks this. Points needing a device are marked HW-VERIFY.</para>
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed unsafe class VulkanComputeContext : IDisposable
{
    // DRM_FORMAT_MOD_LINEAR - the only modifier we assume without negotiating (single-plane BGRA, the avatar
    // case). HW-VERIFY: tiled producers expose a vendor modifier that must be read from PipeWire and matched
    // here via VK_EXT_image_drm_format_modifier; PipeWire.NET does not surface the modifier list yet.
    public const ulong DrmFormatModLinear = 0UL;

    private static readonly string[] s_requiredDeviceExtensions =
    [
        "VK_KHR_external_memory_fd",
        "VK_EXT_external_memory_dma_buf",
        "VK_EXT_image_drm_format_modifier",
        "VK_EXT_queue_family_foreign",
    ];

    private readonly VkInstance _instance;
    private readonly VkInstanceApi _instanceApi;
    private readonly VkQueue _computeQueue;
    private readonly uint _computeQueueFamily;
    private readonly VkCommandPool _commandPool;
    private bool _disposed;

    public VulkanComputeContext()
    {
        vkInitialize().CheckResult();

        VkApplicationInfo appInfo = new() { apiVersion = VkVersion.Version_1_1 };
        VkInstanceCreateInfo instanceInfo = new() { pApplicationInfo = &appInfo };
        vkCreateInstance(&instanceInfo, out _instance).CheckResult();
        _instanceApi = GetApi(_instance);

        VkPhysicalDevice physicalDevice = PickPhysicalDevice(out _computeQueueFamily);

        float priority = 1.0f;
        VkDeviceQueueCreateInfo queueInfo = new()
        {
            queueFamilyIndex = _computeQueueFamily,
            queueCount = 1,
            pQueuePriorities = &priority,
        };

        using var extensions = new VkStringArray(s_requiredDeviceExtensions);
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

        Api.vkGetDeviceQueue(_computeQueueFamily, 0, out _computeQueue);

        VkCommandPoolCreateInfo poolInfo = new()
        {
            flags = VkCommandPoolCreateFlags.ResetCommandBuffer,
            queueFamilyIndex = _computeQueueFamily,
        };
        Api.vkCreateCommandPool(&poolInfo, null, out _commandPool).CheckResult();
    }

    public VkDevice Device { get; }
    public VkDeviceApi Api { get; }

    private VkPhysicalDevice PickPhysicalDevice(out uint computeQueueFamily)
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
            $"({string.Join(", ", s_requiredDeviceExtensions)}).");
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

        foreach (string required in s_requiredDeviceExtensions)
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
    /// Find a device-local memory type compatible with <paramref name="typeBits"/>. HW-VERIFY: for imported
    /// dmabuf the valid type bits come from <c>vkGetMemoryFdPropertiesKHR</c>; this picks the first match.
    /// </summary>
    public uint FindMemoryType(VkPhysicalDevice physicalDevice, uint typeBits, VkMemoryPropertyFlags properties)
    {
        _instanceApi.vkGetPhysicalDeviceMemoryProperties(physicalDevice, out VkPhysicalDeviceMemoryProperties memProps);
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
        Api.vkQueueSubmit(_computeQueue, 1, &submit, VkFence.Null).CheckResult();
        Api.vkQueueWaitIdle(_computeQueue).CheckResult();

        Api.vkFreeCommandBuffers(_commandPool, 1, &cmd);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (Device.IsNotNull)
        {
            Api.vkDeviceWaitIdle().CheckResult();
            Api.vkDestroyCommandPool(_commandPool, null);
            Api.vkDestroyDevice();
        }

        if (_instance.IsNotNull)
        {
            _instanceApi.vkDestroyInstance();
        }
    }
}
#endif
