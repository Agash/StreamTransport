namespace Agash.StreamTransport;

/// <summary>
/// The default <see cref="IMediaCodecRegistry"/>: collects every DI-registered codec descriptor and orders
/// each kind by its <c>Preference</c>. Adding a codec is registering a descriptor - the negotiation reads
/// the registry, it is not edited per codec.
/// </summary>
internal sealed class MediaCodecRegistry : IMediaCodecRegistry
{
    public MediaCodecRegistry(IEnumerable<IVideoCodecDescriptor> videoCodecs, IEnumerable<IAudioCodecDescriptor> audioCodecs)
    {
        ArgumentNullException.ThrowIfNull(videoCodecs);
        ArgumentNullException.ThrowIfNull(audioCodecs);
        VideoCodecs = [.. videoCodecs.OrderBy(c => c.Preference)];
        AudioCodecs = [.. audioCodecs.OrderBy(c => c.Preference)];
    }

    public IReadOnlyList<IVideoCodecDescriptor> VideoCodecs { get; }
    public IReadOnlyList<IAudioCodecDescriptor> AudioCodecs { get; }

    public IVideoCodecDescriptor? FindVideo(string rtpName) =>
        VideoCodecs.FirstOrDefault(c => string.Equals(c.RtpName, rtpName, StringComparison.OrdinalIgnoreCase));

    public IAudioCodecDescriptor? FindAudio(string rtpName) =>
        AudioCodecs.FirstOrDefault(c => string.Equals(c.RtpName, rtpName, StringComparison.OrdinalIgnoreCase));
}
