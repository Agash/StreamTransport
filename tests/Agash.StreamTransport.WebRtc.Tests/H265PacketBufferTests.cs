using Agash.StreamTransport.WebRtc.Rtp;

namespace Agash.StreamTransport.WebRtc.Tests;

/// <summary>
/// Tests the sequence-aware H.265 packet buffer: it must assemble a complete keyframe (VPS+SPS+PPS+IDR), pass
/// delta frames through in order, hold a frame behind a sequence gap (rather than emit a corrupt one), and
/// complete the held frame(s) once the missing packet arrives - the core of NACK/RTX in-order recovery.
/// </summary>
[TestClass]
public sealed class H265PacketBufferTests
{
    // A single-NAL H.265 RTP payload of the given NAL type (RFC 7798): 2-byte NAL header (F=0, type, layer 0,
    // tid 1) followed by a little dummy payload.
    private static byte[] SingleNal(int type) => [(byte)((type << 1) & 0x7E), 0x01, 0xAA, 0xBB, 0xCC];

    private const int Vps = 32, Sps = 33, Pps = 34, IdrWRadl = 19, TrailR = 1;

    [TestMethod]
    public void AssemblesCompleteKeyframe_InOrder()
    {
        using var pb = new H265PacketBuffer();
        ushort seq = 1000;
        const uint ts = 90000;

        Assert.AreEqual(0, pb.Insert(seq++, ts, marker: false, SingleNal(Vps)).Frames.Count);
        Assert.AreEqual(0, pb.Insert(seq++, ts, marker: false, SingleNal(Sps)).Frames.Count);
        Assert.AreEqual(0, pb.Insert(seq++, ts, marker: false, SingleNal(Pps)).Frames.Count);
        H265PacketBuffer.InsertResult result = pb.Insert(seq, ts, marker: true, SingleNal(IdrWRadl));

        Assert.AreEqual(1, result.Frames.Count, "the IDR marker packet completes the keyframe.");
        Assert.IsTrue(result.Frames[0].IsKeyframe, "VPS+SPS+PPS+IDR is a keyframe.");
        Assert.IsTrue(result.Frames[0].Length > 0);
    }

    [TestMethod]
    public void HoldsFrameBehindGap_ThenCompletesOnLateArrival()
    {
        using var pb = new H265PacketBuffer();
        ushort seq = 2000;
        const uint kfTs = 180000;

        // Keyframe (4 single-NAL packets, seq 2000..2003).
        pb.Insert(seq++, kfTs, false, SingleNal(Vps));
        pb.Insert(seq++, kfTs, false, SingleNal(Sps));
        pb.Insert(seq++, kfTs, false, SingleNal(Pps));
        Assert.AreEqual(1, pb.Insert(seq++, kfTs, true, SingleNal(IdrWRadl)).Frames.Count);

        // Two one-packet delta frames at seq 2004 and 2005, but 2005 arrives BEFORE 2004 (reordering / RTX).
        H265PacketBuffer.InsertResult outOfOrder = pb.Insert(2005, 183000, true, SingleNal(TrailR));
        Assert.AreEqual(0, outOfOrder.Frames.Count, "frame 2005 must wait for the missing 2004, not emit early.");
        Assert.IsTrue(pb.HasUnresolvedGap, "the buffer reports the unfilled hole at 2004.");

        // The missing packet arrives: it completes its own frame AND unblocks the buffered 2005.
        H265PacketBuffer.InsertResult filled = pb.Insert(2004, 182000, true, SingleNal(TrailR));
        Assert.AreEqual(2, filled.Frames.Count, "filling the hole emits both 2004 and the held 2005, in order.");
        Assert.IsFalse(filled.Frames[0].IsKeyframe);
        Assert.IsFalse(pb.HasUnresolvedGap, "no gap remains once the hole is filled.");
    }

    [TestMethod]
    public void IdrWithoutParameterSets_RequestsKeyframe()
    {
        using var pb = new H265PacketBuffer();

        // An IDR that begins a coded video sequence is not directly continuous and carries no VPS, so it cannot
        // start assembly - and when an IRAP lacks its parameter sets the buffer asks for a fresh keyframe.
        // Drive it via a VPS-led frame missing SPS/PPS: VPS + IDR only.
        ushort seq = 3000;
        const uint ts = 270000;
        pb.Insert(seq++, ts, false, SingleNal(Vps));
        H265PacketBuffer.InsertResult result = pb.Insert(seq, ts, true, SingleNal(IdrWRadl));

        Assert.AreEqual(0, result.Frames.Count, "an IRAP without SPS/PPS is not assembled.");
        Assert.IsTrue(result.KeyframeRequired, "a keyframe is requested when parameter sets are missing.");
    }
}
