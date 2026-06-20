using System.Collections.Concurrent;
using Agash.StreamTransport.WebRtc;
using Agash.StreamTransport.WebRtc.CongestionControl;
using Agash.StreamTransport.WebRtc.Dtls;
using Agash.StreamTransport.WebRtc.Rtp;
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
    [TestMethod]
    [Timeout(90_000)]
    [TestCategory("Integration")]  // live loopback ICE/DTLS connect; off the gate, races on the GH macOS runner (#1)
    public async Task OfferAnswer_ConnectsAndDeliversEncryptedRtp()
    {
        var opusCodec = new SdpCodec(111, "opus", 48000, 2, null, []);
        var offererOptions = new PeerConnectionOptions
        {
            IncludeLoopback = true,
            Media = [new MediaLine("0", SdpMediaKind.Audio, LocalSsrc: 0x1111_1111, [opusCodec])],
        };
        var answererOptions = new PeerConnectionOptions
        {
            IncludeLoopback = true,
            Media = [new MediaLine("0", SdpMediaKind.Audio, LocalSsrc: 0x2222_2222, [opusCodec])],
        };

        await using var offerer = new PeerConnection(offererOptions, new DtlsTransportFactory());
        await using var answerer = new PeerConnection(answererOptions, new DtlsTransportFactory());

        // Trickle ICE both ways.
        offerer.LocalIceCandidate += c => answerer.AddRemoteIceCandidate(c);
        answerer.LocalIceCandidate += c => offerer.AddRemoteIceCandidate(c);

        var offererConnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answererConnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        offerer.StateChanged += s => { if (s == PeerConnectionState.Connected) { offererConnected.TrySetResult(); } };
        answerer.StateChanged += s => { if (s == PeerConnectionState.Connected) { answererConnected.TrySetResult(); } };

        var received = new TaskCompletionSource<(RtpHeader Header, byte[] Payload)>(TaskCreationOptions.RunContinuationsAsynchronously);
        answerer.RtpReceived += (header, payload) => received.TrySetResult((header, payload.ToArray()));

        // Offer / answer exchange.
        SdpDescription offer = offerer.CreateOffer();
        answerer.SetRemoteDescription(offer, SdpType.Offer);
        SdpDescription answer = answerer.CreateAnswer();
        offerer.SetRemoteDescription(answer, SdpType.Answer);

        await Task.WhenAll(offererConnected.Task, answererConnected.Task).WaitAsync(TimeSpan.FromSeconds(60));
        Assert.AreEqual(PeerConnectionState.Connected, offerer.State);
        Assert.AreEqual(PeerConnectionState.Connected, answerer.State);

        // Send an encrypted RTP packet offerer -> answerer with abs-capture-time.
        const ulong captureNtp = 0xE5_00_00_00_80_00_00_00;
        byte[] payload = [0xCA, 0xFE, 0xBA, 0xBE, 0x10, 0x20];
        await offerer.SendRtp(payloadType: 111, ssrc: 0x1111_1111, rtpTimestamp: 160, marker: true, payload, captureNtp);

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
    [TestCategory("Integration")]  // live loopback ICE/DTLS connect; off the gate, races on the GH macOS runner (#1)
    public async Task Mobility_Recovery_ReconnectsAndPreservesSrtpSession()
    {
        // Connect, then force a mobility recovery on the sender. A packet sent AFTER recovery must still
        // decrypt at the receiver - which only works if the SRTP session (keys + rollover counter) was
        // preserved across the re-probe, the core mobility guarantee.
        var opus = new SdpCodec(111, "opus", 48000, 2, null, []);
        var offererOptions = new PeerConnectionOptions
        {
            IncludeLoopback = true,
            Media = [new MediaLine("0", SdpMediaKind.Audio, LocalSsrc: 0x1111_1111, [opus])],
        };
        var answererOptions = new PeerConnectionOptions
        {
            IncludeLoopback = true,
            Media = [new MediaLine("0", SdpMediaKind.Audio, LocalSsrc: 0x2222_2222, [opus])],
        };

        await using var offerer = new PeerConnection(offererOptions, new DtlsTransportFactory());
        await using var answerer = new PeerConnection(answererOptions, new DtlsTransportFactory());

        offerer.LocalIceCandidate += c => answerer.AddRemoteIceCandidate(c);
        answerer.LocalIceCandidate += c => offerer.AddRemoteIceCandidate(c);

        var firstConnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        offerer.StateChanged += s => { if (s == PeerConnectionState.Connected) { firstConnected.TrySetResult(); } };

        var answererConnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        answerer.StateChanged += s => { if (s == PeerConnectionState.Connected) { answererConnected.TrySetResult(); } };

        var received = new ConcurrentDictionary<uint, byte>();
        answerer.RtpReceived += (header, _) => received.TryAdd(header.Timestamp, 0);

        SdpDescription offer = offerer.CreateOffer();
        answerer.SetRemoteDescription(offer, SdpType.Offer);
        SdpDescription answer = answerer.CreateAnswer();
        offerer.SetRemoteDescription(answer, SdpType.Answer);

        await Task.WhenAll(firstConnected.Task, answererConnected.Task).WaitAsync(TimeSpan.FromSeconds(60));

        byte[] payload = [0xCA, 0xFE, 0xBA, 0xBE];
        await offerer.SendRtp(111, 0x1111_1111, rtpTimestamp: 1000, marker: true, payload);
        await PollAsync(() => received.ContainsKey(1000), TimeSpan.FromSeconds(5));
        Assert.IsTrue(received.ContainsKey(1000), "the first packet should arrive before recovery.");

        // Force the path recovery. The PeerConnection stays Connected (DTLS/SRTP never drop - the whole point);
        // only ICE re-probes underneath. Re-send the post-recovery packet until ICE re-nominates and it lands
        // (sends during the brief no-pair window are dropped). It decrypts only if keys + ROC survived.
        offerer.TriggerNetworkRecovery();
        for (int i = 0; i < 100 && !received.ContainsKey(2000); i++)
        {
            await offerer.SendRtp(111, 0x1111_1111, rtpTimestamp: 2000, marker: true, payload);
            await Task.Delay(100);
        }

        Assert.IsTrue(received.ContainsKey(2000), "a packet sent after recovery must still decrypt (SRTP preserved).");
    }

    [TestMethod]
    [Timeout(90_000)]
    [TestCategory("Integration")]  // live loopback ICE/DTLS connect; off the gate, races on the GH macOS runner (#1)
    public async Task IceRestart_RotatesCredentials_AndPreservesSrtpSession()
    {
        var opus = new SdpCodec(111, "opus", 48000, 2, null, []);
        var offererOptions = new PeerConnectionOptions
        {
            IncludeLoopback = true,
            Media = [new MediaLine("0", SdpMediaKind.Audio, LocalSsrc: 0x3333_3333, [opus])],
        };
        var answererOptions = new PeerConnectionOptions
        {
            IncludeLoopback = true,
            Media = [new MediaLine("0", SdpMediaKind.Audio, LocalSsrc: 0x4444_4444, [opus])],
        };

        await using var offerer = new PeerConnection(offererOptions, new DtlsTransportFactory());
        await using var answerer = new PeerConnection(answererOptions, new DtlsTransportFactory());

        offerer.LocalIceCandidate += c => answerer.AddRemoteIceCandidate(c);
        answerer.LocalIceCandidate += c => offerer.AddRemoteIceCandidate(c);

        var firstConnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        offerer.StateChanged += s => { if (s == PeerConnectionState.Connected) { firstConnected.TrySetResult(); } };
        var answererConnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        answerer.StateChanged += s => { if (s == PeerConnectionState.Connected) { answererConnected.TrySetResult(); } };

        var received = new ConcurrentDictionary<uint, byte>();
        answerer.RtpReceived += (header, _) => received.TryAdd(header.Timestamp, 0);

        SdpDescription offer = offerer.CreateOffer();
        answerer.SetRemoteDescription(offer, SdpType.Offer);
        offerer.SetRemoteDescription(answerer.CreateAnswer(), SdpType.Answer);
        await Task.WhenAll(firstConnected.Task, answererConnected.Task).WaitAsync(TimeSpan.FromSeconds(60));

        string ufragBefore = offer.Media[0].IceUfrag;
        byte[] payload = [0xDE, 0xAD, 0xBE, 0xEF];
        await offerer.SendRtp(111, 0x3333_3333, rtpTimestamp: 1000, marker: true, payload);
        await PollAsync(() => received.ContainsKey(1000), TimeSpan.FromSeconds(5));
        Assert.IsTrue(received.ContainsKey(1000), "first packet should arrive before the restart.");

        // Full ICE restart: fresh credentials + re-gather, re-offered; the answerer restarts on the new ufrag.
        SdpDescription restartOffer = offerer.RestartIce();
        Assert.AreNotEqual(ufragBefore, restartOffer.Media[0].IceUfrag, "the restart must rotate the ICE ufrag.");
        answerer.SetRemoteDescription(restartOffer, SdpType.Offer);
        offerer.SetRemoteDescription(answerer.CreateAnswer(), SdpType.Answer);

        // A packet sent after the restart must still decrypt - the DTLS-SRTP keys + ROC survived the rollover.
        for (int i = 0; i < 200 && !received.ContainsKey(2000); i++)
        {
            await offerer.SendRtp(111, 0x3333_3333, rtpTimestamp: 2000, marker: true, payload);
            await Task.Delay(100);
        }

        Assert.IsTrue(received.ContainsKey(2000), "a packet after the ICE restart must still decrypt (SRTP preserved).");
        Assert.AreEqual(PeerConnectionState.Connected, offerer.State);
    }

    private static async Task PollAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }
    }

    [TestMethod]
    [Timeout(90_000)]
    // Live two-PeerConnection loopback (real ICE/DTLS handshake): off the gate, as the loopback connect races on
    // a loaded CI host - notably the macOS runner (#1). Runs in the non-gating Integration leg.
    [TestCategory("Integration")]
    public async Task Congestion_FeedbackLoop_IsLive_AndProducesEstimates()
    {
        // Proves the congestion loop is actually wired in a live connection (CCFB timer + estimate event +
        // controller integration run end to end). It does not assert backoff behaviour - that needs a lossy
        // link; SCReAM's algorithm itself is unit-tested separately.
        var videoCodec = new SdpCodec(96, "H265", 90000, null, null, ["nack", "nack pli"]);
        var senderOptions = new PeerConnectionOptions
        {
            IncludeLoopback = true,
            Media = [new MediaLine("0", SdpMediaKind.Video, LocalSsrc: 0xCCCC_0001, [videoCodec])],
        };
        var receiverOptions = new PeerConnectionOptions
        {
            IncludeLoopback = true,
            Media = [new MediaLine("0", SdpMediaKind.Video, LocalSsrc: 0xDDDD_0001, [videoCodec])],
        };

        await using var sender = new PeerConnection(senderOptions, new DtlsTransportFactory(), controller: new ScreamCongestionController());
        await using var receiver = new PeerConnection(receiverOptions, new DtlsTransportFactory());

        sender.LocalIceCandidate += c => receiver.AddRemoteIceCandidate(c);
        receiver.LocalIceCandidate += c => sender.AddRemoteIceCandidate(c);

        var bothConnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
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

        var estimate = new TaskCompletionSource<BitrateEstimate>(TaskCreationOptions.RunContinuationsAsynchronously);
        sender.BitrateEstimateChanged += e => estimate.TrySetResult(e);

        SdpDescription offer = sender.CreateOffer();
        receiver.SetRemoteDescription(offer, SdpType.Offer);
        SdpDescription answer = receiver.CreateAnswer();
        sender.SetRemoteDescription(answer, SdpType.Answer);

        await bothConnected.Task.WaitAsync(TimeSpan.FromSeconds(60));

        // Send media so the receiver has arrivals to report back via CCFB.
        byte[] payload = new byte[800];
        for (int i = 0; i < 60; i++)
        {
            await sender.SendRtp(96, 0xCCCC_0001, (uint)(i * 3000), marker: true, payload);
            await Task.Delay(10);
        }

        BitrateEstimate got = await estimate.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.IsTrue(got.TargetBitrateBps > 0, "the controller should produce a positive target bitrate.");
        Assert.IsTrue(got.PacingRateBps >= got.TargetBitrateBps, "pacing rate should not be below the target.");
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

        await using var offerer = new PeerConnection(offererOptions, new DtlsTransportFactory());
        await using var answerer = new PeerConnection(answererOptions, new DtlsTransportFactory());

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
        Assert.AreEqual(0xAAAA_0001u, offererVideo.LocalSsrc, "the offerer sends with its own SSRC.");
        await Task.CompletedTask;
    }

    [TestMethod]
    [Timeout(90_000)]
    [TestCategory("Integration")]  // live loopback ICE/DTLS connect; off the gate, races on the GH macOS runner (#1)
    public async Task Receiver_RequestKeyframe_ReachesSenderAsRtcpPli()
    {
        var videoCodec = new SdpCodec(96, "H264", 90000, null, "packetization-mode=1", ["nack", "nack pli"]);
        var senderOptions = new PeerConnectionOptions
        {
            IncludeLoopback = true,
            Media = [new MediaLine("0", SdpMediaKind.Video, LocalSsrc: 0xAAAA_0001, [videoCodec])],
        };
        var receiverOptions = new PeerConnectionOptions
        {
            IncludeLoopback = true,
            Media = [new MediaLine("0", SdpMediaKind.Video, LocalSsrc: 0xBBBB_0001, [videoCodec])],
        };

        await using var sender = new PeerConnection(senderOptions, new DtlsTransportFactory());
        await using var receiver = new PeerConnection(receiverOptions, new DtlsTransportFactory());

        sender.LocalIceCandidate += c => receiver.AddRemoteIceCandidate(c);
        receiver.LocalIceCandidate += c => sender.AddRemoteIceCandidate(c);

        var bothConnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int connectedCount = 0;
        void OnState(PeerConnectionState s)
        {
            if (s == PeerConnectionState.Connected && Interlocked.Increment(ref connectedCount) == 2)
            {
                bothConnected.TrySetResult();
            }
        }

        sender.StateChanged += OnState;
        receiver.StateChanged += OnState;

        var keyframeRequested = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
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
