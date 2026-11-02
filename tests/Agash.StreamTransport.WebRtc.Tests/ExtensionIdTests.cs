using System.Net;
using Agash.StreamTransport.WebRtc.Sdp;

namespace Agash.StreamTransport.WebRtc.Tests;

/// <summary>
/// An identifier names one extension across a BUNDLE group (RFC 9143 section 12), as within a section
/// (RFC 8285 section 6); the answer leaves out one that names two, in every section.
/// </summary>
[TestClass]
public sealed class ExtensionIdTests
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
    public async Task Answer_IdNamingTwoExtensionsAcrossTheBundle_IsLeftOutEverywhere()
    {
        (PeerConnection offerer, PeerConnection answerer) = Peers();
        await using (offerer)
        await using (answerer)
        {
            SdpDescription offer = offerer.CreateOffer();
            int shared = offer.Media[0].Extensions[0].Id;
            string audioUri = offer.Media[0].Extensions[0].Uri;
            offer = WithExtensions(offer, 1, new SdpExtension(shared, "urn:example:not-the-same"));

            answerer.SetRemoteDescription(offer, SdpType.Offer);
            SdpDescription answer = answerer.CreateAnswer();

            Assert.IsFalse(answer.Media[0].Extensions.Any(e => e.Id == shared), audioUri);
            Assert.IsFalse(answer.Media[1].Extensions.Any(e => e.Id == shared));
        }
    }

    [TestMethod]
    public async Task Answer_SameExtensionUnderOneIdAcrossTheBundle_IsKept()
    {
        (PeerConnection offerer, PeerConnection answerer) = Peers();
        await using (offerer)
        await using (answerer)
        {
            SdpDescription offer = offerer.CreateOffer();
            int mid = offer
                .Media[0]
                .Extensions.First(e => e.Uri.EndsWith("sdes:mid", StringComparison.Ordinal))
                .Id;

            answerer.SetRemoteDescription(offer, SdpType.Offer);
            SdpDescription answer = answerer.CreateAnswer();

            Assert.IsTrue(answer.Media[0].Extensions.Any(e => e.Id == mid));
            Assert.IsTrue(answer.Media[1].Extensions.Any(e => e.Id == mid));
        }
    }

    private static SdpDescription WithExtensions(
        SdpDescription description,
        int section,
        params SdpExtension[] added
    ) =>
        description with
        {
            Media = description.Media.SetItem(
                section,
                description.Media[section] with
                {
                    Extensions = [.. description.Media[section].Extensions, .. added],
                }
            ),
        };

    private static (PeerConnection Offerer, PeerConnection Answerer) Peers()
    {
        InMemoryIceNetwork network = new();
        return (Peer(network, "10.0.0.1", 0xA1, 0xA2), Peer(network, "10.0.0.2", 0xB1, 0xB2));
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
                Media =
                [
                    new MediaLine("0", SdpMediaKind.Audio, audio, [Opus]),
                    new MediaLine("1", SdpMediaKind.Video, video, [H264]),
                ],
                SocketFactory = network.Factory(IPAddress.Parse(address)),
            },
            Certificate
        );
}
