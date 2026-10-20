using Agash.StreamTransport.WebRtc.Rtp;

namespace Agash.StreamTransport.WebRtc.Tests;

/// <summary>What one inserted packet produced: the frames it completed and whether a keyframe is needed.</summary>
internal readonly record struct InsertOutcome(
    List<RtpFrameBuffer.AssembledFrame> Frames,
    bool KeyframeRequired
);

/// <summary>Inserts into a fresh list per packet, so a test reads each packet's outcome on its own.</summary>
internal static class RtpFrameBufferInserts
{
    public static InsertOutcome Insert(
        this RtpFrameBuffer buffer,
        ushort sequenceNumber,
        uint rtpTimestamp,
        bool marker,
        ReadOnlySpan<byte> payload
    )
    {
        List<RtpFrameBuffer.AssembledFrame> frames = [];
        bool keyframeRequired = buffer.Insert(
            sequenceNumber,
            rtpTimestamp,
            marker,
            payload,
            frames
        );
        return new InsertOutcome(frames, keyframeRequired);
    }
}
