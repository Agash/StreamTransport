using Agash.StreamTransport.Codecs;
using Agash.StreamTransport.WebRtc;
using Agash.StreamTransport.WebRtc.CongestionControl;
using Agash.StreamTransport.WebRtc.Dtls;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agash.StreamTransport.Tests;

/// <summary>
/// Shared transport services for tests that construct a <see cref="WebRtcMediaSender"/> /
/// <see cref="WebRtcMediaReceiver"/> / <see cref="MediaPublisher"/> / <see cref="MediaSubscriber"/> directly,
/// mirroring what <c>AddStreamTransport</c> wires at runtime (default HEVC+Opus codec registry, BouncyCastle
/// DTLS, no-op logging, and the WebRTC transport over them).
/// </summary>
internal static class TestMedia
{
    public static readonly IMediaCodecRegistry Codecs =
        new MediaCodecRegistry([new H265VideoCodecDescriptor()], [new OpusAudioCodecDescriptor()]);
    public static readonly IDtlsTransportFactory Dtls = new DtlsTransportFactory();
    public static readonly ILoggerFactory Loggers = NullLoggerFactory.Instance;

    /// <summary>The default WebRTC wire transport, as <c>AddWebRtcMediaTransport</c> registers it.</summary>
    public static readonly IMediaTransport Transport = new WebRtcMediaTransport(
        Codecs, Dtls, Loggers, static () => new ScreamCongestionController(), new MobilityEngine(new NetworkChangeMonitor()));

    /// <summary>A transport whose senders/receivers (and their peer connections) log into <paramref name="loggers"/>.</summary>
    public static IMediaTransport CreateCapturing(out CapturingLoggerFactory loggers)
    {
        loggers = new CapturingLoggerFactory();
        return new WebRtcMediaTransport(
            Codecs, Dtls, loggers, static () => new ScreamCongestionController(), new MobilityEngine(new NetworkChangeMonitor()));
    }
}
