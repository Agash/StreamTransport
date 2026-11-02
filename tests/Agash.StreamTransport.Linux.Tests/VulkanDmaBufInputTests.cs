using Agash.StreamTransport.Codecs.FFmpeg;
using Agash.StreamTransport.Linux.Vulkan;
using Agash.StreamTransport.Media;
using FF = FFmpeg.Interop;

namespace Agash.StreamTransport.Linux.Tests;

[TestClass]
[TestCategory("RequiresGpu")]
public sealed class VulkanDmaBufInputTests
{
    private const int Width = 64;
    private const int Height = 48;

    // A buffer the encoder cannot read in place is imported for reads and copied into an encoder surface
    // on the GPU; every frame from it comes out as the producer wrote it, through one import.
    [TestMethod]
    public void Prepare_AModifierTheEncoderCannotRead_CopiesThePictureOnTheGpu()
    {
        _ = TestDmaBufs.Engine;
        byte[] luma = new byte[Width * Height];
        byte[] chroma = new byte[Width * Height / 2];
        new Random(43).NextBytes(luma);
        new Random(47).NextBytes(chroma);
        PooledDmaBuf picture = TestDmaBufs.Upload(PixelFormat.Nv12, Width, Height, luma, chroma);
        List<ulong> copied = [];
        try
        {
            using VulkanDmaBufInput input = new(
                FF.HardwareDevice.Create(FF.HardwareDeviceType.Vulkan),
                PixelFormat.Nv12,
                new VideoSize(Width, Height),
                copied.Add,
                readsInPlace: static _ => false
            );
            for (long pts = 0; pts < 3; pts++)
            {
                using FF.Frame destination = new();
                input.Prepare(
                    TestDmaBufs.Frame(picture, PixelFormat.Nv12, Width, Height, VideoColor.Bt709),
                    pts,
                    destination
                );

                CollectionAssert.AreEqual(
                    luma.Concat(chroma).ToArray(),
                    Downloaded(destination),
                    $"frame {pts}"
                );
                input.Read(pts);
            }

            Assert.HasCount(1, copied, "the copy is reported once for the modifier");
        }
        finally
        {
            picture.Release();
        }
    }

    private static byte[] Downloaded(FF.Frame surface)
    {
        using FF.Frame picture = new();
        surface.TransferTo(picture);
        byte[] pixels = new byte[picture.GetImageSize()];
        _ = picture.CopyImageTo(pixels);
        return pixels;
    }
}
