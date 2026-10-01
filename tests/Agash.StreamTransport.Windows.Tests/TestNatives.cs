using System.Runtime.InteropServices;
using FF = FFmpeg.Interop;

namespace Agash.StreamTransport.Windows.Tests;

[TestClass]
public static class TestNatives
{
    // The FFmpeg 9 shared libraries eng/fetch-ffmpeg.ps1 puts under native/ffmpeg/<rid>.
    [AssemblyInitialize]
    public static void Load(TestContext context)
    {
        _ = context;
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
