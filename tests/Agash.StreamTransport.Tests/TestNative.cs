using System.Runtime.InteropServices;

namespace Agash.StreamTransport.Tests;

/// <summary>Locates the gitignored development FFmpeg native build by walking up from the test output.</summary>
internal static class TestNative
{
    /// <summary>Find the FFmpeg <c>bin</c> directory containing the shared libraries, or null if absent.</summary>
    public static string? FindFFmpegBin()
    {
        // Prefer the directory matching this process's architecture (e.g. "linux-x64", not a sibling
        // "linux-arm64" fetched for a cross-publish), so a multi-RID dev checkout loads the right build.
        string archToken = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "-arm64",
            Architecture.X64 => "-x64",
            Architecture.X86 => "-x86",
            _ => "-" + RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
        };

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string ffmpegRoot = Path.Combine(dir.FullName, "native", "ffmpeg");
            if (Directory.Exists(ffmpegRoot))
            {
                // Match the avcodec shared library by THIS OS's extension so a multi-RID dev checkout (e.g.
                // both win-x64 and linux-x64 fetched) only ever picks the build that can actually load here:
                // Windows "avcodec-62.dll", Linux "libavcodec.so.62", macOS "libavcodec.62.dylib".
                string?[] dirs = [.. Directory.EnumerateFiles(ffmpegRoot, "*", SearchOption.AllDirectories)
                    .Where(f => Path.GetFileName(f).Contains("avcodec", StringComparison.OrdinalIgnoreCase)
                        && IsCurrentOsLibrary(f))
                    .Select(Path.GetDirectoryName)
                    .Distinct()];

                // Within this OS's builds, prefer the one matching the process architecture (x64 vs arm64).
                return dirs.FirstOrDefault(d => d is not null
                        && Path.GetFileName(d).EndsWith(archToken, StringComparison.OrdinalIgnoreCase))
                    ?? dirs.FirstOrDefault(d => d is not null);
            }

            dir = dir.Parent;
        }

        return null;
    }

    private static bool IsCurrentOsLibrary(string file)
    {
        if (OperatingSystem.IsWindows())
        {
            return file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        }

        if (OperatingSystem.IsMacOS())
        {
            return file.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase);
        }

        return file.Contains(".so", StringComparison.OrdinalIgnoreCase);
    }
}
