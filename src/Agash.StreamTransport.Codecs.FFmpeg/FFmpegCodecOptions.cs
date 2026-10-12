using System.Collections.Immutable;

namespace Agash.StreamTransport.Codecs.FFmpeg;

/// <summary>
/// How the FFmpeg encoder and decoder factories behave. The services they use (logging, metrics, the
/// clock) come from their constructors, which dependency injection fills.
/// </summary>
public sealed class FFmpegCodecOptions
{
    /// <summary>
    /// Whether FFmpeg's own log (<c>av_log</c>) goes to the container's logging, under categories such as
    /// <c>FFmpeg.AVCodecContext</c>, instead of to stderr. FFmpeg's log is process-wide, so the last
    /// container to register the codecs owns it.
    /// </summary>
    public bool RouteFFmpegLog { get; init; } = true;

    /// <summary>
    /// Private FFmpeg options per encoder, by FFmpeg encoder name (<c>hevc_nvenc</c>), applied over the
    /// built-in real-time settings. An option the encoder does not know fails its open.
    /// </summary>
    public ImmutableDictionary<
        string,
        ImmutableDictionary<string, string>
    > EncoderOptions { get; init; } = [];
}
