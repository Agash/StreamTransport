using System.Buffers.Binary;
using System.Net;
using Agash.StreamTransport.Adaptation;
using Agash.StreamTransport.WebRtc.Rtcp;
using Agash.StreamTransport.WebRtc.Sdp;

namespace Agash.StreamTransport.WebRtc.Tests;

// RFC 5104 section 4.2 (TMMBR and TMMBN), which RFC 8834 section 5.1.4 requires.
[TestClass]
public sealed class TmmbrTests
{
    private static readonly RtcCertificate Certificate = RtcCertificate.Generate();

    private static readonly SdpCodec H264 = new(
        96,
        "H264",
        90000,
        null,
        "packetization-mode=1",
        ["nack", "nack pli", "ccm fir", "ccm tmmbr"]
    );

    public TestContext TestContext { get; set; } = null!;

    // Section 4.2.1.1: RTPFB FMT 3, media source 0, then SSRC, exponent (6), mantissa (17), overhead (9).
    [TestMethod]
    public void Tmmbr_WritesTheFieldsAndReadsThemBack()
    {
        byte[] packet = new byte[32];

        int length = RtcpFeedback.BuildTmmbr(packet, 0x1111, 0x2222, 2_500_000, 56);

        Assert.AreEqual(20, length);
        Assert.AreEqual((byte)(0x80 | 3), packet[0]);
        Assert.AreEqual((byte)205, packet[1]);
        Assert.AreEqual(0u, BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(8)));
        uint word = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(16));
        Assert.AreEqual(5u, word >> 26, "2.5 Mbit/s is 78125 << 5");
        Assert.AreEqual(78125u, (word >> 9) & 0x1FFFF);
        Assert.AreEqual(56u, word & 0x1FF);

        RtcpElement element = FirstElement(packet.AsSpan(0, length));
        List<TmmbEntry> entries = [];
        Assert.IsTrue(RtcpFeedback.TryReadTmmbr(element, out uint sender, entries));
        Assert.AreEqual(0x1111u, sender);
        Assert.AreEqual(new TmmbEntry(0x2222, 2_500_000, 56), entries.Single());
        Assert.IsFalse(RtcpFeedback.TryReadTmmbn(element, out _, []));
    }

    // A limit the mantissa cannot hold exactly is rounded down, never above what was asked.
    [TestMethod]
    public void Tmmbr_RoundsDown()
    {
        byte[] packet = new byte[32];
        int length = RtcpFeedback.BuildTmmbr(packet, 1, 2, 1_234_567, 0);
        List<TmmbEntry> entries = [];
        RtcpElement element = FirstElement(packet.AsSpan(0, length));

        Assert.IsTrue(RtcpFeedback.TryReadTmmbr(element, out _, entries));
        Assert.IsLessThanOrEqualTo(1_234_567, entries[0].BitsPerSecond);
        Assert.IsGreaterThan(1_234_567 - 16, entries[0].BitsPerSecond);
    }

    private static RtcpElement FirstElement(ReadOnlySpan<byte> rtcp)
    {
        foreach (RtcpElement element in RtcpCompound.Enumerate(rtcp))
        {
            return element;
        }

        throw new AssertFailedException("no RTCP element");
    }

    // The receiver asks the sender to keep under 300 kbit/s: the sender's capacity follows, and taking
    // the request back lifts it.
    [TestMethod]
    [Timeout(60_000)]
    public async Task Request_CapsTheSendersCapacity()
    {
        InMemoryIceNetwork network = new();
        await using PeerConnection sender = new(
            new PeerConnectionOptions
            {
                Media = [new MediaLine("0", SdpMediaKind.Video, 0xAAAA_0001, [H264])],
                SocketFactory = network.Factory(IPAddress.Parse("10.0.0.1")),
            },
            Certificate,
            controller: new ScreamCongestionController()
        );
        await using PeerConnection receiver = new(
            new PeerConnectionOptions
            {
                Media = [new MediaLine("0", SdpMediaKind.Video, 0xBBBB_0001, [H264])],
                SocketFactory = network.Factory(IPAddress.Parse("10.0.0.2")),
            },
            Certificate
        );
        sender.LocalIceCandidate += receiver.AddRemoteIceCandidate;
        receiver.LocalIceCandidate += sender.AddRemoteIceCandidate;
        TaskCompletionSource connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
        TaskCompletionSource<CapacityEstimate> capped = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        TaskCompletionSource<CapacityEstimate> lifted = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        sender.CapacityChanged += estimate =>
        {
            if (sender.PeerRequestedLimitBitsPerSecond is not null)
            {
                capped.TrySetResult(estimate);
            }
            else if (capped.Task.IsCompleted)
            {
                lifted.TrySetResult(estimate);
            }
        };
        receiver.SetRemoteDescription(sender.CreateOffer(), SdpType.Offer);
        sender.SetRemoteDescription(receiver.CreateAnswer(), SdpType.Answer);
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.CancellationToken);

        Assert.IsTrue(
            await receiver.RequestSendLimitAsync(
                0xAAAA_0001,
                300_000,
                TestContext.CancellationToken
            )
        );
        CapacityEstimate estimate = await capped.Task.WaitAsync(
            TimeSpan.FromSeconds(10),
            TestContext.CancellationToken
        );

        Assert.AreEqual(300_000, sender.PeerRequestedLimitBitsPerSecond);
        Assert.IsLessThanOrEqualTo(300_000, estimate.TargetBitsPerSecond);

        Assert.IsTrue(
            await receiver.RequestSendLimitAsync(0xAAAA_0001, null, TestContext.CancellationToken)
        );
        _ = await lifted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.CancellationToken);
        Assert.IsNull(
            sender.PeerRequestedLimitBitsPerSecond,
            "taking the request back lifts the cap"
        );
    }
}
