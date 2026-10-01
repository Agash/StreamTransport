using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Agash.StreamTransport.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agash.StreamTransport;

/// <summary>
/// The encoder, decoder and processor factories a host registered, chosen by what they can do and then
/// by rank. A factory that fails to make what it said it could is logged and the next one is tried, so a
/// driver that refuses at runtime falls back to the next backend.
/// </summary>
public sealed partial class MediaCodecRegistry
{
    private readonly ImmutableArray<IVideoEncoderFactory> _videoEncoders;
    private readonly ImmutableArray<IVideoDecoderFactory> _videoDecoders;
    private readonly ImmutableArray<IVideoProcessorFactory> _videoProcessors;
    private readonly ImmutableArray<IAudioEncoderFactory> _audioEncoders;
    private readonly ImmutableArray<IAudioDecoderFactory> _audioDecoders;
    private readonly ILogger<MediaCodecRegistry> _logger;

    /// <summary>A registry of the given factories.</summary>
    /// <param name="videoEncoders">Video encoder factories.</param>
    /// <param name="videoDecoders">Video decoder factories.</param>
    /// <param name="videoProcessors">Video processor factories.</param>
    /// <param name="audioEncoders">Audio encoder factories.</param>
    /// <param name="audioDecoders">Audio decoder factories.</param>
    /// <param name="logger">The logger.</param>
    public MediaCodecRegistry(
        IEnumerable<IVideoEncoderFactory> videoEncoders,
        IEnumerable<IVideoDecoderFactory> videoDecoders,
        IEnumerable<IVideoProcessorFactory> videoProcessors,
        IEnumerable<IAudioEncoderFactory> audioEncoders,
        IEnumerable<IAudioDecoderFactory> audioDecoders,
        ILogger<MediaCodecRegistry>? logger = null
    )
    {
        _videoEncoders = [.. videoEncoders.OrderByDescending(static f => f.Rank)];
        _videoDecoders = [.. videoDecoders.OrderByDescending(static f => f.Rank)];
        _videoProcessors = [.. videoProcessors.OrderByDescending(static f => f.Rank)];
        _audioEncoders = [.. audioEncoders.OrderByDescending(static f => f.Rank)];
        _audioDecoders = [.. audioDecoders.OrderByDescending(static f => f.Rank)];
        _logger = logger ?? NullLogger<MediaCodecRegistry>.Instance;
    }

    /// <summary>Whether some registered factory encodes the codec.</summary>
    /// <param name="codec">The codec.</param>
    /// <returns>True when it can be sent.</returns>
    public bool CanEncode(VideoCodecId codec) =>
        _videoEncoders.Any(f => f.SupportedFormats.Any(s => s.Codec == codec));

    /// <summary>Whether some registered factory decodes the codec.</summary>
    /// <param name="codec">The codec.</param>
    /// <returns>True when it can be received.</returns>
    public bool CanDecode(VideoCodecId codec) =>
        _videoDecoders.Any(f => f.SupportedFormats.Any(s => s.Codec == codec));

    /// <summary>Whether some registered factory encodes the codec.</summary>
    /// <param name="codec">The codec.</param>
    /// <returns>True when it can be sent.</returns>
    public bool CanEncode(AudioCodecId codec) =>
        _audioEncoders.Any(f => f.SupportedFormats.Any(s => s.Codec == codec));

    /// <summary>Whether some registered factory decodes the codec.</summary>
    /// <param name="codec">The codec.</param>
    /// <returns>True when it can be received.</returns>
    public bool CanDecode(AudioCodecId codec) =>
        _audioDecoders.Any(f => f.SupportedFormats.Any(s => s.Codec == codec));

    /// <summary>What the best encoder for a format would take, without making one.</summary>
    /// <param name="format">The codec format.</param>
    /// <param name="device">The GPU the frames will be on; null for any.</param>
    /// <param name="refused">Implementations to pass over, by name: ones that failed in use.</param>
    /// <returns>The best encoder's information, or null when nothing encodes the format.</returns>
    public VideoEncoderInfo? QueryVideoEncoder(
        VideoCodecFormat format,
        GpuIdentity? device,
        IReadOnlySet<string>? refused = null
    )
    {
        foreach (IVideoEncoderFactory factory in _videoEncoders)
        {
            if (
                factory.QueryCapabilities(format, device) is { } info
                && refused?.Contains(info.ImplementationName) != true
            )
            {
                return info;
            }
        }

        return null;
    }

    /// <summary>Makes the best encoder that opens for a configuration.</summary>
    /// <param name="configuration">How to set it up.</param>
    /// <param name="device">The GPU the frames are on; null for any.</param>
    /// <param name="encoder">The encoder when one opened.</param>
    /// <param name="refused">Implementations to pass over, by name: ones that failed in use.</param>
    /// <returns>True when an encoder opened.</returns>
    public bool TryCreateVideoEncoder(
        VideoEncoderConfiguration configuration,
        GpuIdentity? device,
        [NotNullWhen(true)] out IVideoEncoder? encoder,
        IReadOnlySet<string>? refused = null
    )
    {
        ArgumentNullException.ThrowIfNull(configuration);
        foreach (IVideoEncoderFactory factory in _videoEncoders)
        {
            if (
                factory.QueryCapabilities(configuration.Format, device) is not { } info
                || refused?.Contains(info.ImplementationName) == true
            )
            {
                continue;
            }

            try
            {
                encoder = factory.Create(configuration, device);
                LogVideoEncoder(
                    info.ImplementationName,
                    configuration.Format.Codec,
                    configuration.Size
                );
                return true;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                LogVideoEncoderFailed(
                    exception,
                    info.ImplementationName,
                    configuration.Format.Codec
                );
            }
        }

        encoder = null;
        return false;
    }

    /// <summary>Makes the best decoder that opens for a format and what its consumer accepts.</summary>
    /// <param name="format">The codec format.</param>
    /// <param name="output">What the consumer of decoded frames accepts.</param>
    /// <param name="decoder">The decoder when one opened.</param>
    /// <returns>True when a decoder opened.</returns>
    public bool TryCreateVideoDecoder(
        VideoCodecFormat format,
        VideoConstraints output,
        [NotNullWhen(true)] out IVideoDecoder? decoder
    )
    {
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(output);

        // The consumer's storages are tried in its order before the factories' ranks: a GPU decoder that
        // hands over shared buffers beats a higher-ranked one that copies to memory, so frames stay on the
        // GPU whenever any decoder can keep them there.
        foreach (VideoStorageKind storage in output.Storages)
        {
            VideoConstraints narrowed = output with { Storages = [storage] };
            foreach (IVideoDecoderFactory factory in _videoDecoders)
            {
                if (factory.QueryCapabilities(format, narrowed) is not { } info)
                {
                    continue;
                }

                try
                {
                    decoder = factory.Create(format, narrowed);
                    LogVideoDecoder(info.ImplementationName, format.Codec);
                    return true;
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    LogVideoDecoderFailed(exception, info.ImplementationName, format.Codec);
                }
            }
        }

        decoder = null;
        return false;
    }

    /// <summary>Makes the best processor that opens for a stream and a request.</summary>
    /// <param name="input">The frames it will be fed.</param>
    /// <param name="processing">What to turn them into.</param>
    /// <param name="processor">The processor when one opened.</param>
    /// <returns>True when a processor opened.</returns>
    public bool TryCreateVideoProcessor(
        VideoStreamDescription input,
        VideoProcessing processing,
        [NotNullWhen(true)] out IVideoProcessor? processor
    )
    {
        ArgumentNullException.ThrowIfNull(processing);
        foreach (IVideoProcessorFactory factory in _videoProcessors)
        {
            if (factory.QueryCapabilities(input, processing) is not { } info)
            {
                continue;
            }

            try
            {
                processor = factory.Create(input, processing);
                LogVideoProcessor(info.ImplementationName, input, info.Output);
                return true;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                LogVideoProcessorFailed(exception, info.ImplementationName, input);
            }
        }

        processor = null;
        return false;
    }

    /// <summary>What the best audio encoder for a format would take, without making one.</summary>
    /// <param name="format">The codec format.</param>
    /// <returns>The encoder's information, or null when nothing encodes the format.</returns>
    public AudioEncoderInfo? QueryAudioEncoder(AudioCodecFormat format)
    {
        foreach (IAudioEncoderFactory factory in _audioEncoders)
        {
            if (factory.QueryCapabilities(format) is { } info)
            {
                return info;
            }
        }

        return null;
    }

    /// <summary>Makes the best audio encoder that opens for a configuration.</summary>
    /// <param name="configuration">How to set it up.</param>
    /// <param name="encoder">The encoder when one opened.</param>
    /// <returns>True when an encoder opened.</returns>
    public bool TryCreateAudioEncoder(
        AudioEncoderConfiguration configuration,
        [NotNullWhen(true)] out IAudioEncoder? encoder
    )
    {
        ArgumentNullException.ThrowIfNull(configuration);
        foreach (IAudioEncoderFactory factory in _audioEncoders)
        {
            if (factory.QueryCapabilities(configuration.Format) is not { } info)
            {
                continue;
            }

            try
            {
                encoder = factory.Create(configuration);
                LogAudioEncoder(info.ImplementationName, configuration.Format.Codec);
                return true;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                LogAudioEncoderFailed(
                    exception,
                    info.ImplementationName,
                    configuration.Format.Codec
                );
            }
        }

        encoder = null;
        return false;
    }

    /// <summary>Makes the best audio decoder that opens for a format.</summary>
    /// <param name="format">The codec format.</param>
    /// <param name="decoder">The decoder when one opened.</param>
    /// <returns>True when a decoder opened.</returns>
    public bool TryCreateAudioDecoder(
        AudioCodecFormat format,
        [NotNullWhen(true)] out IAudioDecoder? decoder
    )
    {
        ArgumentNullException.ThrowIfNull(format);
        foreach (IAudioDecoderFactory factory in _audioDecoders)
        {
            if (factory.QueryCapabilities(format) is not { } info)
            {
                continue;
            }

            try
            {
                decoder = factory.Create(format);
                LogAudioDecoder(info.ImplementationName, format.Codec);
                return true;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                LogAudioDecoderFailed(exception, info.ImplementationName, format.Codec);
            }
        }

        decoder = null;
        return false;
    }

    [LoggerMessage(2000, LogLevel.Information, "Encoding {Codec} at {Size} with {Implementation}.")]
    private partial void LogVideoEncoder(string implementation, VideoCodecId codec, VideoSize size);

    [LoggerMessage(
        2001,
        LogLevel.Warning,
        "{Implementation} could not encode {Codec}; trying the next encoder."
    )]
    private partial void LogVideoEncoderFailed(
        Exception exception,
        string implementation,
        VideoCodecId codec
    );

    [LoggerMessage(2002, LogLevel.Information, "Decoding {Codec} with {Implementation}.")]
    private partial void LogVideoDecoder(string implementation, VideoCodecId codec);

    [LoggerMessage(
        2003,
        LogLevel.Warning,
        "{Implementation} could not decode {Codec}; trying the next decoder."
    )]
    private partial void LogVideoDecoderFailed(
        Exception exception,
        string implementation,
        VideoCodecId codec
    );

    [LoggerMessage(
        2004,
        LogLevel.Information,
        "Processing {Input} to {Output} with {Implementation}."
    )]
    private partial void LogVideoProcessor(
        string implementation,
        VideoStreamDescription input,
        VideoStreamDescription output
    );

    [LoggerMessage(
        2005,
        LogLevel.Warning,
        "{Implementation} could not process {Input}; trying the next processor."
    )]
    private partial void LogVideoProcessorFailed(
        Exception exception,
        string implementation,
        VideoStreamDescription input
    );

    [LoggerMessage(2006, LogLevel.Information, "Encoding {Codec} audio with {Implementation}.")]
    private partial void LogAudioEncoder(string implementation, AudioCodecId codec);

    [LoggerMessage(
        2007,
        LogLevel.Warning,
        "{Implementation} could not encode {Codec} audio; trying the next encoder."
    )]
    private partial void LogAudioEncoderFailed(
        Exception exception,
        string implementation,
        AudioCodecId codec
    );

    [LoggerMessage(2008, LogLevel.Information, "Decoding {Codec} audio with {Implementation}.")]
    private partial void LogAudioDecoder(string implementation, AudioCodecId codec);

    [LoggerMessage(
        2009,
        LogLevel.Warning,
        "{Implementation} could not decode {Codec} audio; trying the next decoder."
    )]
    private partial void LogAudioDecoderFailed(
        Exception exception,
        string implementation,
        AudioCodecId codec
    );
}
