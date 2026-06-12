#if HAS_VULKAN
using System.Runtime.Versioning;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace StreamTransport.Agent;

/// <summary>
/// Linux GPU side-by-side alpha pack/unpack for the PipeWire dmabuf path, built on <see cref="VulkanComputeContext"/>
/// and the embedded <c>.comp</c> shaders. The transport-specific layout (2W x H colour|alpha, BT.709 limited
/// constants, 16..235 alpha) lives in the shaders here; Vulkan is a generic compute binding (Vortice.Vulkan),
/// exactly as the Windows path uses Vortice.Direct3D11. This is the Linux mirror of <c>D3D11AlphaPacker</c> /
/// <c>SyphonAlphaCodec</c>.
///
/// <para><b>Hardware bring-up (compile-verified only).</b> The send-side <see cref="Pack"/> (import a BGRA dmabuf,
/// pack on the GPU, export the 2W x H result as a dmabuf for the encoder) is implemented; the receive-side
/// unpack pipelines are built but their decode-surface import is wired to the FFmpeg hwframe path, which is a
/// follow-up. The dmabuf import/export, DRM modifier and queue-family ownership transfer need a real Linux GPU
/// to validate (WSL has no /dev/dri). HW-VERIFY marks the spots that can only be confirmed on a device.</para>
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed unsafe class VulkanAlphaCodec : IDisposable
{
    private const VkFormat BgraFormat = VkFormat.B8G8R8A8Unorm;

    private readonly VulkanComputeContext _ctx;
    private readonly VkDeviceApi _api;
    private readonly VkSampler _sampler;
    private readonly ComputePipeline _pack;
    private readonly ComputePipeline _unpackBgra;
    private readonly VkDescriptorPool _descriptorPool;
    private bool _disposed;

    public VulkanAlphaCodec(VulkanComputeContext ctx)
    {
        _ctx = ctx;
        _api = ctx.Api;

        VkSamplerCreateInfo samplerInfo = new()
        {
            magFilter = VkFilter.Nearest,
            minFilter = VkFilter.Nearest,
            addressModeU = VkSamplerAddressMode.ClampToEdge,
            addressModeV = VkSamplerAddressMode.ClampToEdge,
            addressModeW = VkSamplerAddressMode.ClampToEdge,
        };
        _api.vkCreateSampler(&samplerInfo, null, out _sampler).CheckResult();

        // A small fixed pool: each pack/unpack call allocates one set (combined image samplers + 1 storage image).
        VkDescriptorPoolSize* sizes = stackalloc VkDescriptorPoolSize[2]
        {
            new() { type = VkDescriptorType.CombinedImageSampler, descriptorCount = 64 },
            new() { type = VkDescriptorType.StorageImage, descriptorCount = 64 },
        };
        VkDescriptorPoolCreateInfo poolInfo = new()
        {
            flags = VkDescriptorPoolCreateFlags.FreeDescriptorSet,
            maxSets = 64,
            poolSizeCount = 2,
            pPoolSizes = sizes,
        };
        _api.vkCreateDescriptorPool(&poolInfo, null, out _descriptorPool).CheckResult();

        // pack: binding 0 sampled colour+alpha, binding 1 storage packed output.
        _pack = ComputePipeline.Create(_api, ctx.CreateShaderModule("alpha_pack.comp"), sampledInputs: 1);
        // unpack_bgra: binding 0 sampled packed BGRA, binding 1 storage BGRA output.
        _unpackBgra = ComputePipeline.Create(_api, ctx.CreateShaderModule("alpha_unpack_bgra.comp"), sampledInputs: 1);
        // unpack_nv12 (binding 0 Y, 1 UV, 2 storage) is built on demand once the decoder hwframe import lands.
    }

    /// <summary>
    /// Pack a W x H BGRA dmabuf into a 2W x H BGRA dmabuf on the GPU (left = colour, right = alpha-as-grey).
    /// Imports <paramref name="srcFd"/> zero-copy, runs the pack compute shader, and exports the result as a
    /// new dmabuf fd ready to hand to the VAAPI/NVENC encoder. The returned <see cref="DmaBufImage.Fd"/> is
    /// owned by the caller (close it after encode). HW-VERIFY: import consumes <paramref name="srcFd"/>.
    /// </summary>
    public DmaBufImage Pack(int srcFd, int width, int height, ulong srcOffset, ulong srcRowPitch, ulong srcModifier)
    {
        ImportedImage src = ImportImage(srcFd, (uint)width, (uint)height, VkImageUsageFlags.Sampled, srcOffset, srcRowPitch, srcModifier);
        ExportedImage dst = CreateExportableImage((uint)(width * 2), (uint)height, VkImageUsageFlags.Storage);

        VkDescriptorSet set = AllocateSet(_pack.SetLayout);
        WriteSampledImage(set, binding: 0, src.View);
        WriteStorageImage(set, binding: 1, dst.View);

        Dispatch(_pack, set, width, height, GroupCount((uint)(width * 2)), GroupCount((uint)height));

        DestroyImported(src);
        // Keep dst's image/memory alive behind the exported fd; the caller owns the fd, we free GPU handles
        // once the encoder has imported it. HW-VERIFY: lifetime handshake with the encoder's dmabuf import.
        return new DmaBufImage(dst, width * 2, height);
    }

    private ImportedImage ImportImage(int fd, uint width, uint height, VkImageUsageFlags usage, ulong offset, ulong rowPitch, ulong modifier)
    {
        VkSubresourceLayout planeLayout = new() { offset = offset, rowPitch = rowPitch };
        VkImageDrmFormatModifierExplicitCreateInfoEXT modInfo = new()
        {
            drmFormatModifier = modifier,
            drmFormatModifierPlaneCount = 1,
            pPlaneLayouts = &planeLayout,
        };
        VkExternalMemoryImageCreateInfo extInfo = new()
        {
            pNext = &modInfo,
            handleTypes = VkExternalMemoryHandleTypeFlags.DmaBufEXT,
        };
        VkImageCreateInfo imageInfo = new()
        {
            pNext = &extInfo,
            imageType = VkImageType.Image2D,
            format = BgraFormat,
            extent = new VkExtent3D(width, height, 1),
            mipLevels = 1,
            arrayLayers = 1,
            samples = VkSampleCountFlags.Count1,
            tiling = VkImageTiling.DrmFormatModifierEXT,
            usage = usage,
            sharingMode = VkSharingMode.Exclusive,
            initialLayout = VkImageLayout.Undefined,
        };
        _api.vkCreateImage(&imageInfo, null, out VkImage image).CheckResult();

        _api.vkGetImageMemoryRequirements(image, out VkMemoryRequirements reqs);
        VkImportMemoryFdInfoKHR importInfo = new()
        {
            handleType = VkExternalMemoryHandleTypeFlags.DmaBufEXT,
            fd = fd,
        };
        // HW-VERIFY: the valid memoryTypeBits for an imported dmabuf come from vkGetMemoryFdPropertiesKHR;
        // reqs.memoryTypeBits is a pragmatic stand-in until that's wired on hardware.
        VkMemoryAllocateInfo allocInfo = new()
        {
            pNext = &importInfo,
            allocationSize = reqs.size,
            memoryTypeIndex = 0,
        };
        _api.vkAllocateMemory(&allocInfo, null, out VkDeviceMemory memory).CheckResult();
        _api.vkBindImageMemory(image, memory, 0).CheckResult();

        VkImageView view = CreateView(image);
        return new ImportedImage(image, memory, view);
    }

    private ExportedImage CreateExportableImage(uint width, uint height, VkImageUsageFlags usage)
    {
        ulong linear = VulkanComputeContext.DrmFormatModLinear;
        VkImageDrmFormatModifierListCreateInfoEXT modList = new()
        {
            drmFormatModifierCount = 1,
            pDrmFormatModifiers = &linear,
        };
        VkExternalMemoryImageCreateInfo extInfo = new()
        {
            pNext = &modList,
            handleTypes = VkExternalMemoryHandleTypeFlags.DmaBufEXT,
        };
        VkImageCreateInfo imageInfo = new()
        {
            pNext = &extInfo,
            imageType = VkImageType.Image2D,
            format = BgraFormat,
            extent = new VkExtent3D(width, height, 1),
            mipLevels = 1,
            arrayLayers = 1,
            samples = VkSampleCountFlags.Count1,
            tiling = VkImageTiling.DrmFormatModifierEXT,
            usage = usage | VkImageUsageFlags.TransferSrc,
            sharingMode = VkSharingMode.Exclusive,
            initialLayout = VkImageLayout.Undefined,
        };
        _api.vkCreateImage(&imageInfo, null, out VkImage image).CheckResult();

        _api.vkGetImageMemoryRequirements(image, out VkMemoryRequirements reqs);
        VkExportMemoryAllocateInfo exportInfo = new()
        {
            handleTypes = VkExternalMemoryHandleTypeFlags.DmaBufEXT,
        };
        VkMemoryAllocateInfo allocInfo = new()
        {
            pNext = &exportInfo,
            allocationSize = reqs.size,
            memoryTypeIndex = _ctx.FindMemoryType(default, reqs.memoryTypeBits, VkMemoryPropertyFlags.DeviceLocal),
        };
        _api.vkAllocateMemory(&allocInfo, null, out VkDeviceMemory memory).CheckResult();
        _api.vkBindImageMemory(image, memory, 0).CheckResult();

        // Read back the chosen plane layout (rowPitch/offset) so the encoder can import the dmabuf correctly.
        VkImageSubresource subresource = new() { aspectMask = VkImageAspectFlags.MemoryPlane0EXT };
        VkSubresourceLayout layout;
        _api.vkGetImageSubresourceLayout(image, &subresource, &layout);

        VkMemoryGetFdInfoKHR getFd = new()
        {
            memory = memory,
            handleType = VkExternalMemoryHandleTypeFlags.DmaBufEXT,
        };
        int fd;
        _api.vkGetMemoryFdKHR(&getFd, &fd).CheckResult();

        VkImageView view = CreateView(image);
        return new ExportedImage(image, memory, view, fd, layout.offset, layout.rowPitch);
    }

    private VkImageView CreateView(VkImage image)
    {
        VkImageViewCreateInfo viewInfo = new()
        {
            image = image,
            viewType = VkImageViewType.Image2D,
            format = BgraFormat,
            subresourceRange = new VkImageSubresourceRange(VkImageAspectFlags.Color, 0, 1, 0, 1),
        };
        _api.vkCreateImageView(&viewInfo, null, out VkImageView view).CheckResult();
        return view;
    }

    private VkDescriptorSet AllocateSet(VkDescriptorSetLayout layout)
    {
        VkDescriptorSetAllocateInfo info = new()
        {
            descriptorPool = _descriptorPool,
            descriptorSetCount = 1,
            pSetLayouts = &layout,
        };
        VkDescriptorSet set;
        _api.vkAllocateDescriptorSets(&info, &set).CheckResult();
        return set;
    }

    private void WriteSampledImage(VkDescriptorSet set, uint binding, VkImageView view)
    {
        VkDescriptorImageInfo imageInfo = new()
        {
            sampler = _sampler,
            imageView = view,
            imageLayout = VkImageLayout.General,
        };
        VkWriteDescriptorSet write = new()
        {
            dstSet = set,
            dstBinding = binding,
            descriptorCount = 1,
            descriptorType = VkDescriptorType.CombinedImageSampler,
            pImageInfo = &imageInfo,
        };
        _api.vkUpdateDescriptorSets(1, &write, 0, null);
    }

    private void WriteStorageImage(VkDescriptorSet set, uint binding, VkImageView view)
    {
        VkDescriptorImageInfo imageInfo = new() { imageView = view, imageLayout = VkImageLayout.General };
        VkWriteDescriptorSet write = new()
        {
            dstSet = set,
            dstBinding = binding,
            descriptorCount = 1,
            descriptorType = VkDescriptorType.StorageImage,
            pImageInfo = &imageInfo,
        };
        _api.vkUpdateDescriptorSets(1, &write, 0, null);
    }

    private void Dispatch(ComputePipeline pipeline, VkDescriptorSet set, int width, int height, uint groupsX, uint groupsY)
    {
        _ctx.SubmitOneShot(cmd =>
        {
            VkDescriptorSet ds = set; // a lambda-local (not a captured var) so its address can be taken.
            _api.vkCmdBindPipeline(cmd, VkPipelineBindPoint.Compute, pipeline.Pipeline);
            _api.vkCmdBindDescriptorSets(cmd, VkPipelineBindPoint.Compute, pipeline.Layout, 0, 1, &ds, 0, null);
            Span<int> push = [width, height];
            fixed (int* p = push)
            {
                _api.vkCmdPushConstants(cmd, pipeline.Layout, VkShaderStageFlags.Compute, 0, 8, p);
            }

            _api.vkCmdDispatch(cmd, groupsX, groupsY, 1);
        });
    }

    private void DestroyImported(ImportedImage img)
    {
        _api.vkDestroyImageView(img.View, null);
        _api.vkDestroyImage(img.Image, null);
        _api.vkFreeMemory(img.Memory, null);
    }

    internal void DestroyExported(ExportedImage img)
    {
        _api.vkDestroyImageView(img.View, null);
        _api.vkDestroyImage(img.Image, null);
        _api.vkFreeMemory(img.Memory, null);
    }

    private static uint GroupCount(uint pixels) => (pixels + 7) / 8;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pack.Dispose(_api);
        _unpackBgra.Dispose(_api);
        _api.vkDestroyDescriptorPool(_descriptorPool, null);
        _api.vkDestroySampler(_sampler, null);
    }

    private readonly record struct ImportedImage(VkImage Image, VkDeviceMemory Memory, VkImageView View);

    internal readonly record struct ExportedImage(VkImage Image, VkDeviceMemory Memory, VkImageView View, int Fd, ulong Offset, ulong RowPitch);

    /// <summary>A GPU-resident packed surface exported as a dmabuf for the encoder. Owns the exported fd.</summary>
    internal readonly record struct DmaBufImage(ExportedImage Surface, int Width, int Height)
    {
        public int Fd => Surface.Fd;
        public ulong RowPitch => Surface.RowPitch;
        public ulong Offset => Surface.Offset;
    }

    /// <summary>A compute pipeline + its descriptor-set/pipeline layout for one shader.</summary>
    private readonly struct ComputePipeline
    {
        public required VkPipeline Pipeline { get; init; }
        public required VkPipelineLayout Layout { get; init; }
        public required VkDescriptorSetLayout SetLayout { get; init; }
        public required VkShaderModule Module { get; init; }

        public static ComputePipeline Create(VkDeviceApi api, VkShaderModule module, int sampledInputs)
        {
            // bindings: [0..sampledInputs) combined image samplers, then one storage image.
            int bindingCount = sampledInputs + 1;
            Span<VkDescriptorSetLayoutBinding> bindings = stackalloc VkDescriptorSetLayoutBinding[bindingCount];
            for (int i = 0; i < sampledInputs; i++)
            {
                bindings[i] = new VkDescriptorSetLayoutBinding
                {
                    binding = (uint)i,
                    descriptorType = VkDescriptorType.CombinedImageSampler,
                    descriptorCount = 1,
                    stageFlags = VkShaderStageFlags.Compute,
                };
            }

            bindings[sampledInputs] = new VkDescriptorSetLayoutBinding
            {
                binding = (uint)sampledInputs,
                descriptorType = VkDescriptorType.StorageImage,
                descriptorCount = 1,
                stageFlags = VkShaderStageFlags.Compute,
            };

            VkDescriptorSetLayout setLayout;
            fixed (VkDescriptorSetLayoutBinding* pb = bindings)
            {
                VkDescriptorSetLayoutCreateInfo setInfo = new() { bindingCount = (uint)bindingCount, pBindings = pb };
                api.vkCreateDescriptorSetLayout(&setInfo, null, out setLayout).CheckResult();
            }

            VkPushConstantRange pushRange = new() { stageFlags = VkShaderStageFlags.Compute, offset = 0, size = 8 };
            VkDescriptorSetLayout setLayoutLocal = setLayout;
            VkPipelineLayoutCreateInfo layoutInfo = new()
            {
                setLayoutCount = 1,
                pSetLayouts = &setLayoutLocal,
                pushConstantRangeCount = 1,
                pPushConstantRanges = &pushRange,
            };
            api.vkCreatePipelineLayout(&layoutInfo, null, out VkPipelineLayout layout).CheckResult();

            ReadOnlySpan<byte> entry = "main\0"u8;
            fixed (byte* pEntry = entry)
            {
                VkPipelineShaderStageCreateInfo stage = new()
                {
                    stage = VkShaderStageFlags.Compute,
                    module = module,
                    pName = pEntry,
                };
                VkComputePipelineCreateInfo pipelineInfo = new() { stage = stage, layout = layout };
                VkPipeline pipeline;
                api.vkCreateComputePipelines(VkPipelineCache.Null, 1, &pipelineInfo, &pipeline).CheckResult();
                return new ComputePipeline
                {
                    Pipeline = pipeline,
                    Layout = layout,
                    SetLayout = setLayout,
                    Module = module,
                };
            }
        }

        public void Dispose(VkDeviceApi api)
        {
            api.vkDestroyPipeline(Pipeline, null);
            api.vkDestroyPipelineLayout(Layout, null);
            api.vkDestroyDescriptorSetLayout(SetLayout, null);
            api.vkDestroyShaderModule(Module, null);
        }
    }
}
#endif
