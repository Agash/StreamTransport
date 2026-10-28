using System.Collections.Concurrent;
using System.Net;
using Agash.StreamTransport.WebRtc.Rtp;
using Agash.StreamTransport.WebRtc.Sdp;

namespace Agash.StreamTransport.WebRtc.Tests;

// RFC 3550 section 8.2 and RFC 5576 sections 5 and 6.2, as RFC 8834 section 4.8 requires them.
[TestClass]
public sealed class SsrcCollisionTests
{
    private static readonly RtcCertificate Certificate = RtcCertificate.Generate();

    private static readonly SdpCodec H264 = new(
        96,
        "H264",
        90000,
        null,
        "packetization-mode=1",
        ["nack", "nack pli"]
    );

    public TestContext TestContext { get; set; } = null!;

    // Section 6.2: the source's earlier SSRCs, after its current one.
    [TestMethod]
    public void PreviousSsrc_RoundTrips()
    {
        SdpDescription description = new()
        {
            Media =
            [
                new SdpMediaDescription
                {
                    Kind = SdpMediaKind.Video,
                    Mid = "0",
                    Codecs = [H264],
                    IceUfrag = "ufrag",
                    IcePwd = "passwordpasswordpassword",
                    Fingerprints = [Certificate.Fingerprint],
                    Ssrc = 7,
                    PreviousSsrcs = [5, 6],
                    Cname = "c",
                },
            ],
        };

        string text = SdpWriter.Write(description);

        StringAssert.Contains(text, "a=ssrc:7 previous-ssrc:5 6");
        Assert.IsTrue(SdpReader.TryParse(text, out SdpDescription read));
        Assert.AreEqual(7u, read.Media[0].Ssrc);
        CollectionAssert.AreEqual(new uint[] { 5, 6 }, read.Media[0].PreviousSsrcs.ToArray());
    }

    // RFC 5576 section 5: two signalled sources that collide are acted on at once. The answerer picks a
    // new SSRC before it answers, names the old one as previous, and its media comes from the new one.
    [TestMethod]
    [Timeout(60_000)]
    public async Task SignalledCollision_IsResolvedBeforeTheAnswer()
    {
        InMemoryIceNetwork network = new();
        await using PeerConnection offerer = Peer(network, "10.0.0.1", 0xAAAA_0001);
        await using PeerConnection answerer = Peer(network, "10.0.0.2", 0xAAAA_0001);
        (uint Old, uint New)? changed = null;
        answerer.LocalSsrcChanged += (old, replacement) => changed = (old, replacement);
        TaskCompletionSource<uint> arrived = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        offerer.RtpReceived += (header, _) => arrived.TrySetResult(header.Ssrc);
        offerer.LocalIceCandidate += answerer.AddRemoteIceCandidate;
        answerer.LocalIceCandidate += offerer.AddRemoteIceCandidate;

        answerer.SetRemoteDescription(offerer.CreateOffer(), SdpType.Offer);
        SdpDescription answer = answerer.CreateAnswer();

        Assert.IsNotNull(changed);
        Assert.AreEqual(0xAAAA_0001u, changed.Value.Old);
        Assert.AreEqual(changed.Value.New, answer.Media[0].Ssrc);
        CollectionAssert.AreEqual(new[] { 0xAAAA_0001u }, answer.Media[0].PreviousSsrcs.ToArray());

        offerer.SetRemoteDescription(answer, SdpType.Answer);
        using PeriodicTimer frames = new(TimeSpan.FromMilliseconds(20));
        for (uint i = 0; !arrived.Task.IsCompleted && i < 500; i++)
        {
            _ = await frames.WaitForNextTickAsync(TestContext.CancellationToken);
            _ = answerer.TrySendRtp(96, 0xAAAA_0001, i * 3000, true, [0x65, 1]);
        }

        Assert.AreEqual(
            changed.Value.New,
            await arrived.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.CancellationToken),
            "the answerer's media leaves from the new SSRC"
        );
    }

    // Section 8.2: the peer sends from this side's SSRC after the session is up; this side says goodbye on
    // it and its media goes on from a fresh one.
    [TestMethod]
    [Timeout(60_000)]
    public async Task RuntimeCollision_MovesToAFreshSsrc()
    {
        InMemoryIceNetwork network = new();
        await using PeerConnection a = Peer(network, "10.0.0.1", 0xAAAA_0001);
        await using PeerConnection b = Peer(network, "10.0.0.2", 0xBBBB_0001);
        TaskCompletionSource<uint> changed = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        b.LocalSsrcChanged += (_, replacement) => changed.TrySetResult(replacement);
        ConcurrentQueue<uint> fromB = new();
        a.RtpReceived += (header, _) => fromB.Enqueue(header.Ssrc);
        await ConnectAsync(a, b);

        // A sends from B's SSRC (unsignalled), as a colliding source would.
        using PeriodicTimer frames = new(TimeSpan.FromMilliseconds(20));
        for (uint i = 0; !changed.Task.IsCompleted && i < 100; i++)
        {
            _ = await frames.WaitForNextTickAsync(TestContext.CancellationToken);
            _ = a.TrySendRtp(96, 0xBBBB_0001, i * 3000, true, [0x65, 1]);
        }

        uint replacement = await changed.Task.WaitAsync(
            TimeSpan.FromSeconds(10),
            TestContext.CancellationToken
        );
        Assert.AreNotEqual(0xBBBB_0001u, replacement);

        fromB.Clear();
        for (uint i = 0; i < 10; i++)
        {
            _ = await frames.WaitForNextTickAsync(TestContext.CancellationToken);
            _ = b.TrySendRtp(96, 0xBBBB_0001, i * 3000, true, [0x65, 2]);
        }

        Assert.Contains(replacement, [.. fromB], "B's media now leaves from the fresh SSRC");
        Assert.DoesNotContain(0xBBBB_0001u, [.. fromB]);
    }

    private async Task ConnectAsync(PeerConnection a, PeerConnection b)
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
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.CancellationToken);
    }

    private static PeerConnection Peer(InMemoryIceNetwork network, string address, uint ssrc) =>
        new(
            new PeerConnectionOptions
            {
                Media = [new MediaLine("0", SdpMediaKind.Video, ssrc, [H264])],
                SocketFactory = network.Factory(IPAddress.Parse(address)),
            },
            Certificate
        );
}
