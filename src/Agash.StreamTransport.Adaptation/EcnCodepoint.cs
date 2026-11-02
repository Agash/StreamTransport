namespace Agash.StreamTransport.Adaptation;

/// <summary>The two ECN bits of an IP header (RFC 3168 section 5).</summary>
public enum EcnCodepoint : byte
{
    /// <summary>Not ECN-capable.</summary>
    NotEct = 0b00,

    /// <summary>ECN-capable, ECT(1): the L4S identifier (RFC 9331).</summary>
    Ect1 = 0b01,

    /// <summary>ECN-capable, ECT(0): classic ECN.</summary>
    Ect0 = 0b10,

    /// <summary>Congestion experienced: a router marked the packet instead of dropping it.</summary>
    Ce = 0b11,
}
