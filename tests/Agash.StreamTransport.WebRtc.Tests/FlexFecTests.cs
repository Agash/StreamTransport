using System.Buffers.Binary;
using Agash.StreamTransport.WebRtc.Rtp;

namespace Agash.StreamTransport.WebRtc.Tests;

/// <summary>
/// FlexFEC (RFC 8627) XOR recovery, byte-exact and deterministic: a repair packet protecting a run of source
/// packets recovers any single lost one exactly (header bits, marker, PT, timestamp, and payload of varying
/// lengths), recovers nothing when none is lost, and gives up when two are lost; and the wire format of the
/// flexible mask and of the repair packet.
/// </summary>
[TestClass]
public sealed class FlexFecTests
{
    private static FecSourcePacket[] SampleRun()
    {
        return
        [
            new FecSourcePacket(100, 0x01, 96, 9000, Body(0xA0, 40)),
            new FecSourcePacket(101, 0x00, 96, 12000, Body(0xB0, 1100)), // largest body
            new FecSourcePacket(102, 0x21, 0x80 | 96, 15000, Body(0xC0, 300)), // padding, marker
            new FecSourcePacket(103, 0x00, 97, 18000, Body(0xD0, 20)),
            new FecSourcePacket(104, 0x11, 0x80 | 96, 21000, Body(0xE0, 700)), // extension, marker
        ];
    }

    [TestMethod]
    public void Recover_SingleLostPacket_RestoresItByteExact()
    {
        FecSourcePacket[] run = SampleRun();
        byte[] fec = FlexFec.BuildRepair(run);

        // Lose the middle packet (seq 102); everything else is available.
        const ushort lost = 102;
        byte[] body = new byte[2048];
        FecRecoveredPacket? recovered = FlexFec.TryRecover(
            fec,
            seq => seq == lost ? null : Find(run, seq),
            body,
            out FecRecoveredPacket r
        )
            ? r
            : null;

        Assert.IsNotNull(recovered);
        FecSourcePacket original = Find(run, lost)!.Value;
        Assert.AreEqual(lost, recovered.Value.SequenceNumber);
        Assert.AreEqual(original.HeaderBits, recovered.Value.HeaderBits);
        Assert.AreEqual(original.MarkerAndPayloadType, recovered.Value.MarkerAndPayloadType);
        Assert.AreEqual(original.Timestamp, recovered.Value.Timestamp);
        CollectionAssert.AreEqual(
            original.BodyAfterHeader.ToArray(),
            body[..recovered.Value.BodyLength]
        );
    }

    [TestMethod]
    public void Recover_LargestPacketLost_RestoresFullLength()
    {
        FecSourcePacket[] run = SampleRun();
        byte[] fec = FlexFec.BuildRepair(run);

        const ushort lost = 101; // the 1100-byte one - exercises the length-recovery path.
        byte[] body = new byte[2048];
        FecRecoveredPacket? recovered = FlexFec.TryRecover(
            fec,
            seq => seq == lost ? null : Find(run, seq),
            body,
            out FecRecoveredPacket r
        )
            ? r
            : null;

        Assert.IsNotNull(recovered);
        CollectionAssert.AreEqual(
            Find(run, lost)!.Value.BodyAfterHeader.ToArray(),
            body[..recovered.Value.BodyLength]
        );
    }

    [TestMethod]
    public void Recover_NoLoss_ReturnsNull()
    {
        FecSourcePacket[] run = SampleRun();
        byte[] fec = FlexFec.BuildRepair(run);
        Assert.IsFalse(FlexFec.TryRecover(fec, seq => Find(run, seq), new byte[2048], out _));
    }

    [TestMethod]
    public void Recover_TwoLost_ReturnsNull_Unrecoverable()
    {
        FecSourcePacket[] run = SampleRun();
        byte[] fec = FlexFec.BuildRepair(run);
        Assert.IsFalse(
            FlexFec.TryRecover(
                fec,
                seq => seq is 101 or 103 ? null : Find(run, seq),
                new byte[2048],
                out _
            )
        );
    }

    [TestMethod]
    [DataRow((ushort)104)]
    [DataRow((ushort)103)]
    public void Recover_RestoresTheMarkerBit_SetOrClear(ushort lost)
    {
        FecSourcePacket[] run = SampleRun();
        byte[] fec = FlexFec.BuildRepair(run);

        byte[] body = new byte[2048];
        FecRecoveredPacket? recovered = FlexFec.TryRecover(
            fec,
            seq => seq == lost ? null : Find(run, seq),
            body,
            out FecRecoveredPacket r
        )
            ? r
            : null;

        Assert.IsNotNull(recovered);
        Assert.AreEqual(
            Find(run, lost)!.Value.MarkerAndPayloadType,
            recovered.Value.MarkerAndPayloadType,
            "the marker bit ends a frame; a recovered last packet without it leaves the frame open"
        );
    }

    // RFC 8627 figure 12: a 15-bit mask after k, a 31-bit one after k, then 64 bits; k=1 when another
    // block follows. The shortest mask that covers the packets is used.
    [TestMethod]
    [DataRow(1, 12, false, false)]
    [DataRow(15, 12, false, false)]
    [DataRow(16, 16, true, false)]
    [DataRow(46, 16, true, false)]
    [DataRow(47, 24, true, true)]
    [DataRow(110, 24, true, true)]
    public void Repair_UsesTheShortestMask_WithItsKBits(
        int span,
        int headerLength,
        bool firstK,
        bool secondK
    )
    {
        FecSourcePacket[] run =
        [
            new(1000, 0, 96, 0, Body(1, 10)),
            new((ushort)(1000 + span - 1), 0, 96, 0, Body(2, 10)),
        ];
        byte[] fec = FlexFec.BuildRepair(span == 1 ? run[..1] : run);

        Assert.AreEqual(headerLength + 10, fec.Length);
        Assert.AreEqual(firstK, (fec[10] & 0x80) != 0);
        if (headerLength > 12)
        {
            Assert.AreEqual(secondK, (fec[12] & 0x80) != 0);
        }
    }

    // Where the bits for packets 0, 14, 15, 45, 46 and 109 sit, as libwebrtc's FlexFEC header writer
    // places them: 15 in the bit after the second k, 46 in the first bit of the last block.
    [TestMethod]
    public void Repair_PlacesMaskBitsWhereTheRfcPutsThem()
    {
        int[] offsets = [0, 14, 15, 45, 46, 109];
        byte[] fec = FlexFec.BuildRepair([
            .. offsets.Select(o => new FecSourcePacket((ushort)(500 + o), 0, 96, 0, Body(3, 4))),
        ]);

        Assert.AreEqual(0x80 | 0x40, fec[10], "k, then bit 0");
        Assert.AreEqual(0x01, fec[11], "bit 14");
        Assert.AreEqual(0x80 | 0x40, fec[12], "k, then bit 15");
        Assert.AreEqual(0x01, fec[15], "bit 45");
        Assert.AreEqual(0x80, fec[16], "bit 46");
        Assert.AreEqual(0x01, fec[23], "bit 109, the last of the last block");
    }

    // A long mask recovers any one of its packets, across the block boundaries and past a sequence wrap.
    [TestMethod]
    [DataRow(0)]
    [DataRow(15)]
    [DataRow(46)]
    [DataRow(109)]
    public void Recover_AcrossTheLongestMask(int lostOffset)
    {
        ushort start = 65_500;
        FecSourcePacket[] run =
        [
            .. Enumerable
                .Range(0, 110)
                .Select(o => new FecSourcePacket(
                    (ushort)(start + o),
                    0,
                    (byte)(o == 109 ? 0x80 | 96 : 96),
                    (uint)(o * 3000),
                    Body((byte)o, 50 + o)
                )),
        ];
        byte[] fec = FlexFec.BuildRepair(run);
        ushort lost = (ushort)(start + lostOffset);

        byte[] body = new byte[2048];
        FecRecoveredPacket? recovered = FlexFec.TryRecover(
            fec,
            seq => seq == lost ? null : Find(run, seq),
            body,
            out FecRecoveredPacket r
        )
            ? r
            : null;

        Assert.IsNotNull(recovered);
        Assert.AreEqual(lost, recovered.Value.SequenceNumber);
        FecSourcePacket original = run[lostOffset];
        Assert.AreEqual(original.MarkerAndPayloadType, recovered.Value.MarkerAndPayloadType);
        Assert.AreEqual(original.Timestamp, recovered.Value.Timestamp);
        CollectionAssert.AreEqual(
            original.BodyAfterHeader.ToArray(),
            body[..recovered.Value.BodyLength]
        );
    }

    [TestMethod]
    public void Recover_TruncatedLongMask_ReturnsNull()
    {
        byte[] fec = FlexFec.BuildRepair([
            new FecSourcePacket(1, 0, 96, 0, Body(1, 4)),
            new FecSourcePacket(60, 0, 96, 0, Body(2, 4)),
        ]);

        Assert.IsFalse(FlexFec.TryRecover(fec.AsSpan(0, 20), _ => null, new byte[2048], out _));
    }

    // RFC 8627 section 4.2.1: the repair names the stream it protects in its CSRC list, and its marker is 0.
    [TestMethod]
    public void RepairPacket_CarriesTheProtectedSsrcAsItsCsrc()
    {
        byte[] body = FlexFec.BuildRepair([new FecSourcePacket(1, 0, 0x80 | 96, 0, Body(1, 8))]);
        byte[] packet = new byte[16 + body.Length];

        int length = FlexFec.WriteRepairPacket(
            packet,
            49,
            7,
            90_000,
            0x0F0F_0F0F,
            0xAAAA_0001,
            body
        );

        Assert.AreEqual(packet.Length, length);
        Assert.AreEqual(0x81, packet[0], "version 2, one CSRC");
        Assert.AreEqual(49, packet[1], "the marker is 0 and the payload type is FlexFEC's");
        Assert.AreEqual(0xAAAA_0001u, BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(12)));
        CollectionAssert.AreEqual(body, packet[16..]);
    }

    private static FecSourcePacket? Find(FecSourcePacket[] run, ushort seq)
    {
        foreach (FecSourcePacket p in run)
        {
            if (p.SequenceNumber == seq)
            {
                return p;
            }
        }

        return null;
    }

    private static byte[] Body(byte seed, int length)
    {
        byte[] b = new byte[length];
        for (int i = 0; i < length; i++)
        {
            b[i] = (byte)(seed + i);
        }

        return b;
    }
}
