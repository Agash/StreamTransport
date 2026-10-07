namespace Agash.StreamTransport.Adaptation.Tests;

// The hybrid retransmission/FEC plan: libwebrtc's mode switches, and a group sized from the loss.
[TestClass]
public sealed class RecoveryPolicyTests
{
    private static readonly RecoveryPolicy Policy = new();

    private static RecoveryInputs Inputs(double loss, int rttMs, long bps = 4_000_000) =>
        new(loss, TimeSpan.FromMilliseconds(rttMs), bps, 30, 12);

    [TestMethod]
    public void NoLoss_SendsNoFec() => Assert.IsFalse(Policy.Plan(Inputs(0, 80)).Fec);

    [TestMethod]
    public void ShortRoundTrip_RetransmitsOnly()
    {
        RecoveryPlan plan = Policy.Plan(Inputs(0.05, 10));

        Assert.IsFalse(plan.Fec);
        Assert.IsTrue(plan.Retransmit);
    }

    [TestMethod]
    public void SmallFrames_OnAModerateRoundTrip_SendNoFec()
    {
        // 150 kbit/s at 30 fps is 625 bytes a frame.
        Assert.IsFalse(Policy.Plan(Inputs(0.05, 60, bps: 150_000)).Fec);
        Assert.IsTrue(Policy.Plan(Inputs(0.05, 250, bps: 150_000)).Fec);
    }

    [TestMethod]
    public void GroupShrinks_AsLossGrows_AndKeepsTheTarget()
    {
        int previous = int.MaxValue;
        foreach (double loss in new[] { 0.002, 0.01, 0.03, 0.08 })
        {
            RecoveryPlan plan = Policy.Plan(Inputs(loss, 80));
            Assert.IsTrue(plan.Fec);
            Assert.IsTrue(plan.Retransmit, "retransmission repairs what FEC leaves.");
            Assert.IsLessThanOrEqualTo(previous, plan.FecGroupSize);
            if (plan.FecGroupSize > 2)
            {
                Assert.IsLessThanOrEqualTo(
                    0.01,
                    RecoveryPolicy.Unrecoverable(loss, plan.FecGroupSize + 1),
                    $"loss {loss}: group {plan.FecGroupSize}"
                );
            }

            previous = plan.FecGroupSize;
        }

        Assert.AreEqual(15, Policy.Plan(Inputs(0.002, 80)).FecGroupSize);
        Assert.AreEqual(2, Policy.Plan(Inputs(0.08, 80)).FecGroupSize, "dense as it gets: 50%.");
    }

    [TestMethod]
    public void Group_SpansAtMostTwoRoundTripsOfFrames()
    {
        // 30 fps and a 20 ms round trip: about one frame, two packets a frame here.
        RecoveryPlan plan = Policy.Plan(
            new RecoveryInputs(0.001, TimeSpan.FromMilliseconds(20), 4_000_000, 30, 2)
        );

        Assert.AreEqual(2, plan.FecGroupSize);
    }

    [TestMethod]
    public void Unrecoverable_IsTheBinomialTail()
    {
        Assert.AreEqual(0, RecoveryPolicy.Unrecoverable(0, 10), 1e-12);
        Assert.AreEqual(
            1 - (0.9 * 0.9) - (2 * 0.1 * 0.9),
            RecoveryPolicy.Unrecoverable(0.1, 2),
            1e-12
        );
    }
}
