using System.Security.Cryptography;
using Agash.StreamTransport.WebRtc;
using Agash.StreamTransport.WebRtc.Srtp;

namespace Agash.StreamTransport.WebRtc.Tests;

[TestClass]
public sealed class SrtpKeyDerivationTests
{
    [TestMethod]
    public void Derive_Aes128_MatchesRfc3711Vector()
    {
        byte[] masterKey = Convert.FromHexString("E1F97A0D3E018BE0D64FA32C06DE4139");
        byte[] masterSalt = Convert.FromHexString("0EC675AD498AFEEBB6960B3AABE6");

        byte[] encKey = SrtpKeyDerivation.Derive(masterKey, masterSalt, SrtpKeyDerivation.LabelRtpEncryption, 16);
        byte[] salt = SrtpKeyDerivation.Derive(masterKey, masterSalt, SrtpKeyDerivation.LabelRtpSalt, 14);

        Assert.AreEqual("C61E7A93744F39EE10734AFE3FF7A087", Convert.ToHexString(encKey));
        Assert.AreEqual("30CBBC08863D8C85D49DB34A9AE1", Convert.ToHexString(salt));
    }

    [TestMethod]
    public void Derive_Aes256_MatchesRfc6188Vector()
    {
        byte[] masterKey = Convert.FromHexString("f0f04914b513f2763a1b1fa130f10e2998f6f6e43e4309d1e622a0e332b9f1b6");
        byte[] masterSalt = Convert.FromHexString("3b04803de51ee7c96423ab5b78d2");

        byte[] encKey = SrtpKeyDerivation.Derive(masterKey, masterSalt, SrtpKeyDerivation.LabelRtpEncryption, 32);
        byte[] salt = SrtpKeyDerivation.Derive(masterKey, masterSalt, SrtpKeyDerivation.LabelRtpSalt, 14);

        Assert.AreEqual("5BA1064E30EC51613CAD926C5A28EF731EC7FB397F70A960653CAF06554CD8C4", Convert.ToHexString(encKey));
        Assert.AreEqual("FA31791685CA444A9E07C6C64E93", Convert.ToHexString(salt));
    }

    [TestMethod]
    public void Session_ClientToServer_AndBack_RoundTrips()
    {
        // Random keying material as DTLS-SRTP would export it (AEAD_AES_128_GCM: 16-octet key, 12-octet salt).
        var keying = new SrtpKeyingMaterial(
            SrtpProtectionProfile.AeadAes128Gcm,
            ClientMasterKey: Random(16), ClientMasterSalt: Random(12),
            ServerMasterKey: Random(16), ServerMasterSalt: Random(12));

        var client = new SrtpSession(keying, isDtlsClient: true);
        var server = new SrtpSession(keying, isDtlsClient: false);

        // client -> server
        AssertRoundTrip(client, server, ssrc: 0x11223344, seq: 1000);
        // server -> client (uses the other key pair)
        AssertRoundTrip(server, client, ssrc: 0x55667788, seq: 40000);
    }

    [TestMethod]
    public void Session_Aes256Gcm_RoundTrips()
    {
        var keying = new SrtpKeyingMaterial(
            SrtpProtectionProfile.AeadAes256Gcm,
            ClientMasterKey: Random(32), ClientMasterSalt: Random(12),
            ServerMasterKey: Random(32), ServerMasterSalt: Random(12));

        var client = new SrtpSession(keying, isDtlsClient: true);
        var server = new SrtpSession(keying, isDtlsClient: false);
        AssertRoundTrip(client, server, ssrc: 0xABCDEF01, seq: 7);
    }

    [TestMethod]
    public void Session_SequenceWrap_AdvancesRollover_AndStillDecodes()
    {
        var keying = new SrtpKeyingMaterial(
            SrtpProtectionProfile.AeadAes128Gcm,
            ClientMasterKey: Random(16), ClientMasterSalt: Random(12),
            ServerMasterKey: Random(16), ServerMasterSalt: Random(12));
        var client = new SrtpSession(keying, isDtlsClient: true);
        var server = new SrtpSession(keying, isDtlsClient: false);

        // Send across a sequence-number wrap (65530 -> 4): the ROC must advance in lock-step on both sides.
        foreach (ushort seq in new ushort[] { 65530, 65533, 2, 4 })
        {
            AssertRoundTrip(client, server, ssrc: 0x01020304, seq: seq);
        }
    }

    private static void AssertRoundTrip(SrtpSession sender, SrtpSession receiver, uint ssrc, ushort seq)
    {
        byte[] payload = [0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02, 0x03, 0x04, 0x05];
        byte[] packet = new byte[12 + payload.Length + SrtpSession.ProtectionOverhead];
        packet[0] = 0x80;
        packet[1] = 0x60;
        packet[2] = (byte)(seq >> 8);
        packet[3] = (byte)seq;
        packet[8] = (byte)(ssrc >> 24);
        packet[9] = (byte)(ssrc >> 16);
        packet[10] = (byte)(ssrc >> 8);
        packet[11] = (byte)ssrc;
        payload.CopyTo(packet, 12);
        int plaintextLength = 12 + payload.Length;

        int protectedLength = sender.ProtectRtp(packet, plaintextLength);
        Assert.AreEqual(plaintextLength + SrtpSession.ProtectionOverhead, protectedLength);

        bool ok = receiver.UnprotectRtp(packet, protectedLength, out int recovered);
        Assert.IsTrue(ok, $"unprotect failed for seq {seq}");
        Assert.AreEqual(plaintextLength, recovered);
        CollectionAssert.AreEqual(payload, packet[12..recovered]);
    }

    private static byte[] Random(int n)
    {
        byte[] b = new byte[n];
        RandomNumberGenerator.Fill(b);
        return b;
    }
}
