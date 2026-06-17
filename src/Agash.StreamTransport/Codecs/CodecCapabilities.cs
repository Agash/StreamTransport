using System.Text;
using FFmpeg.AutoGen;

namespace Agash.StreamTransport.Codecs;

/// <summary>
/// Shared hardware-device probe: whether an FFmpeg hardware device of a given type can actually be created
/// on this host. Cached for the process - creating a device touches the GPU driver (CUDA, libva, QSV/MFX),
/// the answer cannot change at runtime, and some VAAPI/Mesa stacks are unstable across repeated init/teardown.
/// </summary>
internal static unsafe class CodecProbe
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<AVHWDeviceType, bool> s_cache = new();

    /// <summary>True if a hardware device of <paramref name="type"/> can be created (probed once, then cached).</summary>
    public static bool HwDeviceUsable(AVHWDeviceType type) => s_cache.GetOrAdd(type, static t =>
    {
        if (t == AVHWDeviceType.AV_HWDEVICE_TYPE_NONE)
        {
            return true;
        }

        AVBufferRef* device = null;
        int created = ffmpeg.av_hwdevice_ctx_create(&device, t, null, null, 0);
        if (device is not null)
        {
            ffmpeg.av_buffer_unref(&device);
        }

        return created >= 0;
    });
}

/// <summary>One codec entry in the capability report: its FFmpeg name, whether it is present in the build, and
/// whether its backing hardware is actually usable on this host.</summary>
public readonly record struct CodecEntry(string Name, bool Present, bool HardwareUsable)
{
    /// <summary>True when the codec is both present in the build and backed by usable hardware.</summary>
    public bool Available => Present && HardwareUsable;
}

/// <summary>
/// A snapshot of the host's usable HEVC hardware encode/decode capabilities, used for diagnostics
/// (<c>selftest caps</c>) and to drive deterministic encoder selection/fallback. Probing is done once;
/// the result reflects the FFmpeg build present plus a live hardware-device check per vendor.
/// </summary>
public sealed record CodecCapabilities(
    IReadOnlyList<CodecEntry> Encoders,
    IReadOnlyList<CodecEntry> Decoders,
    string? SelectedEncoder,
    string SelectedReceiveDecoderPath)
{
    /// <summary>Probe the host once and return its usable HEVC hardware encode/decode capabilities.</summary>
    public static CodecCapabilities Probe()
    {
        FFmpegLibrary.EnsureLoaded();

        var encoders = new List<CodecEntry>();
        foreach ((string name, AVHWDeviceType type) in HevcEncoderSelector.Candidates)
        {
            encoders.Add(new CodecEntry(name, FFmpegLibrary.HasEncoder(name), HevcEncoderSelector.IsUsable(name, type)));
        }

        // Decoders: the FFmpeg named hardware decoders, plus the hwaccel paths keyed off a device probe. The
        // built-in software "hevc" decoder is always present and is the universal fallback.
        var decoders = new List<CodecEntry>
        {
            new("hevc (software)", FFmpegLibrary.HasDecoder("hevc"), true),
            new("hevc_cuvid", FFmpegLibrary.HasDecoder("hevc_cuvid"), CodecProbe.HwDeviceUsable(AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA)),
            new("hevc_qsv", FFmpegLibrary.HasDecoder("hevc_qsv"), CodecProbe.HwDeviceUsable(AVHWDeviceType.AV_HWDEVICE_TYPE_QSV)),
            new("hevc_rkmpp", FFmpegLibrary.HasDecoder("hevc_rkmpp"), true),
            new("hevc + vaapi", FFmpegLibrary.HasDecoder("hevc"), OperatingSystem.IsLinux() && VaapiDevice.IsAvailable()),
            new("hevc + videotoolbox", FFmpegLibrary.HasDecoder("hevc"), OperatingSystem.IsMacOS()),
        };

        string? selected = null;
        try { selected = HevcEncoderSelector.Select(); } catch (NotSupportedException) { /* none usable */ }

        return new CodecCapabilities(encoders, decoders, selected, ReceiveDecoderPath());
    }

    private static string ReceiveDecoderPath()
    {
        if (OperatingSystem.IsMacOS()) return "VTDecompressionSession (BGRA IOSurface, zero-copy) -> Syphon";
        if (OperatingSystem.IsLinux()) return VaapiDevice.IsAvailable() ? "VAAPI (DMA-BUF) -> PipeWire / CPU NV12" : "software hevc -> CPU";
        return "hardware-first (cuvid/qsv) -> software hevc, or D3D11VA -> Spout";
    }

    /// <summary>A human-readable capability report for <c>selftest caps</c>.</summary>
    public string Describe()
    {
        var sb = new StringBuilder();
        string os = OperatingSystem.IsMacOS() ? "macOS" : OperatingSystem.IsLinux() ? "Linux" : OperatingSystem.IsWindows() ? "Windows" : "?";
        sb.AppendLine($"== HEVC hardware codec capabilities ({os}) ==");
        sb.AppendLine($"FFmpeg: {FFmpegLibrary.VersionInfo ?? "(unknown)"}");
        sb.AppendLine("encoders (present / hw-usable):");
        foreach (CodecEntry e in Encoders)
        {
            sb.AppendLine($"  {Mark(e)} {e.Name,-20} present={e.Present,-5} usable={e.HardwareUsable}");
        }
        sb.AppendLine("decoders (present / hw-usable):");
        foreach (CodecEntry d in Decoders)
        {
            sb.AppendLine($"  {Mark(d)} {d.Name,-20} present={d.Present,-5} usable={d.HardwareUsable}");
        }
        sb.AppendLine($"auto-selected encoder: {SelectedEncoder ?? "(none usable - encode would fail)"}");
        sb.AppendLine($"receive decode path:   {SelectedReceiveDecoderPath}");
        return sb.ToString().TrimEnd();
    }

    private static string Mark(CodecEntry e) => e.Available ? "[x]" : "[ ]";
}
