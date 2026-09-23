using Agash.StreamTransport.Codecs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Agash.StreamTransport.Tests;

/// <summary>
/// Verifies the bundled native FFmpeg can be loaded and exposes the hardware encoders we rely on. These
/// run only where the native FFmpeg build is present (a developer machine or the win-x64 native package);
/// elsewhere they report inconclusive rather than fail.
/// </summary>
[TestClass]
public sealed class FFmpegSmokeTests
{
    [TestMethod]
    public void Load_FindsFFmpeg81_AndPlatformHevcEncoder()
    {
        string? nativeBin = TestNative.FindFFmpegBin();
        if (nativeBin is null)
        {
            Assert.Inconclusive(
                "No bundled FFmpeg native build found; skipping hardware-encoder smoke test."
            );
            return;
        }

        FFmpegLibrary.EnsureLoaded(nativeBin);

        Assert.IsNotNull(
            FFmpegLibrary.VersionInfo,
            "FFmpeg version info should be populated after load."
        );
        // The bindings pin one avcodec ABI major; the loaded build must report the matching release.
        string expectedMajor = FFmpeg.AutoGen.ffmpeg.LibraryVersionMap["avcodec"] switch
        {
            63 => "9.",
            int other => throw new AssertFailedException(
                $"No FFmpeg release mapped for avcodec {other}."
            ),
        };
        Assert.Contains(
            expectedMajor,
            FFmpegLibrary.VersionInfo,
            $"Expected FFmpeg {expectedMajor}x, got '{FFmpegLibrary.VersionInfo}'."
        );

        // The bundled build must expose at least one hardware HEVC encoder, but which one is platform- and
        // GPU-specific: VideoToolbox on macOS, nvenc/amf/qsv/vaapi in the BtbN x64 builds, rkmpp on the
        // Rockchip linux-arm64 build. Assert the union rather than any single encoder so this passes on
        // every supported target. HasEncoder reports build-level presence, not GPU availability.
        string[] hevcHardwareEncoders =
        [
            "hevc_videotoolbox",
            "hevc_nvenc",
            "hevc_amf",
            "hevc_qsv",
            "hevc_vaapi",
            "hevc_rkmpp",
        ];
        string[] present = [.. hevcHardwareEncoders.Where(FFmpegLibrary.HasEncoder)];
        Assert.IsTrue(
            present.Length > 0,
            $"Expected at least one hardware HEVC encoder in the build; found none of: {string.Join(", ", hevcHardwareEncoders)}."
        );
    }
}
