namespace Agash.StreamTransport.WebRtc;

/// <summary>
/// The ICE role of an agent (RFC 8445 §6.1). The controlling agent nominates the candidate pair used
/// for media; the controlled agent follows. The offerer is controlling in a full ICE exchange.
/// </summary>
public enum IceRole
{
    /// <summary>Controlled agent - follows the controlling agent's nomination.</summary>
    Controlled = 0,

    /// <summary>Controlling agent - nominates the selected candidate pair.</summary>
    Controlling = 1,
}

/// <summary>
/// The DTLS role chosen by the <c>a=setup</c> SDP attribute (RFC 4145 / RFC 8842). <c>actpass</c> in an
/// offer collapses to <see cref="Server"/> or <see cref="Client"/> once the answer picks a side.
/// </summary>
public enum DtlsRole
{
    /// <summary>DTLS client - sends <c>ClientHello</c> and initiates the handshake (<c>a=setup:active</c>).</summary>
    Client = 0,

    /// <summary>DTLS server - listens for <c>ClientHello</c> (<c>a=setup:passive</c>).</summary>
    Server = 1,
}

/// <summary>ICE candidate type (RFC 8445 §5.1.1), in increasing order of indirection.</summary>
public enum IceCandidateKind
{
    /// <summary>A candidate bound directly to a local interface address.</summary>
    Host = 0,

    /// <summary>A server-reflexive candidate: a public mapping discovered via STUN.</summary>
    ServerReflexive = 1,

    /// <summary>A peer-reflexive candidate: a mapping learned from an incoming connectivity check.</summary>
    PeerReflexive = 2,

    /// <summary>A relayed candidate allocated on a TURN server.</summary>
    Relayed = 3,
}
