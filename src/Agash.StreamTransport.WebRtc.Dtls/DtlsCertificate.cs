using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Asn1.X9;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;
using Org.BouncyCastle.X509;

namespace Agash.StreamTransport.WebRtc.Dtls;

/// <summary>
/// A self-signed ECDSA (P-256) certificate and its private key, used as the DTLS identity. The peer is
/// authenticated by comparing this certificate's SHA-256 fingerprint (carried in SDP, RFC 8122) rather
/// than by a PKI, so the certificate is ephemeral and unsigned by any authority.
/// </summary>
internal sealed class DtlsCertificate
{
    private DtlsCertificate(Certificate certificate, AsymmetricKeyParameter privateKey, DtlsFingerprint fingerprint)
    {
        Certificate = certificate;
        PrivateKey = privateKey;
        Fingerprint = fingerprint;
    }

    /// <summary>The BouncyCastle TLS certificate chain (a single self-signed certificate).</summary>
    public Certificate Certificate { get; }

    /// <summary>The certificate's private key.</summary>
    public AsymmetricKeyParameter PrivateKey { get; }

    /// <summary>The SHA-256 fingerprint to advertise in SDP.</summary>
    public DtlsFingerprint Fingerprint { get; }

    /// <summary>Generates a fresh self-signed P-256 ECDSA certificate.</summary>
    public static DtlsCertificate CreateSelfSigned()
    {
        var random = new SecureRandom();

        var domain = ECNamedCurveTable.GetByName("secp256r1");
        var domainParameters = new ECNamedDomainParameters(
            ECNamedCurveTable.GetOid("secp256r1"), domain.Curve, domain.G, domain.N, domain.H, domain.GetSeed());

        var generator = new ECKeyPairGenerator("EC");
        generator.Init(new ECKeyGenerationParameters(domainParameters, random));
        AsymmetricCipherKeyPair keyPair = generator.GenerateKeyPair();

        var name = new X509Name("CN=StreamTransport");
        var certGenerator = new X509V3CertificateGenerator();
        certGenerator.SetIssuerDN(name);
        certGenerator.SetSubjectDN(name);
        certGenerator.SetPublicKey(keyPair.Public);
        certGenerator.SetNotBefore(DateTime.UtcNow.AddMinutes(-5));
        certGenerator.SetNotAfter(DateTime.UtcNow.AddDays(30));

        byte[] serial = new byte[16];
        random.NextBytes(serial);
        serial[0] = 1; // keep it positive
        certGenerator.SetSerialNumber(new BigInteger(serial));

        ISignatureFactory signatureFactory = new Asn1SignatureFactory("SHA256WITHECDSA", keyPair.Private, random);
        X509Certificate x509 = certGenerator.Generate(signatureFactory);
        byte[] der = x509.GetEncoded();

        var crypto = new BcTlsCrypto();
        var tlsCertificate = crypto.CreateCertificate(der);
        var certificate = new Certificate(null, [new CertificateEntry(tlsCertificate, null)]);

        return new DtlsCertificate(certificate, keyPair.Private, CertificateFingerprint.Sha256(der));
    }
}
