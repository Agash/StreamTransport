namespace Agash.StreamTransport.Media.Tests;

[TestClass]
public sealed class CodecIdTests
{
    [TestMethod]
    public void VideoCodecId_ComparesNamesWithoutCase()
    {
        Assert.AreEqual(VideoCodecId.H264, new VideoCodecId("h264"));
        Assert.AreEqual(VideoCodecId.H264.GetHashCode(), new VideoCodecId("h264").GetHashCode());
        Assert.AreNotEqual(VideoCodecId.H264, VideoCodecId.H265);
        Assert.AreEqual("AV1", VideoCodecId.AV1.ToString());
    }

    [TestMethod]
    public void VideoCodecId_NamesACodecThisLibraryLacks()
    {
        VideoCodecId vp9 = new("VP9");

        Assert.DoesNotContain(vp9, VideoCodecId.BuiltIn);
        Assert.AreEqual("VP9", vp9.Name);
    }

    [TestMethod]
    public void CodecIds_RejectBlankNamesAndDefaultToEmpty()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new VideoCodecId(" "));
        Assert.ThrowsExactly<ArgumentException>(() => new AudioCodecId(""));
        Assert.AreEqual(string.Empty, default(VideoCodecId).Name);
        Assert.AreNotEqual(default, new AudioCodecId("x"));
    }

    [TestMethod]
    public void AudioCodecId_MatchesTheRtpName()
    {
        Assert.AreEqual(AudioCodecId.Opus, new AudioCodecId("OPUS"));
        Assert.AreEqual("opus", AudioCodecId.Opus.Name);
    }
}
