using Microsoft.Extensions.Time.Testing;

namespace Agash.StreamTransport.Adaptation.Tests;

[TestClass]
public sealed class MediaRateAllocatorTests
{
    [TestMethod]
    public void VideoBitsPerSecond_NoRecovery_GetsTheTargetLessAudio()
    {
        FakeTimeProvider time = new();
        MediaRateAllocator allocator = new(new Counters().Read, time);

        Assert.AreEqual(1_936_000, allocator.VideoBitsPerSecond(2_000_000, 64_000, 100_000));
    }

    // RFC 4588 section 7: retransmissions and repairs come out of the same budget as the media.
    [TestMethod]
    public void VideoBitsPerSecond_WithRecoveryTraffic_LeavesRoomForIt()
    {
        FakeTimeProvider time = new();
        Counters sent = new();
        MediaRateAllocator allocator = new(sent.Read, time);

        // Three time constants of 300 kbit/s of retransmissions and repairs.
        for (int i = 0; i < 30; i++)
        {
            sent.Add(TrafficClass.Retransmission, 2500);
            sent.Add(TrafficClass.Repair, 1250);
            time.Advance(TimeSpan.FromMilliseconds(100));
            _ = allocator.VideoBitsPerSecond(2_000_000, 0, 100_000);
        }

        long video = allocator.VideoBitsPerSecond(2_000_000, 0, 100_000);
        Assert.AreEqual(1_700_000, video, 60_000, "about the target less 300 kbit/s");
        Assert.AreEqual(300_000, allocator.RecoveryBitsPerSecond, 60_000);
    }

    [TestMethod]
    public void VideoBitsPerSecond_RecoveryAboveTheTarget_KeepsTheFloor()
    {
        FakeTimeProvider time = new();
        Counters sent = new();
        MediaRateAllocator allocator = new(sent.Read, time);
        sent.Add(TrafficClass.Retransmission, 1_000_000);
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.AreEqual(150_000, allocator.VideoBitsPerSecond(1_000_000, 0, 150_000));
    }

    // Audio counts at the larger of its configured and measured rate, so its packet overhead is room
    // video does not take.
    [TestMethod]
    public void VideoBitsPerSecond_AudioAboveItsConfiguredRate_CountsWhatItSent()
    {
        FakeTimeProvider time = new();
        Counters sent = new();
        MediaRateAllocator allocator = new(sent.Read, time);
        // 100 kbit/s on the wire for three time constants, against 64 kbit/s configured.
        for (int i = 0; i < 30; i++)
        {
            sent.Add(TrafficClass.Audio, 1250);
            time.Advance(TimeSpan.FromMilliseconds(100));
            _ = allocator.VideoBitsPerSecond(2_000_000, 64_000, 100_000);
        }

        Assert.AreEqual(
            1_900_000,
            allocator.VideoBitsPerSecond(2_000_000, 64_000, 100_000),
            10_000
        );
    }

    private sealed class Counters
    {
        private readonly long[] _bytes = new long[4];

        public long Read(TrafficClass trafficClass) => _bytes[(int)trafficClass];

        public void Add(TrafficClass trafficClass, long bytes) =>
            _bytes[(int)trafficClass] += bytes;
    }
}
