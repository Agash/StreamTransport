using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace Agash.StreamTransport.Codecs;

/// <summary>
/// Points FFmpeg.AutoGen at the bundled native FFmpeg 8.1 shared libraries and loads them once. The
/// libraries ship under <c>runtimes/&lt;rid&gt;/native</c>; during development they are fetched into a
/// gitignored <c>native/ffmpeg</c> folder. Call <see cref="EnsureLoaded()"/> before any FFmpeg use.
/// </summary>
public static class FFmpegLibrary
{
    private static readonly object s_gate = new();
    private static bool s_loaded;

    /// <summary>The FFmpeg version string reported by the loaded libraries (e.g. "8.1").</summary>
    public static string? VersionInfo { get; private set; }

    /// <summary>
    /// Load the bundled native FFmpeg libraries by probing the standard locations for the current RID:
    /// <c>runtimes/&lt;rid&gt;/native</c> beside the assembly, the assembly directory itself (where a
    /// single-RID publish flattens them), and a development <c>native/ffmpeg/&lt;rid&gt;</c> folder found
    /// by walking up from the assembly. Throws <see cref="DllNotFoundException"/> if none contain FFmpeg.
    /// </summary>
    public static void EnsureLoaded()
    {
        if (s_loaded)
        {
            return;
        }

        string? directory = ResolveNativeDirectory()
            ?? throw new DllNotFoundException(
                "Could not locate the bundled FFmpeg shared libraries. Expected them under " +
                $"runtimes/{Rid}/native, the application directory, or native/ffmpeg/{Rid}.");

        EnsureLoaded(directory);
    }

    private static string Rid => RuntimeInformation.RuntimeIdentifier;

    private static string? ResolveNativeDirectory()
    {
        string baseDir = AppContext.BaseDirectory;
        string[] candidates =
        [
            Path.Combine(baseDir, "runtimes", Rid, "native"),
            baseDir,
        ];

        foreach (string candidate in candidates)
        {
            if (ContainsFFmpeg(candidate))
            {
                return candidate;
            }
        }

        // Development fallback: walk up to a fetched native/ffmpeg/<rid> folder.
        var dir = new DirectoryInfo(baseDir);
        while (dir is not null)
        {
            string devDir = Path.Combine(dir.FullName, "native", "ffmpeg", Rid);
            if (ContainsFFmpeg(devDir))
            {
                return devDir;
            }

            dir = dir.Parent;
        }

        return null;
    }

    private static bool ContainsFFmpeg(string directory) =>
        Directory.Exists(directory)
        && Directory.EnumerateFiles(directory)
            // Match the avcodec shared library under every platform's naming: Windows "avcodec-62.dll",
            // Linux "libavcodec.so.62", macOS "libavcodec.62.dylib".
            .Any(f => Path.GetFileName(f).Contains("avcodec", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Load the native FFmpeg libraries from <paramref name="nativeDirectory"/>. Idempotent; the first
    /// successful call wins. Throws <see cref="DllNotFoundException"/> if the libraries cannot be loaded.
    /// </summary>
    public static unsafe void EnsureLoaded(string nativeDirectory)
    {
        lock (s_gate)
        {
            if (s_loaded)
            {
                return;
            }

            ffmpeg.RootPath = nativeDirectory;
            DynamicallyLoadedBindings.ThrowErrorIfFunctionNotFound = true;

            try
            {
                VersionInfo = ffmpeg.av_version_info();
            }
            catch (Exception ex)
            {
                throw new DllNotFoundException(
                    $"Could not load FFmpeg shared libraries from '{nativeDirectory}'. Ensure the FFmpeg 8.1 " +
                    "shared build (avcodec-62, avutil-60, ...) is present and loadable.", ex);
            }

            s_loaded = true;
        }
    }

    /// <summary>True if an encoder with the given name (e.g. "hevc_nvenc") is available in the loaded build.</summary>
    public static unsafe bool HasEncoder(string name) => ffmpeg.avcodec_find_encoder_by_name(name) is not null;

    /// <summary>True if a decoder with the given name (e.g. "hevc_cuvid") is available in the loaded build.</summary>
    public static unsafe bool HasDecoder(string name) => ffmpeg.avcodec_find_decoder_by_name(name) is not null;
}
