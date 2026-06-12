using Agash.StreamTransport.WebRtc.Rtp;

namespace Agash.StreamTransport.WebRtc.Tests;

[TestClass]
public sealed class RtxTests
{
    [TestMethod]
    public void WrapThenUnwrap_RecoversOriginalPacket()
    {
        byte[] payload = [0x01, 0x02, 0x03, 0x04, 0x05];
        byte[] original = new byte[64];
        int originalLength = RtpPacket.Write(original, marker: true, payloadType: 96, sequenceNumber: 5000,
            timestamp: 0x12345678, ssrc: 0xAAAA_BBBB, payload);

        byte[] rtx = new byte[128];
        Assert.IsTrue(RtxStream.TryWrap(original.AsSpan(0, originalLength), rtx, rtxPayloadType: 97, rtxSsrc: 0xAAAA_BBBC, rtxSequence: 10, out int rtxLength));

        // The RTX packet carries the RTX PT/SSRC/seq.
        Assert.IsTrue(RtpPacket.TryParse(rtx.AsSpan(0, rtxLength), out RtpHeader rtxHeader, out _));
        Assert.AreEqual(97, rtxHeader.PayloadType);
        Assert.AreEqual(0xAAAA_BBBCu, rtxHeader.Ssrc);
        Assert.AreEqual(10, rtxHeader.SequenceNumber);

        byte[] recovered = new byte[64];
        Assert.IsTrue(RtxStream.TryUnwrap(rtx.AsSpan(0, rtxLength), recovered, originalPayloadType: 96, originalSsrc: 0xAAAA_BBBB, out int recoveredLength));

        Assert.IsTrue(RtpPacket.TryParse(recovered.AsSpan(0, recoveredLength), out RtpHeader header, out ReadOnlySpan<byte> recoveredPayload));
        Assert.AreEqual(96, header.PayloadType);
        Assert.AreEqual(0xAAAA_BBBBu, header.Ssrc);
        Assert.AreEqual(5000, header.SequenceNumber);
        Assert.AreEqual(0x12345678u, header.Timestamp);
        Assert.IsTrue(header.Marker);
        CollectionAssert.AreEqual(payload, recoveredPayload.ToArray());
    }

    [TestMethod]
    public void SendHistory_StoresAndRetrieves_UntilEvicted()
    {
        var history = new RtpSendHistory(capacity: 4);
        byte[] packet = new byte[20];
        for (ushort seq = 1; seq <= 3; seq++)
        {
            packet[3] = (byte)seq;
            history.Store(seq, packet.AsSpan(0, 20));
        }

        Assert.IsTrue(history.TryGet(2, out ReadOnlyMemory<byte> got));
        Assert.AreEqual(2, got.Span[3]);

        // Storing seq 5 evicts seq 1 (both map to slot 1 in a capacity-4 buffer).
        history.Store(5, packet);
        Assert.IsFalse(history.TryGet(1, out _));
        Assert.IsTrue(history.TryGet(5, out _));
    }
}
