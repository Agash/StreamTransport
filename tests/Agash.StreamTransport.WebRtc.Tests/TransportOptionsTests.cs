using Agash.StreamTransport.WebRtc.Ice;
using Agash.StreamTransport.WebRtc.Transport;

namespace Agash.StreamTransport.WebRtc.Tests;

// Transport-neutral options reach the WebRTC transport whole, and a path failover time sets ICE's.
[TestClass]
public sealed class TransportOptionsTests
{
    [TestMethod]
    public void NeutralOptions_AreCarriedOverWhole()
    {
        // What the IRL contribution profile sets.
        MediaTransportOptions neutral = new()
        {
            ForwardErrorCorrection = true,
            RepairOverSecondPath = true,
            PathFailover = TimeSpan.FromSeconds(1),
        };

        WebRtcTransportOptions webRtc = new(neutral);

        Assert.IsTrue(webRtc.ForwardErrorCorrection);
        Assert.IsTrue(
            webRtc.RepairOverSecondPath,
            "the second-path repairs a field device relies on"
        );
        Assert.AreEqual(TimeSpan.FromSeconds(1), webRtc.PathFailover);
    }

    [TestMethod]
    public void PathFailover_SetsTheReceivingTimeoutAndKeepsAStandbyFresh()
    {
        IceTimings? timings = WebRtcMediaTransport.IceTimingsFor(
            new WebRtcTransportOptions { PathFailover = TimeSpan.FromSeconds(1) }
        );

        Assert.IsNotNull(timings);
        Assert.AreEqual(TimeSpan.FromSeconds(1), timings.ReceivingTimeout);
        Assert.AreEqual(TimeSpan.FromSeconds(2), timings.StandbyPingInterval);
        Assert.AreEqual(IceTimings.Default.ConsentTimeout, timings.ConsentTimeout);
    }

    [TestMethod]
    public void ExplicitTimings_WinOverThePathFailover()
    {
        IceTimings own = IceTimings.Default with
        {
            ReceivingTimeout = TimeSpan.FromMilliseconds(700),
        };

        IceTimings? timings = WebRtcMediaTransport.IceTimingsFor(
            new WebRtcTransportOptions { PathFailover = TimeSpan.FromSeconds(3), IceTimings = own }
        );

        Assert.AreSame(own, timings);
    }

    [TestMethod]
    public void NoFailoverOrTimings_LeavesICEsDefaults() =>
        Assert.IsNull(WebRtcMediaTransport.IceTimingsFor(new WebRtcTransportOptions()));
}
