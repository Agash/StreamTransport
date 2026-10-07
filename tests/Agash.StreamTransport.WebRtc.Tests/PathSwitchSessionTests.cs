using System.Collections.Concurrent;
using System.Net;
using Agash.StreamTransport.Adaptation;
using Agash.StreamTransport.WebRtc.Ice;
using Agash.StreamTransport.WebRtc.Rtp;
using Agash.StreamTransport.WebRtc.Sdp;

namespace Agash.StreamTransport.WebRtc.Tests;

/// <summary>
/// A sender and a receiver on two interfaces each, over the in-memory network, with media flowing when the
/// selected path dies: ICE moves to the other interface, congestion control starts over for the new path
/// (RFC 9000 section 9.4) from the round trip the standby pair measured, and media carries on.
/// </summary>
[TestClass]
public sealed class PathSwitchSessionTests
{
    private const uint VideoSsrc = 0xAAAA_0001;
    private static readonly ScreamOptions Scream = new() { StartBitrateBps = 1_000_000 };
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
    [Timeout(90_000)]
    public async Task SelectedPathDies_MediaMovesAndCongestionControlStartsOver()
    {
        HashSet<IPAddress> cut = [];
        InMemoryIceNetwork network = new()
        {
            Drop = (from, to, _) =>
            {
                lock (cut)
                {
                    return cut.Contains(from.Address) || cut.Contains(to.Address);
                }
            },
        };
        await using PeerConnection sender = new(
            Options(network, VideoSsrc, "10.0.0.1", "10.1.0.1"),
            Certificate,
            controller: new ScreamCongestionController(Scream)
        );
        await using PeerConnection receiver = new(
            Options(network, 0xBBBB_0001, "10.0.0.2", "10.1.0.2"),
            Certificate,
            controller: new ScreamCongestionController(Scream)
        );
        await ConnectAsync(sender, receiver);

        ConcurrentQueue<CapacityEstimate> estimates = new();
        int received = 0;
        int keyframeRequests = 0;
        sender.CapacityChanged += estimates.Enqueue;
        receiver.RtpReceived += (_, _) => Interlocked.Increment(ref received);
        sender.KeyframeRequested += _ => Interlocked.Increment(ref keyframeRequests);

        using var media = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.CancellationToken
        );
        Task sending = SendAsync(sender, media.Token);

        // Warm: the standby pair's keep-alives measure its round trip, feedback moves the estimate.
        await Task.Delay(TimeSpan.FromSeconds(6), TestContext.CancellationToken);
        IcePath before = sender.SelectedPath!.Value;
        int keyframesBefore = Volatile.Read(ref keyframeRequests);
        estimates.Clear();
        lock (cut)
        {
            cut.Add(before.Local.Address);
        }

        // The path dies silently; ICE moves within seconds.
        using (CancellationTokenSource guard = new(TimeSpan.FromSeconds(20)))
        {
            while (
                sender.SelectedPath is not { } now || now.Local.Address.Equals(before.Local.Address)
            )
            {
                await Task.Delay(50, guard.Token);
            }
        }

        IcePath after = sender.SelectedPath!.Value;
        int receivedAtSwitch = Volatile.Read(ref received);
        await Task.Delay(TimeSpan.FromSeconds(2), TestContext.CancellationToken);
        await media.CancelAsync();
        await sending;

        TestContext.WriteLine($"moved {before.Local} -> {after.Local}");
        Assert.IsTrue(
            estimates.Any(e =>
                e.TargetBitsPerSecond == Scream.StartBitrateBps
                && e.SmoothedRoundTrip > TimeSpan.Zero
            ),
            "the controller starts over at the start rate, seeded with the new pair's round trip."
        );
        Assert.IsGreaterThan(
            receivedAtSwitch + 100,
            Volatile.Read(ref received),
            "media carries on over the new path."
        );
        Assert.AreEqual(
            keyframesBefore,
            Volatile.Read(ref keyframeRequests),
            "the switch costs no keyframe: NACK repairs the gap."
        );
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

    // A frame every 10 ms, a full-size packet and a small one.
    private static async Task SendAsync(PeerConnection sender, CancellationToken cancellationToken)
    {
        using PeriodicTimer frames = new(TimeSpan.FromMilliseconds(10));
        uint timestamp = 0;
        try
        {
            while (await frames.WaitForNextTickAsync(cancellationToken))
            {
                byte[] full = new byte[sender.MaximumRtpPayloadSize];
                full[0] = 0x65;
                timestamp += 900;
                _ = sender.TrySendRtp(96, VideoSsrc, timestamp, false, full);
                _ = sender.TrySendRtp(96, VideoSsrc, timestamp, true, [0x65, 1, 2, 3]);
            }
        }
        catch (OperationCanceledException)
        {
            // Deliberately not logged: the test stops the media this way.
        }
    }

    private static PeerConnectionOptions Options(
        InMemoryIceNetwork network,
        uint ssrc,
        params string[] addresses
    ) =>
        new()
        {
            Media =
            [
                new MediaLine("0", SdpMediaKind.Video, ssrc, [H264, Rtx.For(97, 96)])
                {
                    RtxSsrc = ssrc + 1,
                },
            ],
            SocketFactory = network.Factory([.. addresses.Select(IPAddress.Parse)]),
        };
}
