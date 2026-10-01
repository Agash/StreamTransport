using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace Agash.StreamTransport.WebRtc.Stun;

/// <summary>
/// Builds a STUN message in place into a caller-supplied buffer (RFC 8489 §5-6), allocation-free. Write
/// the header via the constructor, append attributes in order, then call MESSAGE-INTEGRITY and/or
/// FINGERPRINT last (they cover everything before them, so order matters), and read <see cref="Length"/>.
/// </summary>
public ref struct StunMessageWriter
{
    private readonly Span<byte> _buffer;

    /// <summary>
    /// Writes the 20-byte STUN header into <paramref name="buffer"/> with a zero body length, which is
    /// kept current as attributes are appended.
    /// </summary>
    public StunMessageWriter(
        Span<byte> buffer,
        StunMessageClass messageClass,
        StunMethod method,
        ReadOnlySpan<byte> transactionId
    )
    {
        if (buffer.Length < StunHeader.Length)
        {
            throw new ArgumentException("Buffer too small for the STUN header.", nameof(buffer));
        }

        if (transactionId.Length != StunHeader.TransactionIdLength)
        {
            throw new ArgumentException("Transaction id must be 12 bytes.", nameof(transactionId));
        }

        _buffer = buffer;
        BinaryPrimitives.WriteUInt16BigEndian(buffer, StunHeader.EncodeType(messageClass, method));
        BinaryPrimitives.WriteUInt16BigEndian(buffer[2..], 0);
        BinaryPrimitives.WriteUInt32BigEndian(buffer[4..], StunHeader.MagicCookie);
        transactionId.CopyTo(buffer[8..]);
        Length = StunHeader.Length;
    }

    /// <summary>The total length of the message written so far, including the header.</summary>
    public int Length { get; private set; }

    /// <summary>Appends a raw attribute, padding the value to a 4-byte boundary (RFC 8489 §14).</summary>
    public void AddAttribute(StunAttributeType type, scoped ReadOnlySpan<byte> value)
    {
        int padded = (value.Length + 3) & ~3;
        EnsureCapacity(4 + padded);

        Span<byte> dst = _buffer[Length..];
        BinaryPrimitives.WriteUInt16BigEndian(dst, (ushort)type);
        BinaryPrimitives.WriteUInt16BigEndian(dst[2..], (ushort)value.Length);
        value.CopyTo(dst[4..]);
        dst.Slice(4 + value.Length, padded - value.Length).Clear();

        Advance(4 + padded);
    }

    /// <summary>Appends an XOR-MAPPED-ADDRESS attribute (RFC 8489 §14.2) for the given endpoint.</summary>
    public void AddXorMappedAddress(IPEndPoint endpoint) =>
        AddXorAddress(StunAttributeType.XorMappedAddress, endpoint);

    /// <summary>
    /// Appends an XOR-encoded address attribute: XOR-MAPPED-ADDRESS, or TURN's XOR-PEER-ADDRESS and
    /// XOR-RELAYED-ADDRESS, which share its encoding (RFC 8489 §14.2).
    /// </summary>
    public void AddXorAddress(StunAttributeType type, IPEndPoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        bool ipv6 = endpoint.AddressFamily == AddressFamily.InterNetworkV6;
        int valueLength = ipv6 ? 20 : 8;
        Span<byte> value = stackalloc byte[20];
        value[0] = 0;
        value[1] = (byte)(ipv6 ? 0x02 : 0x01);

        ushort xPort = (ushort)(endpoint.Port ^ (ushort)(StunHeader.MagicCookie >> 16));
        BinaryPrimitives.WriteUInt16BigEndian(value[2..], xPort);

        Span<byte> addr = stackalloc byte[16];
        endpoint.Address.TryWriteBytes(addr, out _);

        if (ipv6)
        {
            Span<byte> mask = stackalloc byte[16];
            BinaryPrimitives.WriteUInt32BigEndian(mask, StunHeader.MagicCookie);
            _buffer.Slice(8, StunHeader.TransactionIdLength).CopyTo(mask[4..]);
            for (int i = 0; i < 16; i++)
            {
                value[4 + i] = (byte)(addr[i] ^ mask[i]);
            }
        }
        else
        {
            uint a = BinaryPrimitives.ReadUInt32BigEndian(addr[..4]);
            BinaryPrimitives.WriteUInt32BigEndian(value[4..], a ^ StunHeader.MagicCookie);
        }

        AddAttribute(type, value[..valueLength]);
    }

    /// <summary>Appends ERROR-CODE (RFC 8489 §14.8) with a reason phrase.</summary>
    public void AddErrorCode(int code, string reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        Span<byte> value = stackalloc byte[4 + System.Text.Encoding.UTF8.GetMaxByteCount(reason.Length)];
        value[..2].Clear();
        value[2] = (byte)(code / 100);
        value[3] = (byte)(code % 100);
        int length = System.Text.Encoding.UTF8.GetBytes(reason, value[4..]);
        AddAttribute(StunAttributeType.ErrorCode, value[..(4 + length)]);
    }

    /// <summary>Appends a 32-bit big-endian attribute value, such as LIFETIME.</summary>
    public void AddUInt32(StunAttributeType type, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        AddAttribute(type, bytes);
    }

    /// <summary>
    /// Appends MESSAGE-INTEGRITY (RFC 8489 §14.5): HMAC-SHA1 over everything written so far, keyed by
    /// <paramref name="key"/>. Append this after all protected attributes and before FINGERPRINT.
    /// </summary>
    public void AddMessageIntegrity(ReadOnlySpan<byte> key)
    {
        EnsureCapacity(24);
        Span<byte> tag = stackalloc byte[20];
        StunMessageReader.ComputeMessageIntegrity(_buffer[..Length], Length, key, tag);
        AddAttribute(StunAttributeType.MessageIntegrity, tag);
    }

    /// <summary>
    /// Appends FINGERPRINT (RFC 8489 §14.7): CRC-32 of everything written so far, XORed with the STUN
    /// constant. Must be the last attribute.
    /// </summary>
    public void AddFingerprint()
    {
        EnsureCapacity(8);
        uint crc = StunMessageReader.ComputeFingerprint(_buffer[..Length]);
        Span<byte> value = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(value, crc);
        AddAttribute(StunAttributeType.Fingerprint, value);
    }

    private readonly void EnsureCapacity(int additional)
    {
        if (Length + additional > _buffer.Length)
        {
            throw new InvalidOperationException("STUN message exceeds the destination buffer.");
        }
    }

    private void Advance(int written)
    {
        Length += written;
        BinaryPrimitives.WriteUInt16BigEndian(_buffer[2..], (ushort)(Length - StunHeader.Length));
    }
}
