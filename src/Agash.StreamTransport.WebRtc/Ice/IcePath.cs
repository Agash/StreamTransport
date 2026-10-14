using System.Net;

namespace Agash.StreamTransport.WebRtc.Ice;

/// <summary>The path a connection's media takes: the selected candidate pair's two ends.</summary>
/// <param name="Local">This side's endpoint.</param>
/// <param name="Remote">The peer's endpoint.</param>
/// <param name="LocalKind">How this side's candidate was found: a host address, a NAT mapping, a relay.</param>
/// <param name="RemoteKind">How the peer's candidate was found.</param>
public readonly record struct IcePath(
    IPEndPoint Local,
    IPEndPoint Remote,
    IceCandidateKind LocalKind,
    IceCandidateKind RemoteKind
)
{
    /// <summary>Whether media goes through a TURN relay on either side.</summary>
    public bool IsRelayed =>
        LocalKind == IceCandidateKind.Relayed || RemoteKind == IceCandidateKind.Relayed;
}
