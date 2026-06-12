using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;

namespace Agash.StreamTransport.WebRtc.Dtls;

/// <summary>
/// An <see cref="IDtlsTransport"/> backed by BouncyCastle's DTLS 1.2 engine. The handshake is blocking, so
/// it runs on a worker thread; inbound records are fed in from the ICE thread via <see cref="ReceiveRecord"/>
/// and bridged to the engine through <see cref="DtlsBridgeTransport"/>.
/// </summary>
internal sealed class BouncyCastleDtlsTransport : IDtlsTransport
{
    private readonly DtlsCertificate _certificate;
    private readonly DtlsBridgeTransport _bridge;

    public BouncyCastleDtlsTransport(DtlsRole role, DtlsRecordSender send, DtlsCertificate certificate)
    {
        Role = role;
        _certificate = certificate;
        _bridge = new DtlsBridgeTransport(send);
        LocalFingerprint = certificate.Fingerprint;
    }

    public DtlsRole Role { get; }

    public DtlsFingerprint LocalFingerprint { get; }

    public DtlsFingerprint? RemoteFingerprint { get; private set; }

    public void ReceiveRecord(ReadOnlyMemory<byte> record) => _bridge.Enqueue(record);

    public Task<SrtpKeyingMaterial> HandshakeAsync(CancellationToken cancellationToken) =>
        Task.Run(() => Role == DtlsRole.Client ? Connect() : Accept(), cancellationToken);

    private SrtpKeyingMaterial Connect()
    {
        var crypto = new BcTlsCrypto();
        var client = new SrtpTlsClient(crypto, _certificate);
        var protocol = new DtlsClientProtocol();
        protocol.Connect(client, _bridge);
        RemoteFingerprint = client.RemoteFingerprint;
        return SrtpProfiles.Split(client.SelectedProfile, client.KeyingMaterial!);
    }

    private SrtpKeyingMaterial Accept()
    {
        var crypto = new BcTlsCrypto();
        var server = new SrtpTlsServer(crypto, _certificate);
        var protocol = new DtlsServerProtocol();
        protocol.Accept(server, _bridge);
        RemoteFingerprint = server.RemoteFingerprint;
        return SrtpProfiles.Split(server.SelectedProfile, server.KeyingMaterial!);
    }

    public ValueTask DisposeAsync()
    {
        _bridge.Close();
        return ValueTask.CompletedTask;
    }
}
