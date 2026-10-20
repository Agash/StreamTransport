using System.Net;
using Agash.StreamTransport.WebRtc.Sdp;
using Dtls.Core;

namespace Agash.StreamTransport.WebRtc.Tests;

// RFC 8842 sections 5 and 6: a new DTLS association comes with an ICE restart, which gives it its own
// 5-tuple. Its handshake runs on the restarted session's pair while the old association carries media,
// and media moves to the new keys once it is up.
[TestClass]
public sealed class DtlsReassociationTests
{
    private static readonly SdpCodec H264 = new(
        96,
        "H264",
        90000,
        null,
        "packetization-mode=1",
        ["nack", "nack pli"]
    );

    public TestContext TestContext { get; set; } = null!;

    // Section 5.5: the offerer asks with a new tls-id, actpass and an ICE restart, here with a rotated
    // certificate; the answerer agrees with a new tls-id of its own (section 5.2). Both sides end on the
    // new association, authenticated by the new certificate, and media keeps flowing both ways.
    [TestMethod]
    [Timeout(60_000)]
    public async Task Renewal_MovesMediaToANewAssociation()
    {
        InMemoryIceNetwork network = new();
        using var first = RtcCertificate.Generate();
        using var rotated = RtcCertificate.Generate();
        using var other = RtcCertificate.Generate();
        await using PeerConnection a = Peer(network, "10.0.0.1", 0xAAAA_0001, first);
        await using PeerConnection b = Peer(network, "10.0.0.2", 0xBBBB_0001, other);
        SdpDescription firstAnswer = await ConnectAsync(a, b);
        TaskCompletionSource<SrtpProtectionProfile> aRenewed = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        TaskCompletionSource<SrtpProtectionProfile> bRenewed = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        a.DtlsAssociationRenewed += profile => aRenewed.TrySetResult(profile);
        b.DtlsAssociationRenewed += profile => bRenewed.TrySetResult(profile);

        SdpDescription offer = a.RenewDtlsAssociation(rotated);
        b.SetRemoteDescription(offer, SdpType.Offer);
        SdpDescription answer = b.CreateAnswer();
        a.SetRemoteDescription(answer, SdpType.Answer);

        Assert.AreEqual(rotated.Fingerprint, offer.Media[0].Fingerprints[0]);
        Assert.AreEqual(SdpSetup.ActPass, offer.Media[0].Setup);
        Assert.AreNotEqual(firstAnswer.Media[0].TlsId, answer.Media[0].TlsId);
        await Task.WhenAll(aRenewed.Task, bRenewed.Task)
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.CancellationToken);
        Assert.AreEqual(PeerConnectionState.Connected, a.State);
        Assert.AreEqual(PeerConnectionState.Connected, b.State);

        await FlowsAsync(a, b, 0xAAAA_0001);
        await FlowsAsync(b, a, 0xBBBB_0001);
    }

    // Section 5.5: a peer that answers with the tls-id it had keeps the association; nothing moves, and
    // the offerer goes on with the certificate it had.
    [TestMethod]
    [Timeout(60_000)]
    public async Task Renewal_ThePeerDeclines_KeepsTheAssociation()
    {
        InMemoryIceNetwork network = new();
        using var first = RtcCertificate.Generate();
        using var rotated = RtcCertificate.Generate();
        using var other = RtcCertificate.Generate();
        await using PeerConnection a = Peer(network, "10.0.0.1", 0xAAAA_0001, first);
        await using PeerConnection b = Peer(network, "10.0.0.2", 0xBBBB_0001, other);
        SdpDescription firstAnswer = await ConnectAsync(a, b);
        bool renewed = false;
        a.DtlsAssociationRenewed += _ => renewed = true;

        b.SetRemoteDescription(a.RenewDtlsAssociation(rotated), SdpType.Offer);
        SdpDescription answer = b.CreateAnswer();
        SdpDescription kept = answer with
        {
            Media =
            [
                answer.Media[0] with
                {
                    TlsId = firstAnswer.Media[0].TlsId,
                },
                .. answer.Media.Skip(1),
            ],
        };
        a.SetRemoteDescription(kept, SdpType.Answer);

        Assert.AreEqual(first.Fingerprint, a.LocalFingerprint);
        await FlowsAsync(a, b, 0xAAAA_0001);
        Assert.IsFalse(renewed);
    }

    // RTP sent from one side arrives at the other.
    private async Task FlowsAsync(PeerConnection from, PeerConnection to, uint ssrc)
    {
        TaskCompletionSource arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnRtp(Rtp.RtpHeader header, ReadOnlyMemory<byte> _)
        {
            if (header.Ssrc == ssrc)
            {
                arrived.TrySetResult();
            }
        }

        to.RtpReceived += OnRtp;
        using PeriodicTimer frames = new(TimeSpan.FromMilliseconds(20));
        for (uint i = 0; !arrived.Task.IsCompleted && i < 500; i++)
        {
            _ = await frames.WaitForNextTickAsync(TestContext.CancellationToken);
            _ = from.TrySendRtp(96, ssrc, i * 3000, true, [0x65, 1, 2, 3]);
        }

        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.CancellationToken);
        to.RtpReceived -= OnRtp;
    }

    private async Task<SdpDescription> ConnectAsync(PeerConnection a, PeerConnection b)
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
        SdpDescription answer = b.CreateAnswer();
        a.SetRemoteDescription(answer, SdpType.Answer);
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.CancellationToken);
        a.StateChanged -= OnState;
        b.StateChanged -= OnState;
        return answer;
    }

    private static PeerConnection Peer(
        InMemoryIceNetwork network,
        string address,
        uint ssrc,
        RtcCertificate certificate
    ) =>
        new(
            new PeerConnectionOptions
            {
                Media = [new MediaLine("0", SdpMediaKind.Video, ssrc, [H264])],
                SocketFactory = network.Factory(IPAddress.Parse(address)),
            },
            certificate
        );
}
