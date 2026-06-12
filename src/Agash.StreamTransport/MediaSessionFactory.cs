using Microsoft.Extensions.Logging;

namespace Agash.StreamTransport;

/// <summary>
/// Composes <see cref="MediaPublisher"/> / <see cref="MediaSubscriber"/> from the DI-resolved
/// <see cref="IMediaTransport"/> and <see cref="ILoggerFactory"/>, so a host supplies only the per-run
/// options, the <see cref="IMediaRoom"/>, and the capture sources/sinks. Resolve this from the container
/// rather than wiring the transport in by hand.
/// </summary>
public sealed class MediaSessionFactory(IMediaTransport transport, ILoggerFactory loggerFactory)
{
    /// <summary>Create a publisher over a room joined as <see cref="PeerRole.Publisher"/>.</summary>
    public MediaPublisher CreatePublisher(
        MediaTransportOptions options,
        IMediaRoom room,
        IVideoFrameSource? video = null,
        IAudioFrameSource? audio = null,
        nint gpuDeviceHandle = 0) =>
        new(options, transport, loggerFactory, room, video, audio, gpuDeviceHandle);

    /// <summary>Create a subscriber over a room joined as <see cref="PeerRole.Subscriber"/>.</summary>
    public MediaSubscriber CreateSubscriber(
        MediaTransportOptions options,
        IMediaRoom room,
        IVideoFrameSink? video = null,
        IAudioFrameSink? audio = null) =>
        new(options, transport, loggerFactory, room, video, audio);
}
