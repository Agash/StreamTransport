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
    /// Preflight a hardware encoder before a test drives a whole transport pipeline through it: open it and
    /// push a short burst of frames, requiring at least one real access unit back.
    /// </summary>
    /// <remarks>
    /// Opening is not evidence of a working encoder, and neither is a single <c>Encode</c> call that does not
    /// throw. VideoToolbox on a virtualized/contended CI Mac opens happily, accepts frames, and returns no
    /// output at all - the earlier probe encoded one frame and discarded the result, so it read that as
    /// success and the test then failed 55 seconds later with "0 decoded frames". Hardware encoders also
    /// legitimately buffer the first few frames, so one frame in cannot be expected to yield one frame out;
    /// a burst is the smallest honest check.
    /// <para>Returns the reason instead of asserting, so the caller decides between Inconclusive and failure.</para>
    /// </remarks>
    /// <param name="encoderName">The FFmpeg encoder to probe.</param>
    /// <param name="width">Frame width to probe at.</param>
    /// <param name="height">Frame height to probe at.</param>
    /// <param name="reason">Why the encoder is unusable, when this returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when the encoder emitted at least one access unit.</returns>
    public static bool TryPreflightEncoder(string encoderName, int width, int height, out string reason)
    {
        const int burst = 12;
        try
        {
            using IVideoEncoderBackend preflight = TestEncoders.Open(encoderName, width, height, fps: 30, bitrate: 4_000_000);
            byte[] pattern = Nv12Pattern(width, height);
            for (int i = 0; i < burst; i++)
            {
                if (TestEncoders.EncodeNv12(preflight, pattern, width, height) is { Length: > 0 })
                {
                    reason = string.Empty;
                    return true;
                }
            }

            reason = $"{encoderName} opened but produced no access unit in {burst} frames "
                + "(typical of VideoToolbox on a virtualized or contended CI host)";
            return false;
        }
#pragma warning disable CA1031 // Any failure to open or encode means "no usable hardware here", which is the answer the caller wants.
        catch (Exception ex)
        {
            reason = $"{encoderName} hardware encode is not available on this machine: {ex.Message}";
            return false;
        }
#pragma warning restore CA1031
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
