using System.Collections.Immutable;

namespace Agash.StreamTransport.Media;

/// <summary>
/// An audio codec with its format parameters, as SDP describes it with <c>a=fmtp</c>, by their SDP
/// names.
/// </summary>
/// <param name="Codec">The codec.</param>
/// <param name="Parameters">The format parameters.</param>
public sealed record AudioCodecFormat(
    AudioCodecId Codec,
    ImmutableSortedDictionary<string, string> Parameters
)
{
    /// <summary>A codec with no parameters.</summary>
    /// <param name="codec">The codec.</param>
    public AudioCodecFormat(AudioCodecId codec)
        : this(codec, ImmutableSortedDictionary<string, string>.Empty) { }

    /// <inheritdoc/>
    public bool Equals(AudioCodecFormat? other) =>
        other is not null
        && Codec == other.Codec
        && Parameters.Count == other.Parameters.Count
        && !Parameters.Except(other.Parameters).Any();

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Codec, Parameters.Count);
}

/// <summary>What the audio is, so an encoder can pick its modes.</summary>
public enum AudioContent
{
    /// <summary>Music or mixed programme audio: full band.</summary>
    Music,

    /// <summary>Speech: voice-band modes where the codec has them.</summary>
    Speech,
}

/// <summary>How an audio encoder is set up.</summary>
/// <param name="Format">The codec and its parameters.</param>
/// <param name="Input">The PCM the encoder is fed.</param>
/// <param name="BitsPerSecond">The initial target bit rate.</param>
/// <param name="FrameDuration">How much audio each packet carries; zero for the codec's default.</param>
/// <param name="Content">What the audio is.</param>
/// <param name="ExpectedLossPercent">
/// The packet loss to plan redundancy for, in percent; zero for none. Codecs without in-band redundancy
/// ignore it.
/// </param>
public sealed record AudioEncoderConfiguration(
    AudioCodecFormat Format,
    AudioFormat Input,
    int BitsPerSecond,
    TimeSpan FrameDuration = default,
    AudioContent Content = AudioContent.Music,
    int ExpectedLossPercent = 0
);

/// <summary>The PCM a codec takes as it is.</summary>
/// <param name="SampleRates">Sample rates, most preferred first.</param>
/// <param name="SampleFormats">Sample formats, most preferred first.</param>
/// <param name="MaximumChannels">The most channels.</param>
public sealed record AudioConstraints(
    ImmutableArray<int> SampleRates,
    ImmutableArray<SampleFormat> SampleFormats,
    int MaximumChannels
)
{
    /// <summary>Whether PCM in a format is taken as it is.</summary>
    /// <param name="format">The PCM format.</param>
    /// <returns>True when no conversion is needed.</returns>
    public bool Accepts(AudioFormat format) =>
        SampleRates.Contains(format.SampleRate)
        && SampleFormats.Contains(format.SampleFormat)
        && format.Channels >= 1
        && format.Channels <= MaximumChannels;
}

/// <summary>What an audio encoder takes.</summary>
/// <param name="ImplementationName">Which implementation, for logs.</param>
/// <param name="Input">The PCM it takes as it is.</param>
public sealed record AudioEncoderInfo(string ImplementationName, AudioConstraints Input);

/// <summary>What an audio decoder produces.</summary>
/// <param name="ImplementationName">Which implementation, for logs.</param>
/// <param name="Output">The PCM it produces.</param>
public sealed record AudioDecoderInfo(string ImplementationName, AudioFormat Output);

/// <summary>Makes audio encoders of one family.</summary>
public interface IAudioEncoderFactory
{
    /// <summary>How strongly this family is preferred when several can do the job; higher wins.</summary>
    int Rank { get; }

    /// <summary>The codec formats it can encode, for SDP.</summary>
    ImmutableArray<AudioCodecFormat> SupportedFormats { get; }

    /// <summary>What an encoder for a format would take; null when it cannot encode it.</summary>
    /// <param name="format">The codec format.</param>
    /// <returns>The encoder's information, or null.</returns>
    AudioEncoderInfo? QueryCapabilities(AudioCodecFormat format);

    /// <summary>Makes an encoder.</summary>
    /// <param name="configuration">How to set it up.</param>
    /// <returns>The encoder.</returns>
    IAudioEncoder Create(AudioEncoderConfiguration configuration);
}

/// <summary>Makes audio decoders of one family.</summary>
public interface IAudioDecoderFactory
{
    /// <summary>How strongly this family is preferred; higher wins.</summary>
    int Rank { get; }

    /// <summary>The codec formats it can decode, for SDP.</summary>
    ImmutableArray<AudioCodecFormat> SupportedFormats { get; }

    /// <summary>What a decoder for a format would produce; null when it cannot decode it.</summary>
    /// <param name="format">The codec format.</param>
    /// <returns>The decoder's information, or null.</returns>
    AudioDecoderInfo? QueryCapabilities(AudioCodecFormat format);

    /// <summary>Makes a decoder.</summary>
    /// <param name="format">The codec format.</param>
    /// <returns>The decoder.</returns>
    IAudioDecoder Create(AudioCodecFormat format);
}

/// <summary>
/// A decoder that rebuilds a lost packet from redundancy in the packet after it, such as Opus in-band
/// forward error correction. Receivers use it in place of concealment when the next packet is at hand.
/// </summary>
public interface IAudioLossRecovery
{
    /// <summary>
    /// Delivers the audio of a lost packet rebuilt from the packet after it, or concealed when that
    /// packet carries no redundancy. Decode <paramref name="next"/> itself afterwards as usual.
    /// </summary>
    /// <param name="next">The packet that arrived after the lost one.</param>
    /// <param name="lostDuration">How long the lost packet was.</param>
    /// <param name="lostTimestamp">When the lost packet started.</param>
    /// <param name="consumer">Where the audio goes.</param>
    void Recover(
        in EncodedAudioFrame next,
        TimeSpan lostDuration,
        MediaTimestamp lostTimestamp,
        IAudioFrameConsumer consumer
    );
}

/// <summary>A consumer of audio that states the PCM it accepts.</summary>
public interface IAudioSink : IAudioFrameConsumer
{
    /// <summary>The PCM it takes as it is.</summary>
    AudioConstraints Constraints { get; }
}
