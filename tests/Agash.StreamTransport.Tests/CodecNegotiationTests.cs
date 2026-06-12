using Agash.StreamTransport.Codecs;
using Agash.StreamTransport.Transport;
using Agash.StreamTransport.WebRtc;
using Agash.StreamTransport.WebRtc.Sdp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Agash.StreamTransport.Tests;

/// <summary>
/// The registry-driven SDP build: every registered codec is offered with a distinct dynamic payload type, in
/// the profile's preference order, reserving the RTX payload type. This is what lets a consumer add a codec
/// (here a fake "AV1") and have it negotiated without touching the negotiation code.
/// </summary>
[TestClass]
public sealed class CodecNegotiationTests
{
    [TestMethod]
    public void Build_OffersEveryRegisteredCodec_WithDistinctPayloadTypes_SkippingRtx()
    {
        // Registry: a fake AV1 (more preferred by descriptor order) + the built-in HEVC, plus Opus.
        var registry = new MediaCodecRegistry(
            [new FakeVideoCodec("AV1", preference: 5), new H265VideoCodecDescriptor()],
            [new OpusAudioCodecDescriptor()]);

        // Default options prefer H265 first (VideoCodecs = [H265, H264]).
        PeerConnectionOptions pc = MediaConfig.Build(registry, new MediaTransportOptions(), audio: true, video: true);

        IReadOnlyList<SdpCodec> video = pc.Media.Single(m => m.Kind == SdpMediaKind.Video).Codecs;
        IReadOnlyList<SdpCodec> audio = pc.Media.Single(m => m.Kind == SdpMediaKind.Audio).Codecs;

        // Both video codecs offered, H265 first (profile preference beats descriptor order), AV1 second.
        Assert.AreEqual(2, video.Count);
        Assert.AreEqual("H265", video[0].EncodingName);
        Assert.AreEqual("AV1", video[1].EncodingName);

        // Distinct dynamic PTs, and the RTX payload type (97) is reserved, not assigned to a codec.
        Assert.AreEqual(96, video[0].PayloadType);
        Assert.AreEqual(98, video[1].PayloadType);
        Assert.IsFalse(video.Any(c => c.PayloadType == MediaConfig.VideoRtxPayloadType));

        Assert.AreEqual("opus", audio.Single().EncodingName);
        Assert.AreEqual(111, audio.Single().PayloadType);
    }

    [TestMethod]
    public void Build_WithNoProfilePreference_UsesDescriptorPreferenceOrder()
    {
        var registry = new MediaCodecRegistry(
            [new FakeVideoCodec("AV1", preference: 5), new H265VideoCodecDescriptor()],
            [new OpusAudioCodecDescriptor()]);

        // No VideoCodecs preference -> registry (descriptor-preference) order: AV1 (5) before H265 (10).
        PeerConnectionOptions pc = MediaConfig.Build(
            registry, new MediaTransportOptions { VideoCodecs = [] }, audio: false, video: true);

        IReadOnlyList<SdpCodec> video = pc.Media.Single(m => m.Kind == SdpMediaKind.Video).Codecs;
        Assert.AreEqual("AV1", video[0].EncodingName);
        Assert.AreEqual("H265", video[1].EncodingName);
    }

    // A stand-in codec descriptor: only its SDP-facing surface is exercised by MediaConfig.Build; the
    // encoder/decoder/payload-format factories are never called here.
    private sealed class FakeVideoCodec(string name, int preference) : IVideoCodecDescriptor
    {
        public string RtpName => name;
        public int ClockRate => 90_000;
        public string? FormatParameters => null;
        public IReadOnlyList<string> RtcpFeedback => ["nack", "nack pli"];
        public int Preference => preference;
        public IVideoEncoder CreateEncoder(VideoEncoderSettings settings) => throw new NotSupportedException();
        public IVideoDecoder CreateDecoder(VideoDecoderSettings settings) => throw new NotSupportedException();
        public IRtpPacketizer CreatePacketizer() => throw new NotSupportedException();
        public IRtpDepacketizer CreateDepacketizer() => throw new NotSupportedException();
    }
}
