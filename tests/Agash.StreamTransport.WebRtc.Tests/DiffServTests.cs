using System.Collections.Concurrent;
using System.Net;
using Agash.StreamTransport.Adaptation;
using Agash.StreamTransport.WebRtc.Sdp;

namespace Agash.StreamTransport.WebRtc.Tests;

// RFC 8837 section 5, table 1 (interactive video row): what each packet of a session is marked with.
[TestClass]
public sealed class DiffServTests
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

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(TrafficClass.Audio, NetworkPriority.High, DiffServCodepoint.Expedited)]
    [DataRow(TrafficClass.Audio, NetworkPriority.Medium, DiffServCodepoint.Expedited)]
    [DataRow(TrafficClass.Video, NetworkPriority.High, DiffServCodepoint.Af41)]
    [DataRow(TrafficClass.Video, NetworkPriority.Medium, DiffServCodepoint.Af42)]
    [DataRow(TrafficClass.Retransmission, NetworkPriority.Medium, DiffServCodepoint.Af42)]
    [DataRow(TrafficClass.Repair, NetworkPriority.High, DiffServCodepoint.Af43)]
    [DataRow(TrafficClass.Probe, NetworkPriority.Medium, DiffServCodepoint.Af43)]
    [DataRow(TrafficClass.Video, NetworkPriority.Low, DiffServCodepoint.Default)]
    [DataRow(TrafficClass.Audio, NetworkPriority.VeryLow, DiffServCodepoint.LowerEffort)]
    public void DiffServFor_FollowsTable1(
        TrafficClass traffic,
        NetworkPriority priority,
        DiffServCodepoint expected
    ) => Assert.AreEqual(expected, IpMarking.DiffServFor(traffic, priority));

    // The DSCP takes the upper six bits, ECN the lower two (RFC 2474, RFC 3168).
    [TestMethod]
    public void Marking_PacksTheTrafficClassByte()
    {
        IpMarking marking = new(DiffServCodepoint.Expedited, EcnCodepoint.Ect1);

        Assert.AreEqual((byte)0xB9, marking.Value);
        Assert.AreEqual(marking, IpMarking.FromTrafficClass(0xB9));
    }

    // A session marks its media and nothing else: RTP by what it carries, STUN, DTLS and RTCP not at all;
    // at Low priority nothing is marked.
    [TestMethod]
    [Timeout(60_000)]
    [DataRow(NetworkPriority.Medium)]
    [DataRow(NetworkPriority.Low)]
    public async Task Session_MarksMediaByWhatItCarries(NetworkPriority priority)
    {
        InMemoryIceNetwork network = new();
        ConcurrentQueue<(byte Kind, DiffServCodepoint Dscp)> seen = new();
        network.Marked = (data, dscp) =>
        {
            // RTP keeps its payload type in the clear; anything else (STUN, DTLS, RTCP) is kind 0.
            bool rtp = data.Length > 12 && data[0] >> 6 == 2 && (data[1] & 0x7F) is 96 or 111;
            seen.Enqueue((rtp ? (byte)(data[1] & 0x7F) : (byte)0, dscp));
        };
        await using PeerConnection sender = Peer(network, "10.0.0.1", 0xA1, 0xB1, priority);
        await using PeerConnection receiver = Peer(network, "10.0.0.2", 0xA2, 0xB2, priority);
        sender.LocalIceCandidate += receiver.AddRemoteIceCandidate;
        receiver.LocalIceCandidate += sender.AddRemoteIceCandidate;
        TaskCompletionSource connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        sender.StateChanged += state =>
        {
            if (state == PeerConnectionState.Connected)
            {
                connected.TrySetResult();
            }
        };
        receiver.SetRemoteDescription(sender.CreateOffer(), SdpType.Offer);
        sender.SetRemoteDescription(receiver.CreateAnswer(), SdpType.Answer);
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.CancellationToken);

        using PeriodicTimer frames = new(TimeSpan.FromMilliseconds(20));
        for (uint i = 0; i < 10; i++)
        {
            _ = await frames.WaitForNextTickAsync(TestContext.CancellationToken);
            _ = sender.TrySendRtp(111, 0xA1, i * 960, true, [1, 2, 3]);
            _ = sender.TrySendRtp(96, 0xB1, i * 3000, true, [0x65, 1]);
        }

        DiffServCodepoint audio =
            priority == NetworkPriority.Medium
                ? DiffServCodepoint.Expedited
                : DiffServCodepoint.Default;
        DiffServCodepoint video =
            priority == NetworkPriority.Medium ? DiffServCodepoint.Af42 : DiffServCodepoint.Default;
        Assert.IsTrue(seen.Any(static p => p.Kind == 0), "STUN and DTLS were sent");
        Assert.IsTrue(seen.Any(static p => p.Kind == 111) && seen.Any(static p => p.Kind == 96));
        Assert.IsTrue(
            seen.Where(static p => p.Kind == 0).All(static p => p.Dscp == DiffServCodepoint.Default)
        );
        Assert.IsTrue(seen.Where(static p => p.Kind == 111).All(p => p.Dscp == audio));
        Assert.IsTrue(seen.Where(static p => p.Kind == 96).All(p => p.Dscp == video));
    }

    private static PeerConnection Peer(
        InMemoryIceNetwork network,
        string address,
        uint audio,
        uint video,
        NetworkPriority priority
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
                NetworkPriority = priority,
            },
            Certificate
        );
}
