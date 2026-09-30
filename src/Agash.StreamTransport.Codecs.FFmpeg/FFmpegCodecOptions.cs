using System.Collections.Immutable;
using Microsoft.Extensions.Logging;

namespace Agash.StreamTransport.Codecs.FFmpeg;

/// <summary>Options shared by the FFmpeg encoder and decoder factories.</summary>
public sealed class FFmpegCodecOptions
{
    /// <summary>Logging; none when null.</summary>
    public ILoggerFactory? LoggerFactory { get; init; }

    /// <summary>
    /// Private FFmpeg options per encoder, by FFmpeg encoder name (<c>hevc_nvenc</c>), applied over the
    /// built-in real-time settings. An option the encoder does not know fails its open.
    /// </summary>
    public ImmutableDictionary<
        string,
        ImmutableDictionary<string, string>
    > EncoderOptions { get; init; } = [];
}
