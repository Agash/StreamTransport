#if WINDOWS_D3D11
using Agash.StreamTransport.Codecs;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Vortice.Direct3D11;

namespace Agash.StreamTransport.Tests;

[TestClass]
public sealed class D3D11DiagnosticTests
{
    [TestMethod]
    public void Construct_D3D11Encoder_ExposesValidDevice()
    {
        string? nativeBin = TestNative.FindFFmpegBin();
        if (nativeBin is null)
        {
            Assert.Inconclusive("FFmpeg native build not available.");
            return;
        }

        FFmpegLibrary.EnsureLoaded(nativeBin);
        if (!FFmpegLibrary.HasEncoder("hevc_nvenc"))
        {
            Assert.Inconclusive("hevc_nvenc not available.");
            return;
        }

        D3D11VideoEncoder encoder;
        try
        {
            encoder = new D3D11VideoEncoder("hevc_nvenc", 1280, 720, fps: 30, bitrate: 4_000_000);
        }
        catch (Exception ex)
        {
            // A GPU-less host (e.g. a headless CI runner) can't create the D3D11VA device - skip, don't fail.
            Assert.Inconclusive($"D3D11 encoder hardware not available: {ex.Message}");
            return;
        }

        using (encoder)
        {
            Assert.AreNotEqual(0, encoder.NativeDevice, "Encoder should expose a non-null D3D11 device.");

            // Prove the device pointer is a real ID3D11Device by reading its feature level through Vortice.
            using var device = new ID3D11Device(encoder.NativeDevice);
            device.AddRef(); // balance Vortice's Dispose Release against FFmpeg's ownership.
            Vortice.Direct3D.FeatureLevel level = device.FeatureLevel;
            Assert.IsTrue((int)level >= (int)Vortice.Direct3D.FeatureLevel.Level_11_0, $"Unexpected feature level {level}.");
        }
    }
}
#endif
