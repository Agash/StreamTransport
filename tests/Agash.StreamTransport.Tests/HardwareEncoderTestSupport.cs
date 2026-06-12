using Agash.StreamTransport.Codecs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Agash.StreamTransport.Tests;

/// <summary>
/// Shared helpers for the per-vendor hardware encoder tests. Each vendor test runs the same flow and
/// reports <see cref="Assert.Inconclusive(string)"/> when the encoder is missing from the build or the
/// supporting GPU is not present on the current machine, so the suite stays green everywhere while the
/// tests still exist for anyone with the right hardware to run.
/// </summary>
internal static class HardwareEncoderTestSupport
{
    /// <summary>Build a deterministic NV12 test pattern (XOR luma, neutral chroma).</summary>
    public static byte[] Nv12Pattern(int width, int height)
    {
        byte[] nv12 = new byte[width * height * 3 / 2];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                nv12[(y * width) + x] = (byte)((x ^ y) & 0xFF);
            }
        }

        for (int i = width * height; i < nv12.Length; i++)
        {
            nv12[i] = 128;
        }

        return nv12;
    }

    /// <summary>
    /// Verify that the named HEVC encoder produces a valid Annex-B access unit, or report inconclusive
    /// if the encoder or its hardware is unavailable.
    /// </summary>
    public static void AssertEncodesHevc(string encoderName)
    {
        string? nativeBin = TestNative.FindFFmpegBin();
        if (nativeBin is null)
        {
            Assert.Inconclusive("No bundled FFmpeg native build found.");
            return;
        }

        FFmpegLibrary.EnsureLoaded(nativeBin);
        if (!FFmpegLibrary.HasEncoder(encoderName))
        {
            Assert.Inconclusive($"{encoderName} is not present in this FFmpeg build.");
            return;
        }

        const int width = 1280;
        const int height = 720;
        byte[] nv12 = Nv12Pattern(width, height);

        HardwareHevcEncoder encoder;
        try
        {
            encoder = new HardwareHevcEncoder(encoderName, width, height, fps: 30, bitrate: 4_000_000);
        }
        catch (HardwareEncoderUnavailableException ex)
        {
            Assert.Inconclusive($"{encoderName} hardware is not available on this machine: {ex.Message}");
            return;
        }

        using (encoder)
        {
            byte[]? accessUnit = null;
            for (int frame = 0; frame < 10 && accessUnit is null; frame++)
            {
                accessUnit = encoder.EncodeNv12(nv12);
            }

            Assert.IsNotNull(accessUnit, $"Expected an HEVC access unit from {encoderName}.");
            Assert.IsTrue(accessUnit.Length > 4, "Access unit should carry payload.");
            Assert.IsTrue(
                accessUnit[0] == 0 && accessUnit[1] == 0 && (accessUnit[2] == 1 || (accessUnit[2] == 0 && accessUnit[3] == 1)),
                "Access unit should begin with an Annex-B start code.");
        }
    }
}
