namespace Agash.StreamTransport.WebRtc.Stun;

/// <summary>The class of a STUN message (RFC 8489 §5), encoded in two non-contiguous bits of the type field.</summary>
public enum StunMessageClass
{
    /// <summary>A request, which elicits a response.</summary>
    Request = 0b00,

    /// <summary>An indication, which elicits no response.</summary>
    Indication = 0b01,

    /// <summary>A success response to a request.</summary>
    SuccessResponse = 0b10,

    /// <summary>An error response to a request.</summary>
    ErrorResponse = 0b11,
}

/// <summary>STUN/TURN method code points (RFC 8489 / RFC 8656), encoded in 12 bits of the type field.</summary>
public enum StunMethod : ushort
{
    /// <summary>The STUN Binding method (RFC 8489 §3).</summary>
    Binding = 0x001,

    // TURN methods (RFC 8656) are added when the relay package lands.
}

/// <summary>
/// STUN attribute type code points (RFC 8489 §14 and the related ICE/TURN registries). Values below
/// <c>0x8000</c> are comprehension-required; values at or above are comprehension-optional.
/// </summary>
public enum StunAttributeType : ushort
{
    /// <summary>MAPPED-ADDRESS (legacy, RFC 8489 §14.1).</summary>
    MappedAddress = 0x0001,

    /// <summary>USERNAME (RFC 8489 §14.3).</summary>
    Username = 0x0006,

    /// <summary>MESSAGE-INTEGRITY — HMAC-SHA1 of the message (RFC 8489 §14.5).</summary>
    MessageIntegrity = 0x0008,

    /// <summary>ERROR-CODE (RFC 8489 §14.8).</summary>
    ErrorCode = 0x0009,

    /// <summary>UNKNOWN-ATTRIBUTES (RFC 8489 §14.9).</summary>
    UnknownAttributes = 0x000A,

    /// <summary>MESSAGE-INTEGRITY-SHA256 (RFC 8489 §14.6).</summary>
    MessageIntegritySha256 = 0x001C,

    /// <summary>XOR-MAPPED-ADDRESS (RFC 8489 §14.2).</summary>
    XorMappedAddress = 0x0020,

    /// <summary>PRIORITY — ICE connectivity check (RFC 8445 §7.1.1).</summary>
    Priority = 0x0024,

    /// <summary>USE-CANDIDATE — ICE nomination (RFC 8445 §7.1.2).</summary>
    UseCandidate = 0x0025,

    /// <summary>SOFTWARE (RFC 8489 §14.14).</summary>
    Software = 0x8022,

    /// <summary>ALTERNATE-SERVER (RFC 8489 §14.15).</summary>
    AlternateServer = 0x8023,

    /// <summary>FINGERPRINT — CRC-32 of the message (RFC 8489 §14.7).</summary>
    Fingerprint = 0x8028,

    /// <summary>ICE-CONTROLLED — tie-breaker for the controlled role (RFC 8445 §7.1.3).</summary>
    IceControlled = 0x8029,

    /// <summary>ICE-CONTROLLING — tie-breaker for the controlling role (RFC 8445 §7.1.3).</summary>
    IceControlling = 0x802A,
}
