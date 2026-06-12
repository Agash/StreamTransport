using System.Security.Cryptography;

namespace Agash.StreamTransport.WebRtc;

/// <summary>
/// A certificate fingerprint carried in the SDP <c>a=fingerprint</c> attribute (RFC 8122). The peer's
/// DTLS certificate is bound to the signalled identity by comparing its hash to this value, which is how
/// DTLS-SRTP authenticates the media path without a PKI.
/// </summary>
/// <param name="Algorithm">The hash algorithm name as it appears in SDP, e.g. <c>sha-256</c>.</param>
/// <param name="Hash">The raw hash bytes of the DER-encoded certificate.</param>
public readonly record struct DtlsFingerprint(string Algorithm, ReadOnlyMemory<byte> Hash)
{
    /// <summary>
    /// Formats the fingerprint as the colon-separated uppercase hex used in SDP, e.g.
    /// <c>sha-256 AB:CD:...</c>.
    /// </summary>
    public string ToSdpValue()
    {
        ReadOnlySpan<byte> hash = Hash.Span;
        if (hash.IsEmpty)
        {
            return Algorithm;
        }

        // "alg " + N bytes as "XX" + (N-1) ':' separators.
        return string.Create(
            Algorithm.Length + 1 + (hash.Length * 3) - 1,
            (Algorithm, Hash),
            static (dst, state) =>
            {
                ReadOnlySpan<byte> bytes = state.Hash.Span;
                state.Algorithm.CopyTo(dst);
                int pos = state.Algorithm.Length;
                dst[pos++] = ' ';
                for (int i = 0; i < bytes.Length; i++)
                {
                    if (i > 0)
                    {
                        dst[pos++] = ':';
                    }

                    bytes[i].TryFormat(dst[pos..], out _, "X2");
                    pos += 2;
                }
            });
    }
}

/// <summary>
/// The keying material a completed DTLS-SRTP handshake exports (RFC 5764 §4.2) for the SRTP transforms:
/// the negotiated profile plus the client and server master keys and salts. The layout (key/salt lengths)
/// is determined by <see cref="Profile"/>.
/// </summary>
/// <remarks>
/// This is the single value the <c>WebRtc.Dtls</c> package hands back to the core SRTP layer. The core
/// never sees BouncyCastle; it sees only this record. Treat the key/salt memory as secret.
/// </remarks>
/// <param name="Profile">The negotiated SRTP protection profile.</param>
/// <param name="ClientMasterKey">SRTP master key for traffic sent by the DTLS client.</param>
/// <param name="ClientMasterSalt">SRTP master salt for traffic sent by the DTLS client.</param>
/// <param name="ServerMasterKey">SRTP master key for traffic sent by the DTLS server.</param>
/// <param name="ServerMasterSalt">SRTP master salt for traffic sent by the DTLS server.</param>
public readonly record struct SrtpKeyingMaterial(
    SrtpProtectionProfile Profile,
    ReadOnlyMemory<byte> ClientMasterKey,
    ReadOnlyMemory<byte> ClientMasterSalt,
    ReadOnlyMemory<byte> ServerMasterKey,
    ReadOnlyMemory<byte> ServerMasterSalt);

/// <summary>
/// Creates a <see cref="DtlsFingerprint"/> over the supplied DER-encoded certificate using SHA-256, the
/// algorithm WebRTC mandates for new sessions.
/// </summary>
public static class CertificateFingerprint
{
    /// <summary>Computes the <c>sha-256</c> fingerprint of a DER-encoded certificate.</summary>
    public static DtlsFingerprint Sha256(ReadOnlySpan<byte> derEncodedCertificate)
    {
        byte[] hash = SHA256.HashData(derEncodedCertificate);
        return new DtlsFingerprint("sha-256", hash);
    }
}
