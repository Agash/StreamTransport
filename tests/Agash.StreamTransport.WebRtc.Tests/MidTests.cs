using System.Buffers.Binary;
using System.Net;
using System.Text;
using Agash.StreamTransport.WebRtc.Rtcp;
using Agash.StreamTransport.WebRtc.Rtp;
using Agash.StreamTransport.WebRtc.Sdp;

namespace Agash.StreamTransport.WebRtc.Tests;

// RFC 9143 sections 9 and 15: the MID names a stream's media section in RTP (the SDES header extension)
// and RTCP (the SDES MID item), and a receiver associates each stream with a section by MID, then SSRC,
// then a payload type only one section uses.
[TestClass]
public sealed class MidTests
{
    private static readonly RtcCertificate Certificate = RtcCertificate.Generate();

    private static readonly SdpCodec Opus = new(111, "opus", 48000, 2, null, []);

    private static readonly SdpCodec H264 = new(
        96,
        "H264",
        90000,
        null,
        "packetization-mode=1",
        ["nack", "nack pli"]
    );

    [TestMethod]
    [DataRow("0")]
    [DataRow("video")]
    [DataRow("sixteen-bytes-id")]
    public void RtpMid_UpToSixteenBytes_RoundTrips(string text)
    {
        Assert.IsTrue(RtpMid.TryCreate(text, out RtpMid mid));
        Assert.AreEqual(Encoding.UTF8.GetByteCount(text), mid.Length);
        Assert.AreEqual(text, mid.ToString());
        Assert.IsTrue(RtpMid.TryCreate(Encoding.UTF8.GetBytes(text), out RtpMid again));
        Assert.AreEqual(mid, again);
    }

    [TestMethod]
    public void RtpMid_EmptyOrLongerThanSixteenBytes_IsRefused()
    {
        Assert.IsFalse(RtpMid.TryCreate("", out _));
        Assert.IsFalse(RtpMid.TryCreate("seventeen-bytes-x", out _));
    }

    [TestMethod]
    [DataRow(5)]
    [DataRow(20)]
    public void ExtensionMap_Mid_RoundTripsInEitherForm(int id)
    {
        RtpExtensionMap map = new(0, 0, 0, Mid: id);
        Assert.IsTrue(RtpMid.TryCreate("1", out RtpMid mid));
        byte[] block = new byte[map.MaximumBlockLength];

        int length = map.Write(block, new RtpExtensionValues(Mid: mid), out _);

        ushort profile = BinaryPrimitives.ReadUInt16BigEndian(block);
        Assert.AreEqual(mid, map.Read(profile, block.AsSpan(4, length - 4)).Mid);
    }

    // Section 15.1: the item is type 15, its length, the tag without a terminator.
    [TestMethod]
    public void SourceDescription_WithMid_CarriesTheMidItemAfterTheCname()
    {
        Assert.IsTrue(RtpMid.TryCreate("1", out RtpMid mid));
        byte[] packet = new byte[64];

        int length = RtcpSourceDescription.Write(packet, [new SdesSource(0x1234_5678, mid)], "c");

        // Header (4), SSRC (4), CNAME item (type, length, "c"), MID item (15, 1, "1"), null, padding.
        Assert.AreEqual(16, length);
        CollectionAssert.AreEqual(
            new byte[] { 1, 1, (byte)'c', 15, 1, (byte)'1', 0 },
            packet[8..15]
        );
    }

    [TestMethod]
    public async Task Offer_MapsTheMidInEverySection()
    {
        await using PeerConnection peer = Peer(new InMemoryIceNetwork(), "10.0.0.1", 0xA1, 0xB1);

        SdpDescription offer = peer.CreateOffer();

        Assert.IsTrue(
            offer.Media.All(static m => m.Extensions.Any(e => e.Uri == RtpExtensionUris.Mid))
        );
    }

    // Section 9.2: a MID the packet carries places its stream, and later packets of the stream follow
    // without one; a MID naming no section is not decoded; with no MID, a known SSRC or a payload type
    // only one section uses places the stream.
    [TestMethod]
    public async Task AssociateSection_FollowsMidThenSsrcThenPayloadType()
    {
        InMemoryIceNetwork network = new();
        await using PeerConnection offerer = Peer(network, "10.0.0.1", 0xA1, 0xB1);
        await using PeerConnection answerer = Peer(network, "10.0.0.2", 0xA2, 0xB2);
        answerer.SetRemoteDescription(offerer.CreateOffer(), SdpType.Offer);
        _ = answerer.CreateAnswer();
        Assert.IsTrue(RtpMid.TryCreate("1", out RtpMid video));
        Assert.IsTrue(RtpMid.TryCreate("9", out RtpMid unknown));

        Assert.AreEqual("1", answerer.AssociateSection(Header(0x5555, 96, 1, video)));
        Assert.AreEqual("1", answerer.AssociateSection(Header(0x5555, 96, 2)), "follows its SSRC");
        Assert.IsNull(
            answerer.AssociateSection(Header(0x6666, 96, 1, unknown)),
            "names no section"
        );
        Assert.AreEqual("0", answerer.AssociateSection(Header(0x7777, 111, 1)), "by payload type");
        Assert.AreEqual("0", answerer.AssociateSection(Header(0xA1, 111, 1)), "a signalled SSRC");
        Assert.IsNull(answerer.AssociateSection(Header(0xA1, 96, 2)), "not its section's type");
    }

    private static RtpHeader Header(
        uint ssrc,
        byte payloadType,
        ushort sequence,
        RtpMid? mid = null
    ) => new(false, payloadType, sequence, 0, ssrc, new RtpExtensionValues(Mid: mid));

    private static PeerConnection Peer(
        InMemoryIceNetwork network,
        string address,
        uint audio,
        uint video
    ) =>
        new(
            new PeerConnectionOptions
            {
                SocketFactory = network.Factory(IPAddress.Parse(address)),
                Media =
                [
                    new MediaLine("0", SdpMediaKind.Audio, audio, [Opus]),
                    new MediaLine("1", SdpMediaKind.Video, video, [H264]),
                ],
            },
            Certificate
        );
}
