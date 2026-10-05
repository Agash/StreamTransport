using Agash.StreamTransport.Media;
using Vortice.Vulkan;

namespace Agash.StreamTransport.Linux.Vulkan;

/// <summary>
/// Copies between memory and DMA-BUF images through host-visible staging buffers: how a picture from
/// memory reaches a GPU consumer, and how a GPU picture is read back. DMA-BUFs in device memory need not
/// be mappable, so a copy on the GPU is the one way in and out that works on every device.
/// </summary>
internal static unsafe class VulkanTransfer
{
    /// <summary>Writes rows from memory into an image.</summary>
    /// <param name="engine">The GPU.</param>
    /// <param name="image">The image.</param>
    /// <param name="rows">The rows, <paramref name="stride"/> bytes apart.</param>
    /// <param name="stride">Bytes from one row to the next in <paramref name="rows"/>.</param>
    /// <param name="bytesPerTexel">The image's texel size.</param>
    public static void Upload(
        VulkanEngine engine,
        VulkanImage image,
        ReadOnlySpan<byte> rows,
        int stride,
        int bytesPerTexel
    )
    {
        int rowBytes = image.Width * bytesPerTexel;
        using Staging staging = new(engine, rowBytes * image.Height);
        Span<byte> mapped = staging.Bytes;
        for (int row = 0; row < image.Height; row++)
        {
            rows.Slice(row * stride, rowBytes).CopyTo(mapped[(row * rowBytes)..]);
        }

        VkBufferImageCopy region = Region(image);
        using VulkanEngine.Batch batch = engine.Begin();
        Recording.Acquire(engine, batch.Commands, [image]);
        engine.Api.vkCmdCopyBufferToImage(
            batch.Commands,
            staging.Buffer,
            image.Image,
            VkImageLayout.General,
            1,
            &region
        );
        Recording.Release(engine, batch.Commands, [image]);
        batch.Submit();
    }

    /// <summary>Reads an image's rows, packed tightly.</summary>
    /// <param name="engine">The GPU.</param>
    /// <param name="image">The image.</param>
    /// <param name="bytesPerTexel">The image's texel size.</param>
    /// <returns>The rows.</returns>
    public static byte[] Download(VulkanEngine engine, VulkanImage image, int bytesPerTexel)
    {
        int size = image.Width * bytesPerTexel * image.Height;
        using Staging staging = new(engine, size);
        VkBufferImageCopy region = Region(image);
        using (VulkanEngine.Batch batch = engine.Begin())
        {
            Recording.Acquire(engine, batch.Commands, [image]);
            engine.Api.vkCmdCopyImageToBuffer(
                batch.Commands,
                image.Image,
                VkImageLayout.General,
                staging.Buffer,
                1,
                &region
            );
            Recording.Release(engine, batch.Commands, [image]);
            batch.Submit();
        }

        return staging.Bytes.ToArray();
    }

    /// <summary>
    /// Copies a DMA-BUF picture, plane by plane, into images of the same format and size on the GPU,
    /// and waits until the copy has finished: what keeps a frame a producer is about to reuse, and
    /// what fills a consumer's buffer.
    /// </summary>
    /// <param name="engine">The GPU.</param>
    /// <param name="source">The picture.</param>
    /// <param name="format">Its pixel format: BGRA, RGBA or NV12.</param>
    /// <param name="width">Its width.</param>
    /// <param name="height">Its height.</param>
    /// <param name="destination">One image per plane.</param>
    public static void Copy(
        VulkanEngine engine,
        in DmaBufImage source,
        PixelFormat format,
        int width,
        int height,
        ReadOnlySpan<VulkanImage> destination
    )
    {
        int planes = format == PixelFormat.Nv12 ? 2 : 1;
        var leases = new ImportCache.Lease[planes];
        int held = 0;
        try
        {
            for (int plane = 0; plane < planes; plane++)
            {
                DmaBufPlane from =
                    plane < source.PlaneCount
                        ? source[plane]
                        : source[0] with
                        {
                            Offset = source[0].Offset + (source[0].Stride * height),
                        };
                leases[plane] = engine.Imports.Import(
                    from,
                    source.Modifier,
                    destination[plane].Format,
                    destination[plane].Width,
                    destination[plane].Height,
                    VkImageUsageFlags.TransferSrc
                );
                held++;
            }

            VulkanImage[] imported = [.. leases.Select(lease => lease.Image)];
            using VulkanEngine.Batch batch = engine.Begin();
            Recording.Acquire(engine, batch.Commands, imported);
            Recording.Acquire(engine, batch.Commands, destination);
            for (int plane = 0; plane < planes; plane++)
            {
                VkImageCopy region = new()
                {
                    srcSubresource = new VkImageSubresourceLayers(imported[plane].Aspect, 0, 0, 1),
                    dstSubresource = new VkImageSubresourceLayers(
                        destination[plane].Aspect,
                        0,
                        0,
                        1
                    ),
                    extent = new VkExtent3D(destination[plane].Width, destination[plane].Height, 1),
                };
                engine.Api.vkCmdCopyImage(
                    batch.Commands,
                    imported[plane].Image,
                    VkImageLayout.General,
                    destination[plane].Image,
                    VkImageLayout.General,
                    1,
                    &region
                );
            }

            Recording.Release(engine, batch.Commands, destination);
            batch.Submit();
        }
        finally
        {
            for (int i = 0; i < held; i++)
            {
                leases[i].Dispose();
            }
        }
    }

    /// <summary>
    /// Reads a DMA-BUF picture's planes into memory, rows packed tightly: for a consumer that can only
    /// take frames in memory.
    /// </summary>
    /// <param name="engine">The GPU.</param>
    /// <param name="source">The picture.</param>
    /// <param name="format">Its pixel format: BGRA, RGBA or NV12.</param>
    /// <param name="width">Its width.</param>
    /// <param name="height">Its height.</param>
    /// <returns>One array per plane.</returns>
    public static byte[][] Read(
        VulkanEngine engine,
        in DmaBufImage source,
        PixelFormat format,
        int width,
        int height
    )
    {
        int planes = format == PixelFormat.Nv12 ? 2 : 1;
        byte[][] bytes = new byte[planes][];
        for (int plane = 0; plane < planes; plane++)
        {
            (VkFormat vk, int w, int h, int texel) = (format, plane) switch
            {
                (PixelFormat.Nv12, 0) => (VkFormat.R8Unorm, width, height, 1),
                (PixelFormat.Nv12, _) => (VkFormat.R8G8Unorm, width / 2, height / 2, 2),
                (PixelFormat.Rgba, _) => (VkFormat.R8G8B8A8Unorm, width, height, 4),
                _ => (VkFormat.B8G8R8A8Unorm, width, height, 4),
            };
            DmaBufPlane from =
                plane < source.PlaneCount
                    ? source[plane]
                    : source[0] with
                    {
                        Offset = source[0].Offset + (source[0].Stride * height),
                    };
            using ImportCache.Lease imported = engine.Imports.Import(
                from,
                source.Modifier,
                vk,
                w,
                h,
                VkImageUsageFlags.TransferSrc
            );
            bytes[plane] = Download(engine, imported.Image, texel);
        }

        return bytes;
    }

    private static VkBufferImageCopy Region(VulkanImage image) =>
        new()
        {
            imageSubresource = new VkImageSubresourceLayers(image.Aspect, 0, 0, 1),
            imageExtent = new VkExtent3D(image.Width, image.Height, 1),
        };

    // A host-visible, coherent buffer, mapped for its lifetime.
    private sealed class Staging : IDisposable
    {
        private readonly VulkanEngine _engine;
        private readonly VkDeviceMemory _memory;
        private readonly void* _mapped;
        private readonly int _size;

        public Staging(VulkanEngine engine, int size)
        {
            _engine = engine;
            _size = size;
            VkDeviceApi api = engine.Api;
            VkBufferCreateInfo info = new()
            {
                size = (ulong)size,
                usage = VkBufferUsageFlags.TransferSrc | VkBufferUsageFlags.TransferDst,
                sharingMode = VkSharingMode.Exclusive,
            };
            VkBuffer buffer;
            api.vkCreateBuffer(&info, null, &buffer).CheckResult();
            Buffer = buffer;
            VkMemoryRequirements requirements;
            api.vkGetBufferMemoryRequirements(buffer, &requirements);
            VkMemoryAllocateInfo allocate = new()
            {
                allocationSize = requirements.size,
                memoryTypeIndex = engine.MemoryType(
                    requirements.memoryTypeBits,
                    VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent
                ),
            };
            VkDeviceMemory memory;
            api.vkAllocateMemory(&allocate, null, &memory).CheckResult();
            _memory = memory;
            api.vkBindBufferMemory(buffer, memory, 0).CheckResult();
            void* mapped;
            api.vkMapMemory(memory, 0, (ulong)size, 0, &mapped).CheckResult();
            _mapped = mapped;
        }

        public VkBuffer Buffer { get; }

        public Span<byte> Bytes => new(_mapped, _size);

        public void Dispose()
        {
            _engine.Api.vkUnmapMemory(_memory);
            _engine.Api.vkDestroyBuffer(Buffer, null);
            _engine.Api.vkFreeMemory(_memory, null);
        }
    }
}
