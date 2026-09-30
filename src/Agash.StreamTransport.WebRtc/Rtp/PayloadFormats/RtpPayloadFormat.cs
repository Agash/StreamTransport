using System.Buffers;
using System.Collections.Immutable;
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

    /// <summary>The <c>a=fmtp</c> parameters this endpoint offers.</summary>
    public virtual string? FormatParameters => null;

    /// <summary>The <c>a=rtcp-fb</c> feedback this endpoint offers for the format.</summary>
    public virtual ImmutableArray<string> RtcpFeedback => [];

    /// <summary>
    /// The traits a keyframe's payloads must carry between them for a decoder to start from it: the
    /// parameter sets or sequence header the format sends in-band.
    /// </summary>
    public virtual RtpPayloadTraits KeyframeRequires => RtpPayloadTraits.None;

    /// <summary>Creates the packetizer for one send stream.</summary>
    /// <param name="maxPayloadSize">The largest RTP payload in bytes.</param>
    /// <returns>The packetizer.</returns>
    public abstract IRtpPacketizer CreatePacketizer(int maxPayloadSize);

    /// <summary>Creates the depacketizer for one receive stream.</summary>
    /// <returns>The depacketizer.</returns>
    public abstract IRtpDepacketizer CreateDepacketizer();

    /// <summary>What one payload says about its frame, read from the payload headers alone.</summary>
    /// <param name="payload">An RTP payload of this format.</param>
    /// <returns>The payload's traits.</returns>
    public virtual RtpPayloadTraits Inspect(ReadOnlySpan<byte> payload) => RtpPayloadTraits.None;

    /// <summary>The codec as offered in SDP under a payload type.</summary>
    /// <param name="payloadType">The dynamic payload type.</param>
    /// <returns>The SDP codec.</returns>
    public SdpCodec ToSdpCodec(int payloadType) =>
        new(payloadType, EncodingName, ClockRate, Channels, FormatParameters, RtcpFeedback);

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
    /// <param name="writer">Reusable payload storage.</param>
    void Packetize(ReadOnlySpan<byte> frame, RtpPayloadWriter writer);
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

/// <summary>
/// An encoded frame in a buffer rented from <see cref="ArrayPool{T}.Shared"/>. Its holder owns the buffer
/// and returns it by disposing; a default value owns nothing.
/// </summary>
public readonly struct EncodedFrameBuffer : IDisposable, IEquatable<EncodedFrameBuffer>
{
    private readonly byte[]? _array;

    /// <summary>Takes ownership of a rented buffer holding a frame in its first bytes.</summary>
    /// <param name="array">A buffer rented from <see cref="ArrayPool{T}.Shared"/>.</param>
    /// <param name="length">The frame's length in bytes.</param>
    public EncodedFrameBuffer(byte[] array, int length)
    {
        ArgumentNullException.ThrowIfNull(array);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, array.Length);
        _array = array;
        Length = length;
    }

    /// <summary>The frame's bytes.</summary>
    public ReadOnlySpan<byte> Span => _array.AsSpan(0, Length);

    /// <summary>The frame's bytes, for use across an await.</summary>
    public ReadOnlyMemory<byte> Memory => _array.AsMemory(0, Length);

    /// <summary>The frame's length in bytes.</summary>
    public int Length { get; }

    /// <summary>Returns the buffer to the pool.</summary>
    public void Dispose()
    {
        if (_array is not null)
        {
            ArrayPool<byte>.Shared.Return(_array);
        }
    }

    /// <inheritdoc/>
    public bool Equals(EncodedFrameBuffer other) =>
        ReferenceEquals(_array, other._array) && Length == other.Length;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is EncodedFrameBuffer other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(_array, Length);

    /// <summary>Whether two values hold the same buffer and length.</summary>
    /// <param name="left">The first value.</param>
    /// <param name="right">The second value.</param>
    /// <returns>True when they are equal.</returns>
    public static bool operator ==(EncodedFrameBuffer left, EncodedFrameBuffer right) =>
        left.Equals(right);

    /// <summary>Whether two values hold different buffers or lengths.</summary>
    /// <param name="left">The first value.</param>
    /// <param name="right">The second value.</param>
    /// <returns>True when they differ.</returns>
    public static bool operator !=(EncodedFrameBuffer left, EncodedFrameBuffer right) =>
        !left.Equals(right);
}
