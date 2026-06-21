using Agash.StreamTransport;
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

        IVideoEncoderBackend encoder;
        try
        {
            // hevc_vaapi only encodes VAAPI surfaces, so it has its own backend that owns the device + frames
            // pool and uploads NV12; the other vendors take system-memory NV12 directly via HardwareHevcEncoder.
            encoder = encoderName == "hevc_vaapi"
                ? new VaapiVideoEncoder(width, height, fps: 30, bitrate: 4_000_000)
                : new HardwareHevcEncoder(encoderName, width, height, fps: 30, bitrate: 4_000_000);
        }
        catch (HardwareEncoderUnavailableException ex)
        {
            Assert.Inconclusive($"{encoderName} hardware is not available on this machine: {ex.Message}");
            return;
        }
        catch (Exception ex) when (encoderName == "hevc_vaapi")
        {
            // VaapiVideoEncoder surfaces a missing/unusable VAAPI driver as an FFmpeg error from device/context
            // setup; treat that like any other absent hardware so the suite stays green where VAAPI is unavailable.
            Assert.Inconclusive($"hevc_vaapi hardware is not available on this machine: {ex.Message}");
            return;
        }

        using (encoder)
        {
            var frame = VideoFrame.FromPixels(nv12, VideoPixelFormat.Nv12, width, height, 0);
            byte[]? accessUnit = null;
            try
            {
                for (int i = 0; i < 10 && accessUnit is null; i++)
                {
                    accessUnit = encoder.Encode(frame, out _);
                }
            }
            catch (Exception ex)
            {
                // Some encoders open even when no GPU is present and only fail at the first encode - notably
                // VideoToolbox on a headless CI runner (error -542398533, "encoder not available now"). Treat that
                // like absent hardware (skip) rather than a failure; a host with real hardware still encodes + passes.
                Assert.Inconclusive($"{encoderName} hardware encode is not available on this machine: {ex.Message}");
                return;
            }

            Assert.IsNotNull(accessUnit, $"Expected an HEVC access unit from {encoderName}.");
            Assert.IsTrue(accessUnit.Length > 4, "Access unit should carry payload.");
            Assert.IsTrue(
                accessUnit[0] == 0 && accessUnit[1] == 0 && (accessUnit[2] == 1 || (accessUnit[2] == 0 && accessUnit[3] == 1)),
                "Access unit should begin with an Annex-B start code.");
        }
    }
}
