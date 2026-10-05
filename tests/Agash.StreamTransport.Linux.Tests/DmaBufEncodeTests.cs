using Agash.StreamTransport.Codecs.FFmpeg;
using Agash.StreamTransport.Linux.Vulkan;
using Agash.StreamTransport.Media;
using Vortice.Vulkan;

namespace Agash.StreamTransport.Linux.Tests;

/// <summary>
/// GPU pictures made here, encoded as DMA-BUFs through VA-API and Vulkan Video, which read them in place:
/// what the PipeWire sink publishes and a PipeWire source hands an encoder.
/// </summary>
[TestClass]
[TestCategory("Integration")]
[OSCondition(OperatingSystems.Linux)]
public sealed class DmaBufEncodeTests
{
    private const int Width = 320;
    private const int Height = 240;

    private static readonly VideoEncoderConfiguration Configuration = new(
        new VideoCodecFormat(VideoCodecId.H264),
        new VideoSize(Width, Height),
        new RateTarget(1_000_000, 30)
    );

    [TestMethod]
    public void Nv12Picture_IsOneDmaBufObject()
    {
        PooledDmaBuf picture = Grey();
        try
        {
            DmaBufImage image = picture.Describe(TestDmaBufs.Engine.Identity);

            Assert.AreEqual(2, image.PlaneCount);
            Assert.AreEqual(
                DmaBufIdentity.Of(image[0].Fd),
                DmaBufIdentity.Of(image[1].Fd),
                "both planes live in one buffer"
            );
            Assert.IsGreaterThanOrEqualTo(image[0].Stride * Height, image[1].Offset);
        }
        finally
        {
            picture.Release();
        }
    }

    [TestMethod]
    [DataRow(EncoderBackend.Vaapi)]
    [DataRow(EncoderBackend.Vulkan)]
    public void Encoder_EncodesNv12DmaBufs(EncoderBackend backend)
    {
        IVideoEncoder encoder = Create(backend);
        using (encoder)
        {
            PooledDmaBuf picture = Grey();
            try
            {
                Recorder recorder = new();
                for (int i = 0; i < 5; i++)
                {
                    encoder.Encode(
                        TestDmaBufs.Frame(
                            picture,
                            PixelFormat.Nv12,
                            Width,
                            Height,
                            VideoColor.Bt709
                        ),
                        new EncodeRequest(Keyframe: i == 0),
                        recorder
                    );
                }

                encoder.Flush(recorder);
                Assert.IsGreaterThan(0, recorder.Frames);
                Assert.IsTrue(recorder.FirstWasKeyframe);
            }
            finally
            {
                picture.Release();
            }
        }
    }

    [TestMethod]
    [DataRow(EncoderBackend.Vaapi)]
    [DataRow(EncoderBackend.Vulkan)]
    public void Encoder_PlanesInTwoBuffers_FailCleanly(EncoderBackend backend)
    {
        IVideoEncoder encoder = Create(backend);
        using VulkanImage luma = VulkanImage.ExportPicture(
            TestDmaBufs.Engine,
            [(VkFormat.R8Unorm, Width, Height)],
            VkImageUsageFlags.TransferDst
        )[0];
        using VulkanImage chroma = VulkanImage.ExportPicture(
            TestDmaBufs.Engine,
            [(VkFormat.R8G8Unorm, Width / 2, Height / 2)],
            VkImageUsageFlags.TransferDst
        )[0];
        VideoFrame frame = new(
            new VideoStorage(
                new DmaBufImage(
                    [luma.Plane, chroma.Plane],
                    DrmFourcc.Nv12,
                    VulkanEngine.LinearModifier,
                    TestDmaBufs.Engine.Identity
                )
            ),
            new VideoFormat(PixelFormat.Nv12, Width, Height),
            MediaTimestamp.Captured(new MediaTime(42)),
            color: VideoColor.Bt709
        );

        // VA-API and Vulkan read only pictures made of one DMA-BUF object: the frame is refused, and
        // ending the encoder afterwards, as a pipeline does before trying the next one, is safe.
        bool refused = false;
        try
        {
            encoder.Encode(frame, new EncodeRequest(Keyframe: true), new Recorder());
        }
        catch (Exception exception)
            when (exception is FFmpeg.Interop.FFmpegException or NotSupportedException)
        {
            refused = true;
        }

        Assert.IsTrue(refused, "a picture in two buffers is refused");
        encoder.Flush(new Recorder());
        encoder.Dispose();
    }

    private static IVideoEncoder Create(EncoderBackend backend)
    {
        FFmpegVideoEncoderFactory factory = new(backend);
        if (
            factory.QueryCapabilities(Configuration.Format, device: null) is not { } info
            || !info.Input.Storages.Contains(VideoStorageKind.DmaBuf)
        )
        {
            Assert.Inconclusive($"No {backend} H.264 encoder takes DMA-BUFs on this machine.");
        }

        return factory.Create(Configuration, device: null);
    }

    private static PooledDmaBuf Grey() =>
        TestDmaBufs.Upload(
            PixelFormat.Nv12,
            Width,
            Height,
            [.. Enumerable.Repeat((byte)128, Width * Height)],
            [.. Enumerable.Repeat((byte)128, Width * Height / 2)]
        );

    private sealed class Recorder : IEncodedVideoConsumer
    {
        public int Frames { get; private set; }

        public bool FirstWasKeyframe { get; private set; }

        public void OnEncoded(in EncodedVideoFrame frame)
        {
            if (Frames++ == 0)
            {
                FirstWasKeyframe = frame.Keyframe;
            }
        }
    }
}
