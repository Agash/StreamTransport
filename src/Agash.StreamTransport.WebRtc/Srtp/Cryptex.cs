using System.Buffers.Binary;

namespace Agash.StreamTransport.WebRtc.Srtp;

/// <summary>
/// Cryptex (RFC 9335): SRTP that encrypts the CSRCs and the RTP header extensions along with the payload,
/// marking the extension block 0xC0DE (one-byte form) or 0xC2DE (two-byte form) in place of RFC 8285's
/// 0xBEDE and 0x100X. The encrypted portion is the CSRCs, the extension data after its 4-byte header, and
/// the payload; the fixed header and the extension header stay in the clear (section 6.1).
/// </summary>
/// <remarks>
/// Section 6.2's rearrangement makes both portions contiguous in place: the extension header moves in
/// front of the CSRCs, so the first 16 bytes are the clear (and, for AEAD, associated) data and the rest
/// the encrypted portion; afterwards it moves back. Without CSRCs nothing moves.
/// </remarks>
internal static class Cryptex
{
    /// <summary>The marking of an encrypted one-byte extension block.</summary>
    public const ushort OneByteProfile = 0xC0DE;

    /// <summary>The marking of an encrypted two-byte extension block.</summary>
    public const ushort TwoByteProfile = 0xC2DE;

    /// <summary>The clear bytes in front of the encrypted portion once rearranged.</summary>
    public const int ClearLength = 16;

    private const ushort RfcOneByteProfile = 0xBEDE;
    private const ushort RfcTwoByteProfile = 0x1000;

    /// <summary>
    /// Whether a packet about to be sent can go as Cryptex: it has an RFC 8285 extension block. One without
    /// a block goes as plain SRTP, which a sender may choose packet by packet (section 4); this endpoint
    /// never sends CSRCs, so no block has to be added for them.
    /// </summary>
    /// <param name="packet">The RTP packet.</param>
    /// <param name="length">Its length.</param>
    /// <returns>Whether it can.</returns>
    public static bool CanProtect(ReadOnlySpan<byte> packet, int length)
    {
        int at = ExtensionHeaderAt(packet);
        if ((packet[0] & 0x10) == 0 || at + 4 > length)
        {
            return false;
        }

        ushort profile = BinaryPrimitives.ReadUInt16BigEndian(packet[at..]);
        return profile == RfcOneByteProfile || (profile & 0xFFF0) == RfcTwoByteProfile;
    }

    /// <summary>Whether a received packet was sent as Cryptex.</summary>
    /// <param name="packet">The protected RTP packet.</param>
    /// <param name="length">Its length.</param>
    /// <returns>Whether its extension block is marked 0xC0DE or 0xC2DE.</returns>
    public static bool IsProtected(ReadOnlySpan<byte> packet, int length)
    {
        int at = ExtensionHeaderAt(packet);
        if ((packet[0] & 0x10) == 0 || at + 4 > length)
        {
            return false;
        }

        ushort profile = BinaryPrimitives.ReadUInt16BigEndian(packet[at..]);
        return profile is OneByteProfile or TwoByteProfile;
    }

    /// <summary>
    /// Marks the block as Cryptex and moves its header in front of the CSRCs: the first
    /// <see cref="ClearLength"/> bytes are then the clear data, the rest up to the length the plaintext.
    /// </summary>
    /// <param name="packet">The packet, which <see cref="CanProtect"/> accepted.</param>
    public static void BeginProtect(Span<byte> packet)
    {
        int at = ExtensionHeaderAt(packet);
        bool twoByte =
            (BinaryPrimitives.ReadUInt16BigEndian(packet[at..]) & 0xFFF0) == RfcTwoByteProfile;
        BinaryPrimitives.WriteUInt16BigEndian(
            packet[at..],
            twoByte ? TwoByteProfile : OneByteProfile
        );
        HeaderToFront(packet, at);
    }

    /// <summary>Moves a Cryptex packet's extension header back behind its CSRCs.</summary>
    /// <param name="packet">The packet.</param>
    public static void EndProtect(Span<byte> packet) =>
        HeaderBack(packet, ExtensionHeaderAt(packet));

    /// <summary>
    /// Moves a received Cryptex packet's extension header in front of its CSRCs, as
    /// <see cref="BeginProtect"/> left it for encryption.
    /// </summary>
    /// <param name="packet">The packet, which <see cref="IsProtected"/> accepted.</param>
    public static void BeginUnprotect(Span<byte> packet) =>
        HeaderToFront(packet, ExtensionHeaderAt(packet));

    /// <summary>
    /// Moves the extension header back and marks the block as RFC 8285's again, so the packet reads as any
    /// other (section 5.2).
    /// </summary>
    /// <param name="packet">The decrypted packet.</param>
    public static void EndUnprotect(Span<byte> packet)
    {
        int at = ExtensionHeaderAt(packet);
        HeaderBack(packet, at);
        bool twoByte = BinaryPrimitives.ReadUInt16BigEndian(packet[at..]) == TwoByteProfile;
        BinaryPrimitives.WriteUInt16BigEndian(
            packet[at..],
            twoByte ? RfcTwoByteProfile : RfcOneByteProfile
        );
    }

    private static int ExtensionHeaderAt(ReadOnlySpan<byte> packet) =>
        12 + ((packet[0] & 0x0F) * 4);

    private static void HeaderToFront(Span<byte> packet, int at)
    {
        if (at == 12)
        {
            return;
        }

        Span<byte> header = stackalloc byte[4];
        packet.Slice(at, 4).CopyTo(header);
        packet[12..at].CopyTo(packet[16..]);
        header.CopyTo(packet[12..]);
    }

    private static void HeaderBack(Span<byte> packet, int at)
    {
        if (at == 12)
        {
            return;
        }

        Span<byte> header = stackalloc byte[4];
        packet.Slice(12, 4).CopyTo(header);
        packet[16..(at + 4)].CopyTo(packet[12..]);
        header.CopyTo(packet[at..]);
    }
}
