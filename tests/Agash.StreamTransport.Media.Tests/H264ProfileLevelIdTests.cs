using System.Collections.Immutable;
using Agash.StreamTransport.Media;

namespace Agash.StreamTransport.Media.Tests;

[TestClass]
public sealed class H264ProfileLevelIdTests
{
    [TestMethod]
    [DataRow("42e01f", H264Profile.ConstrainedBaseline, 31)]
    [DataRow("42c01f", H264Profile.ConstrainedBaseline, 31)]
    [DataRow("4d801f", H264Profile.ConstrainedBaseline, 31)]
    [DataRow("42001f", H264Profile.Baseline, 31)]
    [DataRow("4d001f", H264Profile.Main, 31)]
    [DataRow("4d4c0d", H264Profile.Main, 13)]
    [DataRow("640c34", H264Profile.ConstrainedHigh, 52)]
    [DataRow("640028", H264Profile.High, 40)]
    [DataRow("f4001f", H264Profile.PredictiveHigh444, 31)]
    [DataRow("42f00b", H264Profile.ConstrainedBaseline, 9)]
    public void TryParse_KnownProfiles_ReadProfileAndLevel(
        string text,
        H264Profile profile,
        int level
    )
    {
        Assert.IsTrue(H264ProfileLevelId.TryParse(text, out H264ProfileLevelId value));
        Assert.AreEqual(new H264ProfileLevelId(profile, level), value);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("42e01")]
    [DataRow("zz0000")]
    [DataRow("6e001f")]
    [DataRow("64e01f")]
    public void TryParse_UnknownOrMalformed_IsRefused(string? text) =>
        Assert.IsFalse(H264ProfileLevelId.TryParse(text, out _));

    [TestMethod]
    [DataRow(H264Profile.ConstrainedBaseline, 31, "42e01f")]
    [DataRow(H264Profile.ConstrainedHigh, 52, "640c34")]
    [DataRow(H264Profile.Main, 9, "4d100b")]
    public void ToString_WritesTheParameterValue(H264Profile profile, int level, string text)
    {
        H264ProfileLevelId value = new(profile, level);

        Assert.AreEqual(text, value.ToString());
        Assert.IsTrue(H264ProfileLevelId.TryParse(text, out H264ProfileLevelId read));
        Assert.AreEqual(value, read);
    }

    [TestMethod]
    public void TryRead_NoParameter_IsBaselineAtLevelOne()
    {
        Assert.IsTrue(
            H264ProfileLevelId.TryRead(
                ImmutableSortedDictionary<string, string>.Empty,
                out H264ProfileLevelId value
            )
        );
        Assert.AreEqual(H264ProfileLevelId.Default, value);
        Assert.AreEqual("42000a", value.ToString());
    }
}
