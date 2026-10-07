using Agash.StreamTransport.Media;

namespace Agash.StreamTransport;

/// <summary>
/// Where one timing frame's latency went, from capture to presentation, after libwebrtc's
/// TimingFrameInfo. The sender stamps are offsets from capture on the sender's clock; the receiver stamps are
/// on this side's wall clock. The stages that span both machines need the clock offset between them, which
/// is estimated from the sender's reports; they are null until it is known.
/// </summary>
/// <param name="Reasons">Why the sender timed the frame.</param>
/// <param name="Capture">When the frame was captured, on the sender's clock.</param>
/// <param name="EncodeStart">When the sender's encoder took it, after capture.</param>
/// <param name="EncodeFinish">When the sender's encoder produced it, after capture.</param>
/// <param name="PacketizationFinish">When the sender had its packets queued, after capture.</param>
/// <param name="PacerExit">When its last packet left the sender, after capture.</param>
/// <param name="FirstPacketReceived">When its first packet arrived here.</param>
/// <param name="LastPacketReceived">When its last packet arrived here.</param>
/// <param name="DecodeStart">When decoding started here.</param>
/// <param name="DecodeFinish">When decoding finished here.</param>
/// <param name="Presented">When it was handed to the sink.</param>
/// <param name="SenderClockOffset">This side's clock less the sender's, when estimated.</param>
public sealed record VideoFrameTimingReport(
    FrameTimingReasons Reasons,
    NtpTime Capture,
    TimeSpan EncodeStart,
    TimeSpan EncodeFinish,
    TimeSpan PacketizationFinish,
    TimeSpan PacerExit,
    NtpTime FirstPacketReceived,
    NtpTime LastPacketReceived,
    NtpTime DecodeStart,
    NtpTime DecodeFinish,
    NtpTime Presented,
    TimeSpan? SenderClockOffset
)
{
    /// <summary>From capture until the encoder took the frame: capture, conversion and the encoder's queue.</summary>
    public TimeSpan EncodeQueue => EncodeStart;

    /// <summary>The encoder's time on the frame.</summary>
    public TimeSpan Encode => EncodeFinish - EncodeStart;

    /// <summary>From the encoder's output to the packets queued.</summary>
    public TimeSpan Packetize => PacketizationFinish - EncodeFinish;

    /// <summary>The frame's packets waiting in the pacer, until the last left.</summary>
    public TimeSpan Pacer => PacerExit - PacketizationFinish;

    /// <summary>From the last packet leaving the sender to it arriving here; null without the clock offset.</summary>
    public TimeSpan? Network =>
        SenderClockOffset is { } offset
            ? Between(Capture + PacerExit + offset, LastPacketReceived)
            : null;

    /// <summary>How long the frame's packets took to arrive, first to last.</summary>
    public TimeSpan Receive => Between(FirstPacketReceived, LastPacketReceived);

    /// <summary>From the last packet to decoding: reassembly, recovery and the decoder's queue.</summary>
    public TimeSpan Assembly => Between(LastPacketReceived, DecodeStart);

    /// <summary>The decoder's time on the frame.</summary>
    public TimeSpan Decode => Between(DecodeStart, DecodeFinish);

    /// <summary>From decoded to presented: the playout buffer, which lip-syncs and absorbs jitter.</summary>
    public TimeSpan Playout => Between(DecodeFinish, Presented);

    /// <summary>From capture to presentation; null without the clock offset.</summary>
    public TimeSpan? EndToEnd =>
        SenderClockOffset is { } offset ? Between(Capture + offset, Presented) : null;

    private static TimeSpan Between(NtpTime from, NtpTime to) =>
        TimeSpan.FromTicks((to.Nanoseconds - from.Nanoseconds) / 100);
}
