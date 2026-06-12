using System.Security.Cryptography;

namespace Agash.StreamTransport.WebRtc.Ice;

/// <summary>
/// An ICE agent's username fragment and password (RFC 8445 §5.3), carried in SDP as <c>a=ice-ufrag</c> /
/// <c>a=ice-pwd</c>. The password keys the MESSAGE-INTEGRITY of connectivity checks.
/// </summary>
/// <param name="UsernameFragment">The ufrag (≥ 24 bits of entropy; RFC 8445 mandates unguessable values).</param>
/// <param name="Password">The password (≥ 128 bits of entropy).</param>
public readonly record struct IceCredentials(string UsernameFragment, string Password)
{
    /// <summary>
    /// Generates fresh credentials with the RFC-mandated entropy (ufrag ≥ 24 bits, password ≥ 128 bits),
    /// using only ICE-permitted characters (RFC 8445 ICE-char: ALPHA / DIGIT / '+' / '/').
    /// </summary>
    public static IceCredentials Generate()
        => new(RandomIceString(4), RandomIceString(22));

    /// <summary>The USERNAME attribute value for a check this agent sends: <c>remoteUfrag:localUfrag</c>.</summary>
    public static string CheckUsername(string remoteUfrag, string localUfrag) => $"{remoteUfrag}:{localUfrag}";

    private static string RandomIceString(int length)
    {
        // base64 yields ICE-chars (ALPHA/DIGIT/'+'/'/'); 22 chars ≈ 132 bits, 4 chars ≈ 24 bits.
        Span<byte> bytes = stackalloc byte[(length * 3 + 3) / 4];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)[..length];
    }
}
