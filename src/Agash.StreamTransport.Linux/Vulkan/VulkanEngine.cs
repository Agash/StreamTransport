using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using Agash.StreamTransport.Media;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace Agash.StreamTransport.Linux.Vulkan;

/// <summary>
/// A Vulkan device with the compute queue and pipelines the package's processors, sources and sinks
/// run on, and DMA-BUF import and export. There is one per GPU for the life of the process, like the
/// GPU itself; work on it is serialised, and every submission is waited for, so frames leave finished:
/// VAAPI, PipeWire consumers and compositors read DMA-BUFs directly.
/// </summary>
internal sealed unsafe partial class VulkanEngine
{
    /// <summary>DRM_FORMAT_MOD_LINEAR: rows one after another, which every importer takes.</summary>
    public const ulong LinearModifier = 0;

    private static readonly ConcurrentDictionary<ulong, Lazy<VulkanEngine>> Engines = new();
    private static readonly Lazy<(VkInstance Instance, VkInstanceApi Api)> Instance = new(
        CreateInstance
    );

    private static readonly string[] DeviceExtensions =
    [
        "VK_KHR_external_memory_fd",
        "VK_EXT_external_memory_dma_buf",
        "VK_EXT_image_drm_format_modifier",
        "VK_EXT_queue_family_foreign",
        "VK_EXT_physical_device_drm",
    ];

    private readonly VkInstanceApi _instanceApi;
    private readonly VkPhysicalDevice _physical;
    private readonly VkPhysicalDeviceMemoryProperties _memory;
    private readonly VkQueue _queue;
    private readonly VkCommandPool _commandPool;
    private readonly VkCommandBuffer _commands;
    private readonly VkFence _fence;
    private readonly VkDescriptorPool _descriptors;
    private readonly Lock _gate = new();

    // Whether a batch is being recorded; read and written only by the thread holding the gate.
    private bool _recording;

    private VulkanEngine(VkPhysicalDevice physical, uint queueFamily, GpuIdentity identity)
    {
        (_, _instanceApi) = Instance.Value;
        _physical = physical;
        Identity = identity;
        QueueFamily = queueFamily;
        VkPhysicalDeviceMemoryProperties memory = default;
        _instanceApi.vkGetPhysicalDeviceMemoryProperties(physical, &memory);
        _memory = memory;

        float priority = 1;
        VkDeviceQueueCreateInfo queueInfo = new()
        {
            queueFamilyIndex = queueFamily,
            queueCount = 1,
            pQueuePriorities = &priority,
        };
        using VkStringArray extensions = new(DeviceExtensions);
        VkDeviceCreateInfo deviceInfo = new()
        {
            queueCreateInfoCount = 1,
            pQueueCreateInfos = &queueInfo,
            enabledExtensionCount = extensions.Length,
            ppEnabledExtensionNames = extensions,
        };
        VkDevice device;
        _instanceApi.vkCreateDevice(physical, &deviceInfo, null, &device).CheckResult();
        Device = device;
        Api = GetApi(Instance.Value.Instance, device);
        VkQueue queue = default;
        Api.vkGetDeviceQueue(queueFamily, 0, &queue);
        _queue = queue;

        VkCommandPoolCreateInfo poolInfo = new()
        {
            flags = VkCommandPoolCreateFlags.ResetCommandBuffer,
            queueFamilyIndex = queueFamily,
        };
        VkCommandPool pool;
        Api.vkCreateCommandPool(&poolInfo, null, &pool).CheckResult();
        _commandPool = pool;
        VkCommandBufferAllocateInfo allocate = new()
        {
            commandPool = pool,
            level = VkCommandBufferLevel.Primary,
            commandBufferCount = 1,
        };
        VkCommandBuffer commands;
        Api.vkAllocateCommandBuffers(&allocate, &commands).CheckResult();
        _commands = commands;
        VkFenceCreateInfo fenceInfo = new();
        VkFence fence;
        Api.vkCreateFence(&fenceInfo, null, &fence).CheckResult();
        _fence = fence;

        VkDescriptorPoolSize* sizes = stackalloc VkDescriptorPoolSize[2];
        sizes[0] = new VkDescriptorPoolSize
        {
            type = VkDescriptorType.StorageImage,
            descriptorCount = 64,
        };
        sizes[1] = new VkDescriptorPoolSize
        {
            type = VkDescriptorType.CombinedImageSampler,
            descriptorCount = 16,
        };
        VkDescriptorPoolCreateInfo descriptorInfo = new()
        {
            flags = VkDescriptorPoolCreateFlags.FreeDescriptorSet,
            maxSets = 16,
            poolSizeCount = 2,
            pPoolSizes = sizes,
        };
        VkDescriptorPool descriptors;
        Api.vkCreateDescriptorPool(&descriptorInfo, null, &descriptors).CheckResult();
        _descriptors = descriptors;

        VkSamplerCreateInfo samplerInfo = new()
        {
            magFilter = VkFilter.Linear,
            minFilter = VkFilter.Linear,
            mipmapMode = VkSamplerMipmapMode.Nearest,
            addressModeU = VkSamplerAddressMode.ClampToEdge,
            addressModeV = VkSamplerAddressMode.ClampToEdge,
            addressModeW = VkSamplerAddressMode.ClampToEdge,
        };
        VkSampler sampler;
        Api.vkCreateSampler(&samplerInfo, null, &sampler).CheckResult();
        Sampler = sampler;

        RgbToYuv = new ComputeKernel(
            this,
            "RgbToYuv.spv",
            [
                VkDescriptorType.CombinedImageSampler,
                VkDescriptorType.StorageImage,
                VkDescriptorType.StorageImage,
            ],
            64
        );
        YuvToRgb = new ComputeKernel(
            this,
            "YuvToRgb.spv",
            [
                VkDescriptorType.StorageImage,
                VkDescriptorType.StorageImage,
                VkDescriptorType.StorageImage,
            ],
            80
        );
    }

    public VkDevice Device { get; }

    public VkDeviceApi Api { get; }

    /// <summary>The compute queue's family.</summary>
    public uint QueueFamily { get; }

    /// <summary>The GPU, as its DRM render node.</summary>
    public GpuIdentity Identity { get; }

    public VkSampler Sampler { get; }

    public ComputeKernel RgbToYuv { get; }

    public ComputeKernel YuvToRgb { get; }

    /// <summary>The engine of a GPU: the one a DRM device names, or the first that can do the work.</summary>
    /// <param name="device">A DRM device; null for the first suitable GPU.</param>
    /// <returns>The engine.</returns>
    /// <exception cref="InvalidOperationException">No GPU here can import and export DMA-BUFs.</exception>
    /// <exception cref="ArgumentException">The identity is not a GPU of this system.</exception>
    public static VulkanEngine For(GpuIdentity? device)
    {
        (VkPhysicalDevice physical, uint family, GpuIdentity identity) = Find(device);
        return Engines
            .GetOrAdd(
                identity.Value,
                _ => new Lazy<VulkanEngine>(() => new VulkanEngine(physical, family, identity))
            )
            .Value;
    }

    /// <summary>
    /// Starts one batch of GPU work on the engine's command buffer. Work on the engine is serialised:
    /// the batch holds the engine until it is submitted or disposed.
    /// </summary>
    /// <returns>The batch; record into <see cref="Batch.Commands"/>, then <see cref="Batch.Submit"/>.</returns>
    public Batch Begin()
    {
        _gate.Enter();
        try
        {
            Api.vkResetCommandBuffer(_commands, 0).CheckResult();
            VkCommandBufferBeginInfo begin = new()
            {
                flags = VkCommandBufferUsageFlags.OneTimeSubmit,
            };
            Api.vkBeginCommandBuffer(_commands, &begin).CheckResult();
            _recording = true;
            return new Batch(this);
        }
        catch
        {
            _gate.Exit();
            throw;
        }
    }

    /// <summary>
    /// One batch of recorded GPU work, holding the engine until it ends. The batch in progress is the
    /// engine's, so a copy of this view (a <c>using</c> variable is read-only) ends the same batch.
    /// </summary>
    public readonly ref struct Batch
    {
        private readonly VulkanEngine _engine;

        internal Batch(VulkanEngine engine) => _engine = engine;

        public VkCommandBuffer Commands => _engine._commands;

        /// <summary>Runs what was recorded and waits until the GPU has finished it.</summary>
        public void Submit()
        {
            if (!_engine._recording)
            {
                throw new InvalidOperationException("The batch has already ended.");
            }

            _engine._recording = false;
            try
            {
                _engine.Execute();
            }
            finally
            {
                _engine._gate.Exit();
            }
        }

        /// <summary>Ends a batch that was not submitted, discarding what was recorded.</summary>
        public void Dispose()
        {
            if (_engine._recording)
            {
                _engine._recording = false;
                _ = _engine.Api.vkEndCommandBuffer(_engine._commands);
                _engine._gate.Exit();
            }
        }
    }

    private void Execute()
    {
        Api.vkEndCommandBuffer(_commands).CheckResult();
        VkCommandBuffer commands = _commands;
        VkSubmitInfo submit = new() { commandBufferCount = 1, pCommandBuffers = &commands };
        VkFence fence = _fence;
        Api.vkResetFences(1, &fence).CheckResult();
        Api.vkQueueSubmit(_queue, 1, &submit, fence).CheckResult();
        Api.vkWaitForFences(1, &fence, true, ulong.MaxValue).CheckResult();
    }

    /// <summary>A descriptor set for a kernel, freed after the batch that uses it has run.</summary>
    public VkDescriptorSet AllocateSet(VkDescriptorSetLayout layout)
    {
        VkDescriptorSetAllocateInfo info = new()
        {
            descriptorPool = _descriptors,
            descriptorSetCount = 1,
            pSetLayouts = &layout,
        };
        VkDescriptorSet set;
        Api.vkAllocateDescriptorSets(&info, &set).CheckResult();
        return set;
    }

    public void FreeSet(VkDescriptorSet set) =>
        Api.vkFreeDescriptorSets(_descriptors, 1, &set).CheckResult();

    /// <summary>A memory type both allowed and with the properties asked for.</summary>
    public uint MemoryType(uint allowed, VkMemoryPropertyFlags properties)
    {
        for (uint i = 0; i < _memory.memoryTypeCount; i++)
        {
            if (
                (allowed & (1u << (int)i)) != 0
                && (_memory.memoryTypes[(int)i].propertyFlags & properties) == properties
            )
            {
                return i;
            }
        }

        for (uint i = 0; i < _memory.memoryTypeCount; i++)
        {
            if ((allowed & (1u << (int)i)) != 0)
            {
                return i;
            }
        }

        throw new InvalidOperationException("No memory type suits the image.");
    }

    /// <summary>Whether the GPU can use images of a format with a DRM modifier for what is asked.</summary>
    public bool Supports(VkFormat format, ulong modifier, VkFormatFeatureFlags features) =>
        Modifiers(format, features).Contains(modifier);

    /// <summary>
    /// The DRM modifiers the GPU can use images of a format with, for what is asked, single-plane only:
    /// what a consumer here offers a producer to import.
    /// </summary>
    public ulong[] Modifiers(VkFormat format, VkFormatFeatureFlags features)
    {
        VkDrmFormatModifierPropertiesListEXT list = new();
        VkFormatProperties2 properties = new() { pNext = &list };
        _instanceApi.vkGetPhysicalDeviceFormatProperties2(_physical, format, &properties);
        if (list.drmFormatModifierCount == 0)
        {
            return [];
        }

        VkDrmFormatModifierPropertiesEXT* modifiers =
            stackalloc VkDrmFormatModifierPropertiesEXT[(int)list.drmFormatModifierCount];
        list.pDrmFormatModifierProperties = modifiers;
        _instanceApi.vkGetPhysicalDeviceFormatProperties2(_physical, format, &properties);
        List<ulong> usable = [];
        for (int i = 0; i < list.drmFormatModifierCount; i++)
        {
            if (
                modifiers[i].drmFormatModifierPlaneCount == 1
                && (modifiers[i].drmFormatModifierTilingFeatures & features) == features
            )
            {
                usable.Add(modifiers[i].drmFormatModifier);
            }
        }

        return [.. usable];
    }

    private static (VkInstance Instance, VkInstanceApi Api) CreateInstance()
    {
        vkInitialize().CheckResult();
        VkApplicationInfo application = new() { apiVersion = VkVersion.Version_1_2 };
        VkInstanceCreateInfo info = new() { pApplicationInfo = &application };
        VkInstance instance;
        vkCreateInstance(&info, null, &instance).CheckResult();
        return (instance, GetApi(instance));
    }

    // The GPU: the one whose render node or primary node a DRM identity names, or the first that has
    // the DMA-BUF extensions and a compute queue.
    private static (VkPhysicalDevice Device, uint QueueFamily, GpuIdentity Identity) Find(
        GpuIdentity? wanted
    )
    {
        if (wanted is { Kind: not GpuIdentityKind.DrmDevice } other)
        {
            throw new ArgumentException($"{other} is not a DRM device.", nameof(wanted));
        }

        VkInstanceApi api = Instance.Value.Api;
        uint count = 0;
        api.vkEnumeratePhysicalDevices(&count, null).CheckResult();
        VkPhysicalDevice* devices = stackalloc VkPhysicalDevice[(int)count];
        api.vkEnumeratePhysicalDevices(&count, devices).CheckResult();
        for (int i = 0; i < count; i++)
        {
            VkPhysicalDevice device = devices[i];
            if (!HasExtensions(api, device) || ComputeFamily(api, device) is not { } family)
            {
                continue;
            }

            VkPhysicalDeviceDrmPropertiesEXT drm = new();
            VkPhysicalDeviceProperties2 properties = new() { pNext = &drm };
            api.vkGetPhysicalDeviceProperties2(device, &properties);
            ulong render = DeviceNumber((uint)drm.renderMajor, (uint)drm.renderMinor);
            ulong primary = DeviceNumber((uint)drm.primaryMajor, (uint)drm.primaryMinor);
            if (
                wanted is { } id
                && (drm.hasRender ? render : 0) != id.Value
                && (drm.hasPrimary ? primary : 0) != id.Value
            )
            {
                continue;
            }

            return (device, family, GpuIdentity.FromDrmDevice(drm.hasRender ? render : primary));
        }

        throw wanted is null
            ? new InvalidOperationException(
                $"No Vulkan GPU here has a compute queue and {string.Join(", ", DeviceExtensions)}."
            )
            : new ArgumentException(
                $"No Vulkan GPU here is DRM device {wanted.Value.Value}.",
                nameof(wanted)
            );
    }

    private static bool HasExtensions(VkInstanceApi api, VkPhysicalDevice device)
    {
        uint count = 0;
        api.vkEnumerateDeviceExtensionProperties(device, null, &count, null).CheckResult();
        VkExtensionProperties* properties = stackalloc VkExtensionProperties[(int)count];
        api.vkEnumerateDeviceExtensionProperties(device, null, &count, properties).CheckResult();
        HashSet<string> available = new(StringComparer.Ordinal);
        for (int i = 0; i < count; i++)
        {
            available.Add(Marshal.PtrToStringUTF8((nint)properties[i].extensionName)!);
        }

        return DeviceExtensions.All(available.Contains);
    }

    private static uint? ComputeFamily(VkInstanceApi api, VkPhysicalDevice device)
    {
        uint count = 0;
        api.vkGetPhysicalDeviceQueueFamilyProperties(device, &count, null);
        VkQueueFamilyProperties* families = stackalloc VkQueueFamilyProperties[(int)count];
        api.vkGetPhysicalDeviceQueueFamilyProperties(device, &count, families);
        for (uint i = 0; i < count; i++)
        {
            if ((families[i].queueFlags & VkQueueFlags.Compute) != 0)
            {
                return i;
            }
        }

        return null;
    }

    // Linux's makedev: the major and minor split across a dev_t as glibc lays it out.
    private static ulong DeviceNumber(uint major, uint minor) =>
        ((ulong)(major & 0xfffff000) << 32)
        | ((ulong)(major & 0xfff) << 8)
        | ((ulong)(minor & 0xffffff00) << 12)
        | (minor & 0xff);

    internal static byte[] Resource(string name)
    {
        using Stream stream =
            typeof(VulkanEngine).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"The kernel {name} is not in the assembly.");
        using MemoryStream copy = new();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}

/// <summary>A compute pipeline with its descriptor layout and push constants.</summary>
internal sealed unsafe class ComputeKernel
{
    public ComputeKernel(
        VulkanEngine engine,
        string spirv,
        VkDescriptorType[] bindings,
        uint pushBytes
    )
    {
        VkDeviceApi api = engine.Api;
        VkDescriptorSetLayoutBinding* layoutBindings =
            stackalloc VkDescriptorSetLayoutBinding[bindings.Length];
        for (int i = 0; i < bindings.Length; i++)
        {
            layoutBindings[i] = new VkDescriptorSetLayoutBinding
            {
                binding = (uint)i,
                descriptorType = bindings[i],
                descriptorCount = 1,
                stageFlags = VkShaderStageFlags.Compute,
            };
        }

        VkDescriptorSetLayoutCreateInfo setInfo = new()
        {
            bindingCount = (uint)bindings.Length,
            pBindings = layoutBindings,
        };
        VkDescriptorSetLayout setLayout;
        api.vkCreateDescriptorSetLayout(&setInfo, null, &setLayout).CheckResult();
        SetLayout = setLayout;

        VkPushConstantRange push = new()
        {
            stageFlags = VkShaderStageFlags.Compute,
            size = pushBytes,
        };
        VkPipelineLayoutCreateInfo layoutInfo = new()
        {
            setLayoutCount = 1,
            pSetLayouts = &setLayout,
            pushConstantRangeCount = 1,
            pPushConstantRanges = &push,
        };
        VkPipelineLayout layout;
        api.vkCreatePipelineLayout(&layoutInfo, null, &layout).CheckResult();
        Layout = layout;

        byte[] code = VulkanEngine.Resource(spirv);
        VkShaderModule module;
        fixed (byte* bytes = code)
        {
            VkShaderModuleCreateInfo moduleInfo = new()
            {
                codeSize = (nuint)code.Length,
                pCode = (uint*)bytes,
            };
            api.vkCreateShaderModule(&moduleInfo, null, &module).CheckResult();
        }

        VkUtf8ReadOnlyString entry = "main"u8;
        VkComputePipelineCreateInfo pipelineInfo = new()
        {
            stage = new VkPipelineShaderStageCreateInfo
            {
                stage = VkShaderStageFlags.Compute,
                module = module,
                pName = entry,
            },
            layout = layout,
        };
        VkPipeline pipeline;
        api.vkCreateComputePipelines(VkPipelineCache.Null, 1, &pipelineInfo, null, &pipeline)
            .CheckResult();
        Pipeline = pipeline;
        api.vkDestroyShaderModule(module, null);
    }

    public VkDescriptorSetLayout SetLayout { get; }

    public VkPipelineLayout Layout { get; }

    public VkPipeline Pipeline { get; }
}
