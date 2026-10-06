using System.Collections.Concurrent;
using System.Net;
using Agash.StreamTransport.WebRtc;
using Agash.StreamTransport.WebRtc.CongestionControl;
using Agash.StreamTransport.WebRtc.Rtp;
using Agash.StreamTransport.WebRtc.Rtp.PayloadFormats;
using Agash.StreamTransport.WebRtc.Sdp;

namespace Agash.StreamTransport.WebRtc.Tests;

/// <summary>
/// The full-stack end-to-end test: two <see cref="PeerConnection"/>s negotiate an offer/answer, trickle
/// candidates, connect over loopback (ICE → DTLS-SRTP), and exchange an encrypted RTP packet. This
/// exercises every layer built so far together - STUN, ICE, DTLS, SRTP, RTP, SDP.
/// </summary>
/// <remarks>
/// These bind real loopback UDP sockets and run full ICE/DTLS-SRTP handshakes. The DTLS handshake is blocking
/// but runs on a dedicated (non-pool) thread, so it no longer starves the thread pool that delivers its inbound
/// records - which means these run correctly alongside the parallel pool with no special isolation.
/// </remarks>
[TestClass]
public sealed class PeerConnectionTests
{
    // Both peers may share one identity: each authenticates the other by the fingerprint it signalled.
    private static readonly RtcCertificate Certificate = RtcCertificate.Generate();

    [TestMethod]
    [Timeout(90_000)]
    public async Task OfferAnswer_ConnectsAndDeliversEncryptedRtp()
    {
        InMemoryIceNetwork network = new();
        var opusCodec = new SdpCodec(111, "opus", 48000, 2, null, []);
        var offererOptions = new PeerConnectionOptions
        {
            SocketFactory = network.Factory(IPAddress.Parse("10.0.0.1")),
            Media = [new MediaLine("0", SdpMediaKind.Audio, LocalSsrc: 0x1111_1111, [opusCodec])],
        };
        var answererOptions = new PeerConnectionOptions
        {
            SocketFactory = network.Factory(IPAddress.Parse("10.0.0.2")),
            Media = [new MediaLine("0", SdpMediaKind.Audio, LocalSsrc: 0x2222_2222, [opusCodec])],
        };

        await using var offerer = new PeerConnection(offererOptions, Certificate);
        await using var answerer = new PeerConnection(answererOptions, Certificate);

        // Trickle ICE both ways.
        offerer.LocalIceCandidate += c => answerer.AddRemoteIceCandidate(c);
        answerer.LocalIceCandidate += c => offerer.AddRemoteIceCandidate(c);

        var offererConnected = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var answererConnected = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        offerer.StateChanged += s =>
        {
            if (s == PeerConnectionState.Connected)
            {
                offererConnected.TrySetResult();
            }
        };
        answerer.StateChanged += s =>
        {
            if (s == PeerConnectionState.Connected)
            {
                answererConnected.TrySetResult();
            }
        };

        var received = new TaskCompletionSource<(RtpHeader Header, byte[] Payload)>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        answerer.RtpReceived += (header, payload) =>
            received.TrySetResult((header, payload.ToArray()));

        // Offer / answer exchange.
        SdpDescription offer = offerer.CreateOffer();
        answerer.SetRemoteDescription(offer, SdpType.Offer);
        SdpDescription answer = answerer.CreateAnswer();
        offerer.SetRemoteDescription(answer, SdpType.Answer);

        await Task.WhenAll(offererConnected.Task, answererConnected.Task)
            .WaitAsync(TimeSpan.FromSeconds(60));
        Assert.AreEqual(PeerConnectionState.Connected, offerer.State);
        Assert.AreEqual(PeerConnectionState.Connected, answerer.State);

        // Send an encrypted RTP packet offerer -> answerer with abs-capture-time.
        const ulong captureNtp = 0xE5_00_00_00_80_00_00_00;
        byte[] payload = [0xCA, 0xFE, 0xBA, 0xBE, 0x10, 0x20];
        Assert.IsTrue(
            offerer.TrySendRtp(
                payloadType: 111,
                ssrc: 0x1111_1111,
                rtpTimestamp: 160,
                marker: true,
                payload,
                captureNtp
            )
        );

        (RtpHeader header, byte[] got) = await received.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.AreEqual(111, header.PayloadType);
        Assert.AreEqual(0x1111_1111u, header.Ssrc);
        Assert.AreEqual(160u, header.Timestamp);
        Assert.IsTrue(header.Marker);
        Assert.AreEqual(captureNtp, header.AbsoluteCaptureTimeNtp);
        CollectionAssert.AreEqual(payload, got);
    }

    [TestMethod]
    [Timeout(90_000)]
    public async Task Mobility_Recovery_ReconnectsAndPreservesSrtpSession()
    {
        InMemoryIceNetwork network = new();
        // Connect, then force a mobility recovery on the sender. A packet sent AFTER recovery must still
        // decrypt at the receiver - which only works if the SRTP session (keys + rollover counter) was
        // preserved across the re-probe, the core mobility guarantee.
        var opus = new SdpCodec(111, "opus", 48000, 2, null, []);
        var offererOptions = new PeerConnectionOptions
        {
            SocketFactory = network.Factory(IPAddress.Parse("10.0.0.1")),
            Media = [new MediaLine("0", SdpMediaKind.Audio, LocalSsrc: 0x1111_1111, [opus])],
        };
        var answererOptions = new PeerConnectionOptions
        {
            SocketFactory = network.Factory(IPAddress.Parse("10.0.0.2")),
            Media = [new MediaLine("0", SdpMediaKind.Audio, LocalSsrc: 0x2222_2222, [opus])],
        };

        await using var offerer = new PeerConnection(offererOptions, Certificate);
        await using var answerer = new PeerConnection(answererOptions, Certificate);

        offerer.LocalIceCandidate += c => answerer.AddRemoteIceCandidate(c);
        answerer.LocalIceCandidate += c => offerer.AddRemoteIceCandidate(c);

        var firstConnected = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        offerer.StateChanged += s =>
        {
            if (s == PeerConnectionState.Connected)
            {
                firstConnected.TrySetResult();
            }
        };

        var answererConnected = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        answerer.StateChanged += s =>
        {
            if (s == PeerConnectionState.Connected)
            {
                answererConnected.TrySetResult();
            }
        };

        Arrivals received = new();
        answerer.RtpReceived += (header, _) => received.Record(header.Timestamp);

        SdpDescription offer = offerer.CreateOffer();
        answerer.SetRemoteDescription(offer, SdpType.Offer);
        SdpDescription answer = answerer.CreateAnswer();
        offerer.SetRemoteDescription(answer, SdpType.Answer);

        await Task.WhenAll(firstConnected.Task, answererConnected.Task)
            .WaitAsync(TimeSpan.FromSeconds(60));

        byte[] payload = [0xCA, 0xFE, 0xBA, 0xBE];
        Assert.IsTrue(
            offerer.TrySendRtp(111, 0x1111_1111, rtpTimestamp: 1000, marker: true, payload)
        );
        await received.Of(1000).WaitAsync(TimeSpan.FromSeconds(5));

        // Force the path recovery. The PeerConnection stays Connected (DTLS/SRTP never drop - the whole point);
        // only ICE re-probes underneath. Re-send the post-recovery packet until ICE re-nominates and it lands
        // (sends during the brief no-pair window are dropped). It decrypts only if keys + ROC survived.
        offerer.TriggerNetworkRecovery();
        await Cadence.SendUntilAsync(
            () => offerer.TrySendRtp(111, 0x1111_1111, rtpTimestamp: 2000, marker: true, payload),
            received.Of(2000),
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromSeconds(10)
        );
        Assert.IsTrue(
            received.Of(2000).IsCompleted,
            "a packet sent after recovery must still decrypt (SRTP preserved)."
        );
    }

    [TestMethod]
    [Timeout(90_000)]
    public async Task IceRestart_RotatesCredentials_AndPreservesSrtpSession()
    {
        InMemoryIceNetwork network = new();
        var opus = new SdpCodec(111, "opus", 48000, 2, null, []);
        var offererOptions = new PeerConnectionOptions
        {
            SocketFactory = network.Factory(IPAddress.Parse("10.0.0.1")),
            Media = [new MediaLine("0", SdpMediaKind.Audio, LocalSsrc: 0x3333_3333, [opus])],
        };
        var answererOptions = new PeerConnectionOptions
        {
            SocketFactory = network.Factory(IPAddress.Parse("10.0.0.2")),
            Media = [new MediaLine("0", SdpMediaKind.Audio, LocalSsrc: 0x4444_4444, [opus])],
        };

        await using var offerer = new PeerConnection(offererOptions, Certificate);
        await using var answerer = new PeerConnection(answererOptions, Certificate);

        offerer.LocalIceCandidate += c => answerer.AddRemoteIceCandidate(c);
        answerer.LocalIceCandidate += c => offerer.AddRemoteIceCandidate(c);

        var firstConnected = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        offerer.StateChanged += s =>
        {
            if (s == PeerConnectionState.Connected)
            {
                firstConnected.TrySetResult();
            }
        };
        var answererConnected = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        answerer.StateChanged += s =>
        {
            if (s == PeerConnectionState.Connected)
            {
                answererConnected.TrySetResult();
            }
        };

        Arrivals received = new();
        answerer.RtpReceived += (header, _) => received.Record(header.Timestamp);

        SdpDescription offer = offerer.CreateOffer();
        answerer.SetRemoteDescription(offer, SdpType.Offer);
        offerer.SetRemoteDescription(answerer.CreateAnswer(), SdpType.Answer);
        await Task.WhenAll(firstConnected.Task, answererConnected.Task)
            .WaitAsync(TimeSpan.FromSeconds(60));

        string ufragBefore = offer.Media[0].IceUfrag;
        byte[] payload = [0xDE, 0xAD, 0xBE, 0xEF];
        Assert.IsTrue(
            offerer.TrySendRtp(111, 0x3333_3333, rtpTimestamp: 1000, marker: true, payload)
        );
        await received.Of(1000).WaitAsync(TimeSpan.FromSeconds(5));

        // Full ICE restart: fresh credentials + re-gather, re-offered; the answerer restarts on the new ufrag.
        SdpDescription restartOffer = offerer.RestartIce();
        Assert.AreNotEqual(
            ufragBefore,
            restartOffer.Media[0].IceUfrag,
            "the restart must rotate the ICE ufrag."
        );
        answerer.SetRemoteDescription(restartOffer, SdpType.Offer);
        offerer.SetRemoteDescription(answerer.CreateAnswer(), SdpType.Answer);

        // A packet sent after the restart must still decrypt - the DTLS-SRTP keys + ROC survived the rollover.
        await Cadence.SendUntilAsync(
            () => offerer.TrySendRtp(111, 0x3333_3333, rtpTimestamp: 2000, marker: true, payload),
            received.Of(2000),
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromSeconds(20)
        );
        Assert.IsTrue(
            received.Of(2000).IsCompleted,
            "a packet after the ICE restart must still decrypt (SRTP preserved)."
        );
        Assert.AreEqual(PeerConnectionState.Connected, offerer.State);
    }

    [TestMethod]
    [Timeout(90_000)]
    public async Task Congestion_FeedbackLoop_IsLive_AndProducesEstimates()
    {
        InMemoryIceNetwork network = new();
        // Proves the congestion loop is actually wired in a live connection (CCFB timer + estimate event +
        // controller integration run end to end). It does not assert backoff behaviour - that needs a lossy
        // link; SCReAM's algorithm itself is unit-tested separately.
        var videoCodec = new SdpCodec(96, "H265", 90000, null, null, ["nack", "nack pli"]);
        var senderOptions = new PeerConnectionOptions
        {
            SocketFactory = network.Factory(IPAddress.Parse("10.0.0.1")),
            Media = [new MediaLine("0", SdpMediaKind.Video, LocalSsrc: 0xCCCC_0001, [videoCodec])],
        };
        var receiverOptions = new PeerConnectionOptions
        {
            SocketFactory = network.Factory(IPAddress.Parse("10.0.0.2")),
            Media = [new MediaLine("0", SdpMediaKind.Video, LocalSsrc: 0xDDDD_0001, [videoCodec])],
        };

        await using var sender = new PeerConnection(
            senderOptions,
            Certificate,
            controller: new ScreamCongestionController()
        );
        await using var receiver = new PeerConnection(receiverOptions, Certificate);

        sender.LocalIceCandidate += c => receiver.AddRemoteIceCandidate(c);
        receiver.LocalIceCandidate += c => sender.AddRemoteIceCandidate(c);

        var bothConnected = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        int connected = 0;
        void OnState(PeerConnectionState s)
        {
            if (s == PeerConnectionState.Connected && Interlocked.Increment(ref connected) == 2)
            {
                bothConnected.TrySetResult();
            }
        }

        sender.StateChanged += OnState;
        receiver.StateChanged += OnState;

        var estimate = new TaskCompletionSource<BitrateEstimate>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        sender.BitrateEstimateChanged += e => estimate.TrySetResult(e);

        SdpDescription offer = sender.CreateOffer();
        receiver.SetRemoteDescription(offer, SdpType.Offer);
        SdpDescription answer = receiver.CreateAnswer();
        sender.SetRemoteDescription(answer, SdpType.Answer);

        await bothConnected.Task.WaitAsync(TimeSpan.FromSeconds(60));

        // Send media so the receiver has arrivals to report back via CCFB.
        byte[] payload = new byte[800];
        uint rtp = 0;
        await Cadence.SendUntilAsync(
            () => sender.TrySendRtp(96, 0xCCCC_0001, rtp += 3000, marker: true, payload),
            estimate.Task,
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromSeconds(30)
        );
        BitrateEstimate got = await estimate.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.IsTrue(
            got.TargetBitrateBps > 0,
            "the controller should produce a positive target bitrate."
        );
        Assert.IsTrue(
            got.PacingRateBps >= got.TargetBitrateBps,
            "pacing rate should not be below the target."
        );
    }

    [TestMethod]
    [DataRow("42e01f", "640c1f", false)]
    [DataRow("42e01f", "42e034", true)]
    [DataRow("42e01f", "42c01f", true)]
    [DataRow("42e01f", "42001f", false)]
    [DataRow("640c1f", "640c34", true)]
    [DataRow("4d001f", "640c1f", false)]
    public void H264_SameProfileAtAnyLevel_IsCompatible(string a, string b, bool compatible)
    {
        Assert.AreEqual(
            compatible,
            H264PayloadFormat.Instance.AreCompatible(
                $"packetization-mode=1;profile-level-id={a}",
                $"level-asymmetry-allowed=1;packetization-mode=1;profile-level-id={b}"
            )
        );
    }

    [TestMethod]
    public void H264_PacketizationModesDiffer_IsNotCompatible() =>
        Assert.IsFalse(
            H264PayloadFormat.Instance.AreCompatible(
                "packetization-mode=1;profile-level-id=42e01f",
                "profile-level-id=42e01f"
            )
        );

    [TestMethod]
    [Timeout(30_000)]
    public async Task OfferAnswer_BrowserH264Profiles_AnswerTheMatchingProfile()
    {
        // A browser offers several H.264 profiles; the answer takes the first that one of ours matches.
        SdpCodec[] browser =
        [
            new(102, "H264", 90000, null, "packetization-mode=1;profile-level-id=4d001f", []),
            new(104, "H264", 90000, null, "packetization-mode=1;profile-level-id=42e01f", []),
            new(106, "H264", 90000, null, "packetization-mode=1;profile-level-id=640c1f", []),
        ];
        await using var offerer = new PeerConnection(
            new PeerConnectionOptions
            {
                Media = [new MediaLine("0", SdpMediaKind.Video, LocalSsrc: 0xAAAA_0003, browser)],
            },
            Certificate
        );
        SdpCodec[] ours =
        [
            .. H264PayloadFormat.Instance.FormatParameterSets.Select(
                (p, i) => H264PayloadFormat.Instance.ToSdpCodec(96 + i, p)
            ),
        ];
        await using var answerer = new PeerConnection(
            new PeerConnectionOptions
            {
                Media = [new MediaLine("0", SdpMediaKind.Video, LocalSsrc: 0xBBBB_0003, ours)],
            },
            Certificate
        );

        answerer.SetRemoteDescription(offerer.CreateOffer(), SdpType.Offer);
        SdpCodec answered = answerer.CreateAnswer().Media[0].Codecs.Single();

        Assert.AreEqual(
            104,
            answered.PayloadType,
            "Main matches none of ours; Constrained Baseline does"
        );
        StringAssert.Contains(answered.FormatParameters, "profile-level-id=42e034");
    }

    [TestMethod]
    public async Task OfferAnswer_EachSideDescribesTheCodecsItSends()
    {
        var offered = new SdpCodec(
            102,
            "H264",
            90000,
            null,
            "packetization-mode=1;profile-level-id=42e01f",
            []
        );
        // The same profile at another level: each side receives up to its own.
        var supported = new SdpCodec(
            120,
            "H264",
            90000,
            null,
            "level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42e034",
            ["nack"]
        );
        await using var offerer = new PeerConnection(
            new PeerConnectionOptions
            {
                Media = [new MediaLine("0", SdpMediaKind.Video, LocalSsrc: 0xAAAA_0002, [offered])],
            },
            Certificate
        );
        await using var answerer = new PeerConnection(
            new PeerConnectionOptions
            {
                Media =
                [
                    new MediaLine("0", SdpMediaKind.Video, LocalSsrc: 0xBBBB_0002, [supported]),
                ],
            },
            Certificate
        );

        answerer.SetRemoteDescription(offerer.CreateOffer(), SdpType.Offer);
        SdpDescription answer = answerer.CreateAnswer();
        offerer.SetRemoteDescription(answer, SdpType.Answer);

        SdpCodec answered = answer.Media[0].Codecs.Single();
        Assert.AreEqual(102, answered.PayloadType, "the answer keeps the offerer's payload type");
        Assert.AreEqual(
            supported.FormatParameters,
            answered.FormatParameters,
            "and its own parameters"
        );
        NegotiatedMediaInfo atAnswerer = answerer.NegotiatedMedia.Single();
        Assert.AreEqual(supported.FormatParameters, atAnswerer.Codecs[0].FormatParameters);
        Assert.AreEqual(offered.FormatParameters, atAnswerer.RemoteCodecs[0].FormatParameters);
        NegotiatedMediaInfo atOfferer = offerer.NegotiatedMedia.Single();
        Assert.AreEqual(offered.FormatParameters, atOfferer.Codecs[0].FormatParameters);
        Assert.AreEqual(supported.FormatParameters, atOfferer.RemoteCodecs[0].FormatParameters);
    }

    [TestMethod]
    [Timeout(90_000)]
    public async Task OfferAnswer_NarrowsToMutuallySupportedCodec()
    {
        // Offerer advertises two video codecs; the answerer supports only H265.
        var h265 = new SdpCodec(96, "H265", 90000, null, null, ["nack", "nack pli"]);
        var av1 = new SdpCodec(98, "AV1", 90000, null, null, ["nack", "nack pli"]);
        var offererOptions = new PeerConnectionOptions
        {
            Media = [new MediaLine("0", SdpMediaKind.Video, LocalSsrc: 0xAAAA_0001, [h265, av1])],
        };
        var answererOptions = new PeerConnectionOptions
        {
            Media = [new MediaLine("0", SdpMediaKind.Video, LocalSsrc: 0xBBBB_0001, [h265])],
        };

        await using var offerer = new PeerConnection(offererOptions, Certificate);
        await using var answerer = new PeerConnection(answererOptions, Certificate);

        SdpDescription offer = offerer.CreateOffer();
        Assert.AreEqual(2, offer.Media[0].Codecs.Count, "the offer advertises both codecs.");

        answerer.SetRemoteDescription(offer, SdpType.Offer);
        SdpDescription answer = answerer.CreateAnswer();

        // The answer keeps only the codec the answerer supports, in the offerer's payload-type space.
        Assert.AreEqual(1, answer.Media[0].Codecs.Count);
        Assert.AreEqual("H265", answer.Media[0].Codecs[0].EncodingName);
        Assert.AreEqual(96, answer.Media[0].Codecs[0].PayloadType);

        Assert.AreEqual("H265", answerer.NegotiatedMedia.Single().Codecs[0].EncodingName);

        offerer.SetRemoteDescription(answer, SdpType.Answer);
        NegotiatedMediaInfo offererVideo = offerer.NegotiatedMedia.Single();
        Assert.AreEqual("H265", offererVideo.Codecs[0].EncodingName);
        Assert.AreEqual(
            0xAAAA_0001u,
            offererVideo.LocalSsrc,
            "the offerer sends with its own SSRC."
        );
        await Task.CompletedTask;
    }

    [TestMethod]
    [Timeout(90_000)]
    public async Task Receiver_RequestKeyframe_ReachesSenderAsRtcpPli()
    {
        InMemoryIceNetwork network = new();
        var videoCodec = new SdpCodec(
            96,
            "H264",
            90000,
            null,
            "packetization-mode=1",
            ["nack", "nack pli"]
        );
        var senderOptions = new PeerConnectionOptions
        {
            SocketFactory = network.Factory(IPAddress.Parse("10.0.0.1")),
            Media = [new MediaLine("0", SdpMediaKind.Video, LocalSsrc: 0xAAAA_0001, [videoCodec])],
        };
        var receiverOptions = new PeerConnectionOptions
        {
            SocketFactory = network.Factory(IPAddress.Parse("10.0.0.2")),
            Media = [new MediaLine("0", SdpMediaKind.Video, LocalSsrc: 0xBBBB_0001, [videoCodec])],
        };

        await using var sender = new PeerConnection(senderOptions, Certificate);
        await using var receiver = new PeerConnection(receiverOptions, Certificate);

        sender.LocalIceCandidate += c => receiver.AddRemoteIceCandidate(c);
        receiver.LocalIceCandidate += c => sender.AddRemoteIceCandidate(c);

        var bothConnected = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        int connectedCount = 0;
        void OnState(PeerConnectionState s)
        {
            if (
                s == PeerConnectionState.Connected
                && Interlocked.Increment(ref connectedCount) == 2
            )
            {
                bothConnected.TrySetResult();
            }
        }

        sender.StateChanged += OnState;
        receiver.StateChanged += OnState;

        var keyframeRequested = new TaskCompletionSource<uint>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        sender.KeyframeRequested += ssrc => keyframeRequested.TrySetResult(ssrc);

        SdpDescription offer = sender.CreateOffer();
        receiver.SetRemoteDescription(offer, SdpType.Offer);
        SdpDescription answer = receiver.CreateAnswer();
        sender.SetRemoteDescription(answer, SdpType.Answer);

        await bothConnected.Task.WaitAsync(TimeSpan.FromSeconds(60));

        // The receiver asks the sender (by the sender's media SSRC) for a keyframe; it arrives as SRTCP PLI.
        await receiver.RequestKeyframeAsync(0xAAAA_0001);

        uint requestedSsrc = await keyframeRequested.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.AreEqual(0xAAAA_0001u, requestedSsrc);
    }
}
