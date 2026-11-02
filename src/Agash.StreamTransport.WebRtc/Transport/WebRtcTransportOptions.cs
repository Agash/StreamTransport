using System.Collections.Immutable;
using Agash.StreamTransport.WebRtc.Ice;

namespace Agash.StreamTransport.WebRtc.Transport;

/// <summary>Per-session settings of the WebRTC transport.</summary>
public sealed record WebRtcTransportOptions : MediaTransportOptions
{
    /// <summary>Options with every setting at its default.</summary>
    public WebRtcTransportOptions() { }

    /// <summary>The WebRTC options for transport-neutral ones, every neutral setting carried over.</summary>
    /// <param name="options">The neutral options.</param>
    public WebRtcTransportOptions(MediaTransportOptions options)
        : base(options) { }

    /// <summary>
    /// ICE's pacing and timeouts, for full control; null derives them from
    /// <see cref="MediaTransportOptions.PathFailover"/>, or uses libwebrtc's values without it.
    /// </summary>
    public IceTimings? IceTimings { get; init; }

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

    /// <summary>
    /// Where ICE gets its local addresses and binds its sockets: the host's UDP stack when null. A network
    /// of another kind, or one a test simulates, plugs in here.
    /// </summary>
    public IIceSocketFactory? SocketFactory { get; init; }
}
