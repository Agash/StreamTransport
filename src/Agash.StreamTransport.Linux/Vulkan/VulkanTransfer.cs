using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Agash.StreamTransport.Media;
using Vortice.Vulkan;

namespace Agash.StreamTransport.Linux.Vulkan;

/// <summary>
/// Copies between memory and DMA-BUF images through host-visible staging buffers: how a picture from
/// memory reaches a GPU consumer, and how a GPU picture is read back. DMA-BUFs in device memory need not
/// be mappable, so a copy on the GPU is the one way in and out that works on every device. Each waits on
/// the GPU for the picture's producer and returns once its copy has run, for its caller reads or
/// publishes the result straight away.
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
        batch.SubmitAndWait();
    }

    /// <summary>
    /// Copies a DMA-BUF picture, plane by plane, into images of the same format and size on the GPU:
    /// what fills a consumer's buffer. With <paramref name="signal"/> the copy signals that point when it
    /// has run and nothing waits; without, this waits until the copy has finished.
    /// </summary>
    /// <param name="engine">The GPU.</param>
    /// <param name="source">The picture.</param>
    /// <param name="format">Its pixel format: BGRA, RGBA or NV12.</param>
    /// <param name="width">Its width.</param>
    /// <param name="height">Its height.</param>
    /// <param name="destination">One image per plane.</param>
    /// <param name="signal">The destination's acquire point, when its consumer waits on one.</param>
    /// <param name="keep">
    /// What holds the source picture, let go once the copy has run; with <paramref name="signal"/>
    /// nothing waits, so the source must stay readable until then.
    /// </param>
    public static void Copy(
        VulkanEngine engine,
        in DmaBufImage source,
        PixelFormat format,
        int width,
        int height,
        ReadOnlySpan<VulkanImage> destination,
        DrmSyncTimeline? signal = null,
        IDisposable? keep = null
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
            if (!batch.WaitFor(in source))
            {
                throw new TimeoutException("The producer's GPU did not finish writing the frame.");
            }

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
            if (signal is { } ready)
            {
                // The consumer waits for the copy on its GPU: nothing waits here, and the sources stay
                // imported until the copy has run.
                batch.Signal(in ready);
                foreach (ImportCache.Lease lease in leases[..held])
                {
                    batch.Then(in lease);
                }

                held = 0;
                if (keep is not null)
                {
                    batch.Then(keep);
                }
                _ = batch.Submit();
            }
            else
            {
                batch.SubmitAndWait();
            }
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
    /// Reads a DMA-BUF picture back into memory, its planes one after another at
    /// <paramref name="stride"/> (the chroma plane at the stride its width scales it to), through
    /// <paramref name="readback"/>'s staging buffer: what a consumer reading memory is handed.
    /// </summary>
    /// <param name="engine">The GPU.</param>
    /// <param name="readback">The staging kept between pictures.</param>
    /// <param name="source">The picture.</param>
    /// <param name="format">Its pixel format: BGRA, RGBA or NV12.</param>
    /// <param name="width">Its width.</param>
    /// <param name="height">Its height.</param>
    /// <param name="destination">Where the planes go.</param>
    /// <param name="stride">Bytes per row of the first plane in <paramref name="destination"/>.</param>
    /// <exception cref="TimeoutException">The producer's GPU did not finish the picture.</exception>
    public static void Read(
        VulkanEngine engine,
        Readback readback,
        in DmaBufImage source,
        PixelFormat format,
        int width,
        int height,
        Span<byte> destination,
        int stride
    )
    {
        ArgumentNullException.ThrowIfNull(readback);
        bool nv12 = format == PixelFormat.Nv12;
        int planes = nv12 ? 2 : 1;
        Span<(VkFormat Format, int Width, int Height, int Texel)> layout =
        [
            nv12 ? (VkFormat.R8Unorm, width, height, 1)
            : format == PixelFormat.Rgba ? (VkFormat.R8G8B8A8Unorm, width, height, 4)
            : (VkFormat.B8G8R8A8Unorm, width, height, 4),
            (VkFormat.R8G8Unorm, width / 2, height / 2, 2),
        ];
        int packed = 0;
        for (int plane = 0; plane < planes; plane++)
        {
            packed += layout[plane].Width * layout[plane].Texel * layout[plane].Height;
        }

        using ImportCache.Lease first = Import(engine, in source, 0, layout[0], height);
        using ImportCache.Lease second = nv12
            ? Import(engine, in source, 1, layout[1], height)
            : default;
        int chromaStride = stride * layout[1].Width * layout[1].Texel / (width * layout[0].Texel);

        // Straight into the destination where the GPU imports it, rows at the destination's strides;
        // pinned for the copy, which has finished when the import is freed.
        fixed (byte* target = destination)
        {
            if (
                HostImport.TryCreate(engine, target, destination.Length, out HostImport direct)
                && stride % layout[0].Texel == 0
                && chromaStride % layout[1].Texel == 0
            )
            {
                using (direct)
                using (VulkanEngine.Batch batch = engine.Begin())
                {
                    if (!batch.WaitFor(in source))
                    {
                        throw new TimeoutException(
                            "The producer's GPU did not finish writing the frame."
                        );
                    }

                    ulong at = 0;
                    for (int plane = 0; plane < planes; plane++)
                    {
                        VulkanImage image = plane == 0 ? first.Image : second.Image;
                        int rowStride = plane == 0 ? stride : chromaStride;
                        VkBufferImageCopy region = Region(image) with
                        {
                            bufferOffset = at,
                            bufferRowLength = (uint)(rowStride / layout[plane].Texel),
                        };
                        Recording.Acquire(engine, batch.Commands, [image]);
                        engine.Api.vkCmdCopyImageToBuffer(
                            batch.Commands,
                            image.Image,
                            VkImageLayout.General,
                            direct.Buffer,
                            1,
                            &region
                        );
                        Recording.Release(engine, batch.Commands, [image]);
                        at += (ulong)(rowStride * layout[plane].Height);
                    }

                    batch.SubmitAndWait();
                }

                return;
            }
        }

        Staging staging = readback.Reserve(engine, packed);
        using (VulkanEngine.Batch batch = engine.Begin())
        {
            if (!batch.WaitFor(in source))
            {
                throw new TimeoutException("The producer's GPU did not finish writing the frame.");
            }

            ulong offset = 0;
            for (int plane = 0; plane < planes; plane++)
            {
                VulkanImage image = plane == 0 ? first.Image : second.Image;
                VkBufferImageCopy region = Region(image) with { bufferOffset = offset };
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
                offset += (ulong)(image.Width * layout[plane].Texel * image.Height);
            }

            batch.SubmitAndWait();
        }

        ReadOnlySpan<byte> bytes = staging.Bytes;
        int from = 0;
        int to = 0;
        for (int plane = 0; plane < planes; plane++)
        {
            int rowBytes = layout[plane].Width * layout[plane].Texel;
            int planeStride = plane == 0 ? stride : stride * rowBytes / (width * layout[0].Texel);
            for (int row = 0; row < layout[plane].Height; row++)
            {
                bytes
                    .Slice(from + (row * rowBytes), rowBytes)
                    .CopyTo(destination[(to + (row * planeStride))..]);
            }

            from += rowBytes * layout[plane].Height;
            to += planeStride * layout[plane].Height;
        }
    }

    // A plane of a DMA-BUF picture as an image to copy from; a single-object NV12 picture keeps its
    // chroma after the luma, at the luma's stride.
    private static ImportCache.Lease Import(
        VulkanEngine engine,
        in DmaBufImage source,
        int plane,
        (VkFormat Format, int Width, int Height, int Texel) layout,
        int height
    )
    {
        DmaBufPlane from =
            plane < source.PlaneCount
                ? source[plane]
                : source[0] with
                {
                    Offset = source[0].Offset + (source[0].Stride * height),
                };
        return engine.Imports.Import(
            from,
            source.Modifier,
            layout.Format,
            layout.Width,
            layout.Height,
            VkImageUsageFlags.TransferSrc
        );
    }

    /// <summary>
    /// Reads images back into memory, plane after plane, through a staging buffer kept between frames
    /// and grown when a larger picture comes; the bytes stay valid until the next read.
    /// </summary>
    /// <param name="engine">The GPU.</param>
    /// <param name="readback">The staging kept between pictures.</param>
    /// <param name="planes">The images, one per plane.</param>
    /// <param name="bytesPerTexel">Each image's texel size.</param>
    /// <returns>The planes, packed one after another.</returns>
    public static ReadOnlySpan<byte> Read(
        VulkanEngine engine,
        Readback readback,
        ReadOnlySpan<VulkanImage> planes,
        ReadOnlySpan<int> bytesPerTexel
    )
    {
        ArgumentNullException.ThrowIfNull(readback);
        int packed = 0;
        for (int plane = 0; plane < planes.Length; plane++)
        {
            packed += planes[plane].Width * bytesPerTexel[plane] * planes[plane].Height;
        }

        Staging staging = readback.Reserve(engine, packed);
        using (VulkanEngine.Batch batch = engine.Begin())
        {
            ulong offset = 0;
            for (int plane = 0; plane < planes.Length; plane++)
            {
                VkBufferImageCopy region = Region(planes[plane]) with { bufferOffset = offset };
                Recording.Acquire(engine, batch.Commands, [planes[plane]]);
                engine.Api.vkCmdCopyImageToBuffer(
                    batch.Commands,
                    planes[plane].Image,
                    VkImageLayout.General,
                    staging.Buffer,
                    1,
                    &region
                );
                Recording.Release(engine, batch.Commands, [planes[plane]]);
                offset += (ulong)(
                    planes[plane].Width * bytesPerTexel[plane] * planes[plane].Height
                );
            }

            batch.SubmitAndWait();
        }

        return staging.Bytes[..packed];
    }

    // Memory of the caller's imported as a buffer the GPU copies into (VK_EXT_external_memory_host), for
    // the length of one copy; none where the GPU does not import host memory or the memory is not
    // aligned as it needs.
    private readonly struct HostImport : IDisposable
    {
        private readonly VulkanEngine _engine;
        private readonly VkDeviceMemory _memory;

        private HostImport(VulkanEngine engine, VkBuffer buffer, VkDeviceMemory memory)
        {
            _engine = engine;
            Buffer = buffer;
            _memory = memory;
        }

        public VkBuffer Buffer { get; }

        public static bool TryCreate(
            VulkanEngine engine,
            byte* pointer,
            int length,
            out HostImport import
        )
        {
            import = default;
            if (
                engine.HostImportAlignment is not { } alignment
                || (ulong)pointer % alignment != 0
                || (ulong)length % alignment != 0
            )
            {
                return false;
            }

            VkDeviceApi api = engine.Api;
            VkMemoryHostPointerPropertiesEXT host = new();
            if (
                api.vkGetMemoryHostPointerPropertiesEXT(
                    VkExternalMemoryHandleTypeFlags.HostAllocationEXT,
                    pointer,
                    &host
                ) != VkResult.Success
            )
            {
                return false;
            }

            VkExternalMemoryBufferCreateInfo external = new()
            {
                handleTypes = VkExternalMemoryHandleTypeFlags.HostAllocationEXT,
            };
            VkBufferCreateInfo info = new()
            {
                pNext = &external,
                size = (ulong)length,
                usage = VkBufferUsageFlags.TransferDst,
                sharingMode = VkSharingMode.Exclusive,
            };
            VkBuffer buffer;
            if (api.vkCreateBuffer(&info, null, &buffer) != VkResult.Success)
            {
                return false;
            }

            VkMemoryRequirements requirements;
            api.vkGetBufferMemoryRequirements(buffer, &requirements);
            uint types = requirements.memoryTypeBits & host.memoryTypeBits;
            VkImportMemoryHostPointerInfoEXT pointerInfo = new()
            {
                handleType = VkExternalMemoryHandleTypeFlags.HostAllocationEXT,
                pHostPointer = pointer,
            };
            VkMemoryAllocateInfo allocate = new()
            {
                pNext = &pointerInfo,
                allocationSize = (ulong)length,
                memoryTypeIndex = types == 0 ? 0 : (uint)BitOperations.TrailingZeroCount(types),
            };
            VkDeviceMemory memory;
            if (types == 0 || api.vkAllocateMemory(&allocate, null, &memory) != VkResult.Success)
            {
                api.vkDestroyBuffer(buffer, null);
                return false;
            }

            api.vkBindBufferMemory(buffer, memory, 0).CheckResult();
            import = new HostImport(engine, buffer, memory);
            return true;
        }

        public void Dispose()
        {
            _engine.Api.vkDestroyBuffer(Buffer, null);
            _engine.Api.vkFreeMemory(_memory, null);
        }
    }

    /// <summary>
    /// The staging a reader keeps between pictures, so reading one back allocates nothing once the
    /// first of its size was read. One reader at a time.
    /// </summary>
    public sealed class Readback : IDisposable
    {
        private Staging? _staging;
        private bool _disposed;

        internal Staging Reserve(VulkanEngine engine, int size)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_staging is null || _staging.Size < size || _staging.Engine != engine)
            {
                _staging?.Dispose();
                _staging = null;
                _staging = new Staging(engine, size);
            }

            return _staging;
        }

        /// <summary>Frees the staging.</summary>
        public void Dispose()
        {
            _disposed = true;
            _staging?.Dispose();
            _staging = null;
        }
    }

    private static VkBufferImageCopy Region(VulkanImage image) =>
        new()
        {
            imageSubresource = new VkImageSubresourceLayers(image.Aspect, 0, 0, 1),
            imageExtent = new VkExtent3D(image.Width, image.Height, 1),
        };

    // A host-visible, coherent buffer, mapped for its lifetime.
    internal sealed class Staging : IDisposable
    {
        private readonly VkDeviceMemory _memory;
        private readonly void* _mapped;

        public Staging(VulkanEngine engine, int size)
        {
            Engine = engine;
            Size = size;
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
                memoryTypeIndex = engine.HostMemoryType(requirements.memoryTypeBits),
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

        public VulkanEngine Engine { get; }

        public int Size { get; }

        public Span<byte> Bytes => new(_mapped, Size);

        public void Dispose()
        {
            Engine.Api.vkUnmapMemory(_memory);
            Engine.Api.vkDestroyBuffer(Buffer, null);
            Engine.Api.vkFreeMemory(_memory, null);
        }
    }
}
