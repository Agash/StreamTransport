using System.Buffers;
using System.Collections.Immutable;
using Agash.StreamTransport.Media;
using Agash.StreamTransport.WebRtc.Sdp;

namespace Agash.StreamTransport.WebRtc.Rtp.PayloadFormats;

/// <summary>
/// An RTP payload format: how one codec's frames travel in RTP packets, what it offers in SDP, and what a
/// payload says about the frame it belongs to. Formats are stateless singletons; the packetizers and
/// depacketizers they create hold the per-stream state.
/// </summary>
public abstract class RtpPayloadFormat
{
    /// <summary>The <c>a=rtpmap</c> encoding name, compared without regard to case.</summary>
    public abstract string EncodingName { get; }

    /// <summary>Whether the format carries audio or video.</summary>
    public abstract SdpMediaKind Kind { get; }

    /// <summary>The RTP clock rate in Hz.</summary>
    public abstract int ClockRate { get; }

    /// <summary>The channel count in <c>a=rtpmap</c>, for audio formats that state one.</summary>
    public virtual int? Channels => null;

    /// <summary>
    /// The <c>a=fmtp</c> parameter sets this endpoint offers, most preferred first, each under a payload
    /// type of its own: the H.264 profiles, say. Null in the list stands for no parameters.
    /// </summary>
    public virtual ImmutableArray<string?> FormatParameterSets => [null];

    /// <summary>
    /// Whether a local and a remote parameter set of this format describe the same stream, so one payload
    /// type can carry it (RFC 3264 section 6.1): beyond the encoding name and clock rate, which match
    /// already, the parameters the format's RFC says must agree. Every set matches by default.
    /// </summary>
    /// <param name="local">This endpoint's <c>a=fmtp</c> parameters, or null.</param>
    /// <param name="remote">The peer's, or null.</param>
    /// <returns>True when they match.</returns>
    public virtual bool AreCompatible(string? local, string? remote) => true;

    /// <summary>The <c>a=rtcp-fb</c> feedback this endpoint offers for the format.</summary>
    public virtual ImmutableArray<string> RtcpFeedback => [];

    /// <summary>
    /// The traits a keyframe's payloads must carry between them for a decoder to start from it: the
    /// parameter sets or sequence header the format sends in-band.
    /// </summary>
    public virtual RtpPayloadTraits KeyframeRequires => RtpPayloadTraits.None;

    /// <summary>Creates the packetizer for one send stream.</summary>
    /// <returns>The packetizer.</returns>
    public abstract IRtpPacketizer CreatePacketizer();

    /// <summary>Creates the depacketizer for one receive stream.</summary>
    /// <returns>The depacketizer.</returns>
    public abstract IRtpDepacketizer CreateDepacketizer();

    /// <summary>What one payload says about its frame, read from the payload headers alone.</summary>
    /// <param name="payload">An RTP payload of this format.</param>
    /// <returns>The payload's traits.</returns>
    public virtual RtpPayloadTraits Inspect(ReadOnlySpan<byte> payload) => RtpPayloadTraits.None;

    /// <summary>The codec as offered in SDP under a payload type, with its preferred parameters.</summary>
    /// <param name="payloadType">The dynamic payload type.</param>
    /// <returns>The SDP codec.</returns>
    public SdpCodec ToSdpCodec(int payloadType) => ToSdpCodec(payloadType, FormatParameterSets[0]);

    /// <summary>The codec as offered in SDP under a payload type, with one of its parameter sets.</summary>
    /// <param name="payloadType">The dynamic payload type.</param>
    /// <param name="formatParameters">One of <see cref="FormatParameterSets"/>.</param>
    /// <returns>The SDP codec.</returns>
    public SdpCodec ToSdpCodec(int payloadType, string? formatParameters) =>
        new(payloadType, EncodingName, ClockRate, Channels, formatParameters, RtcpFeedback);

    /// <summary>Whether a negotiated codec is this format: the encoding name and clock rate agree.</summary>
    /// <param name="codec">The codec from a session description.</param>
    /// <returns>True when the codec is this format.</returns>
    public bool Matches(SdpCodec codec) =>
        string.Equals(codec.EncodingName, EncodingName, StringComparison.OrdinalIgnoreCase)
        && codec.ClockRate == ClockRate;

    /// <inheritdoc/>
    public override string ToString() => $"{EncodingName}/{ClockRate}";
}

/// <summary>What an RTP payload carries that frame assembly and decoder start-up care about.</summary>
[Flags]
public enum RtpPayloadTraits
{
    /// <summary>Nothing of note.</summary>
    None = 0,

    /// <summary>
    /// The payload opens a coded sequence, where a receiver that lost its place can start again: an H.265
    /// VPS, an H.264 SPS, or an AV1 packet with the N bit.
    /// </summary>
    SequenceStart = 1 << 0,

    /// <summary>Part of a picture a decoder can start from: an H.265 IRAP, an H.264 IDR, an AV1 key frame.</summary>
    Keyframe = 1 << 1,

    /// <summary>An H.265 video parameter set.</summary>
    VideoParameters = 1 << 2,

    /// <summary>An H.264 or H.265 sequence parameter set, or an AV1 sequence header.</summary>
    SequenceParameters = 1 << 3,

    /// <summary>An H.264 or H.265 picture parameter set.</summary>
    PictureParameters = 1 << 4,
}

/// <summary>Splits encoded frames into RTP payloads for one send stream.</summary>
public interface IRtpPacketizer
{
    /// <summary>
    /// Splits one encoded frame into RTP payloads, written into <paramref name="writer"/> after resetting
    /// it. The caller sets the RTP marker bit on the last payload.
    /// </summary>
    /// <param name="frame">The encoded frame.</param>
    /// <param name="maxPayloadSize">
    /// The largest RTP payload in bytes, which follows the path MTU and so can change between frames.
    /// </param>
    /// <param name="writer">Reusable payload storage.</param>
    void Packetize(ReadOnlySpan<byte> frame, int maxPayloadSize, RtpPayloadWriter writer);
}

/// <summary>Reassembles encoded frames from the RTP payloads of one receive stream, fed in sequence order.</summary>
public interface IRtpDepacketizer : IDisposable
{
    /// <summary>
    /// Pushes one payload. When it completes a frame (the marker bit, or a format with one frame per
    /// packet) returns true with the frame, which the caller owns and disposes.
    /// </summary>
    /// <param name="payload">The payload, borrowed for this call.</param>
    /// <param name="marker">The RTP marker bit.</param>
    /// <param name="frame">The completed frame when the method returns true.</param>
    /// <returns>True when a frame completed.</returns>
    bool TryPush(ReadOnlySpan<byte> payload, bool marker, out EncodedFrameBuffer frame);
}
