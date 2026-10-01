using System.Runtime.InteropServices;
using Agash.StreamTransport.Media;
using Microsoft.Win32.SafeHandles;
using Vortice.Vulkan;

namespace Agash.StreamTransport.Linux.Vulkan;

/// <summary>
/// One plane of a picture as a Vulkan image on DMA-BUF memory: imported from a producer's buffer, or
/// allocated here and exported for a consumer. Planar pictures are one image per plane; an exported
/// picture's planes share one DMA-BUF at their own offsets, as producers lay them out, since importers
/// such as VA-API map only pictures made of a single DMA-BUF object.
/// </summary>
internal sealed unsafe partial class VulkanImage : IDisposable
{
    private readonly VulkanEngine _engine;

    // An imported image owns its memory; an exported one shares its picture's allocation.
    private readonly VkDeviceMemory _memory;
    private readonly ExportedAllocation? _allocation;
    private int _disposed;

    private VulkanImage(
        VulkanEngine engine,
        VkImage image,
        VkDeviceMemory memory,
        ExportedAllocation? allocation,
        VkFormat format,
        int width,
        int height,
        DmaBufPlane plane,
        ulong modifier
    )
    {
        _engine = engine;
        Image = image;
        _memory = memory;
        _allocation = allocation;
        Format = format;
        Width = width;
        Height = height;
        Plane = plane;
        Modifier = modifier;
    }

    public VkImage Image { get; }

    public VkFormat Format { get; }

    public int Width { get; }

    public int Height { get; }

    /// <summary>The DMA-BUF this image's picture exports; null for an imported image.</summary>
    public SafeFileHandle? Exported => _allocation?.Handle;

    /// <summary>Where the plane lies in its DMA-BUF.</summary>
    public DmaBufPlane Plane { get; }

    public ulong Modifier { get; }

    /// <summary>
    /// Imports a plane of a producer's DMA-BUF. The descriptor is duplicated: Vulkan takes ownership of
    /// what it imports, and the producer keeps its own.
    /// </summary>
    /// <param name="engine">The GPU.</param>
    /// <param name="plane">The plane.</param>
    /// <param name="modifier">The buffer's DRM format modifier.</param>
    /// <param name="format">How the shaders see the plane's texels.</param>
    /// <param name="width">The plane's width in texels.</param>
    /// <param name="height">The plane's height in texels.</param>
    /// <param name="usage">What the image is used for.</param>
    /// <returns>The image.</returns>
    public static VulkanImage Import(
        VulkanEngine engine,
        DmaBufPlane plane,
        ulong modifier,
        VkFormat format,
        int width,
        int height,
        VkImageUsageFlags usage
    )
    {
        VkDeviceApi api = engine.Api;
        VkSubresourceLayout layout = new()
        {
            offset = (ulong)plane.Offset,
            rowPitch = (ulong)plane.Stride,
        };
        VkImageDrmFormatModifierExplicitCreateInfoEXT explicitModifier = new()
        {
            drmFormatModifier = modifier,
            drmFormatModifierPlaneCount = 1,
            pPlaneLayouts = &layout,
        };
        VkExternalMemoryImageCreateInfo external = new()
        {
            pNext = &explicitModifier,
            handleTypes = VkExternalMemoryHandleTypeFlags.DmaBufEXT,
        };
        VkImage image = CreateImage(api, &external, format, width, height, usage);
        try
        {
            int fd = Dup(plane.Fd);
            VkMemoryFdPropertiesKHR fdProperties = new();
            api.vkGetMemoryFdPropertiesKHR(
                    VkExternalMemoryHandleTypeFlags.DmaBufEXT,
                    fd,
                    &fdProperties
                )
                .CheckResult();
            VkMemoryRequirements requirements;
            api.vkGetImageMemoryRequirements(image, &requirements);
            VkMemoryDedicatedAllocateInfo dedicated = new() { image = image };
            VkImportMemoryFdInfoKHR import = new()
            {
                pNext = &dedicated,
                handleType = VkExternalMemoryHandleTypeFlags.DmaBufEXT,
                fd = fd,
            };
            VkMemoryAllocateInfo allocate = new()
            {
                pNext = &import,
                allocationSize = requirements.size,
                memoryTypeIndex = engine.MemoryType(
                    requirements.memoryTypeBits & fdProperties.memoryTypeBits,
                    0
                ),
            };
            VkDeviceMemory memory;
            VkResult allocated = api.vkAllocateMemory(&allocate, null, &memory);
            if (allocated != VkResult.Success)
            {
                // Vulkan took the descriptor only if the import succeeded.
                _ = Close(fd);
                allocated.CheckResult();
            }

            api.vkBindImageMemory(image, memory, 0).CheckResult();
            return new VulkanImage(
                engine,
                image,
                memory,
                null,
                format,
                width,
                height,
                plane,
                modifier
            );
        }
        catch
        {
            api.vkDestroyImage(image, null);
            throw;
        }
    }

    /// <summary>
    /// Allocates a picture's planes as linear images in one exported DMA-BUF, each at its own offset,
    /// and exports it once: every plane names the same descriptor.
    /// </summary>
    /// <param name="engine">The GPU.</param>
    /// <param name="planes">Each plane's texel format and size.</param>
    /// <param name="usage">How the images are used here.</param>
    /// <returns>The planes' images, which share the allocation until the last is disposed.</returns>
    public static VulkanImage[] ExportPicture(
        VulkanEngine engine,
        ReadOnlySpan<(VkFormat Format, int Width, int Height)> planes,
        VkImageUsageFlags usage
    )
    {
        VkDeviceApi api = engine.Api;
        ulong linear = VulkanEngine.LinearModifier;
        VkImageDrmFormatModifierListCreateInfoEXT modifiers = new()
        {
            drmFormatModifierCount = 1,
            pDrmFormatModifiers = &linear,
        };
        VkExternalMemoryImageCreateInfo external = new()
        {
            pNext = &modifiers,
            handleTypes = VkExternalMemoryHandleTypeFlags.DmaBufEXT,
        };

        var images = new VkImage[planes.Length];
        ulong[] offsets = new ulong[planes.Length];
        VkDeviceMemory memory = default;
        try
        {
            // The planes laid end to end, each at its own alignment, in memory every plane can use.
            ulong size = 0;
            uint memoryTypes = uint.MaxValue;
            for (int i = 0; i < planes.Length; i++)
            {
                images[i] = CreateImage(
                    api,
                    &external,
                    planes[i].Format,
                    planes[i].Width,
                    planes[i].Height,
                    usage
                );
                VkMemoryRequirements requirements;
                api.vkGetImageMemoryRequirements(images[i], &requirements);
                ulong alignment = Math.Max(requirements.alignment, 1);
                offsets[i] = (size + alignment - 1) / alignment * alignment;
                size = offsets[i] + requirements.size;
                memoryTypes &= requirements.memoryTypeBits;
            }

            // One image alone takes a dedicated allocation, which some drivers want for export.
            VkMemoryDedicatedAllocateInfo dedicated = new() { image = images[0] };
            VkExportMemoryAllocateInfo export = new()
            {
                pNext = planes.Length == 1 ? &dedicated : null,
                handleTypes = VkExternalMemoryHandleTypeFlags.DmaBufEXT,
            };
            VkMemoryAllocateInfo allocate = new()
            {
                pNext = &export,
                allocationSize = size,
                memoryTypeIndex = engine.MemoryType(memoryTypes, VkMemoryPropertyFlags.DeviceLocal),
            };
            api.vkAllocateMemory(&allocate, null, &memory).CheckResult();
            for (int i = 0; i < planes.Length; i++)
            {
                api.vkBindImageMemory(images[i], memory, offsets[i]).CheckResult();
            }

            VkMemoryGetFdInfoKHR getFd = new()
            {
                memory = memory,
                handleType = VkExternalMemoryHandleTypeFlags.DmaBufEXT,
            };
            int fd;
            api.vkGetMemoryFdKHR(&getFd, &fd).CheckResult();
            ExportedAllocation allocation = new(
                engine,
                memory,
                new SafeFileHandle(fd, ownsHandle: true),
                planes.Length
            );

            var result = new VulkanImage[planes.Length];
            for (int i = 0; i < planes.Length; i++)
            {
                VkImageSubresource subresource = new()
                {
                    aspectMask = VkImageAspectFlags.MemoryPlane0EXT,
                };
                VkSubresourceLayout layout;
                api.vkGetImageSubresourceLayout(images[i], &subresource, &layout);
                result[i] = new VulkanImage(
                    engine,
                    images[i],
                    memory,
                    allocation,
                    planes[i].Format,
                    planes[i].Width,
                    planes[i].Height,
                    new DmaBufPlane(fd, (int)(offsets[i] + layout.offset), (int)layout.rowPitch),
                    VulkanEngine.LinearModifier
                );
            }

            return result;
        }
        catch
        {
            if (memory != VkDeviceMemory.Null)
            {
                api.vkFreeMemory(memory, null);
            }

            foreach (VkImage image in images)
            {
                if (image != VkImage.Null)
                {
                    api.vkDestroyImage(image, null);
                }
            }

            throw;
        }
    }

    // The memory and descriptor a picture's exported planes share, freed when the last plane goes.
    private sealed class ExportedAllocation(
        VulkanEngine engine,
        VkDeviceMemory memory,
        SafeFileHandle handle,
        int planes
    )
    {
        private int _planes = planes;

        public SafeFileHandle Handle { get; } = handle;

        public void Release()
        {
            if (Interlocked.Decrement(ref _planes) == 0)
            {
                engine.Api.vkFreeMemory(memory, null);
                Handle.Dispose();
            }
        }
    }

    /// <summary>A view of the image in a format of the same texel size.</summary>
    public VkImageView View(VkFormat format)
    {
        VkImageViewCreateInfo info = new()
        {
            image = Image,
            viewType = VkImageViewType.Image2D,
            format = format,
            subresourceRange = new VkImageSubresourceRange(VkImageAspectFlags.Color, 0, 1, 0, 1),
        };
        VkImageView view;
        _engine.Api.vkCreateImageView(&info, null, &view).CheckResult();
        return view;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _engine.Api.vkDestroyImage(Image, null);
            if (_allocation is { } allocation)
            {
                allocation.Release();
            }
            else
            {
                _engine.Api.vkFreeMemory(_memory, null);
            }
        }
    }

    private static VkImage CreateImage(
        VkDeviceApi api,
        void* next,
        VkFormat format,
        int width,
        int height,
        VkImageUsageFlags usage
    )
    {
        // An 8-bit RGB image also takes a view with red and blue swapped, which is how a BGRA image
        // is written: through an RGBA storage view, since BGRA storage needs a feature not every GPU
        // has. With DRM-modifier tiling the other view formats have to be listed.
        VkFormat other = format switch
        {
            VkFormat.B8G8R8A8Unorm => VkFormat.R8G8B8A8Unorm,
            VkFormat.R8G8B8A8Unorm => VkFormat.B8G8R8A8Unorm,
            _ => VkFormat.Undefined,
        };
        VkFormat* formats = stackalloc VkFormat[2] { format, other };
        VkImageFormatListCreateInfo list = new()
        {
            pNext = next,
            viewFormatCount = 2,
            pViewFormats = formats,
        };
        bool mutable = other != VkFormat.Undefined;
        VkImageCreateInfo info = new()
        {
            pNext = mutable ? &list : next,
            flags = mutable ? VkImageCreateFlags.MutableFormat : 0,
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
        VkImage image;
        api.vkCreateImage(&info, null, &image).CheckResult();
        return image;
    }

    private static int Dup(int fd)
    {
        int copy = DupNative(fd);
        return copy >= 0
            ? copy
            : throw new IOException(
                $"The DMA-BUF could not be duplicated (errno {Marshal.GetLastPInvokeError()})."
            );
    }

    [LibraryImport("libc", EntryPoint = "dup", SetLastError = true)]
    private static partial int DupNative(int fd);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int Close(int fd);
}
