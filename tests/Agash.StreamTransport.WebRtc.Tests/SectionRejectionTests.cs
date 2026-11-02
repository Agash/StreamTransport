using Agash.StreamTransport.WebRtc.Sdp;

namespace Agash.StreamTransport.WebRtc.Tests;

[TestClass]
public sealed class SectionRejectionTests
{
    private static readonly RtcCertificate Certificate = RtcCertificate.Generate();

    [TestMethod]
    public async Task Answer_NoCodecInCommon_RejectsTheSection()
    {
        await using var offerer = new PeerConnection(
            Options(0xAAAA_0001, new SdpCodec(96, "VP8", 90000, null, null, [])),
            Certificate
        );
        await using var answerer = new PeerConnection(
            Options(0xBBBB_0001, new SdpCodec(96, "H264", 90000, null, "packetization-mode=1", [])),
            Certificate
        );

        answerer.SetRemoteDescription(offerer.CreateOffer(), SdpType.Offer);
        SdpDescription answer = answerer.CreateAnswer();
        Assert.IsTrue(SdpReader.TryParse(SdpWriter.Write(answer), out SdpDescription read));
        offerer.SetRemoteDescription(read, SdpType.Answer);

        SdpMediaDescription section = read.Media[0];
        Assert.IsTrue(section.Rejected, "port 0 on the m-line");
        Assert.AreEqual(SdpDirection.Inactive, section.Direction);
        Assert.AreEqual(
            "VP8",
            section.Codecs[0].EncodingName,
            "a rejected section lists the offered formats"
        );
        StringAssert.StartsWith(SdpWriter.Write(answer).Split("m=video")[1], " 0 ");
        Assert.IsEmpty(answerer.NegotiatedMedia);
        Assert.IsEmpty(offerer.NegotiatedMedia);
    }

    private static PeerConnectionOptions Options(uint ssrc, SdpCodec codec) =>
        new() { Media = [new MediaLine("0", SdpMediaKind.Video, ssrc, [codec])] };
}
