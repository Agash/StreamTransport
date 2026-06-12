using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;

namespace Agash.StreamTransport.WebRtc.Stun;

/// <summary>
/// A zero-allocation, parse-in-place reader over a received STUN datagram (RFC 8489 §5–6). The reader
/// borrows the caller's buffer; it copies nothing. Construct it with <see cref="TryParse"/>, then read the
/// header fields and walk attributes with <see cref="TryFindAttribute"/> / <see cref="TryGetXorMappedAddress"/>.
/// </summary>
public readonly ref struct StunMessageReader
{
    private StunMessageReader(ReadOnlySpan<byte> message)
    {
        Raw = message;
        (StunMessageClass cls, StunMethod method) = StunHeader.DecodeType(
            BinaryPrimitives.ReadUInt16BigEndian(message));
        Class = cls;
        Method = method;
    }

    /// <summary>The message class.</summary>
    public StunMessageClass Class { get; }

    /// <summary>The message method.</summary>
    public StunMethod Method { get; }

    /// <summary>The 12-byte transaction id (RFC 8489 §5).</summary>
    public ReadOnlySpan<byte> TransactionId => Raw.Slice(8, StunHeader.TransactionIdLength);

    /// <summary>The raw datagram this reader borrows.</summary>
    public ReadOnlySpan<byte> Raw { get; }

    /// <summary>
    /// Validates that the buffer is a structurally well-formed STUN message — magic cookie present, the
    /// header length consistent with the buffer, and every attribute fitting inside the message — and, if
    /// so, constructs a reader over it. Returns <see langword="false"/> for anything that is not STUN
    /// (for example a multiplexed DTLS or RTP packet) without throwing.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> datagram, out StunMessageReader reader)
    {
        reader = default;
        if (datagram.Length < StunHeader.Length)
        {
            return false;
        }

        // The two most-significant bits of a STUN message are zero; this disambiguates STUN from RTP/DTLS
        // on a multiplexed socket (RFC 7983).
        if ((datagram[0] & 0xC0) != 0)
        {
            return false;
        }

        if (BinaryPrimitives.ReadUInt32BigEndian(datagram[4..]) != StunHeader.MagicCookie)
        {
            return false;
        }

        int bodyLength = BinaryPrimitives.ReadUInt16BigEndian(datagram[2..]);
        if (bodyLength % 4 != 0 || StunHeader.Length + bodyLength > datagram.Length)
        {
            return false;
        }

        // Walk attributes once to confirm they tile the body exactly.
        ReadOnlySpan<byte> body = datagram.Slice(StunHeader.Length, bodyLength);
        int offset = 0;
        while (offset < body.Length)
        {
            if (offset + 4 > body.Length)
            {
                return false;
            }

            int valueLength = BinaryPrimitives.ReadUInt16BigEndian(body[(offset + 2)..]);
            int padded = (valueLength + 3) & ~3;
            if (offset + 4 + padded > body.Length)
            {
                return false;
            }

            offset += 4 + padded;
        }

        reader = new StunMessageReader(datagram[..(StunHeader.Length + bodyLength)]);
        return true;
    }

    /// <summary>
    /// Finds the first attribute of <paramref name="type"/> and returns its value (without the 4-byte
    /// attribute header and without trailing padding). Returns <see langword="false"/> if absent.
    /// </summary>
    public bool TryFindAttribute(StunAttributeType type, out ReadOnlySpan<byte> value)
    {
        ReadOnlySpan<byte> body = Raw[StunHeader.Length..];
        int offset = 0;
        while (offset + 4 <= body.Length)
        {
            var attrType = (StunAttributeType)BinaryPrimitives.ReadUInt16BigEndian(body[offset..]);
            int valueLength = BinaryPrimitives.ReadUInt16BigEndian(body[(offset + 2)..]);
            if (attrType == type)
            {
                value = body.Slice(offset + 4, valueLength);
                return true;
            }

            offset += 4 + ((valueLength + 3) & ~3);
        }

        value = default;
        return false;
    }

    /// <summary>
    /// Decodes XOR-MAPPED-ADDRESS (RFC 8489 §14.2) into an <see cref="IPEndPoint"/>. Supports IPv4 and IPv6.
    /// </summary>
    public bool TryGetXorMappedAddress(out IPEndPoint endpoint)
    {
        endpoint = default!;
        if (!TryFindAttribute(StunAttributeType.XorMappedAddress, out ReadOnlySpan<byte> value) || value.Length < 8)
        {
            return false;
        }

        byte family = value[1];
        ushort xPort = BinaryPrimitives.ReadUInt16BigEndian(value[2..]);
        int port = xPort ^ (ushort)(StunHeader.MagicCookie >> 16);

        if (family == 0x01)
        {
            // IPv4: X-Address = address XOR magic cookie.
            uint xAddr = BinaryPrimitives.ReadUInt32BigEndian(value[4..]);
            uint addr = xAddr ^ StunHeader.MagicCookie;
            Span<byte> raw = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(raw, addr);
            endpoint = new IPEndPoint(new IPAddress(raw), port);
            return true;
        }

        if (family == 0x02 && value.Length >= 20)
        {
            // IPv6: X-Address = address XOR (magic cookie || transaction id).
            Span<byte> mask = stackalloc byte[16];
            BinaryPrimitives.WriteUInt32BigEndian(mask, StunHeader.MagicCookie);
            TransactionId.CopyTo(mask[4..]);

            Span<byte> raw = stackalloc byte[16];
            for (int i = 0; i < 16; i++)
            {
                raw[i] = (byte)(value[4 + i] ^ mask[i]);
            }

            endpoint = new IPEndPoint(new IPAddress(raw), port);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Verifies the FINGERPRINT attribute (RFC 8489 §14.7): the CRC-32 of the message up to the
    /// FINGERPRINT attribute, with the header length adjusted to include it, XORed with the STUN constant.
    /// Returns <see langword="false"/> if the attribute is absent or does not match.
    /// </summary>
    public bool VerifyFingerprint()
    {
        // FINGERPRINT, if present, is the last attribute. Locate its value offset within the full message.
        ReadOnlySpan<byte> body = Raw[StunHeader.Length..];
        int offset = 0;
        int fingerprintStart = -1;
        while (offset + 4 <= body.Length)
        {
            var attrType = (StunAttributeType)BinaryPrimitives.ReadUInt16BigEndian(body[offset..]);
            int valueLength = BinaryPrimitives.ReadUInt16BigEndian(body[(offset + 2)..]);
            if (attrType == StunAttributeType.Fingerprint)
            {
                fingerprintStart = StunHeader.Length + offset;
                break;
            }

            offset += 4 + ((valueLength + 3) & ~3);
        }

        if (fingerprintStart < 0 || fingerprintStart + 8 > Raw.Length)
        {
            return false;
        }

        uint actual = BinaryPrimitives.ReadUInt32BigEndian(Raw[(fingerprintStart + 4)..]);
        uint expected = ComputeFingerprint(Raw[..fingerprintStart]);
        return actual == expected;
    }

    /// <summary>
    /// Verifies MESSAGE-INTEGRITY (RFC 8489 §14.5): HMAC-SHA1 over the message up to the attribute, with
    /// the header length adjusted to include the 24-byte attribute, keyed by <paramref name="key"/>
    /// (short-term: the password; long-term: <c>MD5(username:realm:password)</c>). Constant-time compare.
    /// </summary>
    public bool VerifyMessageIntegrity(ReadOnlySpan<byte> key)
    {
        ReadOnlySpan<byte> body = Raw[StunHeader.Length..];
        int offset = 0;
        int miStart = -1;
        while (offset + 4 <= body.Length)
        {
            var attrType = (StunAttributeType)BinaryPrimitives.ReadUInt16BigEndian(body[offset..]);
            int valueLength = BinaryPrimitives.ReadUInt16BigEndian(body[(offset + 2)..]);
            if (attrType == StunAttributeType.MessageIntegrity)
            {
                miStart = StunHeader.Length + offset;
                break;
            }

            offset += 4 + ((valueLength + 3) & ~3);
        }

        if (miStart < 0 || miStart + 24 > Raw.Length)
        {
            return false;
        }

        Span<byte> expected = stackalloc byte[20];
        ComputeMessageIntegrity(Raw, miStart, key, expected);
        return CryptographicOperations.FixedTimeEquals(Raw.Slice(miStart + 4, 20), expected);
    }

    /// <summary>
    /// Computes the FINGERPRINT value over <paramref name="precedingMessage"/> (the message up to the
    /// FINGERPRINT attribute) with the header length already set to include the 8-byte FINGERPRINT.
    /// </summary>
    internal static uint ComputeFingerprint(ReadOnlySpan<byte> precedingMessage)
    {
        // CRC over the message with the length field set to (bodySoFar + 8). The body length is
        // (precedingMessage.Length - header) + 8.
        Span<byte> buffer = precedingMessage.Length <= 512
            ? stackalloc byte[precedingMessage.Length]
            : new byte[precedingMessage.Length];
        precedingMessage.CopyTo(buffer);
        int adjustedBody = precedingMessage.Length - StunHeader.Length + 8;
        BinaryPrimitives.WriteUInt16BigEndian(buffer[2..], (ushort)adjustedBody);
        return StunCrc32.Compute(buffer) ^ StunHeader.FingerprintXor;
    }

    /// <summary>
    /// Computes MESSAGE-INTEGRITY (HMAC-SHA1) for the message whose MESSAGE-INTEGRITY attribute begins at
    /// <paramref name="miStart"/>, writing the 20-byte tag to <paramref name="destination"/>. The HMAC
    /// covers the message up to <paramref name="miStart"/> with the header length set to include the
    /// 24-byte attribute.
    /// </summary>
    internal static void ComputeMessageIntegrity(ReadOnlySpan<byte> message, int miStart, ReadOnlySpan<byte> key, Span<byte> destination)
    {
        Span<byte> buffer = miStart <= 512 ? stackalloc byte[miStart] : new byte[miStart];
        message[..miStart].CopyTo(buffer);
        int adjustedBody = miStart - StunHeader.Length + 24;
        BinaryPrimitives.WriteUInt16BigEndian(buffer[2..], (ushort)adjustedBody);
        HMACSHA1.HashData(key, buffer, destination);
    }
}
