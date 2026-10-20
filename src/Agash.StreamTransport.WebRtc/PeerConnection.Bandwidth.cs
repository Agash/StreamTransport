using Agash.StreamTransport.Adaptation;
using Agash.StreamTransport.WebRtc.Sdp;

namespace Agash.StreamTransport.WebRtc;

/// <summary>
/// Bandwidth lines (RFC 8829 section 5.10): a line this side receives says how much RTP the peer may send
/// on it (<c>b=TIAS</c>, RFC 3890, with the <c>b=AS</c> it derives from), and the peer's lines limit what
/// this side sends, per section and in total (<c>b=CT</c>).
/// </summary>
public sealed partial class PeerConnection
{
    // The overhead JSEP takes off AS for TIAS: 50 packets a second of 40-byte headers.
    private const long HeaderBitsPerSecond = 50 * 40 * 8;

    private long _sendLimit = long.MaxValue;

    /// <summary>
    /// The most this side may send in total, in bits per second, as the peer's description limits it: its
    /// <c>b=CT</c>, or the sum of its limits on the sections this side sends when each has one. Null for no
    /// limit. The capacity estimate this side raises never exceeds it.
    /// </summary>
    public long? SendLimitBitsPerSecond
    {
        get
        {
            long limit = Interlocked.Read(ref _sendLimit);
            return limit == long.MaxValue ? null : limit;
        }
    }

    // A line's limit as the section writes it: TIAS as configured, AS so that JSEP's derivation from it
    // gives the same limit back (AS * 0.95 - 16000 = TIAS).
    private SdpBandwidth ReceiveBandwidth(string mid, SdpMediaKind kind, SdpDirection direction)
    {
        if (
            direction is SdpDirection.SendOnly or SdpDirection.Inactive
            || LineFor(mid, kind)?.MaximumReceiveBitsPerSecond is not { } limit
        )
        {
            return default;
        }

        return new SdpBandwidth(
            ApplicationSpecific: ((limit + HeaderBitsPerSecond) * 100 + 94) / 95,
            TransportIndependent: limit
        );
    }

    // The total the peer's description allows this side to send.
    private void ApplyRemoteBandwidth(SdpDescription remote)
    {
        long sections = 0;
        bool everySectionLimited = false;
        foreach (NegotiatedMediaInfo media in NegotiatedMedia)
        {
            if (!Sends(LineFor(media.Mid, media.Kind)?.Direction ?? SdpDirection.Inactive))
            {
                continue;
            }

            if (media.SendLimitBitsPerSecond is not { } limit)
            {
                everySectionLimited = false;
                sections = 0;
                break;
            }

            everySectionLimited = true;
            sections = sections > long.MaxValue - limit ? long.MaxValue : sections + limit;
        }

        long total = Math.Min(
            remote.Bandwidth.ConferenceTotal ?? long.MaxValue,
            everySectionLimited ? sections : long.MaxValue
        );
        Interlocked.Exchange(ref _sendLimit, total);
    }

    // The estimate within the peer's limit, with the pacer's usual headroom above it for repairs.
    private CapacityEstimate ApplySendLimit(CapacityEstimate estimate)
    {
        long limit = Math.Min(Interlocked.Read(ref _sendLimit), Interlocked.Read(ref _tmmbrLimit));
        return limit == long.MaxValue
            ? estimate
            : estimate with
            {
                TargetBitsPerSecond = Math.Min(estimate.TargetBitsPerSecond, limit),
                PacingBitsPerSecond = Math.Min(
                    estimate.PacingBitsPerSecond,
                    limit > long.MaxValue / 5 * 4 ? long.MaxValue : limit * 5 / 4
                ),
            };
    }

    private MediaLine? LineFor(string mid, SdpMediaKind kind) =>
        _options.Media.FirstOrDefault(l => l.Mid == mid && l.Kind == kind)
        ?? _options.Media.FirstOrDefault(l => l.Kind == kind);
}
