using BcSrtp = Org.BouncyCastle.Tls.SrtpProtectionProfile;

namespace Agash.StreamTransport.WebRtc.Dtls;

/// <summary>
/// Maps between the BouncyCastle <c>use_srtp</c> protection-profile code points and the stack's
/// <see cref="SrtpProtectionProfile"/>, and supplies the SRTP key/salt lengths that drive keying-material
/// extraction (RFC 5764 §4.1.2 / RFC 7714 §12).
/// </summary>
internal static class SrtpProfiles
{
    /// <summary>Profiles we offer, most preferred first: AES-256-GCM then AES-128-GCM (AEAD only).</summary>
    public static readonly int[] Offered = [BcSrtp.SRTP_AEAD_AES_256_GCM, BcSrtp.SRTP_AEAD_AES_128_GCM];

    /// <summary>The (key, salt) octet lengths for a profile.</summary>
    public static (int Key, int Salt) Lengths(int bcProfile) => bcProfile switch
    {
        BcSrtp.SRTP_AEAD_AES_128_GCM => (16, 12),
        BcSrtp.SRTP_AEAD_AES_256_GCM => (32, 12),
        _ => throw new NotSupportedException($"Unsupported SRTP protection profile 0x{bcProfile:X4}."),
    };

    /// <summary>The keying material length to export: client+server keys and salts (RFC 5764 §4.2).</summary>
    public static int KeyingMaterialLength(int bcProfile)
    {
        (int key, int salt) = Lengths(bcProfile);
        return (2 * key) + (2 * salt);
    }

    /// <summary>Translates a BouncyCastle profile code point to the abstraction enum.</summary>
    public static SrtpProtectionProfile ToProfile(int bcProfile) => bcProfile switch
    {
        BcSrtp.SRTP_AEAD_AES_128_GCM => SrtpProtectionProfile.AeadAes128Gcm,
        BcSrtp.SRTP_AEAD_AES_256_GCM => SrtpProtectionProfile.AeadAes256Gcm,
        _ => throw new NotSupportedException($"Unsupported SRTP protection profile 0x{bcProfile:X4}."),
    };

    /// <summary>
    /// Splits exported DTLS-SRTP keying material into the client/server master keys and salts, the layout
    /// of RFC 5764 §4.2: <c>clientKey ‖ serverKey ‖ clientSalt ‖ serverSalt</c>.
    /// </summary>
    public static SrtpKeyingMaterial Split(int bcProfile, ReadOnlySpan<byte> keyingMaterial)
    {
        (int key, int salt) = Lengths(bcProfile);
        int o = 0;
        byte[] clientKey = keyingMaterial.Slice(o, key).ToArray(); o += key;
        byte[] serverKey = keyingMaterial.Slice(o, key).ToArray(); o += key;
        byte[] clientSalt = keyingMaterial.Slice(o, salt).ToArray(); o += salt;
        byte[] serverSalt = keyingMaterial.Slice(o, salt).ToArray();
        return new SrtpKeyingMaterial(ToProfile(bcProfile), clientKey, clientSalt, serverKey, serverSalt);
    }
}
