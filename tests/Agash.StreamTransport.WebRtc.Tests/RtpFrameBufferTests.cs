using Agash.StreamTransport.WebRtc.Rtp;
using Agash.StreamTransport.WebRtc.Rtp.PayloadFormats;

namespace Agash.StreamTransport.WebRtc.Tests;

/// <summary>
/// Tests the sequence-aware frame buffer on H.265: it must assemble a complete keyframe (VPS+SPS+PPS+IDR), pass
/// delta frames through in order, hold a frame behind a sequence gap (rather than emit a corrupt one), and
/// complete the held frame(s) once the missing packet arrives - the core of NACK/RTX in-order recovery.
/// </summary>
[TestClass]
public sealed class RtpFrameBufferTests
{
    // A single-NAL H.265 RTP payload of the given NAL type (RFC 7798): 2-byte NAL header (F=0, type, layer 0,
    // tid 1) followed by a little dummy payload.
    private static byte[] SingleNal(int type) =>
        [(byte)((type << 1) & 0x7E), 0x01, 0xAA, 0xBB, 0xCC];

    private const int Vps = 32,
        Sps = 33,
        Pps = 34,
        IdrWRadl = 19,
        TrailR = 1;

    [TestMethod]
    public void AssemblesCompleteKeyframe_InOrder()
    {
        using var pb = new RtpFrameBuffer(H265PayloadFormat.Instance);
        ushort seq = 1000;
        const uint ts = 90000;

        Assert.AreEqual(0, pb.Insert(seq++, ts, marker: false, SingleNal(Vps)).Frames.Count);
        Assert.AreEqual(0, pb.Insert(seq++, ts, marker: false, SingleNal(Sps)).Frames.Count);
        Assert.AreEqual(0, pb.Insert(seq++, ts, marker: false, SingleNal(Pps)).Frames.Count);
        RtpFrameBuffer.InsertResult result = pb.Insert(
            seq,
            ts,
            marker: true,
            SingleNal(IdrWRadl)
        );

        Assert.AreEqual(1, result.Frames.Count, "the IDR marker packet completes the keyframe.");
        Assert.IsTrue(result.Frames[0].IsKeyframe, "VPS+SPS+PPS+IDR is a keyframe.");
        Assert.IsTrue(result.Frames[0].Frame.Length > 0);
    }

    [TestMethod]
    public void HoldsFrameBehindGap_ThenCompletesOnLateArrival()
    {
        using var pb = new RtpFrameBuffer(H265PayloadFormat.Instance);
        ushort seq = 2000;
        const uint kfTs = 180000;

        // Keyframe (4 single-NAL packets, seq 2000..2003).
        pb.Insert(seq++, kfTs, false, SingleNal(Vps));
        pb.Insert(seq++, kfTs, false, SingleNal(Sps));
        pb.Insert(seq++, kfTs, false, SingleNal(Pps));
        Assert.AreEqual(1, pb.Insert(seq++, kfTs, true, SingleNal(IdrWRadl)).Frames.Count);

        // Two one-packet delta frames at seq 2004 and 2005, but 2005 arrives BEFORE 2004 (reordering / RTX).
        RtpFrameBuffer.InsertResult outOfOrder = pb.Insert(2005, 183000, true, SingleNal(TrailR));
        Assert.AreEqual(
            0,
            outOfOrder.Frames.Count,
            "frame 2005 must wait for the missing 2004, not emit early."
        );
        Assert.IsTrue(pb.HasUnresolvedGap, "the buffer reports the unfilled hole at 2004.");

        // The missing packet arrives: it completes its own frame AND unblocks the buffered 2005.
        RtpFrameBuffer.InsertResult filled = pb.Insert(2004, 182000, true, SingleNal(TrailR));
        Assert.AreEqual(
            2,
            filled.Frames.Count,
            "filling the hole emits both 2004 and the held 2005, in order."
        );
        Assert.IsFalse(filled.Frames[0].IsKeyframe);
        Assert.IsFalse(pb.HasUnresolvedGap, "no gap remains once the hole is filled.");
    }

    [TestMethod]
    public void IdrWithoutParameterSets_RequestsKeyframe()
    {
        using var pb = new RtpFrameBuffer(H265PayloadFormat.Instance);

        // An IDR that begins a coded video sequence is not directly continuous and carries no VPS, so it cannot
        // start assembly - and when an IRAP lacks its parameter sets the buffer asks for a fresh keyframe.
        // Drive it via a VPS-led frame missing SPS/PPS: VPS + IDR only.
        ushort seq = 3000;
        const uint ts = 270000;
        pb.Insert(seq++, ts, false, SingleNal(Vps));
        RtpFrameBuffer.InsertResult result = pb.Insert(seq, ts, true, SingleNal(IdrWRadl));

        Assert.AreEqual(0, result.Frames.Count, "an IRAP without SPS/PPS is not assembled.");
        Assert.IsTrue(
            result.KeyframeRequired,
            "a keyframe is requested when parameter sets are missing."
        );
    }

    [TestMethod]
    public void H264_KeyframeWithParameterSets_AssemblesAcrossReorderedPackets()
    {
        byte[] sps = [0x67, .. Body(12)];
        byte[] pps = [0x68, .. Body(4)];
        byte[] idr = [0x65, .. Body(2000)];
        byte[] accessUnit = [0, 0, 0, 1, .. sps, 0, 0, 0, 1, .. pps, 0, 0, 0, 1, .. idr];
        List<byte[]> packets = Packets(H264PayloadFormat.Instance, accessUnit, 700);
        using RtpFrameBuffer buffer = new(H264PayloadFormat.Instance);

        List<RtpFrameBuffer.AssembledFrame> frames = InsertReversedAfterFirst(buffer, packets, 500, 9000);

        Assert.HasCount(1, frames);
        Assert.IsTrue(frames[0].IsKeyframe);
        CollectionAssert.AreEqual(accessUnit, frames[0].Frame.Span.ToArray());
        frames[0].Frame.Dispose();
    }

    [TestMethod]
    public void H264_IdrWithoutPps_RequestsKeyframe()
    {
        byte[] accessUnit = [0, 0, 0, 1, 0x67, .. Body(12), 0, 0, 0, 1, 0x65, .. Body(40)];
        List<byte[]> packets = Packets(H264PayloadFormat.Instance, accessUnit, 1100);
        using RtpFrameBuffer buffer = new(H264PayloadFormat.Instance);

        RtpFrameBuffer.InsertResult result = buffer.Insert(10, 9000, true, packets[0]);

        Assert.IsEmpty(result.Frames);
        Assert.IsTrue(result.KeyframeRequired);
    }

    [TestMethod]
    public void Av1_KeyframeThenDeltaAfterALostFrame_AssemblesAndAsksForAKeyframe()
    {
        byte[] keyframe = [0x0A, 11, .. Body(11), 0x32, 0xD0, 0x0F, .. Body(2000)];
        byte[] delta = [0x32, 100, .. Body(100)];
        List<byte[]> keyPackets = Packets(Av1PayloadFormat.Instance, keyframe, 600);
        using RtpFrameBuffer buffer = new(Av1PayloadFormat.Instance);

        List<RtpFrameBuffer.AssembledFrame> frames = InsertReversedAfterFirst(buffer, keyPackets, 60_000, 3000);
        Assert.HasCount(1, frames);
        Assert.IsTrue(frames[0].IsKeyframe);
        byte[] expected = [0x12, 0x00, .. keyframe];
        CollectionAssert.AreEqual(expected, frames[0].Frame.Span.ToArray());
        frames[0].Frame.Dispose();

        // The delta frame after the keyframe's is lost; the one after it arrives.
        ushort next = (ushort)(60_000 + keyPackets.Count + 1);
        RtpFrameBuffer.InsertResult late = buffer.Insert(
            next,
            9000,
            true,
            Packets(Av1PayloadFormat.Instance, delta, 600)[0]
        );

        Assert.IsEmpty(late.Frames, "a frame behind a sequence gap is held for retransmission");
        Assert.IsTrue(buffer.HasUnresolvedGap);
    }

    private static List<byte[]> Packets(RtpPayloadFormat format, byte[] frame, int maxPayloadSize)
    {
        RtpPayloadWriter writer = new();
        format.CreatePacketizer(maxPayloadSize).Packetize(frame, writer);
        return [.. Enumerable.Range(0, writer.Count).Select(i => writer[i].ToArray())];
    }

    // Inserts the first packet, then the rest last-first, as retransmissions might deliver them.
    private static List<RtpFrameBuffer.AssembledFrame> InsertReversedAfterFirst(
        RtpFrameBuffer buffer,
        List<byte[]> packets,
        ushort firstSequence,
        uint timestamp
    )
    {
        List<RtpFrameBuffer.AssembledFrame> frames = [];
        int[] order = [0, .. Enumerable.Range(1, packets.Count - 1).Reverse()];
        foreach (int i in order)
        {
            RtpFrameBuffer.InsertResult result = buffer.Insert(
                (ushort)(firstSequence + i),
                timestamp,
                i == packets.Count - 1,
                packets[i]
            );
            frames.AddRange(result.Frames);
        }

        return frames;
    }

    private static byte[] Body(int length) =>
        [.. Enumerable.Range(0, length).Select(static i => (byte)((i * 13) + 1))];
}
