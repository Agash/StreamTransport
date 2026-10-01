using System.Collections.Immutable;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace Agash.StreamTransport.Codecs.FFmpeg;

/// <summary>Options shared by the FFmpeg encoder and decoder factories.</summary>
public sealed class FFmpegCodecOptions
{
    /// <summary>Logging; none when null.</summary>
    public ILoggerFactory? LoggerFactory { get; init; }

    /// <summary>
    /// Where the metrics' meter comes from (<see cref="FFmpegCodecDiagnostics.MeterName"/>); one meter
    /// shared by every factory made without one when null.
    /// </summary>
    public IMeterFactory? MeterFactory { get; init; }

    /// <summary>The clock encoder opens are timed on.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>
    /// Whether FFmpeg's own log (<c>av_log</c>) goes to <see cref="LoggerFactory"/>, under categories such
    /// as <c>FFmpeg.AVCodecContext</c>, instead of to stderr. FFmpeg's log is process-wide, so the last
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
