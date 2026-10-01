using System.Diagnostics;

namespace Agash.StreamTransport;

/// <summary>
/// The names this library reports metrics and traces under. Nothing is recorded or emitted until
/// something listens: add <see cref="MeterName"/> and <see cref="ActivitySourceName"/> to an
/// OpenTelemetry provider, watch with <c>dotnet-counters monitor --counters Agash.StreamTransport</c>,
/// or attach a <see cref="System.Diagnostics.Metrics.MeterListener"/> or <see cref="ActivityListener"/>.
/// </summary>
/// <remarks>
/// The WebRTC transport (<c>Agash.StreamTransport.WebRtc</c>) and the FFmpeg codecs
/// (<c>Agash.StreamTransport.Codecs.FFmpeg</c>) report under names of their own.
/// </remarks>
public static class StreamTransportDiagnostics
{
    /// <summary>The meter's name: <c>Agash.StreamTransport</c>.</summary>
    public const string MeterName = "Agash.StreamTransport";

    /// <summary>The activity source's name: <c>Agash.StreamTransport</c>.</summary>
    public const string ActivitySourceName = "Agash.StreamTransport";

    internal static ActivitySource ActivitySource { get; } =
        new(
            ActivitySourceName,
            typeof(StreamTransportDiagnostics).Assembly.GetName().Version?.ToString()
        );
}
