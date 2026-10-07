using System.Net;
using Agash.StreamTransport.Adaptation;
using Agash.StreamTransport.WebRtc.Rtp;
using Agash.StreamTransport.WebRtc.Sdp;

namespace Agash.StreamTransport.WebRtc.Tests;

// Path MTU discovery end to end on the in-memory network: the network drops every datagram above a size,
// as a path with a smaller MTU does, and the sender's media follows what its probes find.
[TestClass]
public sealed class PathMtuSessionTests
{
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
    [Timeout(60_000)]
    public async Task SmallerPath_MediaGrowsToJustUnderIt()
    {
        int limit = 1350;
        await using Session session = await Session.ConnectAsync(() => limit);

        await session.SendUntilAsync(
            () => session.Sender.MaximumDatagramSize > limit - 40,
            TestContext.CancellationToken
        );

        Assert.IsLessThanOrEqualTo(limit, session.Sender.MaximumDatagramSize);
        // The binary search stops once its next step is under the 20-byte minimum change from the size last
        // probed, which after a failed probe leaves it up to twice that below the path's limit.
        Assert.IsGreaterThan(limit - 40, session.Sender.MaximumDatagramSize);
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task PathWithoutRtx_StaysAtTheBase()
    {
        await using Session session = await Session.ConnectAsync(() => 1500, rtx: false);

        await session.SendForAsync(TimeSpan.FromSeconds(2), TestContext.CancellationToken);

        Assert.AreEqual(1200, session.Sender.MaximumDatagramSize);
    }

    // RFC 8899 section 4.3: once the path stops carrying the confirmed size, media falls back to the base.
    [TestMethod]
    [Timeout(60_000)]
    public async Task PathShrinks_BlackHoleDetectionFallsBackToTheBase()
    {
        int limit = 1452;
        await using Session session = await Session.ConnectAsync(() => Volatile.Read(ref limit));
        await session.SendUntilAsync(
            () => session.Sender.MaximumDatagramSize >= 1400,
            TestContext.CancellationToken
        );
        Assert.IsGreaterThanOrEqualTo(1400, session.Sender.MaximumDatagramSize);

        Volatile.Write(ref limit, 1250);
        await session.SendUntilAsync(
            () => session.Sender.MaximumDatagramSize <= 1250,
            TestContext.CancellationToken
        );

        Assert.IsLessThanOrEqualTo(1250, session.Sender.MaximumDatagramSize);
    }

    // A sender and a receiver with RTX and congestion-control feedback, on a network that drops datagrams
    // above the limit.
    private sealed class Session : IAsyncDisposable
    {
        private const uint VideoSsrc = 0xAAAA_0001;

        private Session(PeerConnection sender, PeerConnection receiver)
        {
            Sender = sender;
            Receiver = receiver;
        }

        public PeerConnection Sender { get; }

        public PeerConnection Receiver { get; }

        public static async Task<Session> ConnectAsync(Func<int> limit, bool rtx = true)
        {
            InMemoryIceNetwork network = new() { Drop = (_, _, data) => data.Length > limit() };
            PeerConnection sender = new(
                Options(network, "10.0.0.1", VideoSsrc, rtx ? 0xAAAA_0002 : null),
                Certificate,
                controller: new ScreamCongestionController()
            );
            PeerConnection receiver = new(
                Options(network, "10.0.0.2", 0xBBBB_0001, rtx ? 0xBBBB_0002 : null),
                Certificate,
                controller: new ScreamCongestionController()
            );
            sender.LocalIceCandidate += receiver.AddRemoteIceCandidate;
            receiver.LocalIceCandidate += sender.AddRemoteIceCandidate;
            TaskCompletionSource connected = new(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            int up = 0;
            void OnState(PeerConnectionState state)
            {
                if (state == PeerConnectionState.Connected && Interlocked.Increment(ref up) == 2)
                {
                    connected.TrySetResult();
                }
            }

            sender.StateChanged += OnState;
            receiver.StateChanged += OnState;
            receiver.SetRemoteDescription(sender.CreateOffer(), SdpType.Offer);
            sender.SetRemoteDescription(receiver.CreateAnswer(), SdpType.Answer);
            await connected.Task.WaitAsync(TimeSpan.FromSeconds(30));
            return new Session(sender, receiver);
        }

        // Sends a frame every 10 ms, a full-size packet and a small one, until the condition holds or the
        // guard runs out.
        public async Task SendUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
        {
            using var guard = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            guard.CancelAfter(TimeSpan.FromSeconds(40));
            await SendAsync(condition, guard.Token);
        }

        public async Task SendForAsync(TimeSpan duration, CancellationToken cancellationToken)
        {
            using var guard = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            guard.CancelAfter(duration);
            await SendAsync(static () => false, guard.Token);
        }

        public async ValueTask DisposeAsync()
        {
            await Sender.DisposeAsync();
            await Receiver.DisposeAsync();
        }

        private async Task SendAsync(Func<bool> condition, CancellationToken cancellationToken)
        {
            using PeriodicTimer frames = new(TimeSpan.FromMilliseconds(10));
            uint timestamp = 0;
            try
            {
                while (!condition() && await frames.WaitForNextTickAsync(cancellationToken))
                {
                    byte[] full = new byte[Sender.MaximumRtpPayloadSize];
                    full[0] = 0x65;
                    timestamp += 900;
                    _ = Sender.TrySendRtp(96, VideoSsrc, timestamp, false, full);
                    _ = Sender.TrySendRtp(96, VideoSsrc, timestamp, true, [0x65, 1, 2, 3]);
                }
            }
            catch (OperationCanceledException)
            {
                // The guard ran out; the caller asserts on the outcome.
            }
        }

        private static PeerConnectionOptions Options(
            InMemoryIceNetwork network,
            string address,
            uint ssrc,
            uint? rtxSsrc
        ) =>
            new()
            {
                Media =
                [
                    new MediaLine(
                        "0",
                        SdpMediaKind.Video,
                        ssrc,
                        rtxSsrc is null ? [H264] : [H264, Rtx.For(97, 96)]
                    )
                    {
                        RtxSsrc = rtxSsrc,
                    },
                ],
                SocketFactory = network.Factory(IPAddress.Parse(address)),
            };
    }
}
