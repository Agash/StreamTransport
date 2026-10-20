using System.Net;
using Agash.StreamTransport.WebRtc.Sdp;
using Agash.StreamTransport.WebRtc.Transport;

namespace Agash.StreamTransport.WebRtc.Tests;

// RFC 8830: a section's msid names the MediaStream its track belongs to and the track; tracks in one
// MediaStream, under one CNAME (RFC 3550 section 6.5.1), play out in sync.
[TestClass]
public sealed class MediaStreamIdentityTests
{
    private static readonly RtcCertificate Certificate = RtcCertificate.Generate();

    private static readonly SdpCodec H264 = new(
        96,
        "H264",
        90000,
        null,
        "packetization-mode=1",
        []
    );

    private static readonly SdpCodec Opus = new(111, "opus", 48000, 2, null, []);

    // Section 2: msid-id [SP msid-appdata], each 1*64 token-char; "-" is a track in no MediaStream.
    [TestMethod]
    [DataRow("a=msid:stream track", "stream", "track")]
    [DataRow("a=msid:stream", "stream", null)]
    [DataRow("a=msid:- track", null, "track")]
    [DataRow("a=msid:{6d2c} {7e3d}", "{6d2c}", "{7e3d}")]
    public void Reader_TakesTheGrammar(string line, string? stream, string? track)
    {
        Assert.IsTrue(SdpReader.TryParse(Section(line), out SdpDescription read));

        Assert.AreEqual(new MediaTrackIdentity(stream, track), read.Media[0].Track);
    }

    [TestMethod]
    [DataRow("a=msid:\"quoted\" track")]
    [DataRow("a=msid:stream tr@ck")]
    [DataRow("a=msid:stream track extra")]
    public void Reader_RefusesWhatIsNotTheGrammar(string line)
    {
        Assert.IsTrue(SdpReader.TryParse(Section(line), out SdpDescription read));

        Assert.IsNull(read.Media[0].Track);
    }

    [TestMethod]
    public void Reader_RefusesAnIdLongerThan64() =>
        Reader_RefusesWhatIsNotTheGrammar($"a=msid:{new string('s', 65)} track");

    [TestMethod]
    public void Writer_RoundTripsTheIdentity()
    {
        Assert.IsTrue(SdpReader.TryParse(Section("a=msid:stream track"), out SdpDescription read));

        Assert.IsTrue(SdpReader.TryParse(SdpWriter.Write(read), out SdpDescription again));
        Assert.AreEqual(new MediaTrackIdentity("stream", "track"), again.Media[0].Track);
    }

    // The answerer learns the offerer's tracks and CNAME from negotiation, and the offerer the
    // answerer's: every sending section of one side names the same MediaStream.
    [TestMethod]
    public async Task Negotiation_CarriesThePeersTracksAndCname()
    {
        InMemoryIceNetwork network = new();
        await using PeerConnection offerer = Peer(network, "10.0.0.1");
        await using PeerConnection answerer = Peer(network, "10.0.0.2");

        answerer.SetRemoteDescription(offerer.CreateOffer(), SdpType.Offer);
        offerer.SetRemoteDescription(answerer.CreateAnswer(), SdpType.Answer);

        foreach (PeerConnection side in (PeerConnection[])[offerer, answerer])
        {
            NegotiatedMediaInfo video = side.NegotiatedMedia.Single(m =>
                m.Kind == SdpMediaKind.Video
            );
            NegotiatedMediaInfo audio = side.NegotiatedMedia.Single(m =>
                m.Kind == SdpMediaKind.Audio
            );
            Assert.IsNotNull(video.RemoteTrack?.StreamId);
            Assert.AreEqual(video.RemoteTrack.StreamId, audio.RemoteTrack?.StreamId);
            Assert.AreNotEqual(video.RemoteTrack.TrackId, audio.RemoteTrack?.TrackId);
            Assert.IsNotNull(video.RemoteCname);
            Assert.AreEqual(video.RemoteCname, audio.RemoteCname);
            Assert.IsTrue(WebRtcMediaTransport.Synchronized(video, audio));
        }
    }

    // RFC 3550 section 6.5.1 and RFC 8830 section 1: in sync only under one CNAME and, where the peer
    // names streams, in one MediaStream; a peer naming none keeps the CNAME rule alone.
    [TestMethod]
    [DataRow("c", "c", "s", "s", true)]
    [DataRow("c", "d", "s", "s", false)]
    [DataRow("c", "c", "s", "t", false)]
    [DataRow("c", "c", "s", "-", false)]
    [DataRow("c", "c", null, null, true)]
    [DataRow(null, null, "s", "s", true)]
    public void Synchronized_FollowsCnameAndMediaStream(
        string? videoCname,
        string? audioCname,
        string? videoStream,
        string? audioStream,
        bool expected
    )
    {
        NegotiatedMediaInfo video = Info(SdpMediaKind.Video, videoCname, videoStream);
        NegotiatedMediaInfo audio = Info(SdpMediaKind.Audio, audioCname, audioStream);

        Assert.AreEqual(expected, WebRtcMediaTransport.Synchronized(video, audio));
    }

    private static NegotiatedMediaInfo Info(SdpMediaKind kind, string? cname, string? stream) =>
        new(kind, kind.ToString(), 1, [], [])
        {
            RemoteCname = cname,
            RemoteTrack = stream is null
                ? null
                : new MediaTrackIdentity(stream == "-" ? null : stream, kind.ToString()),
        };

    private static string Section(string msid) =>
        "v=0\r\no=- 1 2 IN IP4 127.0.0.1\r\ns=-\r\nt=0 0\r\n"
        + "a=fingerprint:sha-256 00:11:22:33:44:55:66:77:88:99:AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99:AA:BB:CC:DD:EE:FF\r\n"
        + "m=video 9 UDP/TLS/RTP/SAVPF 96\r\nc=IN IP4 0.0.0.0\r\na=mid:0\r\na=sendonly\r\n"
        + "a=ice-ufrag:abcd\r\na=ice-pwd:0123456789abcdef0123456789\r\na=setup:actpass\r\n"
        + msid
        + "\r\na=rtpmap:96 H264/90000\r\n";

    private static PeerConnection Peer(InMemoryIceNetwork network, string address) =>
        new(
            new PeerConnectionOptions
            {
                Media =
                [
                    new MediaLine("0", SdpMediaKind.Video, 0, [H264]),
                    new MediaLine("1", SdpMediaKind.Audio, 0, [Opus]),
                ],
                SocketFactory = network.Factory(IPAddress.Parse(address)),
            },
            Certificate
        );
}
