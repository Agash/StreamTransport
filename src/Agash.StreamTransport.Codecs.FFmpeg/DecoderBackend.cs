using System.Collections.Immutable;
using Agash.StreamTransport.Media;
using FF = FFmpeg.Interop;

namespace Agash.StreamTransport.Codecs.FFmpeg;

/// <summary>
/// A family of FFmpeg video decoders: FFmpeg's decoder for the codec, accelerated by one hardware API,
/// or in software. Each family is a separate <see cref="FFmpegVideoDecoderFactory"/>.
/// </summary>
public enum DecoderBackend
{
    /// <summary>Direct3D 12 Video Decode (Windows), producing Direct3D 12 textures.</summary>
    D3D12,

    /// <summary>Direct3D 11 Video Acceleration (Windows), producing Direct3D 11 textures.</summary>
    D3D11,

    /// <summary>Vulkan Video, producing Vulkan images, shared as DMA-BUFs on Linux.</summary>
    Vulkan,

    /// <summary>VA-API (Linux), producing surfaces shared as DMA-BUFs.</summary>
    Vaapi,

    /// <summary>VideoToolbox (macOS), producing IOSurfaces.</summary>
    VideoToolbox,

    /// <summary>Software decoding into system memory.</summary>
    Software,
}

// What each decoder family is in FFmpeg terms.
internal sealed record DecoderSpec(
    DecoderBackend Backend,
    FF.HardwareDeviceType? DeviceType,
    ImmutableArray<VideoStorageKind> GpuStorages,
    int WindowsRank,
    int LinuxRank,
    int MacRank
)
{
    public int Rank =>
        OperatingSystem.IsWindows() ? WindowsRank
        : OperatingSystem.IsLinux() ? LinuxRank
        : OperatingSystem.IsMacOS() ? MacRank
        : 0;

    // FFmpeg's decoder for a codec: the built-in one, which every hardware API accelerates, except
    // AV1 in software, where dav1d is FFmpeg's fast decoder. Null for a codec this package lacks.
    public string? DecoderFor(VideoCodecId codec) =>
        codec == VideoCodecId.H264 ? "h264"
        : codec == VideoCodecId.H265 ? "hevc"
        : codec == VideoCodecId.AV1 ? (DeviceType is null ? "libdav1d" : "av1")
        : null;
}

internal static class DecoderCatalog
{
    // Modern APIs first where there is a choice, as for encoders.
    public static ImmutableArray<DecoderSpec> All { get; } =
    [
        new(
            DecoderBackend.D3D12,
            FF.HardwareDeviceType.D3D12VA,
            [VideoStorageKind.D3D12],
            WindowsRank: 100,
            LinuxRank: 0,
            MacRank: 0
        ),
        new(
            DecoderBackend.D3D11,
            FF.HardwareDeviceType.D3D11VA,
            [VideoStorageKind.D3D11],
            WindowsRank: 90,
            LinuxRank: 0,
            MacRank: 0
        ),
        new(
            DecoderBackend.Vulkan,
            FF.HardwareDeviceType.Vulkan,
            [VideoStorageKind.DmaBuf],
            WindowsRank: 70,
            LinuxRank: 100,
            MacRank: 0
        ),
        new(
            DecoderBackend.Vaapi,
            FF.HardwareDeviceType.Vaapi,
            [VideoStorageKind.DmaBuf],
            WindowsRank: 0,
            LinuxRank: 90,
            MacRank: 0
        ),
        new(
            DecoderBackend.VideoToolbox,
            FF.HardwareDeviceType.VideoToolbox,
            [VideoStorageKind.IOSurface],
            WindowsRank: 0,
            LinuxRank: 0,
            MacRank: 100
        ),
        new(
            DecoderBackend.Software,
            DeviceType: null,
            [],
            WindowsRank: 10,
            LinuxRank: 10,
            MacRank: 10
        ),
    ];

    public static DecoderSpec Get(DecoderBackend backend)
    {
        foreach (DecoderSpec spec in All)
        {
            if (spec.Backend == backend)
            {
                return spec;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(backend), backend, null);
    }
}
