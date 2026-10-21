namespace Agash.StreamTransport.Adaptation.Tests;

// The scenarios of quinn's DPLPMTUD tests (quinn-proto, connection/mtud.rs), with the expected probe
// sequences, plus the BASE and ERROR states of RFC 8899 section 5.2 that quinn leaves to QUIC's handshake.
[TestClass]
public sealed class PathMtuDiscoveryTests
{
    private static readonly TimeSpan Start = TimeSpan.FromSeconds(1);

    private long _nextId;

    // Drives the search on a link that carries datagrams up to a size, until it completes; returns the
    // sizes probed, the base probe first.
    private List<int> DriveToCompletion(PathMtuDiscovery search, TimeSpan now, int linkLimit)
    {
        List<int> probed = [];
        for (int i = 0; i < 100; i++)
        {
            int? size = search.TakeProbe(now, int.MaxValue);
            if (search.State == PathMtuState.SearchComplete)
            {
                break;
            }

            Assert.IsNotNull(size);
            probed.Add(size.Value);
            long id = SendProbe(search);
            if (size <= linkLimit)
            {
                _ = search.OnAcknowledged(id, size.Value);
            }
            else
            {
                search.OnLost(id, size.Value, now);
            }
        }

        return probed;
    }

    private long SendProbe(PathMtuDiscovery search)
    {
        long id = ++_nextId;
        search.OnProbeSent(id);
        return id;
    }

    private static PathMtuDiscovery Search(int maximum = 1452) =>
        new(new PathMtuOptions { MaximumSize = maximum });

    [TestMethod]
    public void BlackHoleDetector_EmptyBurst_IsNoBlackHole()
    {
        Assert.IsFalse(Search().CheckBlackHole(Start));
    }

    [TestMethod]
    public void BlackHoleDetector_BurstWithANonSuspiciousPacket_IsNotCounted()
    {
        PathMtuDiscovery search = Search();
        search.OnLost(2, 1300, Start);
        search.OnLost(3, 1300, Start);
        search.OnLost(4, 800, Start);

        Assert.IsFalse(search.CheckBlackHole(Start));
    }

    [TestMethod]
    public void TwoLostBursts_AreNoBlackHole()
    {
        PathMtuDiscovery search = Search();
        for (int i = 0; i < 2; i++)
        {
            search.OnLost(i, 1300, Start);
            Assert.IsFalse(search.CheckBlackHole(Start));
        }
    }

    [TestMethod]
    public void FourLostBursts_AreABlackHole_AndTheSearchRestsForTheCooldown()
    {
        PathMtuDiscovery search = Search();
        for (int i = 0; i < 4; i++)
        {
            // Never contiguous, so each packet is a burst of its own.
            search.OnLost(i * 2, 1300, Start);
        }

        Assert.IsTrue(search.CheckBlackHole(Start));
        Assert.AreEqual(1200, search.Current);
        Assert.AreEqual(PathMtuState.SearchComplete, search.State);
        Assert.IsNull(search.TakeProbe(Start + TimeSpan.FromSeconds(59), int.MaxValue));
        Assert.IsNotNull(search.TakeProbe(Start + TimeSpan.FromSeconds(60), int.MaxValue));
    }

    [TestMethod]
    public void CompleteSearch_ResumesWhenTheRaiseIntervalElapses()
    {
        PathMtuDiscovery search = Search(maximum: 9000);
        _ = DriveToCompletion(search, Start, 1500);

        Assert.IsNull(search.TakeProbe(Start, int.MaxValue));
        Assert.AreEqual(PathMtuState.SearchComplete, search.State);
        Assert.AreEqual(1471, search.Current);
        Assert.AreEqual(5235, search.TakeProbe(Start + TimeSpan.FromSeconds(600), int.MaxValue));
        Assert.AreEqual(PathMtuState.Searching, search.State);
    }

    [TestMethod]
    public void ThreeLostProbes_LowerTheProbeSize()
    {
        PathMtuDiscovery search = Search();
        _ = search.OnAcknowledged(SendProbe(TakeBase(search)), 1200);
        List<int> sizes = [];
        for (int i = 0; i < 4; i++)
        {
            int size = search.TakeProbe(Start, int.MaxValue)!.Value;
            sizes.Add(size);
            search.OnLost(SendProbe(search), size, Start);
        }

        // The first size is probed three times, then the search halves below it.
        Assert.AreEqual(sizes[0], sizes[1]);
        Assert.AreEqual(sizes[0], sizes[2]);
        Assert.AreEqual(sizes[0] - ((sizes[0] - 1200) / 2) - 1, sizes[3]);
    }

    [TestMethod]
    public void Link1500_ProbesTheExpectedSizes()
    {
        PathMtuDiscovery search = Search();

        List<int> probed = DriveToCompletion(search, Start, 1500);

        CollectionAssert.AreEqual(new[] { 1200, 1326, 1389, 1420, 1452 }, probed);
        Assert.AreEqual(1452, search.Current);
        Assert.AreEqual(PathMtuState.SearchComplete, search.State);
    }

    [TestMethod]
    public void Link1500WithA10000UpperBound_ProbesTheExpectedSizes()
    {
        PathMtuDiscovery search = Search(maximum: 10_000);

        List<int> probed = DriveToCompletion(search, Start, 1500);

        CollectionAssert.AreEqual(
            new[]
            {
                1200,
                5600,
                5600,
                5600,
                3399,
                3399,
                3399,
                2299,
                2299,
                2299,
                1749,
                1749,
                1749,
                1474,
                1611,
                1611,
                1611,
                1542,
                1542,
                1542,
                1507,
                1507,
                1507,
            },
            probed
        );
        Assert.AreEqual(1474, search.Current);
    }

    [TestMethod]
    public void NoLostProbes_FindsTheLargestUdpPayload()
    {
        PathMtuDiscovery search = Search(maximum: 65_527);

        _ = DriveToCompletion(search, Start, int.MaxValue);

        Assert.AreEqual(65_527, search.Current);
        Assert.AreEqual(PathMtuState.SearchComplete, search.State);
    }

    [TestMethod]
    public void HalfOfProbesLost_FindsTheLargestUdpPayload()
    {
        PathMtuDiscovery search = Search(maximum: 65_527);
        _ = search.OnAcknowledged(SendProbe(TakeBase(search)), 1200);
        int iterations = 0;
        for (int i = 1; i < 100; i++)
        {
            iterations++;
            int? size = search.TakeProbe(Start, int.MaxValue);
            if (search.State == PathMtuState.SearchComplete)
            {
                break;
            }

            Assert.IsNotNull(size);
            long id = SendProbe(search);

            // Nothing else goes while the probe is out.
            Assert.IsNull(search.TakeProbe(Start, int.MaxValue));
            if (i % 2 == 0)
            {
                _ = search.OnAcknowledged(id, size.Value);
            }
            else
            {
                search.OnLost(id, size.Value, Start);
            }
        }

        Assert.AreEqual(25, iterations);
        Assert.AreEqual(65_527, search.Current);
    }

    // RFC 8899 section 5.2: the base size is confirmed by a probe before any search.
    [TestMethod]
    public void Base_ProbesTheBaseSizeFirst_ThenSearches()
    {
        PathMtuDiscovery search = Search();

        Assert.AreEqual(PathMtuState.Base, search.State);
        Assert.AreEqual(1200, search.TakeProbe(Start, int.MaxValue));
        _ = search.OnAcknowledged(SendProbe(search), 1200);
        Assert.AreEqual(PathMtuState.Searching, search.State);
    }

    // RFC 8899 section 5.2: a base size the path does not carry is the ERROR state, which holds the base
    // and tries again later.
    [TestMethod]
    public void BaseProbeLostEveryTime_EntersErrorAndRetriesAfterTheCooldown()
    {
        PathMtuDiscovery search = Search();
        for (int i = 0; i < 3; i++)
        {
            Assert.AreEqual(1200, search.TakeProbe(Start, int.MaxValue));
            search.OnLost(SendProbe(search), 1200, Start);
        }

        Assert.AreEqual(PathMtuState.Error, search.State);
        Assert.AreEqual(1200, search.Current);
        Assert.IsNull(search.TakeProbe(Start + TimeSpan.FromSeconds(59), int.MaxValue));
        Assert.AreEqual(1200, search.TakeProbe(Start + TimeSpan.FromSeconds(60), int.MaxValue));
        Assert.AreEqual(PathMtuState.Base, search.State);
    }

    // A probe larger than the transport can build waits, without counting as lost.
    [TestMethod]
    public void UnbuildableProbe_WaitsWithoutCountingAsLost()
    {
        PathMtuDiscovery search = Search();
        _ = search.OnAcknowledged(SendProbe(TakeBase(search)), 1200);

        Assert.IsNull(search.TakeProbe(Start, 1300));
        Assert.IsNull(search.TakeProbe(Start, 1300));
        Assert.AreEqual(1326, search.TakeProbe(Start, 1400));
    }

    [TestMethod]
    public void PathChange_ConfirmsTheBaseAgain()
    {
        PathMtuDiscovery search = Search();
        _ = DriveToCompletion(search, Start, 1500);

        Assert.IsTrue(search.OnPathChanged());
        Assert.AreEqual(1200, search.Current);
        Assert.AreEqual(PathMtuState.Base, search.State);
        List<int> probed = DriveToCompletion(search, Start, 1300);
        CollectionAssert.AreEqual(new[] { 1200, 1326 }, probed.Take(2).ToArray());
        Assert.IsLessThanOrEqualTo(1300, search.Current);
        Assert.IsGreaterThan(1300 - 20, search.Current);
    }

    // The black hole detector, as quinn tests it.
    [TestMethod]
    public void BlackHole_LossesLargerThanAcknowledged_AreABlackHole()
    {
        PathMtuDiscovery search = Search();
        _ = search.OnAcknowledged((BlackHoleThreshold + 1) * 2, 1300);
        for (int i = 0; i < BlackHoleThreshold; i++)
        {
            search.OnLost(i * 2, 1400, Start);
        }

        Assert.IsFalse(search.CheckBlackHole(Start));
        search.OnLost(BlackHoleThreshold * 2, 1400, Start);
        Assert.IsTrue(search.CheckBlackHole(Start));
    }

    [TestMethod]
    public void BlackHole_LossesBeforeALargerDelivery_AreNotSuspicious()
    {
        PathMtuDiscovery search = Search();
        _ = search.OnAcknowledged((BlackHoleThreshold + 1) * 2, 1500);
        for (int i = 0; i < BlackHoleThreshold + 1; i++)
        {
            search.OnLost(i * 2, 1400, Start);
        }

        Assert.IsFalse(search.CheckBlackHole(Start));
    }

    [TestMethod]
    public void BlackHole_LossesSmallerThanAnEarlierDelivery_StillCount()
    {
        PathMtuDiscovery search = Search();
        _ = search.OnAcknowledged(0, 1500);
        for (int i = 0; i < BlackHoleThreshold + 1; i++)
        {
            search.OnLost(i * 2, 1400, Start);
        }

        Assert.IsTrue(search.CheckBlackHole(Start));
    }

    [TestMethod]
    public void BlackHole_MixedBursts_AreJudgedByTheirSmallestPacket()
    {
        PathMtuDiscovery search = Search();
        _ = search.OnAcknowledged((BlackHoleThreshold + 1) * 3, 1400);
        for (int i = 0; i < BlackHoleThreshold + 1; i++)
        {
            search.OnLost(i * 3, 1500, Start);
            search.OnLost((i * 3) + 1, 1300, Start);
        }

        Assert.IsFalse(search.CheckBlackHole(Start));
    }

    [TestMethod]
    public void BlackHole_MultiPacketBursts_CountOnce()
    {
        PathMtuDiscovery search = Search();
        _ = search.OnAcknowledged((BlackHoleThreshold + 1) * 3, 1400);
        for (int i = 0; i < BlackHoleThreshold; i++)
        {
            search.OnLost(i * 3, 1500, Start);
            search.OnLost((i * 3) + 1, 1500, Start);
        }

        Assert.IsFalse(search.CheckBlackHole(Start));
        search.OnLost(BlackHoleThreshold * 3, 1500, Start);
        Assert.IsTrue(search.CheckBlackHole(Start));
    }

    [TestMethod]
    public void BlackHole_NonSuspiciousBursts_DoNotHideSuspiciousOnes()
    {
        PathMtuDiscovery search = Search();
        _ = search.OnAcknowledged((BlackHoleThreshold + 1) * 4, 1400);
        for (int i = 0; i < BlackHoleThreshold + 1; i++)
        {
            search.OnLost(i * 4, 1500, Start);
            search.OnLost((i * 4) + 2, 1300, Start);
        }

        Assert.IsTrue(search.CheckBlackHole(Start));
    }

    [TestMethod]
    public void BlackHole_LossesAfterADelivery_AreSuspicious()
    {
        PathMtuDiscovery search = Search();
        _ = search.OnAcknowledged((BlackHoleThreshold + 1) * 2, 1400);
        for (int i = 0; i < BlackHoleThreshold + 1; i++)
        {
            search.OnLost(i * 2, 1300, Start);
        }

        Assert.IsFalse(search.CheckBlackHole(Start), "losses before a larger delivery");
        for (int i = 0; i < BlackHoleThreshold + 1; i++)
        {
            search.OnLost((BlackHoleThreshold + 1 + i) * 2, 1300, Start);
        }

        Assert.IsTrue(search.CheckBlackHole(Start), "losses after it");
    }

    [TestMethod]
    public void BlackHole_EqualSizeDelivery_ClearsTheBurstsBeforeIt()
    {
        PathMtuDiscovery search = Search();
        _ = search.OnAcknowledged(0, 1400);
        _ = search.OnAcknowledged((BlackHoleThreshold + 1) * 2, 1400);
        for (int i = 0; i < BlackHoleThreshold + 1; i++)
        {
            search.OnLost((i * 2) + 1, 1400, Start);
        }

        Assert.IsFalse(
            search.CheckBlackHole(Start),
            "full-size losses before a full-size delivery"
        );
        for (int i = 0; i < BlackHoleThreshold + 1; i++)
        {
            search.OnLost(((BlackHoleThreshold + 1 + i) * 2) + 1, 1400, Start);
        }

        Assert.IsTrue(search.CheckBlackHole(Start), "full-size losses after the last delivery");
    }

    [TestMethod]
    public void BlackHole_LaterDeliveryOfTheSameSize_ClearsEarlierBursts()
    {
        PathMtuDiscovery search = Search();
        for (int i = 0; i < BlackHoleThreshold; i++)
        {
            search.OnLost(i * 2, 1400, Start);
        }

        _ = search.OnAcknowledged(BlackHoleThreshold * 2, 1400);
        search.OnLost((BlackHoleThreshold * 2) + 1, 1400, Start);

        Assert.IsFalse(search.CheckBlackHole(Start));
    }

    private const int BlackHoleThreshold = PathMtuDiscovery.BlackHoleThreshold;

    private static PathMtuDiscovery TakeBase(PathMtuDiscovery search)
    {
        Assert.AreEqual(1200, search.TakeProbe(Start, int.MaxValue));
        return search;
    }
}
