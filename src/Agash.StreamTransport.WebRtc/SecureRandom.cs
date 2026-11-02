using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Agash.StreamTransport.WebRtc;

// Identifiers and starting values an attacker should not predict, from the operating system's
// cryptographic generator: SDP's session id (RFC 9429 section 5.2.1), SSRCs (RFC 3550 section 8.1) and
// initial sequence numbers (RFC 3550 section 5.1, against known-plaintext attacks on SRTP).
internal static class SecureRandom
{
    // A positive 63-bit value, as o= takes it.
    public static long SessionId()
    {
        Span<byte> bytes = stackalloc byte[8];
        while (true)
        {
            RandomNumberGenerator.Fill(bytes);
            long value = BinaryPrimitives.ReadInt64BigEndian(bytes) & long.MaxValue;
            if (value != 0)
            {
                return value;
            }
        }
    }

    // A nonzero SSRC.
    public static uint Ssrc()
    {
        Span<byte> bytes = stackalloc byte[4];
        while (true)
        {
            RandomNumberGenerator.Fill(bytes);
            uint value = BinaryPrimitives.ReadUInt32BigEndian(bytes);
            if (value != 0)
            {
                return value;
            }
        }
    }

    public static ushort SequenceNumber() => (ushort)RandomNumberGenerator.GetInt32(0, 0x10000);
}
