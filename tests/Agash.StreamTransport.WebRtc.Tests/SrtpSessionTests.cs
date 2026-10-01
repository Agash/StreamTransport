using System.Security.Cryptography;
using Agash.StreamTransport.WebRtc.Srtp;
using Dtls.Core;

namespace Agash.StreamTransport.WebRtc.Tests;

[TestClass]
public sealed class SrtpSessionTests
{
    [TestMethod]
    public void Profiles_OfferGcmFirstAndCmLast()
    {
        CollectionAssert.AreEqual(
            new[]
            {
                SrtpProtectionProfile.AeadAes128Gcm,
                SrtpProtectionProfile.AeadAes256Gcm,
                SrtpProtectionProfile.Aes128CmHmacSha180,
            },
            SrtpSession.Profiles
        );
    }

    [TestMethod]
    [DataRow(SrtpProtectionProfile.AeadAes128Gcm, 16, 12)]
    [DataRow(SrtpProtectionProfile.AeadAes256Gcm, 32, 12)]
    [DataRow(SrtpProtectionProfile.Aes128CmHmacSha180, 16, 14)]
    public void Session_EveryProfile_RoundTripsRtpAndRtcp(
        SrtpProtectionProfile profile,
        int keyLength,
        int saltLength
    )
    {
        (SrtpSession client, SrtpSession server) = Pair(profile, keyLength, saltLength);
        using (client)
        using (server)
        {
            foreach (ushort seq in new ushort[] { 65534, 65535, 0, 1 })
            {
                byte[] packet = Rtp(0x01020304, seq, out int length);
                int protectedLength = client.ProtectRtp(packet, length);
                Assert.AreEqual(length + client.ProtectionOverhead, protectedLength);
                Assert.IsTrue(server.UnprotectRtp(packet, protectedLength, out int recovered));
                Assert.AreEqual(length, recovered);
            }

            byte[] rtcp = new byte[32 + SrtpSession.MaxRtcpProtectionOverhead];
            rtcp[0] = 0x81;
            rtcp[1] = 0xC9;
            rtcp[3] = 7;
            int rtcpProtected = server.ProtectRtcp(rtcp, 32);
            Assert.IsTrue(client.UnprotectRtcp(rtcp, rtcpProtected, out int rtcpLength));
            Assert.AreEqual(32, rtcpLength);
        }
    }

    [TestMethod]
    public void UnprotectRtp_ReplayedPacket_IsRejected()
    {
        (SrtpSession client, SrtpSession server) = Pair(SrtpProtectionProfile.AeadAes128Gcm, 16, 12);
        using (client)
        using (server)
        {
            byte[] packet = Rtp(9, 100, out int length);
            int protectedLength = client.ProtectRtp(packet, length);
            byte[] copy = packet[..protectedLength];

            Assert.IsTrue(server.UnprotectRtp(packet, protectedLength, out _));
            Assert.IsFalse(server.UnprotectRtp(copy, protectedLength, out _));
        }
    }

    [TestMethod]
    public void UnprotectRtp_ReorderedWithinWindow_IsAcceptedOnce()
    {
        (SrtpSession client, SrtpSession server) = Pair(
            SrtpProtectionProfile.Aes128CmHmacSha180,
            16,
            14
        );
        using (client)
        using (server)
        {
            byte[][] sent = new byte[5][];
            int[] lengths = new int[5];
            for (int i = 0; i < sent.Length; i++)
            {
                sent[i] = Rtp(9, 200 + i, out int length);
                lengths[i] = client.ProtectRtp(sent[i], length);
            }

            byte[] late = (byte[])sent[1].Clone();
            foreach (int i in new[] { 0, 2, 3, 4, 1 })
            {
                Assert.IsTrue(server.UnprotectRtp(sent[i], lengths[i], out _), $"packet {i}");
            }

            Assert.IsFalse(server.UnprotectRtp(late, lengths[1], out _));
        }
    }

    [TestMethod]
    public void UnprotectRtp_OlderThanWindow_IsRejected()
    {
        (SrtpSession client, SrtpSession server) = Pair(SrtpProtectionProfile.AeadAes128Gcm, 16, 12);
        using (client)
        using (server)
        {
            byte[] old = Rtp(9, 10, out int oldLength);
            int oldProtected = client.ProtectRtp(old, oldLength);
            byte[] newer = Rtp(9, 10 + SrtpSession.ReplayWindowSize, out int newLength);
            int newProtected = client.ProtectRtp(newer, newLength);

            Assert.IsTrue(server.UnprotectRtp(newer, newProtected, out _));
            Assert.IsFalse(server.UnprotectRtp(old, oldProtected, out _));
        }
    }

    [TestMethod]
    public void UnprotectRtp_ForgedPacket_DoesNotAdvanceTheRollover()
    {
        (SrtpSession client, SrtpSession server) = Pair(SrtpProtectionProfile.AeadAes128Gcm, 16, 12);
        using (client)
        using (server)
        {
            byte[] first = Rtp(9, 40000, out int length);
            Assert.IsTrue(server.UnprotectRtp(first, client.ProtectRtp(first, length), out _));

            // Sequence 8000 after 40000 reads as the next rollover; a forgery must not commit it.
            byte[] forged = Rtp(9, 8000, out int forgedLength);
            Assert.IsFalse(server.UnprotectRtp(forged, forgedLength + 16, out _));

            byte[] next = Rtp(9, 40001, out length);
            Assert.IsTrue(server.UnprotectRtp(next, client.ProtectRtp(next, length), out _));
        }
    }

    [TestMethod]
    public void ProtectRtp_RepeatedSequence_Throws()
    {
        (SrtpSession client, SrtpSession server) = Pair(SrtpProtectionProfile.AeadAes128Gcm, 16, 12);
        using (client)
        using (server)
        {
            byte[] packet = Rtp(9, 500, out int length);
            _ = client.ProtectRtp(packet, length);
            byte[] again = Rtp(9, 500, out length);

            _ = Assert.ThrowsExactly<InvalidOperationException>(() =>
                client.ProtectRtp(again, length)
            );
        }
    }

    [TestMethod]
    public void UnprotectRtcp_ReplayedPacket_IsRejected()
    {
        (SrtpSession client, SrtpSession server) = Pair(
            SrtpProtectionProfile.Aes128CmHmacSha180,
            16,
            14
        );
        using (client)
        using (server)
        {
            byte[] rtcp = new byte[16 + SrtpSession.MaxRtcpProtectionOverhead];
            rtcp[0] = 0x80;
            rtcp[1] = 0xC8;
            rtcp[3] = 3;
            int length = client.ProtectRtcp(rtcp, 16);
            byte[] copy = rtcp[..length];

            Assert.IsTrue(server.UnprotectRtcp(rtcp, length, out _));
            Assert.IsFalse(server.UnprotectRtcp(copy, length, out _));
        }
    }

    private static (SrtpSession Client, SrtpSession Server) Pair(
        SrtpProtectionProfile profile,
        int keyLength,
        int saltLength
    )
    {
        var keying = new SrtpKeyingMaterial(
            profile,
            RandomNumberGenerator.GetBytes(keyLength),
            RandomNumberGenerator.GetBytes(saltLength),
            RandomNumberGenerator.GetBytes(keyLength),
            RandomNumberGenerator.GetBytes(saltLength)
        );
        return (new SrtpSession(keying, true), new SrtpSession(keying, false));
    }

    private static byte[] Rtp(uint ssrc, int seq, out int length)
    {
        length = 12 + 24;
        byte[] packet = new byte[length + SrtpSession.MaxProtectionOverhead];
        packet[0] = 0x80;
        packet[1] = 0x60;
        packet[2] = (byte)(seq >> 8);
        packet[3] = (byte)seq;
        packet[8] = (byte)(ssrc >> 24);
        packet[9] = (byte)(ssrc >> 16);
        packet[10] = (byte)(ssrc >> 8);
        packet[11] = (byte)ssrc;
        for (int i = 12; i < length; i++)
        {
            packet[i] = (byte)i;
        }

        return packet;
    }
}
