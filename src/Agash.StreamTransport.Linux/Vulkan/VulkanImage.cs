using System.Runtime.InteropServices;
using Agash.StreamTransport.Media;
using Microsoft.Win32.SafeHandles;
using Vortice.Vulkan;

namespace Agash.StreamTransport.Linux.Vulkan;

/// <summary>
/// One plane of a picture as a Vulkan image on DMA-BUF memory: imported from a producer's buffer, or
/// allocated here and exported for a consumer. An imported plane is an image of its own. An exported
/// picture is one image on one dedicated allocation, exported once, whose planes are the image's plane
/// aspects: importers such as VA-API and Vulkan Video take pictures made of a single DMA-BUF object, and
/// drivers want exported images on memory of their own.
/// </summary>
internal sealed unsafe partial class VulkanImage : IDisposable
{
    private readonly VulkanEngine _engine;
    private readonly SharedImage _shared;
    private readonly List<(VkFormat Format, VkImageView View)> _views = [];
    private readonly Lock _viewsGate = new();
    private int _disposed;

    private VulkanImage(
        VulkanEngine engine,
        SharedImage shared,
        VkImageAspectFlags aspect,
        VkFormat format,
        int width,
        int height,
        DmaBufPlane plane,
        ulong modifier
    )
    {
        _engine = engine;
        _shared = shared;
        Aspect = aspect;
        Format = format;
        Width = width;
        Height = height;
        Plane = plane;
        Modifier = modifier;
    }

    /// <summary>The image the plane is in: the plane's own, or its picture's.</summary>
    public VkImage Image => _shared.Image;

    /// <summary>The plane in the image: the color aspect, or one plane of a multi-planar picture.</summary>
    public VkImageAspectFlags Aspect { get; }

    /// <summary>How the shaders see the plane's texels.</summary>
    public VkFormat Format { get; }

    public int Width { get; }

    public int Height { get; }

    /// <summary>The DMA-BUF this image's picture exports; null for an imported image.</summary>
    public SafeFileHandle? Exported => _shared.Exported;

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

            VkResult bound = api.vkBindImageMemory(image, memory, 0);
            if (bound != VkResult.Success)
            {
                api.vkFreeMemory(memory, null);
                bound.CheckResult();
            }

            SharedImage shared = new(engine, image, memory, exported: null, planes: 1);
            image = VkImage.Null;
            return new VulkanImage(
                engine,
                shared,
                VkImageAspectFlags.Color,
                format,
                width,
                height,
                plane,
                modifier
            );
        }
        finally
        {
            if (image != VkImage.Null)
            {
                api.vkDestroyImage(image, null);
            }
        }
    }

    /// <summary>
    /// Allocates a picture as one linear image on a dedicated allocation and exports it once: a single
    /// plane, or the planes of a multi-planar format, each at the offset the driver lays it at. Every
    /// plane names the same descriptor.
    /// </summary>
    /// <param name="engine">The GPU.</param>
    /// <param name="planes">Each plane's texel format and size, as the shaders see it.</param>
    /// <param name="usage">How the planes are used here.</param>
    /// <returns>The planes, which share the image until the last is disposed.</returns>
    public static VulkanImage[] ExportPicture(
        VulkanEngine engine,
        ReadOnlySpan<(VkFormat Format, int Width, int Height)> planes,
        VkImageUsageFlags usage
    )
    {
        VkDeviceApi api = engine.Api;
        VkFormat format = planes.Length == 1 ? planes[0].Format : MultiPlanar(planes);
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

        // A multi-planar picture is written plane by plane through views in each plane's own format.
        VkFormat* viewFormats = stackalloc VkFormat[planes.Length + 1];
        viewFormats[0] = format;
        for (int i = 0; i < planes.Length; i++)
        {
            viewFormats[i + 1] = planes[i].Format;
        }

        VkImage image =
            planes.Length == 1
                ? CreateImage(api, &external, format, planes[0].Width, planes[0].Height, usage)
                : CreatePlanarImage(
                    api,
                    &external,
                    format,
                    new ReadOnlySpan<VkFormat>(viewFormats, planes.Length + 1),
                    planes[0].Width,
                    planes[0].Height,
                    usage
                );
        VkDeviceMemory memory = default;
        SharedImage? shared = null;
        try
        {
            VkImageMemoryRequirementsInfo2 requirementsInfo = new() { image = image };
            VkMemoryRequirements2 requirements = new();
            api.vkGetImageMemoryRequirements2(&requirementsInfo, &requirements);
            VkMemoryDedicatedAllocateInfo dedicated = new() { image = image };
            VkExportMemoryAllocateInfo export = new()
            {
                pNext = &dedicated,
                handleTypes = VkExternalMemoryHandleTypeFlags.DmaBufEXT,
            };
            VkMemoryAllocateInfo allocate = new()
            {
                pNext = &export,
                allocationSize = requirements.memoryRequirements.size,
                memoryTypeIndex = engine.MemoryType(
                    requirements.memoryRequirements.memoryTypeBits,
                    VkMemoryPropertyFlags.DeviceLocal
                ),
            };
            api.vkAllocateMemory(&allocate, null, &memory).CheckResult();
            api.vkBindImageMemory(image, memory, 0).CheckResult();

            VkMemoryGetFdInfoKHR getFd = new()
            {
                memory = memory,
                handleType = VkExternalMemoryHandleTypeFlags.DmaBufEXT,
            };
            int fd;
            api.vkGetMemoryFdKHR(&getFd, &fd).CheckResult();
            shared = new SharedImage(
                engine,
                image,
                memory,
                new SafeFileHandle(fd, ownsHandle: true),
                planes.Length
            );
            image = VkImage.Null;
            memory = VkDeviceMemory.Null;

            var result = new VulkanImage[planes.Length];
            for (int i = 0; i < planes.Length; i++)
            {
                VkImageSubresource subresource = new() { aspectMask = MemoryPlane(i) };
                VkSubresourceLayout layout;
                api.vkGetImageSubresourceLayout(shared.Image, &subresource, &layout);
                result[i] = new VulkanImage(
                    engine,
                    shared,
                    planes.Length == 1 ? VkImageAspectFlags.Color : PlaneAspect(i),
                    planes[i].Format,
                    planes[i].Width,
                    planes[i].Height,
                    new DmaBufPlane(fd, (int)layout.offset, (int)layout.rowPitch),
                    VulkanEngine.LinearModifier
                );
            }

            return result;
        }
        finally
        {
            if (memory != VkDeviceMemory.Null)
            {
                api.vkFreeMemory(memory, null);
            }

            if (image != VkImage.Null)
            {
                api.vkDestroyImage(image, null);
            }
        }
    }

    /// <summary>
    /// A view of the plane in a format of the same texel size, made on first use and kept with the
    /// image: pooled and imported planes come back every few frames, each read through one or two
    /// formats.
    /// </summary>
    public VkImageView View(VkFormat format)
    {
        lock (_viewsGate)
        {
            foreach ((VkFormat made, VkImageView kept) in _views)
            {
                if (made == format)
                {
                    return kept;
                }
            }

            VkImageViewCreateInfo info = new()
            {
                image = Image,
                viewType = VkImageViewType.Image2D,
                format = format,
                subresourceRange = new VkImageSubresourceRange(Aspect, 0, 1, 0, 1),
            };
            VkImageView view;
            _engine.Api.vkCreateImageView(&info, null, &view).CheckResult();
            _views.Add((format, view));
            return view;
        }
    }

    // Called once the GPU is done with the image, like the image's own destruction.
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            lock (_viewsGate)
            {
                foreach ((_, VkImageView view) in _views)
                {
                    _engine.Api.vkDestroyImageView(view, null);
                }

                _views.Clear();
            }

            _shared.Release();
        }
    }

    // The multi-planar format whose planes are these: luma, then chroma at half size in both directions.
    private static VkFormat MultiPlanar(
        ReadOnlySpan<(VkFormat Format, int Width, int Height)> planes
    ) =>
        planes switch
        {
            [(VkFormat.R8Unorm, _, _), (VkFormat.R8G8Unorm, _, _)] => VkFormat.G8B8R82Plane420Unorm,
            [(VkFormat.R16Unorm, _, _), (VkFormat.R16G16Unorm, _, _)] =>
                VkFormat.G16B16R162Plane420Unorm,
            [(VkFormat.R8Unorm, _, _), (VkFormat.R8Unorm, _, _), (VkFormat.R8Unorm, _, _)] =>
                VkFormat.G8B8R83Plane420Unorm,
            _ => throw new NotSupportedException(
                $"No multi-planar format has the planes {string.Join(", ", planes.ToArray().Select(p => p.Format))}."
            ),
        };

    private static VkImageAspectFlags PlaneAspect(int plane) =>
        plane switch
        {
            0 => VkImageAspectFlags.Plane0,
            1 => VkImageAspectFlags.Plane1,
            _ => VkImageAspectFlags.Plane2,
        };

    private static VkImageAspectFlags MemoryPlane(int plane) =>
        plane switch
        {
            0 => VkImageAspectFlags.MemoryPlane0EXT,
            1 => VkImageAspectFlags.MemoryPlane1EXT,
            _ => VkImageAspectFlags.MemoryPlane2EXT,
        };

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
        return Create(
            api,
            mutable ? &list : next,
            mutable ? VkImageCreateFlags.MutableFormat : 0,
            format,
            width,
            height,
            usage
        );
    }

    // A multi-planar image whose planes take views in their own formats, for storage writes the
    // multi-planar format itself does not support.
    private static VkImage CreatePlanarImage(
        VkDeviceApi api,
        void* next,
        VkFormat format,
        ReadOnlySpan<VkFormat> viewFormats,
        int width,
        int height,
        VkImageUsageFlags usage
    )
    {
        fixed (VkFormat* formats = viewFormats)
        {
            VkImageFormatListCreateInfo list = new()
            {
                pNext = next,
                viewFormatCount = (uint)viewFormats.Length,
                pViewFormats = formats,
            };
            return Create(
                api,
                &list,
                VkImageCreateFlags.MutableFormat | VkImageCreateFlags.ExtendedUsage,
                format,
                width,
                height,
                usage
            );
        }
    }

    private static VkImage Create(
        VkDeviceApi api,
        void* next,
        VkImageCreateFlags flags,
        VkFormat format,
        int width,
        int height,
        VkImageUsageFlags usage
    )
    {
        VkImageCreateInfo info = new()
        {
            pNext = next,
            flags = flags,
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

    // An image, its memory, and the descriptor it exports, shared by its planes and destroyed when the
    // last of them goes.
    private sealed class SharedImage(
        VulkanEngine engine,
        VkImage image,
        VkDeviceMemory memory,
        SafeFileHandle? exported,
        int planes
    )
    {
        private int _planes = planes;

        public VkImage Image { get; } = image;

        public SafeFileHandle? Exported { get; } = exported;

        public void Release()
        {
            if (Interlocked.Decrement(ref _planes) == 0)
            {
                engine.Api.vkDestroyImage(Image, null);
                engine.Api.vkFreeMemory(memory, null);
                Exported?.Dispose();
            }
        }
    }
}
