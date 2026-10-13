using System.Security.Cryptography;
using Agash.StreamTransport.WebRtc;
using Agash.StreamTransport.WebRtc.Sdp;
using Dtls.Core;

namespace Agash.StreamTransport.WebRtc.Tests;

[TestClass]
public sealed class SdpTests
{
    private static SdpDescription SampleOffer()
    {
        var fingerprint = new DtlsFingerprint(
            HashAlgorithmName.SHA256,
            [.. SHA256.HashData([0x01, 0x02, 0x03, 0x04])]
        );
        return new SdpDescription
        {
            Media =
            [
                new SdpMediaDescription
                {
                    Kind = SdpMediaKind.Audio,
                    Mid = "0",
                    Codecs =
                    [
                        new SdpCodec(111, "opus", 48000, 2, "minptime=10;useinbandfec=1", []),
                    ],
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
                    Codecs =
                    [
                        new SdpCodec(
                            96,
                            "H264",
                            90000,
                            null,
                            "profile-level-id=42e01f;packetization-mode=1",
                            ["nack", "nack pli", "goog-remb"]
                        ),
                    ],
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

    // RFC 9143 section 7.3, RFC 8866 section 5.14: a rejected section leaves the BUNDLE group and still
    // lists a format.
    [TestMethod]
    public void Write_RejectedSection_LeavesTheBundleAndListsAFormat()
    {
        SdpDescription offer = SampleOffer();
        SdpDescription answer = offer with
        {
            Media = [offer.Media[0], offer.Media[1] with { Rejected = true, Codecs = [] }],
        };

        string sdp = SdpWriter.Write(answer);

        StringAssert.Contains(sdp, "a=group:BUNDLE 0\r\n");
        StringAssert.Contains(sdp, "m=video 0 UDP/TLS/RTP/SAVPF 0\r\n");
    }

    // RFC 9725 section 4.4.1 and 4.4.2: muxed RTCP only, and every sent track in one media stream.
    [TestMethod]
    public void Write_DeclaresMuxOnlyAndTheMediaStream()
    {
        SdpDescription offer = SampleOffer();
        string sdp = SdpWriter.Write(
            offer with
            {
                Media =
                [
                    offer.Media[0] with
                    {
                        MediaStreamId = "s1",
                        TrackId = "s1-0",
                    },
                    offer.Media[1] with
                    {
                        MediaStreamId = "s1",
                        TrackId = "s1-1",
                    },
                ],
            }
        );

        StringAssert.Contains(sdp, "a=rtcp-mux-only");
        StringAssert.Contains(sdp, "a=msid:s1 s1-0");
        StringAssert.Contains(sdp, "a=msid:s1 s1-1");
    }

    // A browser hiding its host address writes an mDNS name (RFC 8839 section 5.1): kept aside, as written.
    [TestMethod]
    public void Parse_MdnsCandidate_IsKeptAsIgnored()
    {
        const string Mdns =
            "candidate:1 1 udp 2122260223 5f2c3a8e-1b2d-4c5e-9f00-0123456789ab.local 54321 typ host";
        string sdp = SdpWriter.Write(SampleOffer()).Replace(
            "a=mid:0\r\n",
            $"a=mid:0\r\na={Mdns}\r\n",
            StringComparison.Ordinal
        );

        Assert.IsTrue(SdpReader.TryParse(sdp, out SdpDescription read));
        CollectionAssert.AreEqual(new[] { Mdns }, read.Media[0].IgnoredCandidates.ToArray());
        Assert.IsFalse(read.Media[0].Candidates.Any(static c => c.Endpoint.Port == 54321));
    }

    // RFC 3551 section 6: a static payload type needs no a=rtpmap.
    [TestMethod]
    public void Parse_StaticPayloadTypeWithoutRtpmap_IsKept()
    {
        string sdp = SdpWriter.Write(SampleOffer()).Replace(
            "m=audio 9 UDP/TLS/RTP/SAVPF 111",
            "m=audio 9 UDP/TLS/RTP/SAVPF 111 0",
            StringComparison.Ordinal
        );

        Assert.IsTrue(SdpReader.TryParse(sdp, out SdpDescription read));
        SdpCodec pcmu = read.Media[0].Codecs.Single(static c => c.PayloadType == 0);
        Assert.AreEqual("PCMU", pcmu.EncodingName);
        Assert.AreEqual(8000, pcmu.ClockRate);
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
        Assert.AreEqual(original.Media[1].Fingerprint, video.Fingerprint);
    }

    [TestMethod]
    public void Parse_SessionLevelFingerprintAndIce_InheritedByMedia()
    {
        // Chrome-style: ICE creds + fingerprint at session level, applied to each m-section.
        const string sdp =
            "v=0\r\no=- 1 2 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\n"
            + "a=group:BUNDLE 0\r\n"
            + "a=ice-ufrag:sess\r\na=ice-pwd:sesspasswordsesspassword00\r\n"
            + "a=fingerprint:sha-256 00:01:02:03:04:05:06:07:08:09:0A:0B:0C:0D:0E:0F:10:11:12:13:14:15:16:17:18:19:1A:1B:1C:1D:1E:1F\r\n"
            + "m=audio 9 UDP/TLS/RTP/SAVPF 111\r\nc=IN IP4 0.0.0.0\r\n"
            + "a=rtcp-mux\r\na=setup:active\r\na=mid:0\r\na=sendrecv\r\n"
            + "a=rtpmap:111 opus/48000/2\r\n";

        Assert.IsTrue(SdpReader.TryParse(sdp, out SdpDescription parsed));
        SdpMediaDescription audio = parsed.Media[0];
        Assert.AreEqual("sess", audio.IceUfrag);
        Assert.AreEqual("sesspasswordsesspassword00", audio.IcePwd);
        Assert.AreEqual(HashAlgorithmName.SHA256, audio.Fingerprint.Algorithm);
        Assert.AreEqual(SdpSetup.Active, audio.Setup);
        Assert.AreEqual(111, audio.Codecs[0].PayloadType);
    }

    [TestMethod]
    public void WriteThenParse_MultipleCodecsPerMLine_PreservesAllInPreferenceOrder()
    {
        // The registry-driven negotiation offers several codecs per m-line, each with its own dynamic payload
        // type, fmtp, and rtcp-fb. The wire format must round-trip all of them, in order.
        var fingerprint = new DtlsFingerprint(
            HashAlgorithmName.SHA256,
            [.. SHA256.HashData([0x0A, 0x0B, 0x0C, 0x0D])]
        );
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
            "v=0\r\no=- 1 2 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\n"
            + "m=audio 9 UDP/TLS/RTP/SAVPF 111\r\na=ice-ufrag:x\r\na=ice-pwd:y\r\na=mid:0\r\n"
            + "a=rtpmap:111 opus/48000/2\r\n";

        Assert.IsFalse(SdpReader.TryParse(sdp, out _));
    }
}
