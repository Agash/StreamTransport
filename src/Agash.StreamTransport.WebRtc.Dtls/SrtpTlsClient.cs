using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;

namespace Agash.StreamTransport.WebRtc.Dtls;

/// <summary>
/// The DTLS client side of the handshake: offers the <c>use_srtp</c> AES-GCM profiles, presents the
/// local certificate, captures the peer certificate fingerprint, and exports the SRTP keying material
/// on completion. DTLS 1.2, ECDHE_ECDSA + AES-GCM only.
/// </summary>
internal sealed class SrtpTlsClient(TlsCrypto crypto, DtlsCertificate certificate) : DefaultTlsClient(crypto)
{
    private readonly DtlsCertificate _certificate = certificate;

    /// <summary>The selected SRTP protection profile (BouncyCastle code point), set during the handshake.</summary>
    public int SelectedProfile { get; private set; }

    /// <summary>The peer certificate fingerprint, captured during the handshake.</summary>
    public DtlsFingerprint? RemoteFingerprint { get; private set; }

    /// <summary>The exported SRTP keying material, available after <see cref="NotifyHandshakeComplete"/>.</summary>
    public byte[]? KeyingMaterial { get; private set; }

    protected override ProtocolVersion[] GetSupportedVersions() => ProtocolVersion.DTLSv12.Only();

    protected override int[] GetSupportedCipherSuites() =>
    [
        CipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256,
        CipherSuite.TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384,
    ];

    public override IDictionary<int, byte[]> GetClientExtensions()
    {
        IDictionary<int, byte[]> extensions = base.GetClientExtensions() ?? new Dictionary<int, byte[]>();
        TlsSrtpUtilities.AddUseSrtpExtension(extensions, new UseSrtpData(SrtpProfiles.Offered, TlsUtilities.EmptyBytes));
        return extensions;
    }

    public override void ProcessServerExtensions(IDictionary<int, byte[]> serverExtensions)
    {
        base.ProcessServerExtensions(serverExtensions);
        UseSrtpData? srtp = TlsSrtpUtilities.GetUseSrtpExtension(serverExtensions);
        if (srtp is { ProtectionProfiles.Length: 1 })
        {
            SelectedProfile = srtp.ProtectionProfiles[0];
        }
    }

    public override TlsAuthentication GetAuthentication() => new FingerprintAuthentication(this);

    public override void NotifyHandshakeComplete()
    {
        base.NotifyHandshakeComplete();
        KeyingMaterial = m_context.ExportKeyingMaterial(ExporterLabel.dtls_srtp, null, SrtpProfiles.KeyingMaterialLength(SelectedProfile));
    }

    private sealed class FingerprintAuthentication(SrtpTlsClient client) : TlsAuthentication
    {
        public void NotifyServerCertificate(TlsServerCertificate serverCertificate)
        {
            Certificate chain = serverCertificate.Certificate;
            if (!chain.IsEmpty)
            {
                client.RemoteFingerprint = CertificateFingerprint.Sha256(chain.GetCertificateAt(0).GetEncoded());
            }
        }

        public TlsCredentials GetClientCredentials(CertificateRequest certificateRequest)
        {
            var algorithm = new SignatureAndHashAlgorithm(HashAlgorithm.sha256, SignatureAlgorithm.ecdsa);
            return new BcDefaultTlsCredentialedSigner(
                new TlsCryptoParameters(client.m_context),
                (BcTlsCrypto)client.m_context.Crypto,
                client._certificate.PrivateKey,
                client._certificate.Certificate,
                algorithm);
        }
    }
}
