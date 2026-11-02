namespace Agash.StreamTransport.Adaptation.Tests;

// RFC 6679 sections 7.2 and 7.4.
[TestClass]
public sealed class EcnValidatorTests
{
    private static readonly TimeSpan Now = TimeSpan.FromSeconds(1);

    [TestMethod]
    public void MarkFor_Disabled_MarksNothing()
    {
        EcnValidator validator = new();

        Assert.IsTrue(
            Enumerable
                .Range(0, 20)
                .All(i => validator.MarkFor(i, EcnCodepoint.Ect1, Now) == EcnCodepoint.NotEct)
        );
    }

    // Section 7.2.1: a small fraction while testing, never all.
    [TestMethod]
    public void MarkFor_Testing_MarksAFraction()
    {
        EcnValidator validator = new();
        validator.Start();

        int marked = Enumerable
            .Range(0, 40)
            .Count(i => validator.MarkFor(i, EcnCodepoint.Ect1, Now) != EcnCodepoint.NotEct);

        Assert.AreEqual(10, marked);
    }

    // More than three marked packets arriving with their mark make the path capable, and then all is marked.
    [TestMethod]
    public void OnFeedback_MarksArrivingIntact_MakesThePathCapable()
    {
        EcnValidator validator = new();
        validator.Start();
        List<PacketObservation> observations = Send(validator, 40);

        validator.OnFeedback(Arrive(observations, static sent => sent), Now);

        Assert.AreEqual(EcnState.Capable, validator.State);
        Assert.AreEqual(EcnCodepoint.Ect1, validator.MarkFor(100, EcnCodepoint.Ect1, Now));
        Assert.AreEqual(EcnCodepoint.Ect1, validator.MarkFor(101, EcnCodepoint.Ect1, Now));
    }

    [TestMethod]
    public void OnFeedback_CeMarks_CountAsCarried()
    {
        EcnValidator validator = new();
        validator.Start();
        List<PacketObservation> observations = Send(validator, 40);

        validator.OnFeedback(
            Arrive(
                observations,
                static sent => sent == EcnCodepoint.NotEct ? sent : EcnCodepoint.Ce
            ),
            Now
        );

        Assert.AreEqual(EcnState.Capable, validator.State);
    }

    // Section 7.4: a middlebox that clears the field (bleaching) or rewrites ECT(1) to ECT(0).
    [TestMethod]
    [DataRow(EcnCodepoint.NotEct, DisplayName = "bleached")]
    [DataRow(EcnCodepoint.Ect0, DisplayName = "rewritten")]
    public void OnFeedback_MarksChangedOnTheWay_FailsThePath(EcnCodepoint arrivesAs)
    {
        EcnValidator validator = new();
        validator.Start();
        List<PacketObservation> observations = Send(validator, 40);

        validator.OnFeedback(
            Arrive(observations, sent => sent == EcnCodepoint.NotEct ? sent : arrivesAs),
            Now
        );

        Assert.AreEqual(EcnState.Failed, validator.State);
        Assert.AreEqual(EcnCodepoint.NotEct, validator.MarkFor(100, EcnCodepoint.Ect1, Now));
    }

    // A path that drops ECT packets while unmarked ones get through.
    [TestMethod]
    public void OnFeedback_MarkedPacketsLostWhileUnmarkedArrive_FailsThePath()
    {
        EcnValidator validator = new();
        validator.Start();
        List<PacketObservation> observations = Send(validator, 40);

        validator.OnFeedback(
            [
                .. observations.Select(static o =>
                    o.Ecn == EcnCodepoint.NotEct
                        ? o with
                        {
                            Outcome = PacketOutcome.Delivered,
                        }
                        : o with
                        {
                            Outcome = PacketOutcome.Lost,
                        }
                ),
            ],
            Now
        );

        Assert.AreEqual(EcnState.Failed, validator.State);
    }

    // Section 7.4.1: after a failure, try again later.
    [TestMethod]
    public void MarkFor_AfterTheRetryInterval_TestsAgain()
    {
        EcnValidator validator = new(TimeSpan.FromSeconds(10));
        validator.Start();
        List<PacketObservation> observations = Send(validator, 40);
        validator.OnFeedback(Arrive(observations, static _ => EcnCodepoint.NotEct), Now);
        Assert.AreEqual(EcnState.Failed, validator.State);

        Assert.AreEqual(
            EcnCodepoint.NotEct,
            validator.MarkFor(200, EcnCodepoint.Ect1, Now + TimeSpan.FromSeconds(5))
        );
        Assert.AreEqual(
            EcnCodepoint.Ect1,
            validator.MarkFor(201, EcnCodepoint.Ect1, Now + TimeSpan.FromSeconds(11))
        );
        Assert.AreEqual(EcnState.Testing, validator.State);
    }

    // Packets sent as marked, as observations carrying the codepoint they were sent with.
    private static List<PacketObservation> Send(EcnValidator validator, int count) =>
        [
            .. Enumerable
                .Range(0, count)
                .Select(i =>
                {
                    EcnCodepoint sent = validator.MarkFor(i, EcnCodepoint.Ect1, Now);
                    return new PacketObservation(
                        new SentPacket(i, 1200, Now, TrafficClass.Video),
                        PacketOutcome.Delivered,
                        Now,
                        sent
                    );
                }),
        ];

    private static PacketObservation[] Arrive(
        List<PacketObservation> sent,
        Func<EcnCodepoint, EcnCodepoint> path
    ) => [.. sent.Select(o => o with { Ecn = path(o.Ecn) })];
}
