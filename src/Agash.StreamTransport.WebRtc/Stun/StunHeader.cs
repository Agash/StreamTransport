namespace Agash.StreamTransport.WebRtc.Stun;

/// <summary>
/// Constants and the message-type bit-packing for the fixed 20-byte STUN header (RFC 8489 §5).
/// </summary>
public static class StunHeader
{
    /// <summary>The fixed STUN header length in bytes.</summary>
    public const int Length = 20;

    /// <summary>The STUN magic cookie (RFC 8489 §5), at bytes 4..7 of every message.</summary>
    public const uint MagicCookie = 0x2112_A442;

    /// <summary>The length in bytes of the transaction id (RFC 8489 §5).</summary>
    public const int TransactionIdLength = 12;

    /// <summary>The XOR constant FINGERPRINT applies to the CRC-32 (RFC 8489 §14.7).</summary>
    public const uint FingerprintXor = 0x5354_554E;

    /// <summary>Packs a class and method into the 14-bit STUN message-type field (RFC 8489 §5).</summary>
    public static ushort EncodeType(StunMessageClass messageClass, StunMethod method)
    {
        uint m = (uint)method;
        uint c = (uint)messageClass;

        // M11..M5 | C1 | M4..M2 | C0 | M1..M0, with the two leading bits zero.
        uint type = ((m & 0x0F80u) << 2)
            | ((m & 0x0070u) << 1)
            | (m & 0x000Fu)
            | ((c & 0b10u) << 7)
            | ((c & 0b01u) << 4);
        return (ushort)type;
    }

    /// <summary>Unpacks a 14-bit STUN message-type field into its class and method (RFC 8489 §5).</summary>
    public static (StunMessageClass Class, StunMethod Method) DecodeType(ushort type)
    {
        uint method = (type & 0x000Fu)
            | ((type & 0x00E0u) >> 1)
            | ((type & 0x3E00u) >> 2);
        uint cls = ((type & 0x0100u) >> 7) | ((type & 0x0010u) >> 4);
        return ((StunMessageClass)cls, (StunMethod)method);
    }
}
