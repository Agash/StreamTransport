using System.Runtime.Versioning;
using Agash.StreamTransport.Media;
using static FFmpeg.Interop.D3D11VAExtensions;
using static FFmpeg.Interop.D3D12VAExtensions;
using FF = FFmpeg.Interop;

namespace Agash.StreamTransport.Codecs.FFmpeg.Tests;

// GPU surfaces straight into the encoders that take them. The surfaces are made on the GPU the
// backend reports, filled by an upload, and handed over as a capture source would: the encoder copies
// on the GPU (Direct3D 11) or reads the resource in place (Direct3D 12).
[TestClass]
public sealed class GpuInputTests
{
    private const int FrameCount = 20;

    public static IEnumerable<object[]> Cases =>
        from backend in Enum.GetValues<EncoderBackend>()
        from codec in VideoCodecId.BuiltIn
        from storage in new[] { VideoStorageKind.D3D12, VideoStorageKind.D3D11 }
        select new object[] { backend, codec, storage };

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    [DynamicData(nameof(Cases))]
    [SupportedOSPlatform("windows10.0.10240")]
    public void Encode_DirectXSurface_DecodesToThePicturesSent(
        EncoderBackend backend,
        VideoCodecId codec,
        VideoStorageKind storage
    )
    {
        FFmpegVideoEncoderFactory factory = new(backend);
        VideoEncoderInfo? info = factory.QueryCapabilities(
            new VideoCodecFormat(codec),
            device: null
        );
        if (info is null || !info.Input.Storages.Contains(storage))
        {
            Assert.Inconclusive(
                $"{backend} does not take {storage} {codec} frames on this machine."
            );
        }

        GpuIdentity identity =
            info.Input.Device ?? throw new AssertFailedException("A GPU encoder named no GPU.");
        FF.GpuAdapter adapter =
            FF.GpuAdapter.FindByLuid((long)identity.Value)
            ?? throw new AssertFailedException("The encoder's GPU is not among the adapters.");
        using var device = FF.HardwareDevice.Create(
            storage == VideoStorageKind.D3D12
                ? FF.HardwareDeviceType.D3D12VA
                : FF.HardwareDeviceType.D3D11VA,
            adapter
        );
        using var surfaces = FF.HardwareFramePool.Create(
            device,
            storage == VideoStorageKind.D3D12 ? FF.PixelFormat.D3D12 : FF.PixelFormat.D3D11,
            FF.PixelFormat.Nv12,
            Pictures.Width,
            Pictures.Height
        );

        using IVideoEncoder encoder = factory.Create(
            new VideoEncoderConfiguration(
                new VideoCodecFormat(codec),
                Pictures.Size,
                new RateTarget(4_000_000, 30)
            ),
            identity
        );
        Collector collector = new();
        using FF.Frame staging = new();
        using FF.Frame surface = new();
        for (int i = 0; i < FrameCount; i++)
        {
            staging.AllocateVideo(Pictures.Width, Pictures.Height, FF.PixelFormat.Nv12);
            staging.CopyImageFrom(Pictures.Make(PixelFormat.Nv12, i));
            surface.Reset();
            surfaces.Upload(staging, surface);
            VideoStorage image =
                storage == VideoStorageKind.D3D12
                    ? surface.TryGetD3D12Texture(out FF.D3D12Texture d3d12)
                        ? new D3D12Image(
                            d3d12.Resource,
                            d3d12.Subresource,
                            identity,
                            new D3D12Sync(Fence: d3d12.Fence, Value: d3d12.FenceValue)
                        )
                        : throw new AssertFailedException("The upload made no D3D12 resource.")
                    : surface.TryGetD3D11Texture(out FF.D3D11Texture d3d11)
                        ? new D3D11Image(d3d11.Texture, d3d11.ArraySlice, identity)
                        : throw new AssertFailedException("The upload made no D3D11 texture.");
            VideoFrame frame = new(
                image,
                new VideoFormat(PixelFormat.Nv12, Pictures.Width, Pictures.Height),
                Pictures.Timestamp(i),
                retainer: new SurfaceRetainer(surface)
            );
            encoder.Encode(in frame, new EncodeRequest(Keyframe: i == 0), collector);
        }

        encoder.Flush(collector);

        var decoded = Reference.Decode(codec, collector.Units.Select(static u => u.Data));
        Assert.HasCount(FrameCount, decoded);
        for (int i = 0; i < FrameCount; i++)
        {
            Assert.AreEqual(
                Pictures.MeanLuma(i),
                decoded[i].MeanLuma,
                6.0,
                $"Frame {i} decodes to other content than the surface held."
            );
        }
    }
}
