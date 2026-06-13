using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Agash.StreamTransport.Tests;

/// <summary>Verifies HEVC encoding through each vendor's hardware encoder via system-memory NV12 input.</summary>
// Isolate from the parallel pool: a hardware encode session (e.g. VideoToolbox on a real macOS runner) can
// fail to initialise under heavy concurrent load, so give it the sequential scope.
[TestClass]
[DoNotParallelize]
public sealed class HardwareEncoderTests
{
    [TestMethod]
    public void Nvenc_EncodesHevc() => HardwareEncoderTestSupport.AssertEncodesHevc("hevc_nvenc");

    [TestMethod]
    public void Amf_EncodesHevc() => HardwareEncoderTestSupport.AssertEncodesHevc("hevc_amf");

    [TestMethod]
    public void Qsv_EncodesHevc() => HardwareEncoderTestSupport.AssertEncodesHevc("hevc_qsv");

    [TestMethod]
    public void Videotoolbox_EncodesHevc() => HardwareEncoderTestSupport.AssertEncodesHevc("hevc_videotoolbox");
}
