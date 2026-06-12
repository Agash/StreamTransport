using Agash.StreamTransport.WebRtc;
using Agash.StreamTransport.WebRtc.Dtls;
using Agash.StreamTransport.WebRtc.Srtp;

namespace Agash.StreamTransport.WebRtc.Tests;

/// <summary>
/// Runs a real DTLS-SRTP handshake between a client and a server transport cross-wired in memory, and
/// asserts both export identical SRTP keying material, that each captured the other's certificate
/// fingerprint, and that the resulting SRTP sessions interoperate.
/// </summary>
[TestClass]
public sealed class DtlsSrtpHandshakeTests
{
    [TestMethod]
    [Timeout(20_000)]
    public async Task ClientAndServer_Handshake_ExportMatchingKeysAndFingerprints()
    {
        var clientFactory = new DtlsTransportFactory();
        var serverFactory = new DtlsTransportFactory();

        IDtlsTransport? client = null;
        IDtlsTransport? server = null;

        // Cross-wire: each transport's outbound record is the other's inbound record.
        client = clientFactory.Create(DtlsRole.Client, record => server!.ReceiveRecord(record));
        server = serverFactory.Create(DtlsRole.Server, record => client!.ReceiveRecord(record));

        await using (client)
        await using (server)
        {
            Task<SrtpKeyingMaterial> clientHandshake = client.HandshakeAsync(CancellationToken.None);
            Task<SrtpKeyingMaterial> serverHandshake = server.HandshakeAsync(CancellationToken.None);

            await Task.WhenAll(clientHandshake, serverHandshake).WaitAsync(TimeSpan.FromSeconds(15));

            SrtpKeyingMaterial clientKeys = clientHandshake.Result;
            SrtpKeyingMaterial serverKeys = serverHandshake.Result;

            // RFC 5705 exporter is symmetric: both sides derive identical keying material.
            Assert.AreEqual(clientKeys.Profile, serverKeys.Profile);
            Assert.AreEqual(SrtpProtectionProfile.AeadAes256Gcm, clientKeys.Profile, "should negotiate the preferred AES-256-GCM");
            CollectionAssert.AreEqual(clientKeys.ClientMasterKey.ToArray(), serverKeys.ClientMasterKey.ToArray());
            CollectionAssert.AreEqual(clientKeys.ServerMasterKey.ToArray(), serverKeys.ServerMasterKey.ToArray());
            CollectionAssert.AreEqual(clientKeys.ClientMasterSalt.ToArray(), serverKeys.ClientMasterSalt.ToArray());
            CollectionAssert.AreEqual(clientKeys.ServerMasterSalt.ToArray(), serverKeys.ServerMasterSalt.ToArray());

            // Each side authenticated the other by certificate fingerprint.
            Assert.AreEqual(serverFactory.LocalFingerprint.ToSdpValue(), client.RemoteFingerprint!.Value.ToSdpValue());
            Assert.AreEqual(clientFactory.LocalFingerprint.ToSdpValue(), server.RemoteFingerprint!.Value.ToSdpValue());

            // The exported material drives interoperable SRTP sessions.
            var clientSrtp = new SrtpSession(clientKeys, isDtlsClient: true);
            var serverSrtp = new SrtpSession(serverKeys, isDtlsClient: false);

            byte[] payload = [0x10, 0x20, 0x30, 0x40, 0x50];
            byte[] packet = new byte[12 + payload.Length + SrtpSession.ProtectionOverhead];
            packet[0] = 0x80;
            packet[1] = 0x60;
            packet[2] = 0x00;
            packet[3] = 0x05; // seq 5
            packet[8] = 0xAA; // ssrc
            payload.CopyTo(packet, 12);

            int protectedLength = clientSrtp.ProtectRtp(packet, 12 + payload.Length);
            Assert.IsTrue(serverSrtp.UnprotectRtp(packet, protectedLength, out int recovered));
            CollectionAssert.AreEqual(payload, packet[12..recovered]);
        }
    }
}
