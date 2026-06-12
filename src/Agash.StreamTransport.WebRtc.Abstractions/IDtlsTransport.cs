namespace Agash.StreamTransport.WebRtc;

/// <summary>
/// Sends one DTLS record datagram to the peer. The implementation typically forwards it over the ICE
/// transport's selected pair. Fire-and-forget: DTLS handles its own retransmission.
/// </summary>
public delegate void DtlsRecordSender(ReadOnlyMemory<byte> record);

/// <summary>
/// A DTLS-SRTP transport (RFC 5764): it runs the DTLS 1.2 handshake over a caller-supplied datagram path,
/// authenticates the peer by certificate fingerprint, and exports the SRTP keying material. This is the
/// seam that quarantines the DTLS implementation (and its third-party crypto) from the WebRTC core — the
/// core depends only on this interface.
/// </summary>
public interface IDtlsTransport : IAsyncDisposable
{
    /// <summary>Whether this endpoint is the DTLS client (active) or server (passive).</summary>
    DtlsRole Role { get; }

    /// <summary>The fingerprint of this endpoint's certificate, to advertise in the local SDP.</summary>
    DtlsFingerprint LocalFingerprint { get; }

    /// <summary>The peer certificate's fingerprint, available once the handshake completes (for verification).</summary>
    DtlsFingerprint? RemoteFingerprint { get; }

    /// <summary>Feeds an inbound DTLS record (received over ICE) to the handshake/transport.</summary>
    void ReceiveRecord(ReadOnlyMemory<byte> record);

    /// <summary>
    /// Runs the DTLS handshake to completion and returns the negotiated SRTP keying material (RFC 5764 §4.2).
    /// Records are sent via the sender supplied to <see cref="IDtlsTransportFactory.Create"/> and received
    /// via <see cref="ReceiveRecord"/>.
    /// </summary>
    Task<SrtpKeyingMaterial> HandshakeAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Creates <see cref="IDtlsTransport"/> instances. An implementation owns the local certificate (so
/// <see cref="LocalFingerprint"/> is stable across the transports it creates).
/// </summary>
public interface IDtlsTransportFactory
{
    /// <summary>The fingerprint of the certificate this factory's transports present.</summary>
    DtlsFingerprint LocalFingerprint { get; }

    /// <summary>Creates a transport for the given DTLS role, wired to <paramref name="send"/> for output.</summary>
    IDtlsTransport Create(DtlsRole role, DtlsRecordSender send);
}
