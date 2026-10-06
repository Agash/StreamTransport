using Agash.StreamTransport.Media;
using Agash.StreamTransport.WebRtc.DependencyInjection;
using Agash.StreamTransport.WebRtc.Rtp;
using Agash.StreamTransport.WebRtc.Rtp.PayloadFormats;
using Agash.StreamTransport.WebRtc.Sdp;
using Microsoft.Extensions.DependencyInjection;

namespace Agash.StreamTransport.WebRtc.Tests;

[TestClass]
public sealed class RtpPayloadFormatRegistryTests
{
    [TestMethod]
    [DataRow("H264", 90_000)]
    [DataRow("h265", 90_000)]
    [DataRow("AV1", 90_000)]
    [DataRow("OPUS", 48_000)]
    public void BuiltIn_FindsEachFormatByNameInAnyCase(string name, int clockRate)
    {
        Assert.IsTrue(RtpPayloadFormatRegistry.BuiltIn.TryGet(name, out RtpPayloadFormat? format));
        Assert.AreEqual(clockRate, format.ClockRate);
        Assert.IsTrue(
            RtpPayloadFormatRegistry.BuiltIn.TryGet(
                new SdpCodec(96, name, clockRate, null, null, []),
                out _
            )
        );
    }

    [TestMethod]
    public void TryGet_CodecWithAnotherClockRate_FindsNothing()
    {
        Assert.IsFalse(
            RtpPayloadFormatRegistry.BuiltIn.TryGet(
                new SdpCodec(96, "H264", 48_000, null, null, []),
                out RtpPayloadFormat? format
            )
        );
        Assert.IsNull(format);
    }

    [TestMethod]
    public void ToSdpCodec_CarriesTheFormatsOffer()
    {
        var codec = H264PayloadFormat.Instance.ToSdpCodec(102);

        Assert.AreEqual(102, codec.PayloadType);
        Assert.AreEqual("H264", codec.EncodingName);
        StringAssert.Contains(codec.FormatParameters, "packetization-mode=1");
        CollectionAssert.AreEqual(new[] { "nack", "nack pli" }, codec.RtcpFeedback.ToArray());
        Assert.AreEqual(2, OpusPayloadFormat.Instance.ToSdpCodec(111).Channels);
    }

    [TestMethod]
    public void AddStreamTransportWebRtc_RegistersTheBuiltInFormats()
    {
        ServiceCollection services = new();
        services.AddStreamTransportWebRtc();
        using ServiceProvider provider = services.BuildServiceProvider();

        RtpPayloadFormatRegistry registry = provider.GetRequiredService<RtpPayloadFormatRegistry>();

        CollectionAssert.AreEquivalent(
            RtpPayloadFormatRegistry.BuiltIn.Formats.ToArray(),
            registry.Formats.ToArray()
        );
    }

    [TestMethod]
    public void AddRtpPayloadFormat_BeforeOrAfterTheStack_ReplacesTheBuiltInOfItsName()
    {
        foreach (bool first in (bool[])[true, false])
        {
            ServiceCollection services = new();
            if (first)
            {
                services.AddRtpPayloadFormat(new CustomH264());
            }

            services.AddStreamTransportWebRtc();
            if (!first)
            {
                services.AddRtpPayloadFormat(new CustomH264());
            }

            using ServiceProvider provider = services.BuildServiceProvider();
            RtpPayloadFormatRegistry registry =
                provider.GetRequiredService<RtpPayloadFormatRegistry>();

            Assert.IsTrue(registry.TryGet("H264", out RtpPayloadFormat? format));
            Assert.IsInstanceOfType<CustomH264>(format);
            Assert.HasCount(4, registry.Formats);
        }
    }

    [TestMethod]
    public void AddRtpPayloadFormat_NewEncodingName_JoinsTheRegistry()
    {
        ServiceCollection services = new();
        services.AddStreamTransportWebRtc().AddRtpPayloadFormat(new Vp8Like());
        using ServiceProvider provider = services.BuildServiceProvider();

        RtpPayloadFormatRegistry registry = provider.GetRequiredService<RtpPayloadFormatRegistry>();

        Assert.IsTrue(registry.TryGet("VP8", out RtpPayloadFormat? format));
        Assert.AreEqual(SdpMediaKind.Video, format.Kind);
        Assert.HasCount(5, registry.Formats);
    }

    [TestMethod]
    public void SingleFrameFormat_RoundTripsOnePacketPerFrame()
    {
        RtpPayloadWriter writer = new();
        IRtpPacketizer packetizer = OpusPayloadFormat.Instance.CreatePacketizer(1200);
        using IRtpDepacketizer depacketizer = OpusPayloadFormat.Instance.CreateDepacketizer();
        byte[] packet = [0xFC, 1, 2, 3];

        packetizer.Packetize(packet, writer);
        Assert.AreEqual(1, writer.Count);
        Assert.IsTrue(
            depacketizer.TryPush(writer[0].Span, marker: false, out EncodedFrameBuffer frame)
        );
        using (frame)
        {
            CollectionAssert.AreEqual(packet, frame.Span.ToArray());
        }

        Assert.ThrowsExactly<ArgumentException>(() =>
            OpusPayloadFormat.Instance.CreatePacketizer(3).Packetize(packet, writer)
        );
    }

    private sealed class CustomH264 : RtpPayloadFormat
    {
        public override string EncodingName => "H264";

        public override SdpMediaKind Kind => SdpMediaKind.Video;

        public override int ClockRate => 90_000;

        public override IRtpPacketizer CreatePacketizer(int maxPayloadSize) =>
            new H264Packetizer(maxPayloadSize);

        public override IRtpDepacketizer CreateDepacketizer() => new H264Depacketizer();
    }

    private sealed class Vp8Like : RtpPayloadFormat
    {
        public override string EncodingName => "VP8";

        public override SdpMediaKind Kind => SdpMediaKind.Video;

        public override int ClockRate => 90_000;

        public override IRtpPacketizer CreatePacketizer(int maxPayloadSize) =>
            new SingleFramePacketizer(maxPayloadSize);

        public override IRtpDepacketizer CreateDepacketizer() => new SingleFrameDepacketizer();
    }
}
