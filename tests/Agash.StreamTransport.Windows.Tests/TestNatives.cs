using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FF = FFmpeg.Interop;

namespace Agash.StreamTransport.Windows.Tests;

internal static class TestNatives
{
    // The FFmpeg 9 shared libraries eng/fetch-ffmpeg.ps1 puts under native/ffmpeg/<rid>, set when the
    // assembly loads: before anything, discovery included, can load FFmpeg from elsewhere.
    [ModuleInitializer]
    public static void Load()
    {
        for (
            DirectoryInfo? directory = new(AppContext.BaseDirectory);
            directory is not null;
            directory = directory.Parent
        )
        {
            string natives = Path.Combine(
                directory.FullName,
                "native",
                "ffmpeg",
                RuntimeInformation.RuntimeIdentifier
            );
            if (Directory.Exists(natives))
            {
                FF.FFmpegLibraries.SearchDirectory = natives;
                return;
            }
        }
    }
}
