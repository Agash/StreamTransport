using Agash.StreamTransport.WebRtc;
using Agash.StreamTransport.WebRtc.Sdp;

namespace Agash.StreamTransport.WebRtc.Tests;

[TestClass]
public sealed class SdpTests
{
    private static SdpDescription SampleOffer()
    {
        var fingerprint = CertificateFingerprint.Sha256([0x01, 0x02, 0x03, 0x04]);
        return new SdpDescription
        {
            Media =
            [
                new SdpMediaDescription
                {
                    Kind = SdpMediaKind.Audio,
                    Mid = "0",
                    Codecs = [new SdpCodec(111, "opus", 48000, 2, "minptime=10;useinbandfec=1", [])],
                    IceUfrag = "abcd",
                    IcePwd = "0123456789abcdef0123456789",
                    Fingerprint = fingerprint,
                    Setup = SdpSetup.ActPass,
                    Ssrc = 0x11223344,
                    Cname = "st",
                },
                new SdpMediaDescription
                {
                    Kind = SdpMediaKind.Video,
                    Mid = "1",
                    Codecs = [new SdpCodec(96, "H264", 90000, null, "profile-level-id=42e01f;packetization-mode=1", ["nack", "nack pli", "goog-remb"])],
                    IceUfrag = "abcd",
                    IcePwd = "0123456789abcdef0123456789",
                    Fingerprint = fingerprint,
                    Setup = SdpSetup.ActPass,
                    Ssrc = 0x55667788,
                    Cname = "st",
                },
            ],
        };
    }

    [TestMethod]
    public void Write_ProducesRequiredWebRtcAttributes()
    {
        string sdp = SdpWriter.Write(SampleOffer());

        StringAssert.Contains(sdp, "a=group:BUNDLE 0 1");
        StringAssert.Contains(sdp, "m=audio 9 UDP/TLS/RTP/SAVPF 111");
        StringAssert.Contains(sdp, "m=video 9 UDP/TLS/RTP/SAVPF 96");
        StringAssert.Contains(sdp, "a=rtcp-mux");
        StringAssert.Contains(sdp, "a=ice-ufrag:abcd");
        StringAssert.Contains(sdp, "a=ice-pwd:0123456789abcdef0123456789");
        StringAssert.Contains(sdp, "a=fingerprint:sha-256 ");
        StringAssert.Contains(sdp, "a=setup:actpass");
        StringAssert.Contains(sdp, "a=mid:0");
        StringAssert.Contains(sdp, "a=mid:1");
        StringAssert.Contains(sdp, "a=rtpmap:111 opus/48000/2");
        StringAssert.Contains(sdp, "a=rtpmap:96 H264/90000");
        StringAssert.Contains(sdp, "a=rtcp-fb:96 nack pli");
        StringAssert.Contains(sdp, "a=fmtp:96 profile-level-id=42e01f;packetization-mode=1");
    }

    [TestMethod]
    public void WriteThenParse_RoundTrips()
    {
        SdpDescription original = SampleOffer();
        string text = SdpWriter.Write(original);

        Assert.IsTrue(SdpReader.TryParse(text, out SdpDescription parsed));
        Assert.AreEqual(2, parsed.Media.Count);

        SdpMediaDescription audio = parsed.Media[0];
        Assert.AreEqual(SdpMediaKind.Audio, audio.Kind);
        Assert.AreEqual("0", audio.Mid);
        Assert.AreEqual("abcd", audio.IceUfrag);
        Assert.AreEqual(SdpSetup.ActPass, audio.Setup);
        Assert.IsTrue(audio.RtcpMux);
        Assert.AreEqual(1, audio.Codecs.Count);
        Assert.AreEqual(111, audio.Codecs[0].PayloadType);
        Assert.AreEqual("opus", audio.Codecs[0].EncodingName);
        Assert.AreEqual(48000, audio.Codecs[0].ClockRate);
        Assert.AreEqual(2, audio.Codecs[0].Channels);

        SdpMediaDescription video = parsed.Media[1];
        Assert.AreEqual("H264", video.Codecs[0].EncodingName);
        CollectionAssert.Contains(video.Codecs[0].RtcpFeedback.ToArray(), "nack pli");
        Assert.AreEqual(original.Media[1].Fingerprint.ToSdpValue(), video.Fingerprint.ToSdpValue());
    }

    [TestMethod]
    public void Parse_SessionLevelFingerprintAndIce_InheritedByMedia()
    {
        // Chrome-style: ICE creds + fingerprint at session level, applied to each m-section.
        const string sdp =
            "v=0\r\no=- 1 2 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\n" +
            "a=group:BUNDLE 0\r\n" +
            "a=ice-ufrag:sess\r\na=ice-pwd:sesspasswordsesspassword00\r\n" +
            "a=fingerprint:sha-256 AA:BB:CC:DD\r\n" +
            "m=audio 9 UDP/TLS/RTP/SAVPF 111\r\nc=IN IP4 0.0.0.0\r\n" +
            "a=rtcp-mux\r\na=setup:active\r\na=mid:0\r\na=sendrecv\r\n" +
            "a=rtpmap:111 opus/48000/2\r\n";

        Assert.IsTrue(SdpReader.TryParse(sdp, out SdpDescription parsed));
        SdpMediaDescription audio = parsed.Media[0];
        Assert.AreEqual("sess", audio.IceUfrag);
        Assert.AreEqual("sesspasswordsesspassword00", audio.IcePwd);
        Assert.AreEqual("sha-256", audio.Fingerprint.Algorithm);
        Assert.AreEqual(SdpSetup.Active, audio.Setup);
        Assert.AreEqual(111, audio.Codecs[0].PayloadType);
    }

    [TestMethod]
    public void WriteThenParse_MultipleCodecsPerMLine_PreservesAllInPreferenceOrder()
    {
        // The registry-driven negotiation offers several codecs per m-line, each with its own dynamic payload
        // type, fmtp, and rtcp-fb. The wire format must round-trip all of them, in order.
        var fingerprint = CertificateFingerprint.Sha256([0x0A, 0x0B, 0x0C, 0x0D]);
        var offer = new SdpDescription
        {
            Media =
            [
                new SdpMediaDescription
                {
                    Kind = SdpMediaKind.Video,
                    Mid = "0",
                    Codecs =
                    [
                        new SdpCodec(96, "H265", 90000, null, null, ["nack", "nack pli"]),
                        new SdpCodec(98, "AV1", 90000, null, "level-idx=5", ["nack", "nack pli"]),
                        new SdpCodec(100, "H264", 90000, null, "packetization-mode=1", ["nack"]),
                    ],
                    IceUfrag = "uf",
                    IcePwd = "pwdpwdpwdpwdpwdpwdpwdpwd00",
                    Fingerprint = fingerprint,
                    Ssrc = 0xABCDEF01,
                    Cname = "st",
                },
            ],
        };

        string text = SdpWriter.Write(offer);
        StringAssert.Contains(text, "m=video 9 UDP/TLS/RTP/SAVPF 96 98 100");

        Assert.IsTrue(SdpReader.TryParse(text, out SdpDescription parsed));
        IReadOnlyList<SdpCodec> codecs = parsed.Media[0].Codecs;
        Assert.AreEqual(3, codecs.Count);
        Assert.AreEqual((96, "H265"), (codecs[0].PayloadType, codecs[0].EncodingName));
        Assert.AreEqual((98, "AV1"), (codecs[1].PayloadType, codecs[1].EncodingName));
        Assert.AreEqual((100, "H264"), (codecs[2].PayloadType, codecs[2].EncodingName));
        Assert.AreEqual("level-idx=5", codecs[1].FormatParameters);
        CollectionAssert.Contains(codecs[2].RtcpFeedback.ToArray(), "nack");
    }

    [TestMethod]
    public void Parse_NoFingerprint_FailsAsJsepRequires()
    {
        const string sdp =
            "v=0\r\no=- 1 2 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\n" +
            "m=audio 9 UDP/TLS/RTP/SAVPF 111\r\na=ice-ufrag:x\r\na=ice-pwd:y\r\na=mid:0\r\n" +
            "a=rtpmap:111 opus/48000/2\r\n";

        Assert.IsFalse(SdpReader.TryParse(sdp, out _));
    }
}
