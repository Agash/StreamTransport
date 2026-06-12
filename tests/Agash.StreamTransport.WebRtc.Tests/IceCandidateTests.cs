using System.Net;
using System.Net.Sockets;
using Agash.StreamTransport.WebRtc.Ice;

namespace Agash.StreamTransport.WebRtc.Tests;

[TestClass]
public sealed class IceCandidateTests
{
    [TestMethod]
    public void ComputePriority_MatchesRfc8445Formula()
    {
        // host, IPv6, component 1, first candidate: 2^24*126 + 2^8*60000 + (256-1).
        uint p = IceCandidate.ComputePriority(IceCandidateKind.Host, AddressFamily.InterNetworkV6, 1, index: 0);
        Assert.AreEqual((126u << 24) | (60000u << 8) | 255u, p);
    }

    [TestMethod]
    public void ComputePriority_Ipv6OutranksIpv4_ForSameType()
    {
        uint v6 = IceCandidate.ComputePriority(IceCandidateKind.Host, AddressFamily.InterNetworkV6, 1);
        uint v4 = IceCandidate.ComputePriority(IceCandidateKind.Host, AddressFamily.InterNetwork, 1);
        Assert.IsTrue(v6 > v4, "IPv6 host candidate must outrank IPv4 (IPv6-first).");
    }

    [TestMethod]
    public void ComputePriority_HostOutranksReflexive()
    {
        uint host = IceCandidate.ComputePriority(IceCandidateKind.Host, AddressFamily.InterNetwork, 1);
        uint srflx = IceCandidate.ComputePriority(IceCandidateKind.ServerReflexive, AddressFamily.InterNetwork, 1);
        Assert.IsTrue(host > srflx);
    }

    [TestMethod]
    public void ComputePairPriority_MatchesRfc8445Formula()
    {
        uint g = 100, d = 200;
        ulong expected = ((ulong)Math.Min(g, d) << 32) + (2 * Math.Max(g, d)) + (g > d ? 1UL : 0UL);
        Assert.AreEqual(expected, IceCandidate.ComputePairPriority(g, d));
        Assert.AreEqual(expected, IceCandidate.ComputePairPriority(d, g) - 1, "the G>D tie-bit distinguishes the roles");
    }

    [TestMethod]
    public void ToSdp_ThenTryParse_RoundTripsHostCandidate()
    {
        var candidate = new IceCandidate("2-192.168.1.5", 1, 2130706431, new IPEndPoint(IPAddress.Parse("192.168.1.5"), 51556), IceCandidateKind.Host);
        string sdp = candidate.ToSdp();
        StringAssert.StartsWith(sdp, "candidate:");
        StringAssert.Contains(sdp, "typ host");

        Assert.IsTrue(IceCandidate.TryParse(sdp, out IceCandidate parsed));
        Assert.AreEqual(candidate.Foundation, parsed.Foundation);
        Assert.AreEqual(candidate.ComponentId, parsed.ComponentId);
        Assert.AreEqual(candidate.Priority, parsed.Priority);
        Assert.AreEqual(candidate.Endpoint, parsed.Endpoint);
        Assert.AreEqual(IceCandidateKind.Host, parsed.Kind);
    }

    [TestMethod]
    public void TryParse_SrflxWithRelatedAddress_RoundTrips()
    {
        const string line = "candidate:3 1 udp 1694498815 203.0.113.7 54321 typ srflx raddr 192.168.1.5 rport 51556";
        Assert.IsTrue(IceCandidate.TryParse(line, out IceCandidate c));
        Assert.AreEqual(IceCandidateKind.ServerReflexive, c.Kind);
        Assert.AreEqual(new IPEndPoint(IPAddress.Parse("203.0.113.7"), 54321), c.Endpoint);
        Assert.AreEqual(new IPEndPoint(IPAddress.Parse("192.168.1.5"), 51556), c.RelatedAddress);
        Assert.AreEqual(line, c.ToSdp());
    }

    [TestMethod]
    public void TryParse_RejectsTcpAndMalformed()
    {
        Assert.IsFalse(IceCandidate.TryParse("candidate:1 1 tcp 2130706431 192.168.1.5 9 typ host", out _));
        Assert.IsFalse(IceCandidate.TryParse("garbage", out _));
        Assert.IsFalse(IceCandidate.TryParse("candidate:1 1 udp 2130706431 192.168.1.5", out _));
    }

    [TestMethod]
    public void Credentials_Generate_MeetsEntropyAndCharset()
    {
        var a = IceCredentials.Generate();
        var b = IceCredentials.Generate();

        Assert.AreEqual(4, a.UsernameFragment.Length);   // ≥ 24 bits
        Assert.AreEqual(22, a.Password.Length);          // ≥ 128 bits
        Assert.AreNotEqual(a.Password, b.Password);
        Assert.IsTrue((a.UsernameFragment + a.Password).All(static c => char.IsLetterOrDigit(c) || c is '+' or '/'),
            "ICE credentials must use only ICE-chars (ALPHA / DIGIT / '+' / '/').");
        Assert.AreEqual("R:L", IceCredentials.CheckUsername("R", "L"));
    }
}
