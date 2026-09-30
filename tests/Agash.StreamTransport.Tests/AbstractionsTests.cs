using Agash.StreamTransport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Agash.StreamTransport.Tests;

[TestClass]
public sealed class AbstractionsTests
{
    [TestMethod]
    public void SessionDescription_Equality_IsByValue()
    {
        var a = new SessionDescription(SdpKind.Offer, "sdp");
        var b = new SessionDescription(SdpKind.Offer, "sdp");
        Assert.AreEqual(a, b);
    }

    [TestMethod]
    public void IceCandidate_Equality_IsByValue()
    {
        var a = new IceCandidate("candidate:1", "0", 0);
        var b = new IceCandidate("candidate:1", "0", 0);
        Assert.AreEqual(a, b);
    }
}
