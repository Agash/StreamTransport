using System.Security.Cryptography.X509Certificates;
using Dtls.Core;

namespace Agash.StreamTransport.WebRtc;

/// <summary>
/// The certificate a peer connection authenticates its DTLS handshakes with. WebRTC certificates are
/// self-signed: the peer trusts the fingerprint carried in the signalled description, not a chain.
/// One certificate is usually shared by every connection an endpoint makes.
/// </summary>
public sealed class RtcCertificate : IDisposable
{
    /// <summary>Wraps a certificate that has its private key.</summary>
    /// <param name="certificate">The certificate; disposed with this instance.</param>
    /// <exception cref="ArgumentException">The certificate has no private key.</exception>
    public RtcCertificate(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        if (!certificate.HasPrivateKey)
        {
            throw new ArgumentException(
                "A DTLS certificate needs its private key.",
                nameof(certificate)
            );
        }

        Certificate = certificate;
        Fingerprint = DtlsFingerprint.Compute(certificate);
    }

    /// <summary>The certificate.</summary>
    public X509Certificate2 Certificate { get; }

    /// <summary>Its SHA-256 fingerprint, advertised in offers and answers.</summary>
    public DtlsFingerprint Fingerprint { get; }

    /// <summary>Generates a self-signed certificate.</summary>
    /// <param name="keyType">The key; ECDSA P-256 by default, which every WebRTC stack accepts.</param>
    /// <returns>The certificate.</returns>
    public static RtcCertificate Generate(DtlsKeyType keyType = DtlsKeyType.EcdsaP256) =>
        new(DtlsCertificates.CreateSelfSigned(keyType));

    /// <inheritdoc/>
    public void Dispose() => Certificate.Dispose();
}
