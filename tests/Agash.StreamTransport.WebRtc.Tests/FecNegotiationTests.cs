using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using Agash.StreamTransport.Adaptation;
using Agash.StreamTransport.WebRtc.Rtp;
using Agash.StreamTransport.WebRtc.Sdp;

namespace Agash.StreamTransport.WebRtc.Tests;

// FlexFEC negotiated in SDP (RFC 8627 section 5): the flexfec codec with its repair window and an
// FEC-FR group pairing the media and repair SSRCs, so a receiver that sends no video still recovers the
// sender's media from its repairs.
[TestClass]
public sealed class FecNegotiationTests
{
    private const uint SenderVideo = 0xAAAA_0001;
    private const uint SenderFec = 0xAAAA_0003;
    private static readonly RtcCertificate Certificate = RtcCertificate.Generate();

    private static readonly SdpCodec H264 = new(
        96,
        "H264",
        90000,
        null,
        "packetization-mode=1;profile-level-id=42e01f",
        ["nack", "nack pli"]
    );

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void Sdp_FecFrGroup_PairsTheMediaAndRepairSsrcs()
    {
        SdpDescription description = new()
        {
            Media =
            [
                new SdpMediaDescription
                {
                    Kind = SdpMediaKind.Video,
                    Mid = "0",
                    Codecs = [H264, FlexFec.Codec(100)],
                    IceUfrag = "ufrag",
                    IcePwd = "passwordpasswordpassword",
                    Fingerprint = Certificate.Fingerprint,
                    Ssrc = SenderVideo,
                    FecSsrc = SenderFec,
                    Cname = "test",
                },
            ],
        };

        string text = SdpWriter.Write(description);
        Assert.IsTrue(SdpReader.TryParse(text, out SdpDescription read));

        StringAssert.Contains(text, "a=ssrc-group:FEC-FR 2863267841 2863267843");
        StringAssert.Contains(text, "a=rtpmap:100 flexfec/90000");
        StringAssert.Contains(text, "a=fmtp:100 repair-window=1000000");
        Assert.AreEqual(SenderVideo, read.Media[0].Ssrc);
        Assert.AreEqual(SenderFec, read.Media[0].FecSsrc);
        Assert.IsTrue(FlexFec.IsFlexFec(read.Media[0].Codecs[1]));
    }

    // The receiver sends no video and announces no FEC SSRC, as a WHIP server does. Loss turns the
    // sender's FEC on through its recovery policy, and dropped packets come back from the repairs alone,
    // with no retransmission negotiated.
    [TestMethod]
    [Timeout(60_000)]
    public async Task ReceiveOnlyPeer_RecoversLostPacketsFromTheNegotiatedRepairs()
    {
        int mediaSeen = 0;
        ConcurrentDictionary<ushort, byte> dropped = new();
        InMemoryIceNetwork network = new()
        {
            Drop = (_, _, data) =>
            {
                if (
                    data.Length < 12
                    || (data[0] & 0xC0) != 0x80
                    || (data[1] & 0x7F) != 96
                    || BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8)) != SenderVideo
                    || Interlocked.Increment(ref mediaSeen) % 30 != 0
                )
                {
                    return false;
                }

                dropped.TryAdd(BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2)), 0);
                return true;
            },
        };
        await using PeerConnection sender = new(
            new PeerConnectionOptions
            {
                Media =
                [
                    new MediaLine("0", SdpMediaKind.Video, SenderVideo, [H264, FlexFec.Codec(100)])
                    {
                        FecSsrc = SenderFec,
                        Direction = SdpDirection.SendOnly,
                    },
                ],
                SocketFactory = network.Factory(IPAddress.Parse("10.0.0.1")),

                // The in-memory network has next to no round trip, where the policy repairs by
                // retransmission alone; this test is about the FEC wiring.
                Recovery = new RecoveryPolicyOptions
                {
                    RetransmitOnlyRoundTrip = TimeSpan.Zero,
                    SmallFrameRoundTrip = TimeSpan.Zero,
                },
            },
            Certificate,
            controller: new ScreamCongestionController()
        );
        await using PeerConnection receiver = new(
            new PeerConnectionOptions
            {
                Media =
                [
                    new MediaLine("0", SdpMediaKind.Video, 0xBBBB_0001, [H264, FlexFec.Codec(100)])
                    {
                        Direction = SdpDirection.RecvOnly,
                    },
                ],
                SocketFactory = network.Factory(IPAddress.Parse("10.0.0.2")),
            },
            Certificate,
            controller: new ScreamCongestionController()
        );
        ConcurrentDictionary<ushort, byte> arrived = new();
        receiver.RtpReceived += (header, _) => arrived.TryAdd(header.SequenceNumber, 0);
        await ConnectAsync(sender, receiver);

        // Four packets a frame at 30 fps for six seconds; the first loss turns FEC on.
        byte[] payload = new byte[1000];
        payload[0] = 0x65;
        for (uint frame = 0; frame < 180; frame++)
        {
            for (int packet = 0; packet < 4; packet++)
            {
                _ = sender.TrySendRtp(96, SenderVideo, frame * 3000, packet == 3, payload);
            }

            await Task.Delay(33, TestContext.CancellationToken);
        }

        await Task.Delay(500, TestContext.CancellationToken);
        int recovered = dropped.Keys.Count(arrived.ContainsKey);
        TestContext.WriteLine($"dropped {dropped.Count}, recovered {recovered}");
        Assert.IsGreaterThan(0, recovered, "repairs recover dropped packets.");
    }

    private static async Task ConnectAsync(PeerConnection a, PeerConnection b)
    {
        a.LocalIceCandidate += b.AddRemoteIceCandidate;
        b.LocalIceCandidate += a.AddRemoteIceCandidate;
        TaskCompletionSource connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int up = 0;
        void OnState(PeerConnectionState state)
        {
            if (state == PeerConnectionState.Connected && Interlocked.Increment(ref up) == 2)
            {
                connected.TrySetResult();
            }
        }

        a.StateChanged += OnState;
        b.StateChanged += OnState;
        b.SetRemoteDescription(a.CreateOffer(), SdpType.Offer);
        a.SetRemoteDescription(b.CreateAnswer(), SdpType.Answer);
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
