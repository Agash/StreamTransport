using System.Net;
using Agash.StreamTransport.WebRtc.Sdp;

namespace Agash.StreamTransport.WebRtc.Tests;

// RFC 8108 section 5.2: feedback about RTP data goes out from the SSRC of the local media section that
// receives that data.
[TestClass]
public sealed class FeedbackSenderTests
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
    public async Task FeedbackSenderFor_EachRemoteStream_IsTheLocalSsrcOfItsSection()
    {
        InMemoryIceNetwork network = new();
        await using PeerConnection offerer = Peer(network, "10.0.0.1", 0x0000_A001, 0x0000_B001);
        await using PeerConnection answerer = Peer(network, "10.0.0.2", 0x0000_A002, 0x0000_B002);

        answerer.SetRemoteDescription(offerer.CreateOffer(), SdpType.Offer);

        Assert.AreEqual(0x0000_A002u, answerer.FeedbackSenderFor(0x0000_A001));
        Assert.AreEqual(0x0000_B002u, answerer.FeedbackSenderFor(0x0000_B001));
        Assert.AreEqual(
            0x0000_A002u,
            answerer.FeedbackSenderFor(0x1234_5678),
            "a stream it cannot place takes the session's SSRC"
        );
    }

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
