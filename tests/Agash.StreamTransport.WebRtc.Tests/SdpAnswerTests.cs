using System.Net;
using Agash.StreamTransport.WebRtc.Sdp;

namespace Agash.StreamTransport.WebRtc.Tests;

// The answer a PeerConnection builds, rule by rule: RFC 3264 section 6 and RFC 9429 section 5.3.1 (an
// answer mirrors its offer), RFC 8858 section 4.3 (no rtcp-mux-only in an answer), RFC 9143 sections 6
// and 7.3 (bundle-only, a BUNDLE group only when offered), RFC 5763 section 5 (setup follows the offer),
// RFC 4585 section 4.2 (no feedback the offer did not list), RFC 5761 section 4 (no payload types 64-95)
// and RFC 3264 section 8 (one o= session, a new version per description).
[TestClass]
public sealed class SdpAnswerTests
{
    private static readonly RtcCertificate Certificate = RtcCertificate.Generate();

    private static readonly SdpCodec H264 = new(
        96,
        "H264",
        90000,
        null,
        "packetization-mode=1;profile-level-id=42e01f",
        ["nack", "nack pli", "ccm fir"]
    );

    private static readonly SdpCodec Opus = new(111, "opus", 48000, 2, null, []);

    [TestMethod]
    public async Task Answer_MirrorsTheBrowserOffer()
    {
        await using PeerConnection answerer = Answerer();
        answerer.SetRemoteDescription(Parse(BrowserOffer()), SdpType.Offer);

        string answer = SdpWriter.Write(answerer.CreateAnswer());

        StringAssert.Contains(answer, "a=group:BUNDLE 0 1\r\n");
        StringAssert.Contains(answer, "a=rtcp-mux\r\n");
        Assert.DoesNotContain("a=rtcp-mux-only", answer, "never in an answer");
        StringAssert.Contains(answer, "a=setup:active\r\n");
        StringAssert.Contains(answer, "a=rtcp-fb:96 nack pli\r\n");
        Assert.DoesNotContain("ccm fir", answer, "the offer did not list it");
        StringAssert.Contains(answer, "m=application 0 UDP/DTLS/SCTP webrtc-datachannel\r\n");
    }

    [TestMethod]
    public async Task Answer_ToAnActiveOffer_IsPassive()
    {
        await using PeerConnection answerer = Answerer();
        answerer.SetRemoteDescription(
            Parse(
                BrowserOffer()
                    .Replace("a=setup:actpass", "a=setup:active", StringComparison.Ordinal)
            ),
            SdpType.Offer
        );

        StringAssert.Contains(SdpWriter.Write(answerer.CreateAnswer()), "a=setup:passive\r\n");
    }

    [TestMethod]
    public async Task Answer_WithoutAnOfferedGroup_HasNoGroup_AndRunsOneSection()
    {
        await using PeerConnection answerer = Answerer();
        answerer.SetRemoteDescription(
            Parse(BrowserOffer().Replace("a=group:BUNDLE 0 1 2\r\n", "", StringComparison.Ordinal)),
            SdpType.Offer
        );

        SdpDescription answer = answerer.CreateAnswer();

        Assert.IsNull(answer.BundleGroup);
        Assert.IsFalse(answer.Media[0].Rejected);
        Assert.IsTrue(answer.Media[1].Rejected, "a second transport is not run");
    }

    [TestMethod]
    public async Task Answer_KeepsARejectedSectionRejected_AndRefusesPayloadTypes64To95()
    {
        await using PeerConnection answerer = Answerer();
        string offer = BrowserOffer()
            .Replace(
                "m=audio 9 UDP/TLS/RTP/SAVPF 111",
                "m=audio 0 UDP/TLS/RTP/SAVPF 111",
                StringComparison.Ordinal
            )
            .Replace(
                "m=video 9 UDP/TLS/RTP/SAVPF 96",
                "m=video 9 UDP/TLS/RTP/SAVPF 77",
                StringComparison.Ordinal
            )
            .Replace("a=rtpmap:96 ", "a=rtpmap:77 ", StringComparison.Ordinal);
        answerer.SetRemoteDescription(Parse(offer), SdpType.Offer);

        SdpDescription answer = answerer.CreateAnswer();

        Assert.IsTrue(answer.Media[0].Rejected, "rejected in the offer");
        Assert.IsTrue(answer.Media[1].Rejected, "77 is not a payload type with rtcp-mux");
    }

    [TestMethod]
    public void Reader_BundleOnlySection_IsNotRejected()
    {
        string offer = BrowserOffer()
            .Replace("m=video 9 ", "m=video 0 ", StringComparison.Ordinal)
            .Replace("a=mid:1\r\n", "a=mid:1\r\na=bundle-only\r\n", StringComparison.Ordinal);

        SdpDescription read = Parse(offer);

        Assert.IsFalse(read.Media[1].Rejected);
        Assert.IsTrue(read.Media[1].BundleOnly);
        Assert.IsTrue(read.Media[2].Rejected is false && read.Media[2].Protocol == "UDP/DTLS/SCTP");
    }

    [TestMethod]
    public async Task Descriptions_KeepTheSessionId_AndRaiseTheVersion()
    {
        await using PeerConnection offerer = Answerer();

        SdpDescription first = offerer.CreateOffer();
        SdpDescription second = offerer.CreateOffer();

        Assert.AreEqual(first.SessionId, second.SessionId);
        Assert.AreEqual(first.SessionVersion + 1, second.SessionVersion);
        StringAssert.Contains(
            SdpWriter.Write(second),
            $"o=- {second.SessionId} {second.SessionVersion} IN IP4"
        );
        Assert.IsTrue(
            first.Media.All(static m => m.RtcpMuxOnly),
            "offers are mux-only (RFC 9725 4.4.1)"
        );
    }

    private static PeerConnection Answerer() =>
        new(
            new PeerConnectionOptions
            {
                Media =
                [
                    new MediaLine("0", SdpMediaKind.Audio, 0xA1, [Opus]),
                    new MediaLine("1", SdpMediaKind.Video, 0xB1, [H264]),
                ],
                SocketFactory = new InMemoryIceNetwork().Factory(IPAddress.Parse("10.0.0.2")),
            },
            Certificate
        );

    private static SdpDescription Parse(string sdp)
    {
        Assert.IsTrue(SdpReader.TryParse(sdp, out SdpDescription description), sdp);
        return description;
    }

    // An offer as Chrome writes one: BUNDLE over audio, video and a data channel, actpass, rtcp-mux-only.
    private static string BrowserOffer() =>
        string.Join(
            "\r\n",
            "v=0",
            "o=- 4611731400430051336 2 IN IP4 127.0.0.1",
            "s=-",
            "t=0 0",
            "a=group:BUNDLE 0 1 2",
            "a=msid-semantic: WMS",
            "m=audio 9 UDP/TLS/RTP/SAVPF 111",
            "c=IN IP4 0.0.0.0",
            "a=rtcp:9 IN IP4 0.0.0.0",
            "a=ice-ufrag:abcd",
            "a=ice-pwd:0123456789abcdef0123456789",
            $"a=fingerprint:{Certificate.Fingerprint}",
            "a=setup:actpass",
            "a=mid:0",
            "a=sendrecv",
            "a=rtcp-mux",
            "a=rtcp-mux-only",
            "a=rtpmap:111 opus/48000/2",
            "m=video 9 UDP/TLS/RTP/SAVPF 96",
            "c=IN IP4 0.0.0.0",
            "a=rtcp:9 IN IP4 0.0.0.0",
            "a=ice-ufrag:abcd",
            "a=ice-pwd:0123456789abcdef0123456789",
            $"a=fingerprint:{Certificate.Fingerprint}",
            "a=setup:actpass",
            "a=mid:1",
            "a=sendrecv",
            "a=rtcp-mux",
            "a=rtcp-mux-only",
            "a=rtpmap:96 H264/90000",
            "a=rtcp-fb:96 nack",
            "a=rtcp-fb:96 nack pli",
            "a=fmtp:96 level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42e01f",
            "m=application 9 UDP/DTLS/SCTP webrtc-datachannel",
            "c=IN IP4 0.0.0.0",
            "a=ice-ufrag:abcd",
            "a=ice-pwd:0123456789abcdef0123456789",
            $"a=fingerprint:{Certificate.Fingerprint}",
            "a=setup:actpass",
            "a=mid:2",
            "a=sctp-port:5000",
            ""
        );
}
