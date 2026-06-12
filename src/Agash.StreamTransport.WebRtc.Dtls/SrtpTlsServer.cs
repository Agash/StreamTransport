using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;

namespace Agash.StreamTransport.WebRtc.Dtls;

/// <summary>
/// The DTLS server side of the handshake: selects an AES-GCM <c>use_srtp</c> profile from the client's
/// offer, requests and records the client certificate fingerprint, presents the local certificate, and
/// exports the SRTP keying material on completion. DTLS 1.2, ECDHE_ECDSA + AES-GCM only.
/// </summary>
internal sealed class SrtpTlsServer(TlsCrypto crypto, DtlsCertificate certificate) : DefaultTlsServer(crypto)
{
    /// <summary>The selected SRTP protection profile (BouncyCastle code point).</summary>
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

    public override void ProcessClientExtensions(IDictionary<int, byte[]> clientExtensions)
    {
        base.ProcessClientExtensions(clientExtensions);
        UseSrtpData? srtp = TlsSrtpUtilities.GetUseSrtpExtension(clientExtensions);
        if (srtp is not null)
        {
            foreach (int offered in SrtpProfiles.Offered)
            {
                if (Array.IndexOf(srtp.ProtectionProfiles, offered) >= 0)
                {
                    SelectedProfile = offered;
                    break;
                }
            }
        }
    }

    public override IDictionary<int, byte[]> GetServerExtensions()
    {
        IDictionary<int, byte[]> extensions = base.GetServerExtensions() ?? new Dictionary<int, byte[]>();
        TlsSrtpUtilities.AddUseSrtpExtension(extensions, new UseSrtpData([SelectedProfile], TlsUtilities.EmptyBytes));
        return extensions;
    }

    public override CertificateRequest GetCertificateRequest()
    {
        short[] certificateTypes = [ClientCertificateType.ecdsa_sign];
        IList<SignatureAndHashAlgorithm>? signatureAlgorithms = null;
        if (TlsUtilities.IsSignatureAlgorithmsExtensionAllowed(m_context.ServerVersion))
        {
            signatureAlgorithms = TlsUtilities.GetDefaultSupportedSignatureAlgorithms(m_context);
        }

        return new CertificateRequest(certificateTypes, signatureAlgorithms, null);
    }

    public override void NotifyClientCertificate(Certificate clientCertificate)
    {
        TlsCertificate[] chain = clientCertificate.GetCertificateList();
        if (chain.Length > 0)
        {
            RemoteFingerprint = CertificateFingerprint.Sha256(chain[0].GetEncoded());
        }
    }

    protected override TlsCredentialedSigner GetECDsaSignerCredentials()
    {
        var algorithm = new SignatureAndHashAlgorithm(HashAlgorithm.sha256, SignatureAlgorithm.ecdsa);
        return new BcDefaultTlsCredentialedSigner(
            new TlsCryptoParameters(m_context),
            (BcTlsCrypto)m_context.Crypto,
            certificate.PrivateKey,
            certificate.Certificate,
            algorithm);
    }

    public override void NotifyHandshakeComplete()
    {
        base.NotifyHandshakeComplete();
        KeyingMaterial = m_context.ExportKeyingMaterial(ExporterLabel.dtls_srtp, null, SrtpProfiles.KeyingMaterialLength(SelectedProfile));
    }
}
