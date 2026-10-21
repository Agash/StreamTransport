using System.Diagnostics.Metrics;

namespace Agash.StreamTransport.Codecs.FFmpeg;

/// <summary>
/// The meter the FFmpeg codecs report on. Nothing is recorded until something listens: add
/// <see cref="MeterName"/> to an OpenTelemetry provider or watch it with <c>dotnet-counters</c>.
/// </summary>
public static class FFmpegCodecDiagnostics
{
    /// <summary>The meter's name: <c>Agash.StreamTransport.Codecs.FFmpeg</c>.</summary>
    public const string MeterName = "Agash.StreamTransport.Codecs.FFmpeg";
}

// The instruments, tagged with the FFmpeg codec's name (h264_nvenc) and, where it applies, why an
// encoder opened or how a probe went.
internal sealed class FFmpegCodecMetrics
{
    public FFmpegCodecMetrics(IMeterFactory? meterFactory)
    {
        Meter meter =
            meterFactory?.Create(FFmpegCodecDiagnostics.MeterName)
            ?? new Meter(FFmpegCodecDiagnostics.MeterName);
        EncoderOpens = meter.CreateCounter<long>(
            "streamtransport.ffmpeg.encoder.opens",
            "{open}",
            "Encoders opened, by encoder and reason: start, rate, frame_rate, keyframe or storage. Every open after the start costs a keyframe."
        );
        EncoderOpenDuration = meter.CreateHistogram<double>(
            "streamtransport.ffmpeg.encoder.open.duration",
            "s",
            "Time to open an encoder, by encoder."
        );
        Probes = meter.CreateCounter<long>(
            "streamtransport.ffmpeg.probes",
            "{probe}",
            "Encoders and decoders tried at first use, by codec and outcome: works or unavailable."
        );
    }

    // For factories made without dependency injection.
    public static FFmpegCodecMetrics Shared { get; } = new(meterFactory: null);

    public Counter<long> EncoderOpens { get; }

    public Histogram<double> EncoderOpenDuration { get; }

    public Counter<long> Probes { get; }

    public static KeyValuePair<string, object?> Codec(string name) =>
        new("streamtransport.ffmpeg.codec", name);

    public static KeyValuePair<string, object?> Reason(string reason) =>
        new("streamtransport.reason", reason);

    public static KeyValuePair<string, object?> Outcome(bool works) =>
        new("streamtransport.outcome", works ? "works" : "unavailable");
}
