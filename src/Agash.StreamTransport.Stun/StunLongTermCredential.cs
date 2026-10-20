using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Agash.StreamTransport.Stun;

/// <summary>The password algorithms a long-term key comes from (RFC 8489 section 18.5).</summary>
public enum StunPasswordAlgorithm : ushort
{
    /// <summary>MD5, for servers that list no algorithms (section 18.5.1.1).</summary>
    Md5 = 0x0001,

    /// <summary>SHA-256 (section 18.5.1.2).</summary>
    Sha256 = 0x0002,
}

/// <summary>
/// The security features a server announces in its nonce cookie (RFC 8489 sections 9.2 and 18.1), which a
/// client checks so they cannot be stripped on the way (section 9.2.1).
/// </summary>
/// <param name="PasswordAlgorithms">Bit 0: the server lists the password algorithms it takes.</param>
/// <param name="UsernameAnonymity">Bit 1: the request carries USERHASH in place of USERNAME.</param>
public readonly record struct StunSecurityFeatures(bool PasswordAlgorithms, bool UsernameAnonymity);

/// <summary>
/// The long-term credential mechanism's pieces (RFC 8489 section 9.2): the nonce cookie, the keys of the
/// password algorithms, USERHASH, and the PASSWORD-ALGORITHM(S) values.
/// </summary>
public static class StunLongTermCredential
{
    // Section 9.2: "obMatJos2" and the base64 of the 24 feature bits.
    private const string CookiePrefix = "obMatJos2";

    /// <summary>Reads the nonce cookie at the start of a NONCE value.</summary>
    /// <param name="nonce">The NONCE value.</param>
    /// <param name="features">The features it announces.</param>
    /// <returns>Whether the nonce starts with a cookie.</returns>
    public static bool TryReadNonceCookie(
        ReadOnlySpan<byte> nonce,
        out StunSecurityFeatures features
    )
    {
        features = default;
        Span<byte> bits = stackalloc byte[3];
        if (
            nonce.Length < CookiePrefix.Length + 4
            || !nonce.StartsWith("obMatJos2"u8)
            || !Convert.TryFromBase64Chars(
                Encoding.ASCII.GetString(nonce.Slice(CookiePrefix.Length, 4)),
                bits,
                out int written
            )
            || written != 3
        )
        {
            return false;
        }

        // Bit 0 is the most significant bit of the first byte (section 18.1).
        features = new StunSecurityFeatures((bits[0] & 0x80) != 0, (bits[0] & 0x40) != 0);
        return true;
    }

    /// <summary>The algorithms a PASSWORD-ALGORITHMS value lists, in its order (section 14.11).</summary>
    /// <param name="value">The value.</param>
    /// <returns>The algorithm numbers; unknown ones included.</returns>
    public static List<ushort> ReadAlgorithms(ReadOnlySpan<byte> value)
    {
        List<ushort> algorithms = [];
        int at = 0;
        while (at + 4 <= value.Length)
        {
            algorithms.Add(BinaryPrimitives.ReadUInt16BigEndian(value[at..]));
            int parameters = BinaryPrimitives.ReadUInt16BigEndian(value[(at + 2)..]);
            at += 4 + ((parameters + 3) & ~3);
        }

        return algorithms;
    }

    /// <summary>A PASSWORD-ALGORITHM value naming an algorithm without parameters (section 14.12).</summary>
    /// <param name="algorithm">The algorithm.</param>
    /// <param name="destination">Four bytes.</param>
    public static void WriteAlgorithm(StunPasswordAlgorithm algorithm, Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt16BigEndian(destination, (ushort)algorithm);
        BinaryPrimitives.WriteUInt16BigEndian(destination[2..], 0);
    }

    /// <summary>
    /// The long-term key: the algorithm's hash of username ":" OpaqueString(realm) ":"
    /// OpaqueString(password) (sections 9.2.2 and 18.5.1).
    /// </summary>
    /// <param name="algorithm">The algorithm.</param>
    /// <param name="username">The username.</param>
    /// <param name="realm">The REALM value.</param>
    /// <param name="password">The password.</param>
    /// <returns>The 16- or 32-byte key.</returns>
    public static byte[] Key(
        StunPasswordAlgorithm algorithm,
        string username,
        ReadOnlySpan<byte> realm,
        string password
    )
    {
        byte[] input = Encoding.UTF8.GetBytes(
            $"{username}:{OpaqueString(Encoding.UTF8.GetString(realm))}:{OpaqueString(password)}"
        );
#pragma warning disable CA5351 // RFC 8489 section 18.5.1.1: MD5 is the algorithm for servers that list none.
        return algorithm == StunPasswordAlgorithm.Sha256
            ? SHA256.HashData(input)
            : MD5.HashData(input);
#pragma warning restore CA5351
    }

    /// <summary>
    /// The USERHASH value: SHA-256(OpaqueString(username) ":" OpaqueString(realm)) (section 14.4).
    /// </summary>
    /// <param name="username">The username.</param>
    /// <param name="realm">The REALM value.</param>
    /// <param name="destination">32 bytes.</param>
    public static void Userhash(
        string username,
        ReadOnlySpan<byte> realm,
        Span<byte> destination
    ) =>
        SHA256.HashData(
            Encoding.UTF8.GetBytes(
                $"{OpaqueString(username)}:{OpaqueString(Encoding.UTF8.GetString(realm))}"
            ),
            destination
        );

    // RFC 8265 section 4.2 (OpaqueString): non-ASCII spaces become ASCII spaces, then NFC.
    private static string OpaqueString(string value)
    {
        StringBuilder? mapped = null;
        for (int i = 0; i < value.Length; i++)
        {
            if (
                value[i] != ' '
                && CharUnicodeInfo.GetUnicodeCategory(value[i]) == UnicodeCategory.SpaceSeparator
            )
            {
                mapped ??= new StringBuilder(value);
                mapped[i] = ' ';
            }
        }

        return (mapped?.ToString() ?? value).Normalize(NormalizationForm.FormC);
    }
}
