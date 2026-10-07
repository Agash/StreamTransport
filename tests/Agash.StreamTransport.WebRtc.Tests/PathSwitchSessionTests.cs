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

    [TestMethod]
    [Timeout(90_000)]
    public async Task RepairOverStandby_RetransmitsOnTheOtherInterface()
    {
        IPAddress? mediaPath = null;
        int lostOnMediaPath = 0;
        int repairsOnStandby = 0;
        ConcurrentDictionary<ushort, byte> dropped = new();
        InMemoryIceNetwork network = new()
        {
            Drop = (from, _, data) =>
            {
                if (data.Length < 12 || (data[0] & 0xC0) != 0x80)
                {
                    return false;
                }

                uint ssrc = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(
                    data.AsSpan(8)
                );
                if (ssrc == VideoSsrc + 1 && mediaPath is { } media && !from.Address.Equals(media))
                {
                    Interlocked.Increment(ref repairsOnStandby);
                }

                if (
                    ssrc == VideoSsrc
                    && (data[1] & 0x7F) == 96
                    && Interlocked.Increment(ref lostOnMediaPath) % 25 == 0
                )
                {
                    dropped.TryAdd(
                        System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2)),
                        0
                    );
                    return true;
                }

                return false;
            },
        };
        await using PeerConnection sender = new(
            Options(network, VideoSsrc, repairOverStandby: true, "10.0.0.1", "10.1.0.1"),
            Certificate,
            controller: new ScreamCongestionController(Scream)
        );
        await using PeerConnection receiver = new(
            Options(network, 0xBBBB_0001, "10.0.0.2", "10.1.0.2"),
            Certificate,
            controller: new ScreamCongestionController(Scream)
        );
        ConcurrentDictionary<ushort, byte> arrived = new();
        receiver.RtpReceived += (header, _) => arrived.TryAdd(header.SequenceNumber, 0);
        await ConnectAsync(sender, receiver);

        // Let the other interface's pairs validate, then stream with loss on the media path.
        await Task.Delay(TimeSpan.FromSeconds(6), TestContext.CancellationToken);
        mediaPath = sender.SelectedPath!.Value.Local.Address;
        using var media = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.CancellationToken
        );
        media.CancelAfter(TimeSpan.FromSeconds(5));
        await SendAsync(sender, media.Token);
        await Task.Delay(1000, TestContext.CancellationToken);

        int recovered = dropped.Keys.Count(arrived.ContainsKey);
        TestContext.WriteLine(
            $"dropped {dropped.Count}, recovered {recovered}, repairs on standby {repairsOnStandby}"
        );
        Assert.IsGreaterThan(0, repairsOnStandby, "retransmissions travel the other interface.");
        Assert.IsGreaterThan(dropped.Count / 2, recovered);
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
    ) => Options(network, ssrc, repairOverStandby: false, addresses);

    private static PeerConnectionOptions Options(
        InMemoryIceNetwork network,
        uint ssrc,
        bool repairOverStandby,
        params string[] addresses
    ) =>
        new()
        {
            RepairOverStandby = repairOverStandby,
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
