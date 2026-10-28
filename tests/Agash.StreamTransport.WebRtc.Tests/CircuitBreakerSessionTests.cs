using System.Net;
using Agash.StreamTransport.Adaptation;
using Agash.StreamTransport.WebRtc.Sdp;

namespace Agash.StreamTransport.WebRtc.Tests;

// RFC 8083 on a connection: an outage on the forward path, with the receiver's reports still coming back,
// trips the media timeout and media ceases (section 4.2); once ICE shows the path carries packets both ways
// again, media resumes without the application, no sooner than the time it took to trip (section 4.5).
[TestClass]
public sealed class CircuitBreakerSessionTests
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

    [TestMethod]
    [Timeout(90_000)]
    public async Task ForwardPathOutage_CeasesMedia_ThenResumesOnceThePathWorks()
    {
        InMemoryIceNetwork network = new();
        await using var sender = new PeerConnection(
            new PeerConnectionOptions
            {
                Media = [new MediaLine("0", SdpMediaKind.Video, 0xAAAA_0001, [H264])],
                SocketFactory = network.Factory(IPAddress.Parse("10.0.0.1")),
            },
            Certificate
        );
        await using var receiver = new PeerConnection(
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
        receiver.SetRemoteDescription(sender.CreateOffer(), SdpType.Offer);
        sender.SetRemoteDescription(receiver.CreateAnswer(), SdpType.Answer);
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(30));

        TaskCompletionSource opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        sender.CircuitBreakerChanged += (state, reason) =>
        {
            if (state == CircuitBreakerState.Open && reason == CircuitBreakerReason.MediaTimeout)
            {
                opened.TrySetResult();
            }
            else if (state == CircuitBreakerState.Closed && opened.Task.IsCompleted)
            {
                closed.TrySetResult();
            }
        };

        // The sender keeps sending a frame every 20 ms throughout.
        using CancellationTokenSource stop = new();
        var media = Task.Run(
            async () =>
            {
                for (uint i = 0; !stop.IsCancellationRequested; i++)
                {
                    _ = sender.TrySendRtp(96, 0xAAAA_0001, i * 1800, marker: true, [0x65, (byte)i]);
                    await Task.Delay(20, CancellationToken.None);
                }
            },
            CancellationToken.None
        );
        await Task.Delay(TimeSpan.FromSeconds(2));

        // Everything the sender sends is lost; the receiver's reports still arrive.
        var senderAddress = IPAddress.Parse("10.0.0.1");
        network.Drop = (from, _, _) => from.Address.Equals(senderAddress);
        await opened.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // A send that passed the gate just before the trip may still be on its way out; once it is,
        // nothing more goes.
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        long sentWhileOpen = sender.SentBytes(TrafficClass.Video);
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.AreEqual(sentWhileOpen, sender.SentBytes(TrafficClass.Video), "media ceased");

        network.Drop = null;
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.AreEqual(CircuitBreakerState.Closed, sender.CircuitBreakerState);

        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.IsGreaterThan(
            sentWhileOpen,
            sender.SentBytes(TrafficClass.Video),
            "media flows again"
        );
        stop.Cancel();
        await media;
    }
}
