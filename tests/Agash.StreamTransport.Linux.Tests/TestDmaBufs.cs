using Agash.StreamTransport.Linux.Vulkan;
using Agash.StreamTransport.Media;
using Vortice.Vulkan;

namespace Agash.StreamTransport.Linux.Tests;

// DMA-BUFs written and read through the GPU's staging copies, row by row; downloads import the
// frame's planes the way a consumer on another device would.
internal static class TestDmaBufs
{
    public static VulkanEngine Engine { get; } = VulkanEngine.For(null);

    // A pooled picture holding the planes, tightly packed rows each; the pool goes with the picture.
    public static PooledDmaBuf Upload(
        PixelFormat format,
        int width,
        int height,
        params byte[][] planes
    )
    {
        using DmaBufPool pool = new(Engine, format, new VideoSize(width, height));
        PooledDmaBuf picture = pool.Rent();
        for (int plane = 0; plane < planes.Length; plane++)
        {
            VulkanImage image = picture.Planes[plane];
            int texel = TexelBytes(format, plane);
            VulkanTransfer.Upload(Engine, image, planes[plane], image.Width * texel, texel);
        }

        return picture;
    }

    public static byte[][] Download(DmaBufImage image, PixelFormat format, int width, int height)
    {
        int count = format == PixelFormat.Nv12 ? 2 : 1;
        byte[][] planes = new byte[count][];
        for (int plane = 0; plane < count; plane++)
        {
            (VkFormat vk, int w, int h) = format switch
            {
                PixelFormat.Nv12 when plane == 0 => (VkFormat.R8Unorm, width, height),
                PixelFormat.Nv12 => (VkFormat.R8G8Unorm, width / 2, height / 2),
                PixelFormat.Rgba => (VkFormat.R8G8B8A8Unorm, width, height),
                _ => (VkFormat.B8G8R8A8Unorm, width, height),
            };
            using var imported = VulkanImage.Import(
                Engine,
                image[plane],
                image.Modifier,
                vk,
                w,
                h,
                VkImageUsageFlags.TransferSrc
            );
            planes[plane] = VulkanTransfer.Download(Engine, imported, TexelBytes(format, plane));
        }

        return planes;
    }

    private static int TexelBytes(PixelFormat format, int plane) =>
        format == PixelFormat.Nv12 ? plane + 1 : 4;

    public static VideoFrame Frame(
        PooledDmaBuf picture,
        PixelFormat format,
        int width,
        int height,
        VideoColor color = default
    ) =>
        new(
            new VideoStorage(picture.Describe(Engine.Identity)),
            new VideoFormat(format, width, height),
            MediaTimestamp.Captured(new MediaTime(42)),
            color: color,
            retainer: new Held(picture)
        );

    // A test picture kept by holding it again.
    private sealed class Held(PooledDmaBuf picture) : IVideoFrameRetainer
    {
        public VideoFrameLease Retain(in VideoFrame frame)
        {
            picture.Hold();
            return new DmaBufFrameLease(in frame, picture);
        }
    }
}
