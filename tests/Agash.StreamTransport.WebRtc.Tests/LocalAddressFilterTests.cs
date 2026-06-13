using System.Net;
using Agash.StreamTransport.WebRtc.Ice;

namespace Agash.StreamTransport.WebRtc.Tests;

/// <summary>
/// The ICE local-address selector: an empty preference list gathers everything; otherwise an address is kept
/// only if it matches a selector by family keyword, literal IP, or NIC name/id/description. This is what lets a
/// host force IPv4/IPv6 or confine an IRL uplink to specific modems.
/// </summary>
[TestClass]
public sealed class LocalAddressFilterTests
{
    private static readonly IPAddress V4 = IPAddress.Parse("192.168.20.51");
    private static readonly IPAddress V6 = IPAddress.Parse("fe80::1");

    [TestMethod]
    public void EmptyPreferences_IncludesEverything()
    {
        Assert.IsTrue(LocalAddressFilter.Includes([], "Wi-Fi", "id0", "Intel Wi-Fi", V4));
        Assert.IsTrue(LocalAddressFilter.Includes([], "Wi-Fi", "id0", "Intel Wi-Fi", V6));
    }

    [TestMethod]
    public void FamilyKeyword_SelectsByFamily()
    {
        Assert.IsTrue(LocalAddressFilter.Includes(["ipv4"], "eth0", "id", "desc", V4));
        Assert.IsFalse(LocalAddressFilter.Includes(["ipv4"], "eth0", "id", "desc", V6));
        Assert.IsTrue(LocalAddressFilter.Includes(["IPv6"], "eth0", "id", "desc", V6), "keyword is case-insensitive");
        Assert.IsFalse(LocalAddressFilter.Includes(["ipv6"], "eth0", "id", "desc", V4));
    }

    [TestMethod]
    public void LiteralIp_SelectsExactAddress()
    {
        Assert.IsTrue(LocalAddressFilter.Includes(["192.168.20.51"], "eth0", "id", "desc", V4));
        Assert.IsFalse(LocalAddressFilter.Includes(["192.168.20.99"], "eth0", "id", "desc", V4));
    }

    [TestMethod]
    public void NicName_SelectsByInterface()
    {
        // The IRL field-uplink case: keep only the two modems, drop Wi-Fi.
        string[] modems = ["modem1", "modem2"];
        Assert.IsTrue(LocalAddressFilter.Includes(modems, "modem1", "id1", "Cellular 1", V4));
        Assert.IsTrue(LocalAddressFilter.Includes(modems, "MODEM2", "id2", "Cellular 2", V6), "NIC match is case-insensitive");
        Assert.IsFalse(LocalAddressFilter.Includes(modems, "Wi-Fi", "idw", "Intel Wi-Fi", V4));
    }

    [TestMethod]
    public void NicIdOrDescription_AlsoMatch()
    {
        Assert.IsTrue(LocalAddressFilter.Includes(["{GUID-ID}"], "eth0", "{GUID-ID}", "desc", V4), "matches NIC id");
        Assert.IsTrue(LocalAddressFilter.Includes(["Intel Wi-Fi 6E"], "wlan0", "id", "Intel Wi-Fi 6E", V4), "matches NIC description");
    }

    [TestMethod]
    public void MultipleSelectors_MatchIsUnion()
    {
        string[] sel = ["ipv4", "modem2"];
        Assert.IsTrue(LocalAddressFilter.Includes(sel, "wlan0", "id", "desc", V4), "matched by family");
        Assert.IsTrue(LocalAddressFilter.Includes(sel, "modem2", "id", "desc", V6), "matched by NIC name");
        Assert.IsFalse(LocalAddressFilter.Includes(sel, "wlan0", "id", "desc", V6), "matches neither");
    }
}
