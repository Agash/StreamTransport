using Agash.StreamTransport.Linux.Vulkan;
using Agash.StreamTransport.Media;
using Vortice.Vulkan;

namespace Agash.StreamTransport.Linux.Tests;

// DMA-BUFs written and read through the GPU's staging copies, row by row; downloads import the
// frame's planes the way a consumer on another device would.
internal static class TestDmaBufs
{
    // The machine's Vulkan GPU; a test that needs one is inconclusive where there is none (a CI runner).
    private static readonly Lazy<(VulkanEngine? Engine, string? Missing)> s_engine = new(Create);

    public static VulkanEngine Engine =>
        s_engine.Value.Engine
        ?? throw new AssertInconclusiveException(s_engine.Value.Missing ?? "No Vulkan GPU.");

    private static (VulkanEngine?, string?) Create()
    {
        try
        {
            return (VulkanEngine.For(null), null);
        }
        catch (Exception exception) when (exception is VkException or InvalidOperationException)
        {
            return (null, $"No Vulkan GPU that shares DMA-BUFs: {exception.Message}");
        }
    }

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

    // Read once the picture's producer has written it, as any reader on the CPU waits.
    public static byte[][] Download(DmaBufImage image, PixelFormat format, int width, int height) =>
        VulkanTransfer.Read(Engine, in image, format, width, height);

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
