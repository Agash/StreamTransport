using System.Buffers.Binary;
using Agash.StreamTransport.WebRtc.Rtp;
using Agash.StreamTransport.WebRtc.Sdp;

namespace Agash.StreamTransport.WebRtc.Tests;

// RTP header extensions (RFC 8285): the wire forms, the typed values, and their SDP negotiation.
[TestClass]
public sealed class RtpExtensionTests
{
    private static readonly RtpExtensionValues All = new(
        0x0123456789ABCDEFUL,
        new VideoTiming(VideoTiming.ByTimer | VideoTiming.BySize, 1, 5, 6, 9, 0, 0),
        new PlayoutDelay(TimeSpan.Zero, TimeSpan.FromMilliseconds(250))
    );

    [TestMethod]
    [DataRow(1, 2, 3)]
    [DataRow(20, 200, 255)] // past 14: the two-byte form
    public void Write_ThenParse_RoundTripsEveryValue(int capture, int timing, int delay)
    {
        RtpExtensionMap map = new(capture, timing, delay);
        byte[] buffer = new byte[256];

        int length = RtpPacket.Write(buffer, true, 96, 7, 1000, 0x1234, [1, 2, 3], map, All);

        Assert.IsTrue(
            RtpPacket.TryParse(
                buffer.AsSpan(0, length),
                map,
                out RtpHeader header,
                out ReadOnlySpan<byte> payload
            )
        );
        Assert.AreEqual(All, header.Extensions);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, payload.ToArray());
        Assert.AreEqual(
            capture > 14 ? 0x1000 : 0xBEDE,
            BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(12)),
            "one-byte profile below 15, two-byte above"
        );
    }

    [TestMethod]
    public void Write_ReportsWhereTheVideoTimingLanded_ForThePacerToStamp()
    {
        byte[] buffer = new byte[256];

        _ = RtpPacket.Write(
            buffer,
            true,
            96,
            7,
            1000,
            0x1234,
            [1],
            RtpExtensionMap.Offered,
            All,
            out int timingAt
        );

        Assert.AreEqual(
            9,
            BinaryPrimitives.ReadUInt16BigEndian(
                buffer.AsSpan(timingAt + VideoTiming.PacerExitOffset)
            )
        );
    }

    [TestMethod]
    public void Write_WithoutMappedExtensions_WritesNoBlock()
    {
        byte[] buffer = new byte[64];

        int length = RtpPacket.Write(buffer, false, 96, 1, 0, 1, [9], RtpExtensionMap.None, All);

        Assert.AreEqual(13, length);
        Assert.AreEqual(0, buffer[0] & 0x10, "no X bit");
    }

    // An element whose identifier the map does not hold is skipped, whatever its length: an unmapped
    // 8-byte element is not a capture time.
    [TestMethod]
    public void Parse_UnmappedElement_IsNotReadAsACaptureTime()
    {
        byte[] buffer = new byte[64];
        int length = RtpPacket.Write(
            buffer,
            false,
            96,
            1,
            0,
            1,
            [9],
            new RtpExtensionMap(5, 0, 0),
            new RtpExtensionValues(42UL)
        );

        Assert.IsTrue(
            RtpPacket.TryParse(
                buffer.AsSpan(0, length),
                RtpExtensionMap.Offered,
                out RtpHeader header,
                out _
            )
        );
        Assert.IsNull(header.AbsoluteCaptureTimeNtp);
    }

    // RFC 8285 section 4.2: in the one-byte form, identifier 15 ends the block.
    [TestMethod]
    public void Parse_OneByteIdentifier15_EndsTheBlock()
    {
        byte[] packet = new byte[12 + 4 + 12];
        packet[0] = 0x90;
        packet[1] = 96;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(12), 0xBEDE);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(14), 3);
        packet[16] = 0xF0; // id 15
        packet[17] = 0x17; // id 1, 8 bytes: past the end marker, so not read
        BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(18), 42);

        Assert.IsTrue(
            RtpPacket.TryParse(packet, RtpExtensionMap.Offered, out RtpHeader header, out _)
        );
        Assert.IsNull(header.AbsoluteCaptureTimeNtp);
    }

    [TestMethod]
    public void PlayoutDelay_TravelsIn10MillisecondUnits_ClampedTo12Bits()
    {
        byte[] buffer = new byte[64];
        RtpExtensionMap map = new(0, 0, 3);
        int length = RtpPacket.Write(
            buffer,
            false,
            96,
            1,
            0,
            1,
            [],
            map,
            new RtpExtensionValues(
                PlayoutDelay: new PlayoutDelay(
                    TimeSpan.FromMilliseconds(37),
                    TimeSpan.FromMinutes(5)
                )
            )
        );

        Assert.IsTrue(
            RtpPacket.TryParse(buffer.AsSpan(0, length), map, out RtpHeader header, out _)
        );
        Assert.AreEqual(
            TimeSpan.FromMilliseconds(30),
            header.Extensions.PlayoutDelay!.Value.Minimum
        );
        Assert.AreEqual(
            TimeSpan.FromMilliseconds(40950),
            header.Extensions.PlayoutDelay!.Value.Maximum
        );
    }

    [TestMethod]
    public void Sdp_Extmap_RoundTripsWithDirectionAndAllowMixed()
    {
        SdpDescription description = Description(
            [
                new SdpExtension(1, RtpExtensionUris.AbsoluteCaptureTime),
                new SdpExtension(12, RtpExtensionUris.VideoTiming, SdpDirection.SendOnly),
            ],
            allowMixed: true
        );

        string text = SdpWriter.Write(description);

        StringAssert.Contains(text, "a=extmap:1 " + RtpExtensionUris.AbsoluteCaptureTime + "\r\n");
        StringAssert.Contains(
            text,
            "a=extmap:12/sendonly " + RtpExtensionUris.VideoTiming + "\r\n"
        );
        StringAssert.Contains(text, "a=extmap-allow-mixed\r\n");
        Assert.IsTrue(SdpReader.TryParse(text, out SdpDescription read));
        CollectionAssert.AreEqual(
            description.Media[0].Extensions.ToArray(),
            read.Media[0].Extensions.ToArray()
        );
        Assert.IsTrue(read.Media[0].ExtmapAllowMixed);
    }

    // RFC 8285 section 5: mappings may sit at session level, before the first m-line, for every section.
    [TestMethod]
    public void Sdp_SessionLevelExtmap_AppliesToEverySection()
    {
        string text = SdpWriter
            .Write(Description([], allowMixed: false))
            .Replace(
                "m=video",
                "a=extmap:4 " + RtpExtensionUris.AbsoluteCaptureTime + "\r\nm=video",
                StringComparison.Ordinal
            );

        Assert.IsTrue(SdpReader.TryParse(text, out SdpDescription read));
        Assert.AreEqual(4, RtpExtensionMap.From(read.Media[0].Extensions).AbsoluteCaptureTime);
    }

    // RFC 8285 section 7: the answer keeps the offerer's identifier for each extension it understands and
    // leaves out what it does not understand, and what is inactive.
    [TestMethod]
    public async Task Answer_KeepsTheOfferersIdentifiers_AndDropsWhatItDoesNotUse()
    {
        await using PeerConnection answerer = new(
            new PeerConnectionOptions
            {
                Media = [new MediaLine("0", SdpMediaKind.Video, 0xBBBB_0001, [H264])],
            },
            Certificate
        );
        SdpDescription offer = Description(
            [
                new SdpExtension(3, "urn:ietf:params:rtp-hdrext:sdes:mid"),
                new SdpExtension(7, RtpExtensionUris.AbsoluteCaptureTime),
                new SdpExtension(11, RtpExtensionUris.VideoTiming),
                new SdpExtension(13, RtpExtensionUris.PlayoutDelay, SdpDirection.Inactive),
            ],
            allowMixed: false
        );

        answerer.SetRemoteDescription(offer, SdpType.Offer);
        SdpDescription answer = answerer.CreateAnswer();

        CollectionAssert.AreEqual(
            new[]
            {
                new SdpExtension(7, RtpExtensionUris.AbsoluteCaptureTime),
                new SdpExtension(11, RtpExtensionUris.VideoTiming),
            },
            answer.Media[0].Extensions.ToArray()
        );
        Assert.AreEqual(new RtpExtensionMap(7, 11, 0), answerer.NegotiatedExtensions);
    }

    [TestMethod]
    public async Task Offerer_UsesWhatTheAnswerKept()
    {
        await using PeerConnection offerer = new(
            new PeerConnectionOptions
            {
                Media = [new MediaLine("0", SdpMediaKind.Video, 0xAAAA_0001, [H264])],
            },
            Certificate
        );
        SdpDescription offer = offerer.CreateOffer();
        CollectionAssert.AreEquivalent(
            new[]
            {
                RtpExtensionUris.AbsoluteCaptureTime,
                RtpExtensionUris.VideoTiming,
                RtpExtensionUris.PlayoutDelay,
            },
            offer.Media[0].Extensions.Select(static e => e.Uri).ToArray()
        );

        offerer.SetRemoteDescription(
            offer with
            {
                Media =
                [
                    offer.Media[0] with
                    {
                        Setup = SdpSetup.Active,
                        Extensions = [new SdpExtension(1, RtpExtensionUris.AbsoluteCaptureTime)],
                    },
                ],
            },
            SdpType.Answer
        );

        Assert.AreEqual(new RtpExtensionMap(1, 0, 0), offerer.NegotiatedExtensions);
    }

    private static readonly RtcCertificate Certificate = RtcCertificate.Generate();

    private static readonly SdpCodec H264 = new(
        96,
        "H264",
        90000,
        null,
        "packetization-mode=1;profile-level-id=42e01f",
        ["nack", "nack pli"]
    );

    private static SdpDescription Description(
        IReadOnlyList<SdpExtension> extensions,
        bool allowMixed
    ) =>
        new()
        {
            Media =
            [
                new SdpMediaDescription
                {
                    Kind = SdpMediaKind.Video,
                    Mid = "0",
                    Codecs = [H264],
                    IceUfrag = "abcd",
                    IcePwd = "abcdefghijklmnopqrstuvwx",
                    Fingerprint = Certificate.Fingerprint,
                    Extensions = extensions,
                    ExtmapAllowMixed = allowMixed,
                },
            ],
        };
}
