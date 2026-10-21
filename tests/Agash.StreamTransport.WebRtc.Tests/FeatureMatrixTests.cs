using System.Collections.Concurrent;
using System.Net;
using Agash.StreamTransport.Adaptation;
using Agash.StreamTransport.WebRtc.Rtp;
using Agash.StreamTransport.WebRtc.Sdp;

namespace Agash.StreamTransport.WebRtc.Tests;

// Every negotiable feature switched by the SDP alone: the offer either carries it or has it taken out, the
// session runs the same traffic (video with one packet lost, audio, a keyframe request, a rate limit), and
// the feature's effect shows when it was agreed and is absent when it was not. The effects are read where
// they happen: the receiver's RTP headers, the recovery counters, the bytes sent per class, the ECN mark on
// the wire, the requests the peer gets.
[TestClass]
public sealed class FeatureMatrixTests
{
    private const uint SenderVideo = 0xAAAA_0001;
    private const uint SenderRtx = 0xAAAA_0002;
    private const uint SenderFec = 0xAAAA_0003;
    private const uint SenderAudio = 0xAAAA_0004;

    private static readonly RtcCertificate Certificate = RtcCertificate.Generate();

    private static readonly SdpCodec H264 = new(
        96,
        "H264",
        90000,
        null,
        "packetization-mode=1",
        ["nack", "nack pli", "ccm tmmbr"]
    );

    private static readonly SdpCodec Opus = new(111, "opus", 48000, 2, null, []);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [Timeout(90_000)]
    [DataRow("mid", true)]
    [DataRow("mid", false)]
    [DataRow("abs-capture-time", true)]
    [DataRow("abs-capture-time", false)]
    [DataRow("audio-level", true)]
    [DataRow("audio-level", false)]
    [DataRow("video-orientation", true)]
    [DataRow("video-orientation", false)]
    [DataRow("transport-cc", true)]
    [DataRow("transport-cc", false)]
    [DataRow("nack", true)]
    [DataRow("nack", false)]
    [DataRow("rtx", true)]
    [DataRow("rtx", false)]
    [DataRow("flexfec", true)]
    [DataRow("flexfec", false)]
    [DataRow("pli", true)]
    [DataRow("pli", false)]
    [DataRow("tmmbr", true)]
    [DataRow("tmmbr", false)]
    [DataRow("ecn", true)]
    [DataRow("ecn", false)]
    [DataRow("bandwidth", true)]
    [DataRow("bandwidth", false)]
    public async Task Feature_TakesEffectOnlyWhenNegotiated(string feature, bool offered)
    {
        InMemoryIceNetwork network = new();
        Observed seen = new();
        network.Remark = (data, ecn) =>
        {
            if (data.Length >= 12 && data[0] >> 6 == 2 && (data[1] & 0x7F) == 96)
            {
                seen.VideoMarks.Enqueue(ecn);
            }

            return ecn;
        };
        await using PeerConnection sender = Peer(network, "10.0.0.1", sending: true);
        await using PeerConnection receiver = Peer(network, "10.0.0.2", sending: false);
        receiver.RtpReceived += (header, _) => seen.Headers.Enqueue(header);
        sender.KeyframeRequested += _ => Interlocked.Increment(ref seen.KeyframeRequests);

        SdpDescription offer = sender.CreateOffer();
        if (feature == "transport-cc")
        {
            // A browser-like peer: with RFC 8888 feedback offered too the answer keeps that one, so
            // transport-wide feedback is measured where it is the only kind.
            offer = offer with
            {
                Media =
                [
                    .. offer.Media.Select(static m => m with { CongestionControlFeedback = false }),
                ],
            };
        }

        if (!offered && feature != "bandwidth")
        {
            offer = Without(offer, feature);
        }

        // RFC 8829 section 5.10: the answer's b= line limits what the offerer sends.
        Func<SdpDescription, SdpDescription> answer = static a => a;
        if (offered && feature == "bandwidth")
        {
            answer = static a =>
                a with
                {
                    Media =
                    [
                        a.Media[0] with
                        {
                            Bandwidth = new SdpBandwidth(TransportIndependent: 800_000),
                        },
                        .. a.Media.Skip(1),
                    ],
                };
        }

        await ConnectAsync(sender, receiver, offer, answer);

        // Video with the fifth packet lost once, audio alongside, every packet carrying the values the
        // header extensions can say.
        int lost = -1;
        int media = 0;
        network.Drop = (_, _, data) =>
            data.Length >= 12
            && data[0] >> 6 == 2
            && (data[1] & 0x7F) == 96
            && Volatile.Read(ref lost) < 0
            && Interlocked.Increment(ref media) == 5
            && Interlocked.Exchange(ref lost, 1) < 0;
        await SendAsync(sender, frames: 60);
        await receiver.RequestKeyframeAsync(SenderVideo, TestContext.CancellationToken);
        bool limited = await receiver.RequestSendLimitAsync(
            SenderVideo,
            300_000,
            TestContext.CancellationToken
        );
        await SendAsync(sender, frames: 40);

        bool effect = feature switch
        {
            "mid" => seen.Headers.Any(static h => h.Extensions.Mid is not null),
            "abs-capture-time" => seen.Headers.Any(static h =>
                h.Extensions.AbsoluteCaptureTimeNtp is not null
            ),
            "audio-level" => seen.Headers.Any(static h => h.Extensions.AudioLevel is not null),
            "video-orientation" => seen.Headers.Any(static h =>
                h.Extensions.VideoOrientation is not null
            ),
            "transport-cc" => seen.Headers.Any(static h =>
                h.Extensions.TransportSequence is not null
            ),
            "nack" => receiver.CurrentLossStats.NackSequencesRequested > 0,
            "rtx" => sender.CurrentLossStats.RtxPacketsSent > 0,
            "flexfec" => sender.SentBytes(TrafficClass.Repair) > 0,
            "pli" => Volatile.Read(ref seen.KeyframeRequests) > 0,
            "tmmbr" => limited && sender.PeerRequestedLimitBitsPerSecond == 300_000,
            "ecn" => seen.VideoMarks.Any(static e => e != EcnCodepoint.NotEct),
            "bandwidth" => sender
                .NegotiatedMedia.Single(m => m.Kind == SdpMediaKind.Video)
                .SendLimitBitsPerSecond
                is not null,
            _ => throw new ArgumentOutOfRangeException(nameof(feature)),
        };

        Assert.AreEqual(
            offered,
            effect,
            offered
                ? $"{feature} was agreed but had no effect"
                : $"{feature} was taken out of the offer but still had an effect"
        );
    }

    // The offer with one feature taken out: its extension map entry, its feedback, its codec or its
    // capability, as an endpoint that does not support it would offer.
    private static SdpDescription Without(SdpDescription offer, string feature) =>
        offer with
        {
            Media = [.. offer.Media.Select(m => WithoutIn(m, feature))],
        };

    private static SdpMediaDescription WithoutIn(SdpMediaDescription section, string feature) =>
        feature switch
        {
            "mid" => WithoutExtension(section, RtpExtensionUris.Mid),
            "abs-capture-time" => WithoutExtension(section, RtpExtensionUris.AbsoluteCaptureTime),
            "audio-level" => WithoutExtension(section, RtpExtensionUris.AudioLevel),
            "video-orientation" => WithoutExtension(section, RtpExtensionUris.VideoOrientation),
            "transport-cc" => WithoutExtension(section, RtpExtensionUris.TransportWideSequence) with
            {
                TransportWideFeedback = false,
                Codecs =
                [
                    .. section.Codecs.Select(static c =>
                        c with
                        {
                            RtcpFeedback =
                            [
                                .. c.RtcpFeedback.Where(static f => f != "transport-cc"),
                            ],
                        }
                    ),
                ],
            },
            "nack" => WithoutFeedback(
                section,
                static f => f.StartsWith("nack", StringComparison.Ordinal)
            ),
            "pli" => WithoutFeedback(section, static f => f is "nack pli" or "ccm fir"),
            "tmmbr" => WithoutFeedback(section, static f => f == "ccm tmmbr"),
            "rtx" => section with
            {
                Codecs = [.. section.Codecs.Where(static c => !Rtx.IsRtx(c))],
                RtxSsrc = null,
            },
            "flexfec" => section with
            {
                Codecs = [.. section.Codecs.Where(static c => !FlexFec.IsFlexFec(c))],
                FecSsrc = null,
            },
            "ecn" => section with { Ecn = null },
            "bandwidth" => section with { Bandwidth = default },
            _ => throw new ArgumentOutOfRangeException(nameof(feature)),
        };

    private static SdpMediaDescription WithoutExtension(SdpMediaDescription section, string uri) =>
        section with
        {
            Extensions = [.. section.Extensions.Where(e => e.Uri != uri)],
        };

    private static SdpMediaDescription WithoutFeedback(
        SdpMediaDescription section,
        Func<string, bool> taken
    ) =>
        section with
        {
            Codecs =
            [
                .. section.Codecs.Select(c =>
                    c with
                    {
                        RtcpFeedback = [.. c.RtcpFeedback.Where(f => !taken(f))],
                    }
                ),
            ],
        };

    private async Task SendAsync(PeerConnection sender, int frames)
    {
        using PeriodicTimer tick = new(TimeSpan.FromMilliseconds(20));
        byte[] video = new byte[900];
        byte[] audio = new byte[80];
        for (uint i = 0; i < frames; i++)
        {
            _ = await tick.WaitForNextTickAsync(TestContext.CancellationToken);
            video[0] = 0x65;
            _ = sender.TrySendRtp(
                96,
                SenderVideo,
                i * 3000,
                true,
                video,
                new RtpExtensionValues(
                    AbsoluteCaptureTimeNtp: 0x0123_4567_89AB_CDEF,
                    VideoOrientation: 1
                )
            );
            _ = sender.TrySendRtp(
                111,
                SenderAudio,
                i * 960,
                false,
                audio,
                new RtpExtensionValues(AudioLevel: 30)
            );
        }
    }

    private async Task ConnectAsync(
        PeerConnection sender,
        PeerConnection receiver,
        SdpDescription offer,
        Func<SdpDescription, SdpDescription> answer
    )
    {
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
        receiver.SetRemoteDescription(offer, SdpType.Offer);
        sender.SetRemoteDescription(answer(receiver.CreateAnswer()), SdpType.Answer);
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.CancellationToken);
    }

    private static PeerConnection Peer(InMemoryIceNetwork network, string address, bool sending) =>
        new(
            new PeerConnectionOptions
            {
                Media =
                [
                    new MediaLine(
                        "0",
                        SdpMediaKind.Video,
                        sending ? SenderVideo : 0xBBBB_0001,
                        [H264, Rtx.For(97, 96), FlexFec.Codec(100)]
                    )
                    {
                        RtxSsrc = sending ? SenderRtx : 0xBBBB_0002,
                        FecSsrc = sending ? SenderFec : null,
                        Direction = sending ? SdpDirection.SendOnly : SdpDirection.RecvOnly,
                    },
                    new MediaLine(
                        "1",
                        SdpMediaKind.Audio,
                        sending ? SenderAudio : 0xBBBB_0004,
                        [Opus]
                    )
                    {
                        Direction = sending ? SdpDirection.SendOnly : SdpDirection.RecvOnly,
                    },
                ],
                SocketFactory = network.Factory(IPAddress.Parse(address)),

                // The in-memory network has next to no round trip, where the recovery policy repairs by
                // retransmission alone; FEC is offered here to be measured.
                Recovery = new RecoveryPolicyOptions
                {
                    RetransmitOnlyRoundTrip = TimeSpan.Zero,
                    SmallFrameRoundTrip = TimeSpan.Zero,
                },
            },
            Certificate,
            controller: new ScreamCongestionController()
        );

    private sealed class Observed
    {
        public ConcurrentQueue<RtpHeader> Headers { get; } = new();

        public ConcurrentQueue<EcnCodepoint> VideoMarks { get; } = new();

        public int KeyframeRequests;
    }
}
