using System.Collections.Immutable;
using Agash.StreamTransport.Media;

namespace Agash.StreamTransport.Codecs.Opus;

/// <summary>Makes <see cref="OpusAudioEncoder"/>s.</summary>
/// <param name="options">Opus settings for every encoder it makes; defaults when null.</param>
public sealed class OpusAudioEncoderFactory(OpusOptions? options = null) : IAudioEncoderFactory
{
    /// <summary>The PCM Opus encodes as it is.</summary>
    public static AudioConstraints Constraints { get; } =
        new([48_000, 24_000, 16_000, 12_000, 8_000], [SampleFormat.F32, SampleFormat.S16], 2);

    // After Constraints, which it reads during type initialisation.
    private static readonly AudioEncoderInfo Info = new("Concentus Opus", Constraints);

    /// <inheritdoc/>
    public int Rank => 100;

    /// <inheritdoc/>
    public ImmutableArray<AudioCodecFormat> SupportedFormats { get; } =
    [new AudioCodecFormat(AudioCodecId.Opus)];

    /// <inheritdoc/>
    public AudioEncoderInfo? QueryCapabilities(AudioCodecFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        return format.Codec == AudioCodecId.Opus ? Info : null;
    }

    /// <inheritdoc/>
    public IAudioEncoder Create(AudioEncoderConfiguration configuration) =>
        new OpusAudioEncoder(configuration, options);
}

/// <summary>Makes <see cref="OpusAudioDecoder"/>s.</summary>
public sealed class OpusAudioDecoderFactory : IAudioDecoderFactory
{
    private static readonly AudioDecoderInfo Info = new(
        "Concentus Opus",
        new AudioFormat(SampleFormat.F32, 48_000, 2)
    );

    /// <inheritdoc/>
    public int Rank => 100;

    /// <inheritdoc/>
    public ImmutableArray<AudioCodecFormat> SupportedFormats { get; } =
    [new AudioCodecFormat(AudioCodecId.Opus)];

    /// <inheritdoc/>
    public AudioDecoderInfo? QueryCapabilities(AudioCodecFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        return format.Codec == AudioCodecId.Opus ? Info : null;
    }

    /// <inheritdoc/>
    public IAudioDecoder Create(AudioCodecFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        return format.Codec == AudioCodecId.Opus
            ? new OpusAudioDecoder()
            : throw new ArgumentException(
                $"This factory decodes Opus; the format is {format.Codec}.",
                nameof(format)
            );
    }
}
