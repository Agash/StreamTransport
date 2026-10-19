using Agash.StreamTransport.Adaptation;
using Microsoft.Extensions.Logging;

namespace Agash.StreamTransport.WebRtc;

/// <summary>
/// DSCP marking of media (RFC 8837): each packet is marked by what it carries, at the priority the options
/// give, beside its ECN codepoint. STUN, DTLS and RTCP stay unmarked. A network may drop marked packets
/// (RFC 8835 section 4.2): when the media timeout trips while media is marked, marking stops for the rest
/// of the connection, so the path that worked unmarked keeps working.
/// </summary>
public sealed partial class PeerConnection
{
    private volatile bool _marksDropped;

    private IpMarking MarkingFor(TrafficClass traffic, EcnCodepoint ecn) =>
        new(
            _marksDropped
                ? DiffServCodepoint.Default
                : IpMarking.DiffServFor(traffic, _options.NetworkPriority),
            ecn
        );

    private void DropMarksAfter(CircuitBreakerReason reason)
    {
        if (
            reason == CircuitBreakerReason.MediaTimeout
            && !_marksDropped
            && _options.NetworkPriority is not NetworkPriority.Low
        )
        {
            _marksDropped = true;
            LogMarksDropped(_logger);
        }
    }

    [LoggerMessage(
        EventId = 1192,
        Level = LogLevel.Warning,
        Message = "Media timed out while marked; sending unmarked from now on (RFC 8835 section 4.2)"
    )]
    private static partial void LogMarksDropped(ILogger logger);
}
