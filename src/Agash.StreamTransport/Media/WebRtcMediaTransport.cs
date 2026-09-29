using Agash.StreamTransport.WebRtc;
using Microsoft.Extensions.Logging;

namespace Agash.StreamTransport;

/// <summary>
/// The default <see cref="IMediaTransport"/>: WebRTC senders/receivers (ICE + DTLS-SRTP + RTP). It owns the
/// transport-specific services - the codec <see cref="IMediaCodecRegistry"/>, the DTLS
/// <see cref="RtcCertificate"/>, and logging - and threads them into each per-peer endpoint, so
/// callers (and the orchestration layer) never name the WebRTC types. Replace the <see cref="IMediaTransport"/>
/// registration to run the same orchestration over a different wire transport.
/// </summary>
internal sealed class WebRtcMediaTransport(
    IMediaCodecRegistry codecs,
    RtcCertificate certificate,
    ILoggerFactory loggerFactory,
    Func<INetworkController> controllerFactory,
    MobilityEngine mobility
) : IMediaTransport
{
    public IMediaSender CreateSender(
        MediaTransportOptions options,
        IVideoFrameSource? video = null,
        IAudioFrameSource? audio = null,
        nint gpuDeviceHandle = 0
    ) =>
        // Each sender gets its own congestion controller (per-connection state) so its encoder + pacer adapt
        // to that path's feedback, and registers with the mobility engine for proactive path recovery.
        new WebRtcMediaSender(
            options,
            codecs,
            certificate,
            loggerFactory,
            video,
            audio,
            gpuDeviceHandle,
            controllerFactory(),
            mobility
        );

    public IMediaReceiver CreateReceiver(
        MediaTransportOptions options,
        IVideoFrameSink? video = null,
        IAudioFrameSink? audio = null
    ) => new WebRtcMediaReceiver(options, codecs, certificate, loggerFactory, video, audio);
}
