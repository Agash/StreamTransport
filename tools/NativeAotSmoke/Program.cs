using System.Diagnostics;
using Agash.StreamTransport.WebRtc;
using Agash.StreamTransport.WebRtc.CongestionControl;
using Agash.StreamTransport.WebRtc.Dtls;
using Agash.StreamTransport.WebRtc.Sdp;

// A NativeAOT smoke test for the first-party WebRTC stack: two peer connections negotiate over loopback,
// complete ICE -> DTLS-SRTP, and exchange an encrypted RTP packet. If this runs as an AOT single-file
// binary, the stack (incl. BouncyCastle DTLS and the SCReAM controller) is AOT-safe end to end.

var opusCodec = new SdpCodec(111, "opus", 48000, 2, null, []);
PeerConnectionOptions Options(uint ssrc) => new()
{
    IncludeLoopback = true,
    Media = [new MediaLine("0", SdpMediaKind.Audio, ssrc, [opusCodec])],
};

await using var offerer = new PeerConnection(Options(0x1111_1111), new DtlsTransportFactory());
await using var answerer = new PeerConnection(Options(0x2222_2222), new DtlsTransportFactory());

offerer.LocalIceCandidate += c => answerer.AddRemoteIceCandidate(c);
answerer.LocalIceCandidate += c => offerer.AddRemoteIceCandidate(c);

var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
int count = 0;
void OnState(PeerConnectionState s)
{
    if (s == PeerConnectionState.Connected && Interlocked.Increment(ref count) == 2)
    {
        connected.TrySetResult();
    }
}

offerer.StateChanged += OnState;
answerer.StateChanged += OnState;

var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
answerer.RtpReceived += (_, payload) => received.TrySetResult(payload.ToArray());

// Exercise the SCReAM controller too (pure AOT-safe math), so it's covered by this binary.
var controller = new ScreamCongestionController();
_ = controller.OnProcessInterval(0);

var sw = Stopwatch.StartNew();
SdpDescription offer = offerer.CreateOffer();
answerer.SetRemoteDescription(offer, SdpType.Offer);
offerer.SetRemoteDescription(answerer.CreateAnswer(), SdpType.Answer);

try
{
    await Task.WhenAll(connected.Task, AwaitFirstRtp()).WaitAsync(TimeSpan.FromSeconds(20));
}
catch (TimeoutException)
{
    Console.WriteLine("NATIVE-AOT-FAIL: timed out before connect/media");
    return 1;
}

Console.WriteLine($"NATIVE-AOT-OK: connected + encrypted RTP delivered in {sw.ElapsedMilliseconds} ms (controller {controller.CurrentEstimate.TargetBitrateBps} bps)");
return 0;

async Task AwaitFirstRtp()
{
    await connected.Task.ConfigureAwait(false);
    byte[] payload = [0xCA, 0xFE, 0xBA, 0xBE];
    await offerer.SendRtp(111, 0x1111_1111, rtpTimestamp: 0, marker: true, payload).ConfigureAwait(false);
    byte[] got = await received.Task.ConfigureAwait(false);
    if (!got.AsSpan().SequenceEqual(payload))
    {
        throw new InvalidOperationException("payload mismatch");
    }
}
