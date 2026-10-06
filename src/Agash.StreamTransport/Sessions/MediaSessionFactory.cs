using System.Diagnostics.Metrics;
using Agash.StreamTransport.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agash.StreamTransport.Sessions;

/// <summary>Makes media sessions over the registered transport.</summary>
/// <param name="codecs">The registered codecs and processors.</param>
/// <param name="transports">Makes each session's transport.</param>
/// <param name="timeProvider">The clock.</param>
/// <param name="loggerFactory">Logging; none when null.</param>
/// <param name="meterFactory">Where the metrics' meter comes from; one shared meter when null.</param>
public sealed class MediaSessionFactory(
    MediaCodecRegistry codecs,
    IMediaTransportFactory transports,
    TimeProvider timeProvider,
    ILoggerFactory? loggerFactory = null,
    IMeterFactory? meterFactory = null
) : IMediaSessionFactory, IDisposable
{
    private readonly SessionServices _services = new(
        codecs,
        transports,
        new MediaClock(timeProvider),
        loggerFactory ?? NullLoggerFactory.Instance,
        new StreamTransportMetrics(meterFactory)
    );

    /// <inheritdoc/>
    public void Dispose() => _services.Metrics.Dispose();

    /// <inheritdoc/>
    public IMediaSession Create(
        ISignalingChannel signaling,
        MediaSessionRole role,
        MediaEndpoints endpoints,
        MediaSessionOptions options
    )
    {
        ArgumentNullException.ThrowIfNull(signaling);
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(options);
        return new MediaSession(signaling, role, endpoints, options, _services);
    }
}
