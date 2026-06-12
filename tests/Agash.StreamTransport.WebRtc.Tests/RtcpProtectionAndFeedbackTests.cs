using System.Security.Cryptography;
using Agash.StreamTransport.WebRtc;
using Agash.StreamTransport.WebRtc.Rtcp;
using Agash.StreamTransport.WebRtc.Srtp;

namespace Agash.StreamTransport.WebRtc.Tests;

[TestClass]
public sealed class RtcpProtectionAndFeedbackTests
{
    [TestMethod]
    public void Srtcp_ProtectThenUnprotect_AcrossSessions_RoundTrips()
    {
        var keying = new SrtpKeyingMaterial(
            SrtpProtectionProfile.AeadAes128Gcm,
            ClientMasterKey: Rng(16), ClientMasterSalt: Rng(12),
            ServerMasterKey: Rng(16), ServerMasterSalt: Rng(12));
        var sender = new SrtpSession(keying, isDtlsClient: true);
        var receiver = new SrtpSession(keying, isDtlsClient: false);

        var sr = new RtcpSenderReport(0xABCD1234, 0xAABB_CCDD_EEFF_0011, 90000, 100, 20000, []);
        byte[] buffer = new byte[256];
        int rtcpLength = sr.Write(buffer);

        int protectedLength = sender.ProtectRtcp(buffer, rtcpLength);
        Assert.AreEqual(rtcpLength + SrtpSession.RtcpProtectionOverhead, protectedLength);

        Assert.IsTrue(receiver.UnprotectRtcp(buffer, protectedLength, out int recovered));
        Assert.AreEqual(rtcpLength, recovered);
        Assert.IsTrue(RtcpSenderReport.TryParse(buffer.AsSpan(0, recovered), out RtcpSenderReport parsed));
        Assert.AreEqual(0xAABB_CCDD_EEFF_0011ul, parsed.NtpTimestamp);
    }

    [TestMethod]
    public void Srtcp_Tampered_FailsAuthentication()
    {
        var keying = new SrtpKeyingMaterial(SrtpProtectionProfile.AeadAes128Gcm, Rng(16), Rng(12), Rng(16), Rng(12));
        var sender = new SrtpSession(keying, true);
        var receiver = new SrtpSession(keying, false);

        var rr = new RtcpReceiverReport(1, []);
        byte[] buffer = new byte[128];
        int len = sender.ProtectRtcp(buffer, rr.Write(buffer));
        buffer[10] ^= 0xFF;
        Assert.IsFalse(receiver.UnprotectRtcp(buffer, len, out _));
    }

    [TestMethod]
    public void Nack_BuildThenParse_RecoversSequencesIncludingBlp()
    {
        // 102 and 103 ride the BLP of PID 100 (deltas 2,3); 117 is out of range -> its own FCI.
        ushort[] lost = [100, 102, 103, 117];
        byte[] buffer = new byte[64];
        int length = RtcpFeedback.BuildNack(buffer, senderSsrc: 0x1, mediaSsrc: 0xDEADBEEF, lost);

        var recovered = new List<ushort>();
        Assert.IsTrue(RtcpFeedback.TryParseNack(buffer.AsSpan(0, length), out uint mediaSsrc, recovered));
        Assert.AreEqual(0xDEADBEEFu, mediaSsrc);
        CollectionAssert.AreEquivalent(lost, recovered);
    }

    [TestMethod]
    public void Pli_BuildThenDetect()
    {
        byte[] buffer = new byte[32];
        int length = RtcpFeedback.BuildPli(buffer, senderSsrc: 0x11, mediaSsrc: 0x22);
        Assert.AreEqual(12, length);
        Assert.IsTrue(RtcpFeedback.ContainsPli(buffer.AsSpan(0, length), out uint mediaSsrc));
        Assert.AreEqual(0x22u, mediaSsrc);
    }

    private static byte[] Rng(int n)
    {
        byte[] b = new byte[n];
        RandomNumberGenerator.Fill(b);
        return b;
    }
}
