using Agash.StreamTransport.WebRtc.Rtp;
using Agash.StreamTransport.WebRtc.Rtp.PayloadFormats;

namespace Agash.StreamTransport.WebRtc.Tests;

[TestClass]
public sealed class H265PayloadFormatTests
{
    [TestMethod]
    public void Packetize_SmallNal_ProducesSingleNalUnitPacket()
    {
        // One small NAL (type 32 = VPS): header byte0 = (32<<1)=0x40.
        byte[] au = [0, 0, 0, 1, 0x40, 0x01, 0xAA, 0xBB, 0xCC];
        List<byte[]> packets = Packetize(au, maxPayloadSize: 1100);

        Assert.AreEqual(1, packets.Count);
        // Single NAL packet payload is the NAL unit verbatim (no start code).
        CollectionAssert.AreEqual(new byte[] { 0x40, 0x01, 0xAA, 0xBB, 0xCC }, packets[0]);
    }

    [TestMethod]
    public void Packetize_LargeNal_FragmentsIntoFusWithStartAndEndBits()
    {
        // A NAL too big for the MTU forces FU fragmentation.
        byte[] nalPayload = new byte[300];
        for (int i = 0; i < nalPayload.Length; i++)
        {
            nalPayload[i] = (byte)i;
        }

        byte[] au = BuildAccessUnit((nalType: 19, nalPayload)); // type 19 = IDR_W_RADL
        List<byte[]> packets = Packetize(au, maxPayloadSize: 100);

        Assert.IsTrue(packets.Count > 1, "a 302-byte NAL at MTU 100 must fragment");
        foreach (byte[] p in packets)
        {
            Assert.AreEqual(49, (p[0] >> 1) & 0x3F, "every fragment is an FU (type 49)");
        }

        Assert.AreEqual(0x80, packets[0][2] & 0x80, "first FU has the S bit");
        Assert.AreEqual(0x40, packets[^1][2] & 0x40, "last FU has the E bit");
        Assert.AreEqual(19, packets[0][2] & 0x3F, "FuType is the original NAL type");
    }

    [TestMethod]
    public void PacketizeThenDepacketize_RoundTripsMultiNalAccessUnit()
    {
        // Two NALs: a small VPS-like one (single packet) and a large IDR (fragmented).
        byte[] small = MakeNal(nalType: 32, length: 10);
        byte[] large = MakeNal(nalType: 19, length: 500);
        byte[] au = Concat(WithStartCode(small), WithStartCode(large));

        List<byte[]> packets = Packetize(au, maxPayloadSize: 120);

        using var depacketizer = new H265Depacketizer();
        byte[]? assembled = null;
        for (int i = 0; i < packets.Count; i++)
        {
            bool marker = i == packets.Count - 1;
            bool completed = depacketizer.Push(packets[i], marker, out byte[] result, out int length);
            if (marker)
            {
                Assert.IsTrue(completed, "the marker payload completes the access unit");
                assembled = result[..length];
            }
            else
            {
                Assert.IsFalse(completed, "no access unit should be emitted before the marker");
            }
        }

        Assert.IsNotNull(assembled);
        CollectionAssert.AreEqual(au, assembled);
    }

    [TestMethod]
    public void Depacketize_StrayFragmentMidStream_DoesNotThrow()
    {
        // A receiver that joins mid-stream sees an FU continuation/end with no preceding start. Per RFC 7798
        // it must tolerate this without throwing (the decoder + a PLI handle the resulting gap).
        using var depacketizer = new H265Depacketizer();

        // FU packet: PayloadHdr (type 49 = 0x62,0x01), FU header with E bit set + original type 19, then data.
        byte[] fuEnd = [0x62, 0x01, (byte)(0x40 | 19), 0xAA, 0xBB];

        // No exception is the assertion; whatever it returns, a subsequent clean frame must still work.
        _ = depacketizer.Push(fuEnd, marker: true, out _, out _);
        byte[] single = [0x40, 0x01, 0x11, 0x22];
        bool completed = depacketizer.Push(single, marker: true, out byte[] next, out int nextLength);
        Assert.IsTrue(completed, "the depacketizer must recover and assemble the next complete access unit.");
        Assert.IsTrue(nextLength > 0, "the recovered access unit must be non-empty.");
        _ = next;
    }

    // Runs the zero-alloc writer-based packetizer and materializes the produced payloads as arrays for assertions.
    private static List<byte[]> Packetize(byte[] accessUnit, int maxPayloadSize)
    {
        var writer = new RtpPayloadWriter();
        H265Packetizer.Packetize(accessUnit, writer, maxPayloadSize);
        var list = new List<byte[]>(writer.Count);
        for (int i = 0; i < writer.Count; i++)
        {
            list.Add(writer[i].ToArray());
        }

        return list;
    }

    private static byte[] BuildAccessUnit((int nalType, byte[] payload) nal) =>
        Concat([0, 0, 0, 1], NalHeader(nal.nalType), nal.payload);

    private static byte[] MakeNal(int nalType, int length)
    {
        byte[] payload = new byte[length - 2];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i * 7 + nalType);
        }

        return Concat(NalHeader(nalType), payload);
    }

    private static byte[] NalHeader(int nalType) => [(byte)((nalType << 1) & 0x7E), 0x01];

    private static byte[] WithStartCode(byte[] nal) => Concat([0, 0, 0, 1], nal);

    private static byte[] Concat(params byte[][] parts)
    {
        byte[] result = new byte[parts.Sum(p => p.Length)];
        int o = 0;
        foreach (byte[] p in parts)
        {
            p.CopyTo(result, o);
            o += p.Length;
        }

        return result;
    }
}
