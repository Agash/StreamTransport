namespace Agash.StreamTransport.WebRtc.Ice;

/// <summary>Which candidates an ICE agent gathers (the <c>iceTransportPolicy</c> of WebRTC).</summary>
public enum IceTransportPolicy
{
    /// <summary>Host, server-reflexive and relayed candidates.</summary>
    All,

    /// <summary>
    /// Relayed candidates only: media always goes through TURN, which hides this side's addresses from
    /// the peer and exercises the relay path on networks where a direct one would win.
    /// </summary>
    Relay,
}
