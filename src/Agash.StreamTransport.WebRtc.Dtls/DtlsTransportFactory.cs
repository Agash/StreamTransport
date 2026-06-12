namespace Agash.StreamTransport.WebRtc.Dtls;

/// <summary>
/// The BouncyCastle implementation of <see cref="IDtlsTransportFactory"/>. It owns one self-signed
/// certificate (so <see cref="LocalFingerprint"/> is stable) and creates a DTLS transport per peer
/// connection. This is the type a consumer registers to plug DTLS-SRTP into the WebRTC core.
/// </summary>
public sealed class DtlsTransportFactory : IDtlsTransportFactory
{
    private readonly DtlsCertificate _certificate;

    /// <summary>Creates a factory with a freshly generated self-signed ECDSA certificate.</summary>
    public DtlsTransportFactory() => _certificate = DtlsCertificate.CreateSelfSigned();

    /// <inheritdoc/>
    public DtlsFingerprint LocalFingerprint => _certificate.Fingerprint;

    /// <inheritdoc/>
    public IDtlsTransport Create(DtlsRole role, DtlsRecordSender send) =>
        new BouncyCastleDtlsTransport(role, send, _certificate);
}
