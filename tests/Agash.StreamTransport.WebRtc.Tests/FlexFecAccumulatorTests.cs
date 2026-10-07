using System.Buffers.Binary;
using Agash.StreamTransport.WebRtc.Rtp;

namespace Agash.StreamTransport.WebRtc.Tests;

// The in-place parity builder makes the same repair as FlexFec.BuildRepair from the same packets.
[TestClass]
public sealed class FlexFecAccumulatorTests
{
    [TestMethod]
    [DataRow(1)]
    [DataRow(5)]
    [DataRow(15)]
    public void Repairs_MatchTheReferenceBuilder_GroupAfterGroup(int groupSize)
    {
        FlexFecAccumulator accumulator = new();
        Random random = new(7);
        ushort sequence = 65_530;
        for (int group = 0; group < 3; group++)
        {
            List<FecSourcePacket> sources = [];
            ReadOnlySpan<byte> repair = default;
            for (int i = 0; i < groupSize; i++)
            {
                byte[] rtp = Packet(random, sequence++, marker: i == groupSize - 1);
                sources.Add(Source(rtp));
                accumulator.Add(rtp);
                bool complete = accumulator.TryComplete(groupSize, out repair);
                Assert.AreEqual(i == groupSize - 1, complete);
            }

            CollectionAssert.AreEqual(FlexFec.BuildRepair(sources), repair.ToArray());
        }
    }

    [TestMethod]
    public void Recover_FromTheAccumulatedRepair_RestoresTheLostPacket()
    {
        FlexFecAccumulator accumulator = new();
        Random random = new(11);
        Dictionary<ushort, FecSourcePacket> received = [];
        byte[]? lost = null;
        ReadOnlySpan<byte> repair = default;
        for (ushort sequence = 100; sequence < 110; sequence++)
        {
            byte[] rtp = Packet(random, sequence, marker: false);
            accumulator.Add(rtp);
            if (sequence == 104)
            {
                lost = rtp;
            }
            else
            {
                received[sequence] = Source(rtp);
            }

            _ = accumulator.TryComplete(10, out repair);
        }

        FecRecoveredPacket? recovered = FlexFec.TryRecover(
            repair,
            s => received.TryGetValue(s, out FecSourcePacket p) ? p : null
        );

        Assert.IsNotNull(recovered);
        Assert.AreEqual((ushort)104, recovered.Value.SequenceNumber);
        CollectionAssert.AreEqual(lost![12..], recovered.Value.BodyAfterHeader);
    }

    private static byte[] Packet(Random random, ushort sequence, bool marker)
    {
        byte[] rtp = new byte[12 + random.Next(1, 1200)];
        random.NextBytes(rtp.AsSpan(12));
        rtp[0] = 0x80;
        rtp[1] = (byte)((marker ? 0x80 : 0) | 96);
        BinaryPrimitives.WriteUInt16BigEndian(rtp.AsSpan(2), sequence);
        BinaryPrimitives.WriteUInt32BigEndian(rtp.AsSpan(4), (uint)random.Next());
        BinaryPrimitives.WriteUInt32BigEndian(rtp.AsSpan(8), 0xAAAA_0001);
        return rtp;
    }

    private static FecSourcePacket Source(byte[] rtp) =>
        new(
            BinaryPrimitives.ReadUInt16BigEndian(rtp.AsSpan(2)),
            (byte)((rtp[0] & 0x3F) | (((rtp[1] >> 7) & 1) << 6)),
            (byte)(rtp[1] & 0x7F),
            BinaryPrimitives.ReadUInt32BigEndian(rtp.AsSpan(4)),
            rtp.AsMemory(12)
        );
}
