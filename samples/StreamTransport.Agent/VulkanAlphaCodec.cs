#if HAS_VULKAN
using System.Runtime.InteropServices;
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
    private const VkFormat R8Format = VkFormat.R8Unorm;     // NV12 luma plane.
    private const VkFormat Rg8Format = VkFormat.R8G8Unorm;  // NV12 interleaved CbCr plane.

    private readonly VulkanComputeContext _ctx;
    private readonly VkDeviceApi _api;
    private readonly VkSampler _sampler;
    private readonly ComputePipeline _pack;
    private readonly ComputePipeline _unpackBgra;
    private readonly ComputePipeline _unpackNv12;
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

        // pack: binding 0 sampled colour+alpha, binding 1 storage packed output. Precompiled SPIR-V (see
        // VulkanComputeContext.CreateShaderModule for why it is built ahead of time, not via runtime shaderc).
        _pack = ComputePipeline.Create(_api, ctx.CreateShaderModule("alpha_pack.spv"), sampledInputs: 1);
        // unpack_bgra: binding 0 sampled packed BGRA, binding 1 storage BGRA output.
        _unpackBgra = ComputePipeline.Create(_api, ctx.CreateShaderModule("alpha_unpack_bgra.spv"), sampledInputs: 1);
        // unpack_nv12: binding 0 sampled Y (R8), 1 sampled UV (R8G8), 2 storage BGRA. The common VAAPI/NVDEC
        // receive case - the hardware decoder emits the packed 2W x H frame as NV12, so we unpack from the two
        // luma/chroma planes directly rather than after a colour conversion.
        _unpackNv12 = ComputePipeline.Create(_api, ctx.CreateShaderModule("alpha_unpack_nv12.spv"), sampledInputs: 2);
    }

    /// <summary>
    /// Pack a W x H BGRA dmabuf into a 2W x H BGRA dmabuf on the GPU (left = colour, right = alpha-as-grey).
    /// Imports <paramref name="srcFd"/> zero-copy, runs the pack compute shader, and exports the result as a
    /// new dmabuf fd ready to hand to the VAAPI/NVENC encoder. The returned <see cref="DmaBufImage.Fd"/> is
    /// owned by the caller (close it after encode). HW-VERIFY: import consumes <paramref name="srcFd"/>.
    /// </summary>
    public DmaBufImage Pack(int srcFd, int width, int height, ulong srcOffset, ulong srcRowPitch, ulong srcModifier)
    {
        ImportedImage src = ImportImage(srcFd, BgraFormat, (uint)width, (uint)height, VkImageUsageFlags.Sampled, srcOffset, srcRowPitch, srcModifier);
        ExportedImage dst = CreateExportableImage((uint)(width * 2), (uint)height, VkImageUsageFlags.Storage, default);

        VkDescriptorSet set = AllocateSet(_pack.SetLayout);
        WriteSampledImage(set, binding: 0, src.View);
        WriteStorageImage(set, binding: 1, dst.View);

        Dispatch(_pack, set, width, height, GroupCount((uint)(width * 2)), GroupCount((uint)height));

        DestroyImported(src);
        _api.vkFreeDescriptorSets(_descriptorPool, 1, &set).CheckResult();
        // Keep dst's image/memory alive behind the exported fd; the caller owns the fd, we free GPU handles
        // once the encoder has imported it. HW-VERIFY: lifetime handshake with the encoder's dmabuf import.
        return new DmaBufImage(dst, width * 2, height);
    }

    /// <summary>
    /// Unpack a decoded 2W x H NV12 dmabuf surface (the hardware HEVC decoder's output) into a W x H BGRA
    /// dmabuf with reconstructed alpha, on the GPU. <paramref name="y"/> is the full-resolution R8 luma plane
    /// (2W x H) and <paramref name="uv"/> the half-resolution R8G8 CbCr plane (W x H/2); both carry the same
    /// DRM <paramref name="modifier"/>. The returned dmabuf is ready to hand to the PipeWire republish pool.
    /// This is the Linux receive-side mirror of <c>D3D11AlphaUnpacker</c> / the macOS Metal unpacker, and the
    /// inverse of <see cref="Pack"/>. HW-VERIFY: importing a tiled VAAPI NV12 surface's planes as R8/R8G8
    /// Vulkan images under its DRM modifier.
    /// </summary>
    public DmaBufImage Unpack(in Nv12Plane y, in Nv12Plane uv, int width, int height, ulong modifier)
    {
        ExportedImage dst = CreateExportableImage((uint)width, (uint)height, VkImageUsageFlags.Storage, default);
        UnpackInto(y, uv, width, height, modifier, dst);
        return new DmaBufImage(dst, width, height);
    }

    /// <summary>The DRM modifiers the device can export a BGRA storage image with (tiled first). Advertise the
    /// chosen one (<see cref="ExportedImage.Modifier"/> of a <see cref="CreateOutputImage"/> result) to PipeWire.</summary>
    public ulong[] OutputModifiers() => _ctx.ExportableModifiers(BgraFormat);

    /// <summary>
    /// Copy a W x H output image's pixels back to a CPU BGRA buffer (GPU-&gt;host, for <c>--verify</c> content
    /// checks). Tightly packed (stride = W*4). Slow relative to the zero-copy path - use only in verify mode.
    /// </summary>
    public byte[] ReadbackToBgra(in ExportedImage src, int width, int height)
        => CopyImageToCpu(src.Image, width, height, 4);

    /// <summary>
    /// Read a decoded NV12 dmabuf surface (planes <paramref name="y"/> R8 + <paramref name="uv"/> R8G8, sharing
    /// <paramref name="modifier"/>) back to a tightly-packed CPU NV12 buffer via Vulkan - the readback for the
    /// opaque GPU path's <c>--verify</c> check. Uses Vulkan (not libva vaGetImage/vaDeriveImage, which abort
    /// inside the radeonsi driver for these tiled surfaces). For verify only; slow vs the zero-copy path.
    /// </summary>
    public byte[] ReadbackNv12(in Nv12Plane y, in Nv12Plane uv, int width, int height, ulong modifier)
    {
        ImportedImage yImg = ImportImage(y.Fd, R8Format, (uint)width, (uint)height, VkImageUsageFlags.TransferSrc, y.Offset, y.RowPitch, modifier);
        ImportedImage uvImg = ImportImage(uv.Fd, Rg8Format, (uint)(width / 2), (uint)(height / 2), VkImageUsageFlags.TransferSrc, uv.Offset, uv.RowPitch, modifier);
        byte[] yBytes = CopyImageToCpu(yImg.Image, width, height, 1);          // R8 luma: W*H bytes
        byte[] uvBytes = CopyImageToCpu(uvImg.Image, width / 2, height / 2, 2); // R8G8 chroma: (W/2)*(H/2)*2 = W*H/2 bytes
        DestroyImported(yImg);
        DestroyImported(uvImg);

        byte[] outBytes = new byte[width * height * 3 / 2];
        yBytes.CopyTo(outBytes, 0);
        uvBytes.CopyTo(outBytes, width * height);
        return outBytes;
    }

    // Copy a (tiled, GENERAL-layout) image's pixels to a tightly-packed CPU buffer via a host-visible staging
    // buffer. Vulkan detiles on the copy. bytesPerPixel covers R8 (1), R8G8 (2) and B8G8R8A8 (4).
    private byte[] CopyImageToCpu(VkImage image, int width, int height, int bytesPerPixel)
    {
        nuint size = (nuint)(width * height * bytesPerPixel);
        VkBufferCreateInfo bufInfo = new()
        {
            size = size,
            usage = VkBufferUsageFlags.TransferDst,
            sharingMode = VkSharingMode.Exclusive,
        };
        _api.vkCreateBuffer(&bufInfo, null, out VkBuffer buffer).CheckResult();
        _api.vkGetBufferMemoryRequirements(buffer, out VkMemoryRequirements reqs);
        VkMemoryAllocateInfo allocInfo = new()
        {
            allocationSize = reqs.size,
            memoryTypeIndex = _ctx.FindMemoryType(reqs.memoryTypeBits, VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent),
        };
        _api.vkAllocateMemory(&allocInfo, null, out VkDeviceMemory memory).CheckResult();
        _api.vkBindBufferMemory(buffer, memory, 0).CheckResult();

        _ctx.SubmitOneShot(cmd =>
        {
            // Source images are read as GENERAL (compute storage output, or freshly-imported dmabuf); GENERAL is
            // a valid source layout for vkCmdCopyImageToBuffer, so no explicit transition is needed.
            VkBufferImageCopy region = new()
            {
                imageSubresource = new VkImageSubresourceLayers(VkImageAspectFlags.Color, 0, 0, 1),
                imageExtent = new VkExtent3D((uint)width, (uint)height, 1),
            };
            _api.vkCmdCopyImageToBuffer(cmd, image, VkImageLayout.General, buffer, 1, &region);
        });

        byte[] outBytes = new byte[size];
        void* mapped;
        _api.vkMapMemory(memory, 0, size, 0, &mapped).CheckResult();
        new ReadOnlySpan<byte>(mapped, (int)size).CopyTo(outBytes);
        _api.vkUnmapMemory(memory);
        _api.vkDestroyBuffer(buffer, null);
        _api.vkFreeMemory(memory, null);
        return outBytes;
    }

    /// <summary>
    /// Create a W x H BGRA dmabuf-exportable image to serve as one slot of a republish pool, choosing from the
    /// supplied <paramref name="modifiers"/> (driver picks; read it back via <see cref="ExportedImage.Modifier"/>).
    /// The returned image owns a stable dmabuf fd (<see cref="ExportedImage.Fd"/>) plus its plane offset/row-pitch;
    /// hand those to the PipeWire <c>add_buffer</c> and reuse the image every <see cref="UnpackInto"/>. Free it
    /// with <see cref="DestroyExported"/> at teardown.
    /// </summary>
    public ExportedImage CreateOutputImage(int width, int height, ReadOnlySpan<ulong> modifiers)
        => CreateExportableImage((uint)width, (uint)height, VkImageUsageFlags.Storage, modifiers);

    /// <summary>
    /// Unpack a decoded 2W x H NV12 dmabuf surface (planes <paramref name="y"/> R8 + <paramref name="uv"/>
    /// R8G8, sharing <paramref name="modifier"/>) into the pre-existing <paramref name="dst"/> BGRA image - the
    /// pull-model variant of <see cref="Unpack"/> that writes into a pool slot PipeWire already imported, so no
    /// per-frame dmabuf is allocated. The two NV12 planes are imported and freed each call (cheap); only the
    /// reused <paramref name="dst"/> persists.
    /// </summary>
    public void UnpackInto(in Nv12Plane y, in Nv12Plane uv, int width, int height, ulong modifier, in ExportedImage dst)
    {
        ImportedImage yImg = ImportImage(y.Fd, R8Format, (uint)(width * 2), (uint)height, VkImageUsageFlags.Sampled, y.Offset, y.RowPitch, modifier);
        ImportedImage uvImg = ImportImage(uv.Fd, Rg8Format, (uint)width, (uint)(height / 2), VkImageUsageFlags.Sampled, uv.Offset, uv.RowPitch, modifier);

        VkDescriptorSet set = AllocateSet(_unpackNv12.SetLayout);
        WriteSampledImage(set, binding: 0, yImg.View);
        WriteSampledImage(set, binding: 1, uvImg.View);
        WriteStorageImage(set, binding: 2, dst.View);

        Dispatch(_unpackNv12, set, width, height, GroupCount((uint)width), GroupCount((uint)height));

        DestroyImported(yImg);
        DestroyImported(uvImg);
        _api.vkFreeDescriptorSets(_descriptorPool, 1, &set).CheckResult();
    }

    private ImportedImage ImportImage(int fd, VkFormat format, uint width, uint height, VkImageUsageFlags usage, ulong offset, ulong rowPitch, ulong modifier)
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
            format = format,
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
        // Importing a dmabuf transfers OWNERSHIP of the fd to Vulkan (it closes it when the memory is freed).
        // The staging surface's fd is stable and reused every frame, so we must dup() a fresh fd per import -
        // otherwise the first import consumes the surface's fd and every later import fails with
        // VK_ERROR_INVALID_EXTERNAL_HANDLE (and the VAAPI surface's own fd is left dangling).
        int dupFd = dup(fd);
        if (dupFd < 0)
        {
            _api.vkDestroyImage(image, null);
            throw new InvalidOperationException($"dup() failed for dmabuf fd {fd}.");
        }

        VkImportMemoryFdInfoKHR importInfo = new()
        {
            handleType = VkExternalMemoryHandleTypeFlags.DmaBufEXT,
            fd = dupFd,
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

        VkImageView view = CreateView(image, format);
        return new ImportedImage(image, memory, view);
    }

    private ExportedImage CreateExportableImage(uint width, uint height, VkImageUsageFlags usage, ReadOnlySpan<ulong> modifiers)
    {
        VkImage image;
        ReadOnlySpan<ulong> mods = modifiers.IsEmpty ? [VulkanComputeContext.DrmFormatModLinear] : modifiers;
        fixed (ulong* pMods = mods)
        {
            VkImageDrmFormatModifierListCreateInfoEXT modList = new()
            {
                drmFormatModifierCount = (uint)mods.Length,
                pDrmFormatModifiers = pMods,
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
            _api.vkCreateImage(&imageInfo, null, out image).CheckResult();
        }

        // Read back which modifier the driver actually chose, so we advertise the real one to PipeWire (and so
        // the consumer's EGL import matches). A tiled AMD modifier is what radeonsi's GL import accepts.
        VkImageDrmFormatModifierPropertiesEXT modProps = new();
        _api.vkGetImageDrmFormatModifierPropertiesEXT(image, &modProps).CheckResult();
        ulong chosenModifier = modProps.drmFormatModifier;

        _api.vkGetImageMemoryRequirements(image, out VkMemoryRequirements reqs);
        VkExportMemoryAllocateInfo exportInfo = new()
        {
            handleTypes = VkExternalMemoryHandleTypeFlags.DmaBufEXT,
        };
        VkMemoryAllocateInfo allocInfo = new()
        {
            pNext = &exportInfo,
            allocationSize = reqs.size,
            memoryTypeIndex = _ctx.FindMemoryType(reqs.memoryTypeBits, VkMemoryPropertyFlags.DeviceLocal),
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

        VkImageView view = CreateView(image, BgraFormat);
        return new ExportedImage(image, memory, view, fd, layout.offset, layout.rowPitch, chosenModifier);
    }

    private VkImageView CreateView(VkImage image, VkFormat format)
    {
        VkImageViewCreateInfo viewInfo = new()
        {
            image = image,
            viewType = VkImageViewType.Image2D,
            format = format,
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

    // Duplicate a file descriptor: Vulkan dmabuf import consumes the fd, so each import needs its own copy.
    [DllImport("libc", SetLastError = true)]
    private static extern int dup(int fd);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pack.Dispose(_api);
        _unpackBgra.Dispose(_api);
        _unpackNv12.Dispose(_api);
        _api.vkDestroyDescriptorPool(_descriptorPool, null);
        _api.vkDestroySampler(_sampler, null);
    }

    /// <summary>One plane of a decoded NV12 dmabuf surface: its fd plus the per-plane byte offset and row
    /// pitch. The two planes of a VAAPI export may share one fd at different offsets or use separate fds.</summary>
    internal readonly record struct Nv12Plane(int Fd, ulong Offset, ulong RowPitch);

    private readonly record struct ImportedImage(VkImage Image, VkDeviceMemory Memory, VkImageView View);

    internal readonly record struct ExportedImage(VkImage Image, VkDeviceMemory Memory, VkImageView View, int Fd, ulong Offset, ulong RowPitch, ulong Modifier);

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
