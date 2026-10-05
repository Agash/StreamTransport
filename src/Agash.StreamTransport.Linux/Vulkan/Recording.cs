using System.Runtime.InteropServices;
using Agash.StreamTransport.Media;
using Vortice.Vulkan;

namespace Agash.StreamTransport.Linux.Vulkan;

// Commands shared by everything that records GPU work on DMA-BUF images.
internal static unsafe partial class Recording
{
    // VK_QUEUE_FAMILY_FOREIGN_EXT: whoever else uses the buffer, outside this Vulkan device.
    private const uint ForeignQueueFamily = ~2u;

    /// <summary>
    /// Takes images from their other users: their contents are kept, in the general layout every use
    /// here reads and writes them in.
    /// </summary>
    public static void Acquire(
        VulkanEngine engine,
        VkCommandBuffer commands,
        ReadOnlySpan<VulkanImage> images
    ) =>
        Barrier(
            engine,
            commands,
            images,
            ForeignQueueFamily,
            VkQueueFamilyIgnored,
            VkAccessFlags.None,
            VkAccessFlags.ShaderRead
                | VkAccessFlags.ShaderWrite
                | VkAccessFlags.TransferRead
                | VkAccessFlags.TransferWrite,
            VkPipelineStageFlags.TopOfPipe,
            VkPipelineStageFlags.ComputeShader | VkPipelineStageFlags.Transfer
        );

    /// <summary>Hands images back to their other users once this device's work on them is recorded.</summary>
    public static void Release(
        VulkanEngine engine,
        VkCommandBuffer commands,
        ReadOnlySpan<VulkanImage> images
    ) =>
        Barrier(
            engine,
            commands,
            images,
            VkQueueFamilyIgnored,
            ForeignQueueFamily,
            VkAccessFlags.ShaderWrite | VkAccessFlags.TransferWrite,
            VkAccessFlags.None,
            VkPipelineStageFlags.ComputeShader | VkPipelineStageFlags.Transfer,
            VkPipelineStageFlags.BottomOfPipe
        );

    /// <summary>Binds a storage view.</summary>
    public static VkWriteDescriptorSet Storage(
        VkDescriptorSet set,
        uint binding,
        VkDescriptorImageInfo* image
    ) =>
        new()
        {
            dstSet = set,
            dstBinding = binding,
            descriptorCount = 1,
            descriptorType = VkDescriptorType.StorageImage,
            pImageInfo = image,
        };

    /// <summary>Binds a sampled view.</summary>
    public static VkWriteDescriptorSet Sampled(
        VkDescriptorSet set,
        uint binding,
        VkDescriptorImageInfo* image
    ) =>
        new()
        {
            dstSet = set,
            dstBinding = binding,
            descriptorCount = 1,
            descriptorType = VkDescriptorType.CombinedImageSampler,
            pImageInfo = image,
        };

    /// <summary>
    /// Waits until everything that wrote a DMA-BUF has finished, for producers that rely on the kernel's
    /// implicit fences: a DMA-BUF polls readable once its writers are done.
    /// </summary>
    /// <returns>False when the writers did not finish in time.</returns>
    public static bool WaitForWriters(in DmaBufImage image, TimeSpan timeout)
    {
        int previous = -1;
        for (int i = 0; i < image.PlaneCount; i++)
        {
            int fd = image[i].Fd;
            if (fd == previous)
            {
                continue;
            }

            previous = fd;
            PollFd poll = new() { Fd = fd, Events = PollIn };
            if (Poll(&poll, 1, (int)timeout.TotalMilliseconds) <= 0)
            {
                return false;
            }
        }

        return true;
    }

    private static void Barrier(
        VulkanEngine engine,
        VkCommandBuffer commands,
        ReadOnlySpan<VulkanImage> images,
        uint fromFamily,
        uint toFamily,
        VkAccessFlags fromAccess,
        VkAccessFlags toAccess,
        VkPipelineStageFlags fromStage,
        VkPipelineStageFlags toStage
    )
    {
        // The planes of a multi-planar picture are one image, which takes one barrier.
        VkImageMemoryBarrier* barriers = stackalloc VkImageMemoryBarrier[images.Length];
        int count = 0;
        for (int i = 0; i < images.Length; i++)
        {
            bool seen = false;
            for (int j = 0; j < count && !seen; j++)
            {
                seen = barriers[j].image == images[i].Image;
            }

            if (seen)
            {
                continue;
            }

            barriers[count++] = new VkImageMemoryBarrier
            {
                srcAccessMask = fromAccess,
                dstAccessMask = toAccess,
                oldLayout = VkImageLayout.General,
                newLayout = VkImageLayout.General,
                srcQueueFamilyIndex =
                    fromFamily == VkQueueFamilyIgnored ? engine.QueueFamily : fromFamily,
                dstQueueFamilyIndex =
                    toFamily == VkQueueFamilyIgnored ? engine.QueueFamily : toFamily,
                image = images[i].Image,
                subresourceRange = new VkImageSubresourceRange(
                    VkImageAspectFlags.Color,
                    0,
                    1,
                    0,
                    1
                ),
            };
        }

        engine.Api.vkCmdPipelineBarrier(
            commands,
            fromStage,
            toStage,
            0,
            0,
            null,
            0,
            null,
            (uint)count,
            barriers
        );
    }

    private const short PollIn = 0x001;

    private const uint VkQueueFamilyIgnored = ~0u;

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

    [LibraryImport("libc", EntryPoint = "poll", SetLastError = true)]
    private static partial int Poll(PollFd* fds, nuint count, int timeout);
}
