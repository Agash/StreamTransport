using System.Collections.Immutable;
using Agash.StreamTransport.WebRtc.Ice;

namespace Agash.StreamTransport.WebRtc.Transport;

/// <summary>Per-session settings of the WebRTC transport.</summary>
public sealed record WebRtcTransportOptions : MediaTransportOptions
{
    /// <summary>
    /// Which candidates ICE gathers. <see cref="IceTransportPolicy.Relay"/> sends only through the TURN
    /// servers, which keeps this side's addresses from the peer.
    /// </summary>
    public IceTransportPolicy IceTransportPolicy { get; init; } = IceTransportPolicy.All;

    /// <summary>
    /// Restricts ICE to local addresses matching one of these: a NIC name, a literal address, or
    /// <c>ipv4</c>/<c>ipv6</c>. Empty gathers every usable address.
    /// </summary>
    public ImmutableArray<string> LocalAddressPreferences { get; init; } = [];

    /// <summary>Whether loopback candidates are gathered, for peers on one host.</summary>
    public bool IncludeLoopbackCandidates { get; init; }
}
