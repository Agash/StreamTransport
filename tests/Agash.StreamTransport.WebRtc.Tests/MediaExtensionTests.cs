using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;
using Agash.StreamTransport.Media;
using Agash.StreamTransport.WebRtc.Rtp;
using Agash.StreamTransport.WebRtc.Sdp;
using Agash.StreamTransport.WebRtc.Transport;

namespace Agash.StreamTransport.WebRtc.Tests;

// RFC 6464 (client-to-mixer audio level) and CVO (3GPP TS 26.114 section 7.4.5, RFC 7742 section 4), which
// RFC 8834 sections 5.2.2 and 5.2.5 make mandatory.
[TestClass]
public sealed class MediaExtensionTests
{
    private static readonly InMemoryIceNetwork OfflineNetwork = new();
    private static readonly RtcCertificate Certificate = RtcCertificate.Generate();

    // Section 3: -dBov of the RMS; full scale is 0, a half-scale sine -9, and silence 127.
    [TestMethod]
    public void Level_FollowsTheRmsInDecibelsBelowOverload()
    {
        Assert.AreEqual(0, AudioLevel.FromMeanSquare(1).DecibelsBelowOverload);
        Assert.AreEqual(9, AudioLevel.FromMeanSquare(0.125).DecibelsBelowOverload);
        Assert.AreEqual(AudioLevel.Silent, AudioLevel.FromMeanSquare(0).DecibelsBelowOverload);
        Assert.AreEqual(AudioLevel.Silent, AudioLevel.FromMeanSquare(1e-14).DecibelsBelowOverload);
    }

    // The same sine as floats and as 16-bit samples measures alike.
    [TestMethod]
    public void Measure_FloatAndSixteenBit_Agree()
    {
        float[] floats = new float[960];
        short[] shorts = new short[960];
        for (int i = 0; i < floats.Length; i++)
        {
            floats[i] = 0.5f * MathF.Sin(2 * MathF.PI * 1000 * i / 48_000f);
            shorts[i] = (short)MathF.Round(floats[i] * 32767);
        }

        (double floatSquares, long floatCount) = AudioLevel.Measure(
            new AudioFrame(
                MemoryMarshal.AsBytes(floats.AsSpan()),
                new AudioFormat(SampleFormat.F32, 48_000, 1),
                default
            )
        );
        (double shortSquares, long shortCount) = AudioLevel.Measure(
            new AudioFrame(
                MemoryMarshal.AsBytes(shorts.AsSpan()),
                new AudioFormat(SampleFormat.S16, 48_000, 1),
                default
            )
        );

        Assert.AreEqual(960, floatCount);
        Assert.AreEqual(960, shortCount);
        Assert.AreEqual(0.125, floatSquares / floatCount, 1e-4);
        Assert.AreEqual(floatSquares, shortSquares, 0.01);
    }

    // The CVO byte: F flips, R1 R0 count clockwise quarter turns; the camera bit is not orientation.
    [TestMethod]
    public void Cvo_RoundTripsEveryOrientation()
    {
        foreach (VideoRotation rotation in Enum.GetValues<VideoRotation>())
        {
            foreach (bool mirrored in (bool[])[false, true])
            {
                VideoOrientation orientation = new(rotation, mirrored);
                Assert.AreEqual(
                    orientation,
                    MediaExtensionValues.FromCvo(MediaExtensionValues.ToCvo(orientation))
                );
            }
        }

        Assert.AreEqual((byte)1, MediaExtensionValues.ToCvo(new(VideoRotation.Clockwise90)));
        Assert.AreEqual(
            new VideoOrientation(VideoRotation.CounterClockwise90),
            MediaExtensionValues.FromCvo(0x0B)
        );
    }

    // Section 4: the V bit means voice only where the sender's mapping does not say vad=off.
    [TestMethod]
    public void AudioLevelByte_ReadsTheVBitOnlyWhenInUse()
    {
        Assert.IsTrue(MediaExtensionValues.FromAudioLevel(0x89, voiceActivity: true).Voice);
        Assert.IsNull(MediaExtensionValues.FromAudioLevel(0x89, voiceActivity: false).Voice);
        Assert.AreEqual(9, MediaExtensionValues.FromAudioLevel(0x89, false).DecibelsBelowOverload);
        Assert.AreEqual((byte)9, MediaExtensionValues.ToAudioLevel(new AudioLevel(9)));
    }

    [TestMethod]
    [DataRow(6, 7)]
    [DataRow(20, 21)]
    public void ExtensionMap_LevelAndOrientation_RoundTripInEitherForm(int level, int orientation)
    {
        RtpExtensionMap map = new(0, 0, 0, AudioLevel: level, VideoOrientation: orientation);
        byte[] block = new byte[map.MaximumBlockLength];

        int length = map.Write(
            block,
            new RtpExtensionValues(AudioLevel: 0x89, VideoOrientation: 0x05),
            out _
        );

        RtpExtensionValues read = map.Read(
            BinaryPrimitives.ReadUInt16BigEndian(block),
            block.AsSpan(4, length - 4)
        );
        Assert.AreEqual((byte)0x89, read.AudioLevel);
        Assert.AreEqual((byte)0x05, read.VideoOrientation);
    }

    [TestMethod]
    public void Extmap_KeepsItsAttributes()
    {
        SdpExtension? read = SdpReader.ParseExtmap(
            "6/sendonly urn:ietf:params:rtp-hdrext:ssrc-audio-level vad=on"
        );

        Assert.AreEqual(
            new SdpExtension(6, RtpExtensionUris.AudioLevel, SdpDirection.SendOnly, "vad=on"),
            read
        );
    }

    // This side runs no VAD: it says vad=off in its own mapping, whatever the offerer said in its.
    [TestMethod]
    public async Task Answer_SaysVadOffForWhatThisSideSends()
    {
        SdpCodec opus = new(111, "opus", 48000, 2, null, []);
        await using PeerConnection offerer = Peer("10.0.0.1", 0xA1, opus);
        await using PeerConnection answerer = Peer("10.0.0.2", 0xB1, opus);
        SdpDescription offer = offerer.CreateOffer();
        offer = offer with
        {
            Media =
            [
                offer.Media[0] with
                {
                    Extensions =
                    [
                        .. offer
                            .Media[0]
                            .Extensions.Select(static e =>
                                e.Uri == RtpExtensionUris.AudioLevel
                                    ? e with
                                    {
                                        Attributes = null,
                                    }
                                    : e
                            ),
                    ],
                },
            ],
        };

        answerer.SetRemoteDescription(offer, SdpType.Offer);
        SdpDescription answer = answerer.CreateAnswer();

        Assert.AreEqual(
            "vad=off",
            answer
                .Media[0]
                .Extensions.Single(static e => e.Uri == RtpExtensionUris.AudioLevel)
                .Attributes
        );
        Assert.IsTrue(answerer.PeerSignalsVoiceActivity, "the offer left vad at its default, on");
    }

    private static PeerConnection Peer(string address, uint ssrc, SdpCodec codec) =>
        new(
            new PeerConnectionOptions
            {
                SocketFactory = OfflineNetwork.Factory(IPAddress.Parse(address)),
                Media = [new MediaLine("0", SdpMediaKind.Audio, ssrc, [codec])],
            },
            Certificate
        );
}
