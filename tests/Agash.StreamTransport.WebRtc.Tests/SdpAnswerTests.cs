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

    [TestMethod]
    public async Task Answer_SectionOutsideTheOfferedGroup_IsRefused()
    {
        await using PeerConnection answerer = Answerer();
        answerer.SetRemoteDescription(
            Parse(
                BrowserOffer()
                    .Replace("a=group:BUNDLE 0 1 2", "a=group:BUNDLE 0 2", StringComparison.Ordinal)
            ),
            SdpType.Offer
        );

        SdpDescription answer = answerer.CreateAnswer();

        Assert.IsFalse(answer.Media[0].Rejected);
        Assert.IsTrue(
            answer.Media[1].Rejected,
            "video is outside the group, so it would need a transport of its own (RFC 9143 section 7.3)"
        );
        CollectionAssert.AreEqual(new[] { "0" }, answer.BundleGroup!.Value.ToArray());
    }

    [TestMethod]
    [DataRow("1 0 2", new[] { "1", "0" })]
    [DataRow("2 1 0", new[] { "1", "0" })]
    public async Task Answer_GroupStartsWithTheTaggedSection(string offered, string[] answered)
    {
        await using PeerConnection answerer = Answerer();
        answerer.SetRemoteDescription(
            Parse(
                BrowserOffer()
                    .Replace(
                        "a=group:BUNDLE 0 1 2",
                        "a=group:BUNDLE " + offered,
                        StringComparison.Ordinal
                    )
            ),
            SdpType.Offer
        );

        SdpDescription answer = answerer.CreateAnswer();

        // RFC 9143 section 7.3.1: the first tag whose section is accepted is the offerer-tagged one, and
        // the answer's BUNDLE-tag, its first, names the matching answerer-tagged section.
        CollectionAssert.AreEqual(answered, answer.BundleGroup!.Value.ToArray());
    }

    [TestMethod]
    public async Task Offer_TransportOnlyInTheTaggedSection_IsRead()
    {
        // A subsequent offer as RFC 9143 section 7.1.3 writes one: the BUNDLE attributes appear only in the
        // tagged section, here the video section the group names first, not the first m= line.
        string audioTransport = string.Join(
            "\r\n",
            "a=ice-ufrag:abcd",
            "a=ice-pwd:0123456789abcdef0123456789",
            $"a=fingerprint:{Certificate.Fingerprint}",
            "a=setup:actpass",
            "a=mid:0"
        );
        string offer = BrowserOffer()
            .Replace("a=group:BUNDLE 0 1 2", "a=group:BUNDLE 1 0", StringComparison.Ordinal)
            .Replace(audioTransport, "a=mid:0", StringComparison.Ordinal)
            .Replace(
                "a=setup:actpass\r\na=mid:1",
                "a=setup:active\r\na=mid:1",
                StringComparison.Ordinal
            );
        SdpDescription parsed = Parse(offer);
        Assert.AreEqual(
            "abcd",
            parsed.Media[0].IceUfrag,
            "the audio section takes the tagged section's transport"
        );
        Assert.IsTrue(parsed.Media[0].RtcpMux, "rtcp-mux is a BUNDLE attribute too");
        Assert.AreEqual("1", parsed.TransportSection.Mid);

        await using PeerConnection answerer = Answerer();
        answerer.SetRemoteDescription(parsed, SdpType.Offer);
        string answer = SdpWriter.Write(answerer.CreateAnswer());

        StringAssert.Contains(
            answer,
            "a=setup:passive\r\n",
            "the role follows the tagged section's"
        );
    }

    [TestMethod]
    public void TransportSection_PassesOverABundleOnlySection()
    {
        SdpDescription parsed = Parse(
            BrowserOffer()
                .Replace("a=group:BUNDLE 0 1 2", "a=group:BUNDLE 1 0 2", StringComparison.Ordinal)
                .Replace(
                    "m=video 9 UDP/TLS/RTP/SAVPF 96",
                    "m=video 0 UDP/TLS/RTP/SAVPF 96\r\na=bundle-only",
                    StringComparison.Ordinal
                )
        );

        Assert.IsTrue(parsed.Media[1].BundleOnly);
        Assert.AreEqual(
            "0",
            parsed.TransportSection.Mid,
            "a bundle-only section is never the tagged one (RFC 9143 section 7.2.1)"
        );
    }

    [TestMethod]
    public async Task LocalCandidates_GoOnlyIntoTheTaggedSection()
    {
        await using PeerConnection answerer = Answerer();
        answerer.SetRemoteDescription(Parse(BrowserOffer()), SdpType.Offer);

        SdpDescription answer = answerer.WithLocalCandidates(answerer.CreateAnswer());

        // RFC 9429 sections 5.2.1 and 5.3.1: a bundled section other than the tagged one has no candidates.
        Assert.IsTrue(answer.Media[0].EndOfCandidates, "the tagged section");
        Assert.IsFalse(answer.Media[1].EndOfCandidates, "bundled into the tagged section");
        Assert.IsEmpty(answer.Media[1].Candidates);
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
