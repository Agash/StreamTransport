using Agash.StreamTransport.Media;
using Agash.StreamTransport.WebRtc.Rtp;
using Agash.StreamTransport.WebRtc.Rtp.PayloadFormats;

namespace Agash.StreamTransport.WebRtc.Tests;

[TestClass]
public sealed class Av1PayloadFormatTests
{
    private const int SequenceHeader = 1;
    private const int TemporalDelimiter = 2;
    private const int Frame = 6;
    private const int Padding = 15;

    [TestMethod]
    public void Packetize_SmallTemporalUnit_SendsOnePacketWithoutDelimiterOrSizes()
    {
        byte[] sequence = Body(12);
        byte[] frame = Body(40);
        byte[] unit =
        [
            .. Obu(TemporalDelimiter, []),
            .. Obu(SequenceHeader, sequence),
            .. Obu(Frame, frame),
        ];

        List<byte[]> packets = Packetize(unit, maxPayloadSize: 1100);

        Assert.HasCount(1, packets);
        byte[] packet = packets[0];
        Assert.AreEqual(0x28, packet[0], "Z=0 Y=0 W=2 N=1");
        Assert.AreEqual(13, packet[1], "the first element's length");
        Assert.AreEqual(SequenceHeader << 3, packet[2], "the header without a size field");
        CollectionAssert.AreEqual(sequence, packet[3..15]);
        Assert.AreEqual(Frame << 3, packet[15], "the last element has no length");
        CollectionAssert.AreEqual(frame, packet[16..]);
    }

    [TestMethod]
    public void Packetize_ManyObus_LengthPrefixesEveryElement()
    {
        byte[] unit =
        [
            .. Obu(Frame, Body(5)),
            .. Obu(Frame, Body(5)),
            .. Obu(Frame, Body(5)),
            .. Obu(Frame, Body(5)),
        ];

        List<byte[]> packets = Packetize(unit, maxPayloadSize: 1100);

        Assert.HasCount(1, packets);
        Assert.AreEqual(0x00, packets[0][0], "W=0 and no sequence start");
        Assert.AreEqual(1 + (4 * 7), packets[0].Length);
    }

    [TestMethod]
    public void Packetize_LargeObu_ContinuesAcrossPackets()
    {
        byte[] unit = Obu(Frame, Body(3000));

        List<byte[]> packets = Packetize(unit, maxPayloadSize: 1000);

        Assert.HasCount(4, packets);
        Assert.AreEqual(0x50, packets[0][0], "Z=0 Y=1 W=1");
        Assert.AreEqual(0xD0, packets[1][0], "Z=1 Y=1 W=1");
        Assert.AreEqual(0x90, packets[^1][0], "Z=1 Y=0 W=1");
        Assert.IsTrue(packets.All(static p => p.Length <= 1000));
    }

    [TestMethod]
    public void Packetize_PaddingAndExtensionObus_DropsPaddingKeepsExtension()
    {
        byte[] withExtension = [(Frame << 3) | 0x04 | 0x02, 0x28, 3, 0xA1, 0xA2, 0xA3];
        byte[] unit = [.. Obu(Padding, Body(8)), .. withExtension];

        List<byte[]> packets = Packetize(unit, maxPayloadSize: 1100);

        CollectionAssert.AreEqual(
            new byte[] { 0x10, (Frame << 3) | 0x04, 0x28, 0xA1, 0xA2, 0xA3 },
            packets[0]
        );
    }

    [TestMethod]
    public void Packetize_LastObuWithoutSizeField_TakesTheRest()
    {
        byte[] unit = [.. Obu(SequenceHeader, Body(4)), Frame << 3, .. Body(9)];

        List<byte[]> packets = Packetize(unit, maxPayloadSize: 1100);

        Assert.AreEqual(1 + 1 + 5 + 10, packets[0].Length);
    }

    [TestMethod]
    public void Packetize_OversizedObu_Throws() =>
        Assert.ThrowsExactly<InvalidDataException>(() =>
            Packetize([(Frame << 3) | 0x02, 50, 1, 2, 3], maxPayloadSize: 1100)
        );

    [TestMethod]
    [DataRow(1100)]
    [DataRow(300)]
    [DataRow(3)]
    public void RoundTrip_TemporalUnit_ComesBackSizedBehindADelimiter(int maxPayloadSize)
    {
        byte[][] obus =
        [
            Obu(SequenceHeader, Body(11)),
            Obu(Frame, Body(2500)),
            Obu(Frame, Body(1)),
            Obu(Frame, Body(300)),
        ];
        byte[] unit = [.. Obu(TemporalDelimiter, []), .. obus.SelectMany(static o => o)];

        List<byte[]> packets = Packetize(unit, maxPayloadSize);
        byte[] assembled = Depacketize(packets);

        Assert.IsTrue(packets.All(p => p.Length <= maxPayloadSize));
        CollectionAssert.AreEqual(unit, assembled);
        Assert.AreEqual(
            RtpPayloadTraits.SequenceStart
                | RtpPayloadTraits.Keyframe
                | RtpPayloadTraits.SequenceParameters,
            Av1PayloadFormat.Instance.Inspect(packets[0])
        );
        Assert.IsFalse(
            packets
                .Skip(1)
                .Any(static p =>
                    (Av1PayloadFormat.Instance.Inspect(p) & RtpPayloadTraits.SequenceStart) != 0
                )
        );
    }

    [TestMethod]
    public void Depacketize_ContinuationWithoutItsStart_IsDropped()
    {
        byte[] small = Obu(Frame, Body(20));
        List<byte[]> packets = Packetize(
            [.. Obu(Frame, Body(3000)), .. small],
            maxPayloadSize: 1000
        );

        byte[] assembled = Depacketize([.. packets.Skip(1)]);

        CollectionAssert.AreEqual((byte[])[.. Obu(TemporalDelimiter, []), .. small], assembled);
    }

    [TestMethod]
    public void Depacketize_FragmentWithoutItsEnd_IsDropped()
    {
        byte[] small = Obu(Frame, Body(20));
        List<byte[]> large = Packetize(Obu(Frame, Body(3000)), maxPayloadSize: 1000);
        List<byte[]> tail = Packetize(small, maxPayloadSize: 1000);

        byte[] assembled = Depacketize([.. large.Take(2), .. tail]);

        CollectionAssert.AreEqual((byte[])[.. Obu(TemporalDelimiter, []), .. small], assembled);
    }

    [TestMethod]
    public void Depacketize_ElementLengthPastTheEnd_KeepsWhatCameBefore()
    {
        byte[] good = Obu(Frame, Body(4));
        byte[] packet = [0x00, 5, (byte)(Frame << 3), .. Body(4), 40, 1, 2];

        byte[] assembled = Depacketize([packet]);

        CollectionAssert.AreEqual((byte[])[.. Obu(TemporalDelimiter, []), .. good], assembled);
    }

    private static List<byte[]> Packetize(byte[] unit, int maxPayloadSize)
    {
        RtpPayloadWriter writer = new();
        new Av1Packetizer(maxPayloadSize).Packetize(unit, writer);
        return [.. Enumerable.Range(0, writer.Count).Select(i => writer[i].ToArray())];
    }

    private static byte[] Depacketize(List<byte[]> packets)
    {
        using Av1Depacketizer depacketizer = new();
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

        throw new AssertFailedException("No temporal unit completed.");
    }

    // An OBU with its size field, as encoders write them.
    private static byte[] Obu(int type, byte[] body) =>
        [(byte)((type << 3) | 0x02), .. Leb128(body.Length), .. body];

    private static byte[] Leb128(int value)
    {
        List<byte> bytes = [];
        do
        {
            byte b = (byte)(value & 0x7F);
            value >>= 7;
            bytes.Add(value > 0 ? (byte)(b | 0x80) : b);
        } while (value > 0);

        return [.. bytes];
    }

    private static byte[] Body(int length) =>
        [.. Enumerable.Range(0, length).Select(static i => (byte)((i * 13) + 1))];
}
