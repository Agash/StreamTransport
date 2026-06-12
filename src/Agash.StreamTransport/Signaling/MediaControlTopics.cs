namespace Agash.StreamTransport;

/// <summary>
/// Well-known <see cref="PeerControlMessage.Topic"/> values the media layer uses over the generic peer
/// control channel. These are an internal contract between <see cref="MediaPublisher"/> and
/// <see cref="MediaSubscriber"/>; a host using the channel for its own messages should namespace its topics
/// separately to avoid collisions.
/// </summary>
internal static class MediaControlTopics
{
    /// <summary>Publisher → subscriber, sent before the SDP offer. Payload is "1" (alpha) or "0" (opaque).</summary>
    public const string Alpha = "stream.alpha";
}
