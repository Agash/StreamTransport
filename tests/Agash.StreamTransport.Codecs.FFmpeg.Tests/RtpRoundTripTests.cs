using Agash.StreamTransport.Media;
using Agash.StreamTransport.WebRtc.Rtp;
using Agash.StreamTransport.WebRtc.Rtp.PayloadFormats;

namespace Agash.StreamTransport.Codecs.FFmpeg.Tests;

// Real encoder output through the RTP payload formats and the receive-side frame buffer, then decoded:
// the pictures that come out are the pictures that went in.
[TestClass]
public sealed class RtpRoundTripTests
{
    private const int FrameCount = 20;

    [TestMethod]
    [DataRow("H264", 1200)]
    [DataRow("H264", 200)]
    [DataRow("H265", 1200)]
    [DataRow("H265", 200)]
    [DataRow("AV1", 1200)]
    [DataRow("AV1", 200)]
    public void EncodedStream_ThroughRtpWithReorderedPackets_DecodesToThePictures(
        string codecName,
        int maxPayloadSize
    )
    {
        VideoCodecId codec = new(codecName);
        List<(byte[] Data, MediaTimestamp Timestamp)> stream = Streams.Encode(codec, FrameCount);
        Assert.IsTrue(
            RtpPayloadFormatRegistry.BuiltIn.TryGet(codec.ToString(), out RtpPayloadFormat? format)
        );
        IRtpPacketizer packetizer = format.CreatePacketizer(maxPayloadSize);
        RtpPayloadWriter writer = new();
        using RtpFrameBuffer buffer = new(format);
        List<byte[]> received = [];
        bool firstIsKeyframe = false;
        ushort sequence = 65_000; // crosses the 16-bit wrap
        for (int i = 0; i < stream.Count; i++)
        {
            packetizer.Packetize(stream[i].Data, writer);
            uint timestamp = (uint)(i * 3000);
            ushort first = sequence;
            sequence += (ushort)writer.Count;

            // Each frame's packets arrive last-first after the first, as retransmissions might.
            int[] order = [0, .. Enumerable.Range(1, writer.Count - 1).Reverse()];
            foreach (int p in order)
            {
                Assert.IsLessThanOrEqualTo(maxPayloadSize, writer[p].Length);
                RtpFrameBuffer.InsertResult result = buffer.Insert(
                    (ushort)(first + p),
                    timestamp,
                    p == writer.Count - 1,
                    writer[p].Span
                );
                Assert.IsFalse(result.KeyframeRequired, $"Frame {i} asked for a keyframe.");
                foreach (RtpFrameBuffer.AssembledFrame frame in result.Frames)
                {
                    firstIsKeyframe |= received.Count == 0 && frame.IsKeyframe;
                    using (frame.Frame)
                    {
                        received.Add(frame.Frame.Span.ToArray());
                    }
                }
            }
        }

        Assert.IsTrue(firstIsKeyframe, "The first frame out is a keyframe.");
        Assert.HasCount(FrameCount, received);
        List<(int Width, int Height, double MeanLuma)> pictures = Reference.Decode(codec, received);
        Assert.HasCount(FrameCount, pictures);
        for (int i = 0; i < FrameCount; i++)
        {
            Assert.AreEqual(Pictures.MeanLuma(i), pictures[i].MeanLuma, 6.0, $"Frame {i}.");
        }
    }
}
