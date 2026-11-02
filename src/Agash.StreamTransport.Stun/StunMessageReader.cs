using System.Buffers;
using System.Buffers.Binary;
using System.IO.Hashing;
using System.Net;
using System.Security.Cryptography;

namespace Agash.StreamTransport.Stun;

/// <summary>
/// A zero-allocation, parse-in-place reader over a received STUN datagram (RFC 8489 §5-6). The reader
/// borrows the caller's buffer; it copies nothing. Construct it with <see cref="TryParse"/>, then read the
/// header fields and walk attributes with <see cref="TryFindAttribute"/> / <see cref="TryGetXorMappedAddress"/>.
/// </summary>
public readonly ref struct StunMessageReader
{
    private StunMessageReader(ReadOnlySpan<byte> message)
    {
        Raw = message;
        (StunMessageClass cls, StunMethod method) = StunHeader.DecodeType(
            BinaryPrimitives.ReadUInt16BigEndian(message)
        );
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
    /// Validates that the buffer is a structurally well-formed STUN message - magic cookie present, the
    /// header length consistent with the buffer, and every attribute fitting inside the message - and, if
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
    /// <remarks>
    /// Attributes after MESSAGE-INTEGRITY are not found, FINGERPRINT aside (RFC 8489 section 14.5):
    /// integrity does not cover them, so one appended to a captured message (a USE-CANDIDATE, say) must
    /// not count.
    /// </remarks>
    public bool TryFindAttribute(StunAttributeType type, out ReadOnlySpan<byte> value)
    {
        ReadOnlySpan<byte> body = Raw[StunHeader.Length..];
        int offset = 0;
        bool protectedPart = true;
        while (offset + 4 <= body.Length)
        {
            var attrType = (StunAttributeType)BinaryPrimitives.ReadUInt16BigEndian(body[offset..]);
            int valueLength = BinaryPrimitives.ReadUInt16BigEndian(body[(offset + 2)..]);
            if (attrType == type && (protectedPart || type == StunAttributeType.Fingerprint))
            {
                value = body.Slice(offset + 4, valueLength);
                return true;
            }

            if (
                attrType
                is StunAttributeType.MessageIntegrity
                    or StunAttributeType.MessageIntegritySha256
            )
            {
                protectedPart = false;
            }

            offset += 4 + ((valueLength + 3) & ~3);
        }

        value = default;
        return false;
    }

    /// <summary>
    /// Collects the comprehension-required attributes (types below 0x8000) the receiver does not
    /// understand, which a request is answered with 420 for (RFC 8489 sections 6.3.1 and 15). Attributes
    /// after MESSAGE-INTEGRITY are ignored, as section 14.5 has them.
    /// </summary>
    /// <param name="understood">The attribute types the receiver understands.</param>
    /// <param name="unknown">Where the unknown types go.</param>
    /// <returns>How many were written to <paramref name="unknown"/>.</returns>
    public int CollectUnknownRequired(
        scoped ReadOnlySpan<StunAttributeType> understood,
        Span<ushort> unknown
    )
    {
        ReadOnlySpan<byte> body = Raw[StunHeader.Length..];
        int offset = 0;
        int count = 0;
        while (offset + 4 <= body.Length && count < unknown.Length)
        {
            ushort type = BinaryPrimitives.ReadUInt16BigEndian(body[offset..]);
            int valueLength = BinaryPrimitives.ReadUInt16BigEndian(body[(offset + 2)..]);
            if (type < 0x8000 && !understood.Contains((StunAttributeType)type))
            {
                unknown[count++] = type;
            }

            if (
                (StunAttributeType)type
                is StunAttributeType.MessageIntegrity
                    or StunAttributeType.MessageIntegritySha256
            )
            {
                break;
            }

            offset += 4 + ((valueLength + 3) & ~3);
        }

        return count;
    }

    /// <summary>
    /// Decodes XOR-MAPPED-ADDRESS (RFC 8489 §14.2) into an <see cref="IPEndPoint"/>. Supports IPv4 and IPv6.
    /// </summary>
    public bool TryGetXorMappedAddress(out IPEndPoint endpoint) =>
        TryGetXorAddress(StunAttributeType.XorMappedAddress, out endpoint);

    /// <summary>
    /// Decodes an XOR-encoded address attribute (XOR-MAPPED-ADDRESS, XOR-PEER-ADDRESS,
    /// XOR-RELAYED-ADDRESS) into an <see cref="IPEndPoint"/>.
    /// </summary>
    public bool TryGetXorAddress(StunAttributeType type, out IPEndPoint endpoint)
    {
        endpoint = default!;
        if (!TryFindAttribute(type, out ReadOnlySpan<byte> value) || value.Length < 8)
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
    /// Decodes an address attribute in the plain MAPPED-ADDRESS format (RFC 8489 §14.1), such as
    /// ALTERNATE-SERVER (§14.15), into an <see cref="IPEndPoint"/>.
    /// </summary>
    public bool TryGetAddress(StunAttributeType type, out IPEndPoint endpoint)
    {
        endpoint = default!;
        if (!TryFindAttribute(type, out ReadOnlySpan<byte> value) || value.Length < 8)
        {
            return false;
        }

        int port = BinaryPrimitives.ReadUInt16BigEndian(value[2..]);
        switch (value[1])
        {
            case 0x01:
                endpoint = new IPEndPoint(new IPAddress(value.Slice(4, 4)), port);
                return true;
            case 0x02 when value.Length >= 20:
                endpoint = new IPEndPoint(new IPAddress(value.Slice(4, 16)), port);
                return true;
            default:
                return false;
        }
    }

    /// <summary>Reads ERROR-CODE (RFC 8489 §14.8): the class times 100 plus the number.</summary>
    public bool TryGetErrorCode(out int code)
    {
        code = 0;
        if (
            !TryFindAttribute(StunAttributeType.ErrorCode, out ReadOnlySpan<byte> value)
            || value.Length < 4
        )
        {
            return false;
        }

        code = ((value[2] & 0x07) * 100) + value[3];
        return true;
    }

    /// <summary>Reads a 32-bit big-endian attribute value, such as LIFETIME.</summary>
    public bool TryGetUInt32(StunAttributeType type, out uint value)
    {
        value = 0;
        if (!TryFindAttribute(type, out ReadOnlySpan<byte> bytes) || bytes.Length < 4)
        {
            return false;
        }

        value = BinaryPrimitives.ReadUInt32BigEndian(bytes);
        return true;
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
    /// Verifies MESSAGE-INTEGRITY-SHA256 (RFC 8489 §14.6): HMAC-SHA256 over the message up to the
    /// attribute, with the header length adjusted to include it, compared in constant time over the
    /// attribute's length, which is 16 to 32 bytes in steps of 4.
    /// </summary>
    /// <param name="key">The key: the password (short-term) or the long-term key of §9.2.2.</param>
    /// <returns>Whether the attribute is present and matches.</returns>
    public bool VerifyMessageIntegritySha256(ReadOnlySpan<byte> key)
    {
        ReadOnlySpan<byte> body = Raw[StunHeader.Length..];
        int offset = 0;
        while (offset + 4 <= body.Length)
        {
            var attrType = (StunAttributeType)BinaryPrimitives.ReadUInt16BigEndian(body[offset..]);
            int valueLength = BinaryPrimitives.ReadUInt16BigEndian(body[(offset + 2)..]);
            if (attrType == StunAttributeType.MessageIntegritySha256)
            {
                int start = StunHeader.Length + offset;
                if (
                    valueLength is < 16 or > 32
                    || valueLength % 4 != 0
                    || start + 4 + valueLength > Raw.Length
                )
                {
                    return false;
                }

                Span<byte> expected = stackalloc byte[32];
                ComputeMessageIntegritySha256(Raw, start, key, expected, valueLength);
                return CryptographicOperations.FixedTimeEquals(
                    Raw.Slice(start + 4, valueLength),
                    expected[..valueLength]
                );
            }

            offset += 4 + ((valueLength + 3) & ~3);
        }

        return false;
    }

    /// <summary>
    /// Computes MESSAGE-INTEGRITY-SHA256 for the message whose attribute begins at
    /// <paramref name="start"/>, writing the 32-byte HMAC to <paramref name="destination"/>; the header
    /// length covers the attribute of <paramref name="tagLength"/> bytes.
    /// </summary>
    internal static void ComputeMessageIntegritySha256(
        ReadOnlySpan<byte> message,
        int start,
        ReadOnlySpan<byte> key,
        Span<byte> destination,
        int tagLength = 32
    )
    {
        byte[]? rented = start <= 512 ? null : ArrayPool<byte>.Shared.Rent(start);
        Span<byte> buffer = rented is null ? stackalloc byte[start] : rented.AsSpan(0, start);
        message[..start].CopyTo(buffer);
        BinaryPrimitives.WriteUInt16BigEndian(
            buffer[2..],
            (ushort)(start - StunHeader.Length + 4 + tagLength)
        );
        HMACSHA256.HashData(key, buffer, destination);
        if (rented is not null)
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Computes the FINGERPRINT value over <paramref name="precedingMessage"/> (the message up to the
    /// FINGERPRINT attribute) with the header length already set to include the 8-byte FINGERPRINT.
    /// </summary>
    internal static uint ComputeFingerprint(ReadOnlySpan<byte> precedingMessage)
    {
        // CRC over the message with the length field set to (bodySoFar + 8): the header with that length,
        // then the rest as it is, so nothing but the 20-byte header is copied.
        Span<byte> header = stackalloc byte[StunHeader.Length];
        precedingMessage[..StunHeader.Length].CopyTo(header);
        BinaryPrimitives.WriteUInt16BigEndian(
            header[2..],
            (ushort)(precedingMessage.Length - StunHeader.Length + 8)
        );
        Crc32 crc = t_crc ??= new Crc32();
        crc.Reset();
        crc.Append(header);
        crc.Append(precedingMessage[StunHeader.Length..]);
        return crc.GetCurrentHashAsUInt32() ^ StunHeader.FingerprintXor;
    }

    // RFC 8489 section 14.7's CRC-32 (IEEE 802.3), hardware-accelerated; one per thread, reset per message.
    [ThreadStatic]
    private static Crc32? t_crc;

    /// <summary>
    /// Computes MESSAGE-INTEGRITY (HMAC-SHA1) for the message whose MESSAGE-INTEGRITY attribute begins at
    /// <paramref name="miStart"/>, writing the 20-byte tag to <paramref name="destination"/>. The HMAC
    /// covers the message up to <paramref name="miStart"/> with the header length set to include the
    /// 24-byte attribute.
    /// </summary>
    internal static void ComputeMessageIntegrity(
        ReadOnlySpan<byte> message,
        int miStart,
        ReadOnlySpan<byte> key,
        Span<byte> destination
    )
    {
        // The HMAC needs the length-adjusted message in one piece: on the stack, or pooled when large.
        byte[]? rented = miStart <= 512 ? null : ArrayPool<byte>.Shared.Rent(miStart);
        Span<byte> buffer = rented is null ? stackalloc byte[miStart] : rented.AsSpan(0, miStart);
        message[..miStart].CopyTo(buffer);
        int adjustedBody = miStart - StunHeader.Length + 24;
        BinaryPrimitives.WriteUInt16BigEndian(buffer[2..], (ushort)adjustedBody);
        HMACSHA1.HashData(key, buffer, destination);
        if (rented is not null)
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }
}
