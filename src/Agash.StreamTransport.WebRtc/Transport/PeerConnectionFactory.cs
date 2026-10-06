using System.Diagnostics.Metrics;
using Agash.StreamTransport.Adaptation;
using Microsoft.Extensions.Logging;

namespace Agash.StreamTransport.WebRtc.Transport;

/// <summary>
/// Makes peer connections that share a host's certificate, clock, logging and metrics, each with a
/// congestion controller of its own.
/// </summary>
/// <param name="certificate">The DTLS certificate every connection authenticates with.</param>
/// <param name="congestionControl">Makes a connection's congestion controller; none when null.</param>
/// <param name="timeProvider">The clock; the system's when null.</param>
/// <param name="loggerFactory">Logging; none when null.</param>
/// <param name="meterFactory">Where the metrics' meter comes from; one shared meter when null.</param>
public sealed class PeerConnectionFactory(
    RtcCertificate certificate,
    Func<ICongestionController?>? congestionControl = null,
    TimeProvider? timeProvider = null,
    ILoggerFactory? loggerFactory = null,
    IMeterFactory? meterFactory = null
)
{
    /// <summary>The clock the connections run on.</summary>
    public TimeProvider TimeProvider { get; } = timeProvider ?? TimeProvider.System;

    /// <summary>Makes a peer connection.</summary>
    /// <param name="options">Its media and ICE configuration.</param>
    /// <returns>The connection, not yet negotiated.</returns>
    public PeerConnection Create(PeerConnectionOptions options) =>
        new(
            options,
            certificate,
            loggerFactory,
            congestionControl?.Invoke(),
            TimeProvider,
            meterFactory
        );
}
