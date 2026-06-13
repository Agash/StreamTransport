using Agash.StreamTransport.WebRtc.Rtp;

namespace Agash.StreamTransport.WebRtc.Tests;

/// <summary>
/// FlexFEC (RFC 8627) XOR recovery, byte-exact and deterministic: a repair packet protecting a run of source
/// packets recovers any single lost one exactly (header bits, PT, timestamp, and payload of varying lengths),
/// recovers nothing when none is lost, and gives up when two are lost.
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
            new FecSourcePacket(102, 0x01, 96, 15000, Body(0xC0, 300)),
            new FecSourcePacket(103, 0x00, 97, 18000, Body(0xD0, 20)),
            new FecSourcePacket(104, 0x01, 96, 21000, Body(0xE0, 700)),
        ];
    }

    [TestMethod]
    public void Recover_SingleLostPacket_RestoresItByteExact()
    {
        FecSourcePacket[] run = SampleRun();
        byte[] fec = FlexFec.BuildRepair(run);

        // Lose the middle packet (seq 102); everything else is available.
        const ushort lost = 102;
        FecRecoveredPacket? recovered = FlexFec.TryRecover(fec, seq => seq == lost ? null : Find(run, seq));

        Assert.IsNotNull(recovered);
        FecSourcePacket original = Find(run, lost)!.Value;
        Assert.AreEqual(lost, recovered.Value.SequenceNumber);
        Assert.AreEqual(original.HeaderBits, recovered.Value.HeaderBits);
        Assert.AreEqual(original.PayloadType, recovered.Value.PayloadType);
        Assert.AreEqual(original.Timestamp, recovered.Value.Timestamp);
        CollectionAssert.AreEqual(original.BodyAfterHeader.ToArray(), recovered.Value.BodyAfterHeader);
    }

    [TestMethod]
    public void Recover_LargestPacketLost_RestoresFullLength()
    {
        FecSourcePacket[] run = SampleRun();
        byte[] fec = FlexFec.BuildRepair(run);

        const ushort lost = 101; // the 1100-byte one - exercises the length-recovery path.
        FecRecoveredPacket? recovered = FlexFec.TryRecover(fec, seq => seq == lost ? null : Find(run, seq));

        Assert.IsNotNull(recovered);
        CollectionAssert.AreEqual(Find(run, lost)!.Value.BodyAfterHeader.ToArray(), recovered.Value.BodyAfterHeader);
    }

    [TestMethod]
    public void Recover_NoLoss_ReturnsNull()
    {
        FecSourcePacket[] run = SampleRun();
        byte[] fec = FlexFec.BuildRepair(run);
        Assert.IsNull(FlexFec.TryRecover(fec, seq => Find(run, seq)));
    }

    [TestMethod]
    public void Recover_TwoLost_ReturnsNull_Unrecoverable()
    {
        FecSourcePacket[] run = SampleRun();
        byte[] fec = FlexFec.BuildRepair(run);
        Assert.IsNull(FlexFec.TryRecover(fec, seq => seq is 101 or 103 ? null : Find(run, seq)));
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
