using Agash.StreamTransport.Codecs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Agash.StreamTransport.Tests;

/// <summary>
/// Verifies FFmpeg can create a Vulkan hardware device and that we read its Vulkan handles correctly
/// (the leading fields of AVVulkanDeviceContext). Inconclusive where no Vulkan driver is present.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class VulkanDeviceTests
{
    [TestMethod]
    public void CreateVulkanDevice_ExposesHandles()
    {
        string? nativeBin = TestNative.FindFFmpegBin();
        if (nativeBin is null)
        {
            Assert.Inconclusive("No bundled FFmpeg native build found.");
            return;
        }

        FFmpegLibrary.EnsureLoaded(nativeBin);

        if (!VulkanDevice.IsAvailable())
        {
            Assert.Inconclusive("No Vulkan device available on this machine.");
            return;
        }

        Assert.AreNotEqual(nint.Zero, VulkanDevice.Instance, "VkInstance must be non-null");
        Assert.AreNotEqual(nint.Zero, VulkanDevice.PhysicalDevice, "VkPhysicalDevice must be non-null");
        Assert.AreNotEqual(nint.Zero, VulkanDevice.Device, "VkDevice must be non-null");

        // Validates the full AVVulkanDeviceContext ABI read (past the embedded VkPhysicalDeviceFeatures2): a
        // sane compute queue family index means the qf[] offset and nb_qf were read correctly.
        int computeFamily = VulkanDevice.ComputeQueueFamily;
        Assert.IsTrue(computeFamily >= 0, "FFmpeg's Vulkan device must expose a compute queue family");
        Assert.IsTrue(computeFamily < 64, $"compute queue family index {computeFamily} is implausible (bad ABI read)");
    }

    [TestMethod]
    public void DecodeHevcToVulkanImage_ProducesVkImage()
    {
        string? nativeBin = TestNative.FindFFmpegBin();
        if (nativeBin is null)
        {
            Assert.Inconclusive("No bundled FFmpeg native build found.");
            return;
        }

        FFmpegLibrary.EnsureLoaded(nativeBin);

        VaapiVideoEncoder encoder;
        try
        {
            encoder = new VaapiVideoEncoder(1280, 720, fps: 30, bitrate: 4_000_000);
        }
        catch (Exception ex)
        {
            Assert.Inconclusive($"hevc_vaapi hardware is not available: {ex.Message}");
            return;
        }

        VulkanVideoDecoder decoder;
        try
        {
            decoder = new VulkanVideoDecoder();
        }
        catch (Exception ex)
        {
            Assert.Inconclusive($"Vulkan decode is not available on this machine: {ex.Message}");
            return;
        }

        using (encoder)
        using (decoder)
        {
            byte[] nv12 = HardwareEncoderTestSupport.Nv12Pattern(1280, 720);
            var seed = VideoFrame.FromPixels(nv12, VideoPixelFormat.Nv12, 1280, 720, 0);

            bool decoded = false;
            for (int frame = 0; frame < 30 && !decoded; frame++)
            {
                byte[]? au = encoder.Encode(seed, out _);
                if (au is not null && decoder.TryDecode(au, out int w, out int h))
                {
                    Assert.AreEqual(1280, w);
                    Assert.AreEqual(720, h);
                    Assert.AreNotEqual(nint.Zero, decoder.Image0, "decoded Vulkan frame must expose a VkImage");
                    decoded = true;
                }
            }

            Assert.IsTrue(decoded, "decoder should have produced a Vulkan image from the HEVC stream");
        }
    }
}
