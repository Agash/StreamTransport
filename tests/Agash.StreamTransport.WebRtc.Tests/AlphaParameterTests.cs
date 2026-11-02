using Agash.StreamTransport.Media;
using Agash.StreamTransport.WebRtc.Transport;

namespace Agash.StreamTransport.WebRtc.Tests;

/// <summary>
/// The <c>x-alpha</c> format parameter: the ways each side lists, and the sender's first way the receiver
/// also lists is the one used, whichever side offered.
/// </summary>
[TestClass]
public sealed class AlphaParameterTests
{
    [TestMethod]
    public void AlphaValue_RoundTripsEveryWay()
    {
        AlphaLayout[] ways =
        [
            AlphaLayout.Layer,
            AlphaLayout.PackStacked,
            AlphaLayout.PackSideBySide,
        ];

        string value = FormatParameters.AlphaValue(ways);

        Assert.AreEqual("layer,stacked,side-by-side", value);
        CollectionAssert.AreEqual(
            ways,
            FormatParameters.AlphaLayouts($"x-alpha={value}").ToArray()
        );
    }

    [TestMethod]
    public void AlphaLayouts_UnknownWay_IsLeftOut() =>
        CollectionAssert.AreEqual(
            new[] { AlphaLayout.PackStacked },
            FormatParameters.AlphaLayouts("x-alpha=hologram,stacked").ToArray()
        );

    [TestMethod]
    public void ChooseAlpha_TheSendersOrderDecides()
    {
        // The sender's picture packs only stacked into its encoder; the receiver prefers side by side.
        AlphaLayout chosen = FormatParameters.ChooseAlpha(
            sender: [AlphaLayout.PackStacked, AlphaLayout.PackSideBySide],
            receiver: [AlphaLayout.PackSideBySide, AlphaLayout.PackStacked]
        );

        Assert.AreEqual(AlphaLayout.PackStacked, chosen);
    }

    [TestMethod]
    public void ChooseAlpha_NothingShared_IsNone() =>
        Assert.AreEqual(
            AlphaLayout.None,
            FormatParameters.ChooseAlpha([AlphaLayout.Layer], [AlphaLayout.PackSideBySide])
        );
}
