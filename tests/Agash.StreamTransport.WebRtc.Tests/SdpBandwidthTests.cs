using System.Net;
using Agash.StreamTransport.WebRtc.Sdp;

namespace Agash.StreamTransport.WebRtc.Tests;

// RFC 8866 section 5.8, RFC 3556, RFC 3890 and RFC 8829 section 5.10.
[TestClass]
public sealed class SdpBandwidthTests
{
    private static readonly InMemoryIceNetwork OfflineNetwork = new();
    private static readonly RtcCertificate Certificate = RtcCertificate.Generate();

    private static readonly SdpCodec H264 = new(
        96,
        "H264",
        90000,
        null,
        "packetization-mode=1",
        ["nack", "nack pli"]
    );

    private const string Description = """
        v=0
        o=- 1 1 IN IP4 127.0.0.1
        s=-
        t=0 0
        b=CT:3000
        b=AS:9999
        b=RS:800
        m=video 9 UDP/TLS/RTP/SAVPF 96
        c=IN IP4 0.0.0.0
        b=AS:2000
        b=TIAS:1800000
        b=RR:2400
        b=X-UNKNOWN:5
        a=mid:0
        a=ice-ufrag:ufrag
        a=ice-pwd:passwordpasswordpassword
        a=fingerprint:sha-256 00:11:22:33:44:55:66:77:88:99:AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99:AA:BB:CC:DD:EE:FF
        a=setup:actpass
        a=rtpmap:96 H264/90000

        """;

    // AS and CT count kilobits, TIAS, RS and RR bits; an unknown type is ignored, and so is a session AS.
    [TestMethod]
    public void Read_TakesEachTypeInItsUnits()
    {
        Assert.IsTrue(SdpReader.TryParse(Description, out SdpDescription read));

        Assert.AreEqual(
            new SdpBandwidth(RtcpSenders: 800, ConferenceTotal: 3_000_000),
            read.Bandwidth
        );
        Assert.AreEqual(
            new SdpBandwidth(
                ApplicationSpecific: 2_000_000,
                TransportIndependent: 1_800_000,
                RtcpReceivers: 2400
            ),
            read.Media[0].Bandwidth
        );
    }

    [TestMethod]
    [DataRow("b=AS:-5")]
    [DataRow("b=TIAS:fast")]
    [DataRow("b=AS")]
    public void Read_AMalformedValue_FailsTheDescription(string line)
    {
        Assert.IsFalse(SdpReader.TryParse(Description.Replace("b=AS:2000", line), out _));
    }

    [TestMethod]
    public void Write_RoundTripsEveryLine()
    {
        Assert.IsTrue(SdpReader.TryParse(Description, out SdpDescription read));

        Assert.IsTrue(SdpReader.TryParse(SdpWriter.Write(read), out SdpDescription again));

        Assert.AreEqual(read.Bandwidth, again.Bandwidth);
        Assert.AreEqual(read.Media[0].Bandwidth, again.Media[0].Bandwidth);
    }

    // RFC 8829 section 5.10: TIAS when given, else AS * 1000 * 0.95 - 50 * 40 * 8.
    [TestMethod]
    public void MaximumRtpBitsPerSecond_FollowsJsep()
    {
        Assert.AreEqual(1_800_000, new SdpBandwidth(2_000_000, 1_800_000).MaximumRtpBitsPerSecond);
        Assert.AreEqual(934_000, new SdpBandwidth(1_000_000).MaximumRtpBitsPerSecond);
        Assert.IsNull(new SdpBandwidth(RtcpSenders: 5).MaximumRtpBitsPerSecond);
    }

    // A receiving line's limit goes out as TIAS and the AS JSEP turns back into at least that limit.
    [TestMethod]
    public async Task Offer_WritesTheReceiveLimit()
    {
        await using PeerConnection peer = new(
            new PeerConnectionOptions
            {
                SocketFactory = OfflineNetwork.Factory(IPAddress.Parse("10.0.0.1")),
                Media =
                [
                    new MediaLine("0", SdpMediaKind.Video, 0xAAAA_0001, [H264])
                    {
                        MaximumReceiveBitsPerSecond = 2_500_000,
                    },
                ],
            },
            Certificate
        );

        Assert.IsTrue(
            SdpReader.TryParse(SdpWriter.Write(peer.CreateOffer()), out SdpDescription offer)
        );

        SdpBandwidth written = offer.Media[0].Bandwidth;
        Assert.AreEqual(2_500_000, written.TransportIndependent);
        Assert.IsGreaterThanOrEqualTo(
            2_500_000,
            (new SdpBandwidth(written.ApplicationSpecific)).MaximumRtpBitsPerSecond!.Value
        );
    }

    // The peer's TIAS limits the section, its CT the whole.
    [TestMethod]
    public async Task RemoteLimits_BecomeSendLimits()
    {
        await using PeerConnection offerer = Peer("10.0.0.1", 0xAAAA_0001);
        await using PeerConnection answerer = Peer("10.0.0.2", 0xBBBB_0001);
        answerer.SetRemoteDescription(offerer.CreateOffer(), SdpType.Offer);
        SdpDescription answer = answerer.CreateAnswer();

        offerer.SetRemoteDescription(
            answer with
            {
                Bandwidth = new SdpBandwidth(ConferenceTotal: 1_000_000),
                Media =
                [
                    answer.Media[0] with
                    {
                        Bandwidth = new SdpBandwidth(TransportIndependent: 1_500_000),
                    },
                ],
            },
            SdpType.Answer
        );

        Assert.AreEqual(1_500_000, offerer.NegotiatedMedia[0].SendLimitBitsPerSecond);
        Assert.AreEqual(1_000_000, offerer.SendLimitBitsPerSecond);
    }

    private static PeerConnection Peer(string address, uint ssrc) =>
        new(
            new PeerConnectionOptions
            {
                SocketFactory = OfflineNetwork.Factory(IPAddress.Parse(address)),
                Media = [new MediaLine("0", SdpMediaKind.Video, ssrc, [H264])],
            },
            Certificate
        );
}
