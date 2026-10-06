using Agash.StreamTransport.Media;
using Agash.StreamTransport.WebRtc.Rtp;
using Agash.StreamTransport.WebRtc.Rtp.PayloadFormats;

namespace Agash.StreamTransport.WebRtc.Tests;

[TestClass]
public sealed class H264PayloadFormatTests
{
    [TestMethod]
    public void Packetize_OneSmallNal_SendsItAsASingleNalUnitPacket()
    {
        byte[] nal = MakeNal(type: 5, length: 40);

        List<byte[]> packets = Packetize(WithStartCodes(nal), maxPayloadSize: 1100);

        Assert.HasCount(1, packets);
        CollectionAssert.AreEqual(nal, packets[0]);
    }

    [TestMethod]
    public void Packetize_ParameterSetsAndSlice_AggregatesThemIntoAStapA()
    {
        byte[] sps = MakeNal(type: 7, length: 12, nri: 3);
        byte[] pps = MakeNal(type: 8, length: 5, nri: 2);
        byte[] slice = MakeNal(type: 5, length: 60, nri: 3);

        List<byte[]> packets = Packetize(WithStartCodes(sps, pps, slice), maxPayloadSize: 1100);

        Assert.HasCount(1, packets);
        byte[] stap = packets[0];
        Assert.AreEqual(24, stap[0] & 0x1F, "STAP-A");
        Assert.AreEqual(0x60, stap[0] & 0x60, "the highest NRI of the units");
        Assert.AreEqual(1 + (3 * 2) + 12 + 5 + 60, stap.Length);
        Assert.AreEqual(12, (stap[1] << 8) | stap[2]);
        CollectionAssert.AreEqual(sps, stap[3..15]);
    }

    [TestMethod]
    public void Packetize_LargeNal_FragmentsIntoFuAWithStartAndEnd()
    {
        byte[] slice = MakeNal(type: 5, length: 1000, nri: 3);

        List<byte[]> packets = Packetize(WithStartCodes(slice), maxPayloadSize: 300);

        Assert.HasCount(4, packets);
        foreach (byte[] packet in packets)
        {
            Assert.AreEqual(0x60 | 28, packet[0], "FU indicator keeps NRI, type 28");
            Assert.IsLessThanOrEqualTo(300, packet.Length);
            Assert.AreEqual(5, packet[1] & 0x1F);
        }

        Assert.AreEqual(0x80, packets[0][1] & 0xC0, "only the first has S");
        Assert.AreEqual(0x00, packets[1][1] & 0xC0);
        Assert.AreEqual(0x40, packets[^1][1] & 0xC0, "only the last has E");
    }

    [TestMethod]
    public void Packetize_UnitThatWouldOverflowTheRun_StartsANewPacket()
    {
        byte[] a = MakeNal(type: 1, length: 500);
        byte[] b = MakeNal(type: 1, length: 500);
        byte[] c = MakeNal(type: 1, length: 500);

        List<byte[]> packets = Packetize(WithStartCodes(a, b, c), maxPayloadSize: 1100);

        Assert.HasCount(2, packets);
        Assert.AreEqual(24, packets[0][0] & 0x1F, "a and b share a STAP-A");
        CollectionAssert.AreEqual(c, packets[1]);
    }

    [TestMethod]
    public void Packetize_ThreeByteStartCodesAndTrailingZeros_FindsTheUnits()
    {
        byte[] a = MakeNal(type: 7, length: 10);
        byte[] b = MakeNal(type: 5, length: 20);
        byte[] stream = [0, 0, 1, .. a, 0, 0, 0, 0, 1, .. b, 0, 0];

        List<byte[]> packets = Packetize(stream, maxPayloadSize: 1100);

        Assert.HasCount(1, packets);
        Assert.AreEqual(1 + 2 + 10 + 2 + 20, packets[0].Length);
    }

    [TestMethod]
    [DataRow(1100)]
    [DataRow(200)]
    [DataRow(3)]
    public void RoundTrip_MixedAccessUnit_ReassemblesItExactly(int maxPayloadSize)
    {
        byte[] accessUnit = WithStartCodes(
            MakeNal(type: 9, length: 2),
            MakeNal(type: 7, length: 14, nri: 3),
            MakeNal(type: 8, length: 6, nri: 3),
            MakeNal(type: 5, length: 3000, nri: 3),
            MakeNal(type: 5, length: 150, nri: 3)
        );

        byte[] assembled = Depacketize(Packetize(accessUnit, maxPayloadSize));

        CollectionAssert.AreEqual(accessUnit, assembled);
    }

    [TestMethod]
    public void Depacketize_FragmentWithoutItsStart_IsDropped()
    {
        byte[] slice = MakeNal(type: 5, length: 1000);
        byte[] single = MakeNal(type: 1, length: 30);
        List<byte[]> packets = Packetize(WithStartCodes(slice, single), maxPayloadSize: 300);

        byte[] assembled = Depacketize([.. packets.Skip(1)]);

        CollectionAssert.AreEqual(WithStartCodes(single), assembled);
    }

    [TestMethod]
    public void Depacketize_FragmentWithoutItsEnd_IsDropped()
    {
        byte[] slice = MakeNal(type: 5, length: 1000);
        byte[] single = MakeNal(type: 1, length: 30);
        List<byte[]> fragments = Packetize(WithStartCodes(slice), maxPayloadSize: 300);
        List<byte[]> packets = [.. fragments.Take(fragments.Count - 1), single];

        byte[] assembled = Depacketize(packets);

        CollectionAssert.AreEqual(WithStartCodes(single), assembled);
    }

    [TestMethod]
    public void Depacketize_TruncatedStapA_KeepsTheCompleteUnits()
    {
        byte[] a = MakeNal(type: 7, length: 10);
        byte[] stap = [24, 0, 10, .. a, 0, 50, 0x65, 0x01];

        byte[] assembled = Depacketize([stap]);

        CollectionAssert.AreEqual(WithStartCodes(a), assembled);
    }

    internal static List<byte[]> Packetize(byte[] accessUnit, int maxPayloadSize)
    {
        RtpPayloadWriter writer = new();
        new H264Packetizer(maxPayloadSize).Packetize(accessUnit, writer);
        return [.. Enumerable.Range(0, writer.Count).Select(i => writer[i].ToArray())];
    }

    private static byte[] Depacketize(List<byte[]> packets)
    {
        using H264Depacketizer depacketizer = new();
        for (int i = 0; i < packets.Count; i++)
        {
            bool last = i == packets.Count - 1;
            bool completed = depacketizer.TryPush(packets[i], last, out EncodedFrameBuffer unit);
            Assert.AreEqual(last, completed);
            if (completed)
            {
                using (unit)
                {
                    return unit.Span.ToArray();
                }
            }
        }

        throw new AssertFailedException("No access unit completed.");
    }

    // A NAL unit whose body never contains a start code or ends in zero.
    private static byte[] MakeNal(int type, int length, int nri = 0)
    {
        byte[] nal = new byte[length];
        nal[0] = (byte)((nri << 5) | type);
        for (int i = 1; i < length; i++)
        {
            nal[i] = (byte)(1 + (((i * 7) + type) % 255));
        }

        return nal;
    }

    private static byte[] WithStartCodes(params byte[][] nals) =>
        [.. nals.SelectMany(static nal => (byte[])[0, 0, 0, 1, .. nal])];
}
