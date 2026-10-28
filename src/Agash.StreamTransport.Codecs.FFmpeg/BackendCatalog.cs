using System.Collections.Immutable;
using Agash.StreamTransport.Media;
using FF = FFmpeg.Interop;

namespace Agash.StreamTransport.Codecs.FFmpeg;

// How a backend takes frames of a GPU storage without a copy through memory: each is an encoder input
// of its own, so a backend that takes surfaces another way (DRM PRIME pass-through, V4L2 DMA-BUF queues)
// adds a way here and declares it in its catalogue entry.
internal enum GpuInput
{
    // Direct3D 12 textures, wrapped as the encoder's surfaces.
    D3D12Texture,

    // Direct3D 11 textures on the encoder's device, wrapped as its surfaces.
    D3D11Texture,

    // DMA-BUFs imported as Vulkan images made for the encoder's input.
    VulkanImport,

    // DMA-BUFs mapped into VA-API surfaces.
    VaapiMap,

    // IOSurfaces, wrapped as VideoToolbox pixel buffers.
    IOSurface,
}

// What each encoder family is in FFmpeg terms: its encoder per codec, the device it runs on, how it
// takes each GPU storage without a CPU copy, and how strongly it is preferred on each platform.
internal sealed record BackendSpec(
    EncoderBackend Backend,
    ImmutableArray<(VideoCodecId Codec, string Encoder)> Encoders,
    FF.HardwareDeviceType? DeviceType,
    ImmutableArray<GpuInput> GpuInputs,
    int WindowsRank,
    int LinuxRank,
    int MacRank
)
{
    public bool IsHardware => DeviceType is not null || Backend == EncoderBackend.MediaFoundation;

    // The GPU storages the backend takes.
    public ImmutableArray<VideoStorageKind> GpuStorages => [.. GpuInputs.Select(StorageOf)];

    // How the backend takes a storage, or null when it takes it only through memory.
    public GpuInput? InputFor(VideoStorageKind storage)
    {
        foreach (GpuInput input in GpuInputs)
        {
            if (StorageOf(input) == storage)
            {
                return input;
            }
        }

        return null;
    }

    private static VideoStorageKind StorageOf(GpuInput input) =>
        input switch
        {
            GpuInput.D3D12Texture => VideoStorageKind.D3D12,
            GpuInput.D3D11Texture => VideoStorageKind.D3D11,
            GpuInput.VulkanImport or GpuInput.VaapiMap => VideoStorageKind.DmaBuf,
            GpuInput.IOSurface => VideoStorageKind.IOSurface,
            _ => throw new ArgumentOutOfRangeException(nameof(input), input, null),
        };

    // Zero when the family does not exist on this platform.
    public int Rank =>
        OperatingSystem.IsWindows() ? WindowsRank
        : OperatingSystem.IsLinux() ? LinuxRank
        : OperatingSystem.IsMacOS() ? MacRank
        : 0;

    public string? EncoderFor(VideoCodecId codec)
    {
        foreach ((VideoCodecId id, string name) in Encoders)
        {
            if (id == codec)
            {
                return name;
            }
        }

        return null;
    }
}

internal static class BackendCatalog
{
    // Modern APIs rank first where there is a choice: Direct3D 12 ahead of the Direct3D 11 vendor
    // paths on Windows, Vulkan ahead of VA-API on Linux. The vendor paths stay registered: they carry
    // tuning the generic APIs lack, and a pipeline falls back to them when the modern path will not open.
    public static ImmutableArray<BackendSpec> All { get; } =
    [
        new(
            EncoderBackend.D3D12,
            [
                (VideoCodecId.H264, "h264_d3d12va"),
                (VideoCodecId.H265, "hevc_d3d12va"),
                (VideoCodecId.AV1, "av1_d3d12va"),
            ],
            FF.HardwareDeviceType.D3D12VA,
            [GpuInput.D3D12Texture],
            WindowsRank: 100,
            LinuxRank: 0,
            MacRank: 0
        ),
        new(
            EncoderBackend.Nvenc,
            [
                (VideoCodecId.H264, "h264_nvenc"),
                (VideoCodecId.H265, "hevc_nvenc"),
                (VideoCodecId.AV1, "av1_nvenc"),
            ],
            OperatingSystem.IsWindows()
                ? FF.HardwareDeviceType.D3D11VA
                : FF.HardwareDeviceType.Cuda,
            [GpuInput.D3D11Texture],
            WindowsRank: 90,
            LinuxRank: 85,
            MacRank: 0
        ),
        new(
            EncoderBackend.Amf,
            [
                (VideoCodecId.H264, "h264_amf"),
                (VideoCodecId.H265, "hevc_amf"),
                (VideoCodecId.AV1, "av1_amf"),
            ],
            FF.HardwareDeviceType.D3D11VA,
            [GpuInput.D3D11Texture],
            WindowsRank: 85,
            LinuxRank: 0,
            MacRank: 0
        ),
        new(
            EncoderBackend.Qsv,
            [
                (VideoCodecId.H264, "h264_qsv"),
                (VideoCodecId.H265, "hevc_qsv"),
                (VideoCodecId.AV1, "av1_qsv"),
            ],
            FF.HardwareDeviceType.Qsv,
            // QSV takes Direct3D 11 surfaces only once they are mapped into QSV surfaces, which is not
            // verified on Intel hardware yet; until it is, frames reach it through system memory.
            [],
            WindowsRank: 80,
            LinuxRank: 80,
            MacRank: 0
        ),
        new(
            EncoderBackend.Vulkan,
            [
                (VideoCodecId.H264, "h264_vulkan"),
                (VideoCodecId.H265, "hevc_vulkan"),
                (VideoCodecId.AV1, "av1_vulkan"),
            ],
            FF.HardwareDeviceType.Vulkan,
            [GpuInput.VulkanImport],
            WindowsRank: 70,
            LinuxRank: 100,
            MacRank: 0
        ),
        new(
            EncoderBackend.Vaapi,
            [
                (VideoCodecId.H264, "h264_vaapi"),
                (VideoCodecId.H265, "hevc_vaapi"),
                (VideoCodecId.AV1, "av1_vaapi"),
            ],
            FF.HardwareDeviceType.Vaapi,
            [GpuInput.VaapiMap],
            WindowsRank: 0,
            LinuxRank: 90,
            MacRank: 0
        ),
        new(
            EncoderBackend.VideoToolbox,
            [(VideoCodecId.H264, "h264_videotoolbox"), (VideoCodecId.H265, "hevc_videotoolbox")],
            FF.HardwareDeviceType.VideoToolbox,
            [GpuInput.IOSurface],
            WindowsRank: 0,
            LinuxRank: 0,
            MacRank: 100
        ),
        new(
            EncoderBackend.MediaFoundation,
            [
                (VideoCodecId.H264, "h264_mf"),
                (VideoCodecId.H265, "hevc_mf"),
                (VideoCodecId.AV1, "av1_mf"),
            ],
            DeviceType: null,
            [],
            WindowsRank: 20,
            LinuxRank: 0,
            MacRank: 0
        ),
        new(
            EncoderBackend.Software,
            [
                (VideoCodecId.H264, "libopenh264"),
                (VideoCodecId.H265, "libkvazaar"),
                (VideoCodecId.AV1, "libsvtav1"),
            ],
            DeviceType: null,
            [],
            WindowsRank: 10,
            LinuxRank: 10,
            MacRank: 10
        ),
    ];

    public static BackendSpec Get(EncoderBackend backend)
    {
        foreach (BackendSpec spec in All)
        {
            if (spec.Backend == backend)
            {
                return spec;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(backend), backend, null);
    }
}
