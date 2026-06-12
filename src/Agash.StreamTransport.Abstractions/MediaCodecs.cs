using System.Buffers;

namespace Agash.StreamTransport;

/// <summary>
/// A byte buffer rented from <see cref="ArrayPool{T}.Shared"/> with a valid length, used to hand an assembled
/// frame from a depacketizer to a decode worker across a thread boundary with no per-frame allocation. The
/// receiver that takes ownership must <see cref="Dispose"/> it once the bytes are consumed (e.g. after decode)
/// to return the backing array to the pool. A defaulted value owns nothing and is safe to dispose.
/// </summary>
public readonly struct PooledBuffer : IDisposable, IEquatable<PooledBuffer>
{
    private readonly byte[]? _array;

    /// <summary>Wraps a pool-rented <paramref name="array"/>, exposing its first <paramref name="length"/> bytes.</summary>
    /// <param name="array">A buffer rented from <see cref="ArrayPool{T}.Shared"/> whose ownership passes to this value.</param>
    /// <param name="length">The number of valid bytes at the start of <paramref name="array"/>.</param>
    public PooledBuffer(byte[] array, int length)
    {
        _array = array;
        Memory = array.AsMemory(0, length);
    }

    /// <summary>The valid bytes of the assembled frame.</summary>
    public ReadOnlyMemory<byte> Memory { get; }

    /// <summary>Returns the backing array to the shared pool. Safe to call on a defaulted value.</summary>
    public void Dispose()
    {
        if (_array is not null)
        {
            ArrayPool<byte>.Shared.Return(_array);
        }
    }

    /// <inheritdoc/>
    public bool Equals(PooledBuffer other) => _array == other._array && Memory.Equals(other.Memory);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is PooledBuffer other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => _array is null ? 0 : _array.GetHashCode();

    /// <summary>Equality over the same backing array and slice.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    public static bool operator ==(PooledBuffer left, PooledBuffer right) => left.Equals(right);

    /// <summary>Inequality over the backing array and slice.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    public static bool operator !=(PooledBuffer left, PooledBuffer right) => !left.Equals(right);
}

/// <summary>
/// An encoded video access unit plus the timing the packetizer needs: the RTP duration (advanced by the
/// real capture interval, not a nominal frame time) and the capture instant of the frame that produced it
/// (for abs-capture-time). The codec↔transport interchange unit on the send side.
/// </summary>
/// <param name="DurationRtpUnits">RTP timestamp advance for this access unit, in the 90 kHz video clock.</param>
/// <param name="AccessUnit">The encoded HEVC/AVC/AV1 access unit (Annex B or length-prefixed per codec).</param>
/// <param name="CaptureNs">Capture time, in nanoseconds, of the frame this access unit encodes.</param>
public readonly record struct EncodedVideoAccessUnit(uint DurationRtpUnits, byte[] AccessUnit, long CaptureNs);

/// <summary>
/// An encoded audio packet plus its RTP duration (samples per channel in the track clock). The codec↔transport
/// interchange unit on the send side.
/// </summary>
/// <param name="DurationRtpUnits">RTP timestamp advance, in per-channel samples at the track clock rate.</param>
/// <param name="Payload">The encoded Opus packet.</param>
public readonly record struct EncodedAudioPacket(uint DurationRtpUnits, byte[] Payload);

/// <summary>Settings for an <see cref="IVideoEncoder"/>, fixed for the lifetime of one send session.</summary>
/// <param name="Fps">Nominal frame rate, used as the duration fallback for the first/non-monotonic frame.</param>
/// <param name="Bitrate">Target encode bitrate in bits per second.</param>
/// <param name="EncoderName">Explicit hardware encoder name, or null to auto-select per platform/GPU.</param>
/// <param name="GpuDeviceHandle">Shared GPU device handle for zero-copy encode (Windows ID3D11Device*), or 0.</param>
/// <param name="PreserveAlpha">Pack colour|alpha side by side so an opaque codec carries transparency.</param>
/// <param name="MaxBFrames">Maximum consecutive B-frames (compression vs. reorder latency).</param>
public sealed record VideoEncoderSettings(
    int Fps, long Bitrate, string? EncoderName = null, nint GpuDeviceHandle = 0, bool PreserveAlpha = false, int MaxBFrames = 0);

/// <summary>Settings for an <see cref="IVideoDecoder"/>, fixed for the lifetime of one receive session.</summary>
/// <param name="PreferGpuOutput">Decode straight into GPU surfaces for a zero-copy publish (Windows D3D11).</param>
/// <param name="PreserveAlpha">The stream carries side-by-side alpha; split it back on the CPU decode path.</param>
public sealed record VideoDecoderSettings(bool PreferGpuOutput = false, bool PreserveAlpha = false);

/// <summary>Encodes <see cref="VideoFrame"/>s to a compressed video bitstream.</summary>
public interface IVideoEncoder : IDisposable
{
    /// <summary>
    /// Encode one frame. Returns the access unit and its timing, or null when the encoder buffered the frame
    /// (pipeline fill / B-frame reorder) and produced no output this call.
    /// </summary>
    EncodedVideoAccessUnit? Encode(VideoFrame frame);

    /// <summary>
    /// Retune the running encoder to a new target bitrate (driven by congestion control) without tearing it
    /// down. Best-effort: an encoder that cannot retune live leaves this as a no-op.
    /// </summary>
    void UpdateBitrate(long bitrateBps) { }
}

/// <summary>Decodes a compressed video bitstream back to <see cref="VideoFrame"/>s.</summary>
public interface IVideoDecoder : IDisposable
{
    /// <summary>True when frames are decoded into GPU surfaces (zero-copy), false for the CPU path.</summary>
    bool IsGpuOutput { get; }

    /// <summary>
    /// Adopt the publisher's negotiated side-by-side-alpha setting, before the first frame is decoded. Lets a
    /// subscriber match the publisher without a flag of its own.
    /// </summary>
    void SetPreserveAlpha(bool value);

    /// <summary>
    /// Decode one access unit. Returns the decoded frame, or null when the decoder buffered it.
    /// <paramref name="frameRtpTimestamp"/> is the RTP timestamp of the frame actually emitted (which, with a
    /// decode pipeline / B-frame reorder, may be an earlier access unit than the one just submitted).
    /// </summary>
    /// <param name="accessUnit">The encoded access unit to decode (borrowed for this call).</param>
    /// <param name="rtpTimestamp">The RTP timestamp of the submitted access unit.</param>
    /// <param name="nowNs">The current time in nanoseconds, used to stamp the produced frame.</param>
    /// <param name="frameRtpTimestamp">The RTP timestamp of the frame actually emitted.</param>
    /// <returns>The decoded frame, or null when the decoder buffered the access unit.</returns>
    VideoFrame? Decode(ReadOnlySpan<byte> accessUnit, uint rtpTimestamp, long nowNs, out uint frameRtpTimestamp);
}

/// <summary>Encodes <see cref="AudioFrame"/>s to a compressed audio bitstream.</summary>
public interface IAudioEncoder : IDisposable
{
    /// <summary>Encode one PCM frame to a packet plus its per-channel RTP duration.</summary>
    EncodedAudioPacket Encode(AudioFrame frame);
}

/// <summary>Decodes a compressed audio bitstream back to PCM <see cref="AudioFrame"/>s.</summary>
public interface IAudioDecoder : IDisposable
{
    /// <summary>Decode one packet to an interleaved PCM frame, stamped with <paramref name="nowNs"/>.</summary>
    /// <param name="payload">The encoded audio packet to decode (borrowed for this call).</param>
    /// <param name="nowNs">The current time in nanoseconds, used to stamp the produced frame.</param>
    /// <returns>The decoded interleaved-PCM audio frame.</returns>
    AudioFrame Decode(ReadOnlySpan<byte> payload, long nowNs);
}

/// <summary>
/// Splits an encoded access unit / audio frame into RTP payloads (one per outgoing RTP packet), per the
/// codec's RTP payload format (RFC 7798 for H.265, 6184 for H.264, a single packet for Opus).
/// </summary>
public interface IRtpPacketizer
{
    /// <summary>
    /// Split one encoded frame into its RTP payloads, in order, returning how many were produced. The payloads
    /// are written into storage owned by this packetizer and read back with <see cref="GetPayload"/>; they are
    /// valid only until the next <see cref="Packetize"/> call. One packetizer serves a single stream whose send
    /// loop sends every payload before packetizing the next frame, so the storage is reused with no per-packet
    /// allocation.
    /// </summary>
    /// <param name="frame">The encoded access unit / audio frame to split.</param>
    /// <returns>The number of RTP payloads produced.</returns>
    int Packetize(ReadOnlySpan<byte> frame);

    /// <summary>The <paramref name="index"/>-th payload produced by the most recent <see cref="Packetize"/> call.</summary>
    /// <param name="index">A payload index in <c>[0, count)</c> where <c>count</c> was the last return value.</param>
    /// <returns>A view of the payload bytes, valid until the next <see cref="Packetize"/> call.</returns>
    ReadOnlyMemory<byte> GetPayload(int index);
}

/// <summary>Reassembles RTP payloads back into encoded access units / audio frames (the inverse of <see cref="IRtpPacketizer"/>).</summary>
public interface IRtpDepacketizer : IDisposable
{
    /// <summary>
    /// Push one received RTP payload (a borrowed span, valid only for this call). When this payload completes a
    /// frame, returns it as a <see cref="PooledBuffer"/> the caller owns and must dispose once consumed (so the
    /// assembled frame can cross to a decode worker without a per-frame allocation); returns null while still
    /// assembling.
    /// </summary>
    /// <param name="payload">The received RTP payload, valid only for the duration of this call.</param>
    /// <param name="marker">The RTP marker bit, set on the last payload of an access unit.</param>
    /// <returns>The completed frame as an owned pooled buffer, or null while still assembling.</returns>
    PooledBuffer? Push(ReadOnlySpan<byte> payload, bool marker);
}

/// <summary>
/// Describes one video codec end to end: how it is advertised in SDP (encoding name, clock, fmtp, rtcp-fb),
/// how preferred it is, and how to create its encoder, decoder, and RTP payload format. Register one per
/// codec to make it DI-pluggable and automatically negotiated; the host never edits the negotiation code.
/// </summary>
public interface IVideoCodecDescriptor
{
    /// <summary>The SDP <c>a=rtpmap</c> encoding name, e.g. <c>H265</c>, <c>H264</c>, <c>AV1</c>.</summary>
    string RtpName { get; }

    /// <summary>The RTP clock rate (90000 for video).</summary>
    int ClockRate { get; }

    /// <summary>The SDP <c>a=fmtp</c> parameter string (profile-level-id, packetization-mode, …), or null.</summary>
    string? FormatParameters { get; }

    /// <summary>The SDP <c>a=rtcp-fb</c> values advertised (e.g. <c>nack</c>, <c>nack pli</c>).</summary>
    IReadOnlyList<string> RtcpFeedback { get; }

    /// <summary>Preference order across registered video codecs; lower is more preferred.</summary>
    int Preference { get; }

    /// <summary>Create an encoder for a send session.</summary>
    IVideoEncoder CreateEncoder(VideoEncoderSettings settings);

    /// <summary>Create a decoder for a receive session.</summary>
    IVideoDecoder CreateDecoder(VideoDecoderSettings settings);

    /// <summary>Create the RTP packetizer for this codec's payload format.</summary>
    IRtpPacketizer CreatePacketizer();

    /// <summary>Create the RTP depacketizer for this codec's payload format.</summary>
    IRtpDepacketizer CreateDepacketizer();
}

/// <summary>Describes one audio codec end to end (the audio counterpart of <see cref="IVideoCodecDescriptor"/>).</summary>
public interface IAudioCodecDescriptor
{
    /// <summary>The SDP <c>a=rtpmap</c> encoding name, e.g. <c>opus</c>.</summary>
    string RtpName { get; }

    /// <summary>The RTP clock rate (48000 for Opus).</summary>
    int ClockRate { get; }

    /// <summary>The channel count advertised in <c>a=rtpmap</c>.</summary>
    int Channels { get; }

    /// <summary>The SDP <c>a=fmtp</c> parameter string, or null.</summary>
    string? FormatParameters { get; }

    /// <summary>Preference order across registered audio codecs; lower is more preferred.</summary>
    int Preference { get; }

    /// <summary>Create an encoder for a send session.</summary>
    IAudioEncoder CreateEncoder();

    /// <summary>Create a decoder for a receive session.</summary>
    IAudioDecoder CreateDecoder();

    /// <summary>Create the RTP packetizer for this codec's payload format.</summary>
    IRtpPacketizer CreatePacketizer();

    /// <summary>Create the RTP depacketizer for this codec's payload format.</summary>
    IRtpDepacketizer CreateDepacketizer();
}

/// <summary>
/// The set of codecs the transport can negotiate, collected from every registered
/// <see cref="IVideoCodecDescriptor"/> / <see cref="IAudioCodecDescriptor"/>. The negotiation enumerates this
/// to build the SDP offer (assigning payload types) and resolves a negotiated encoding name back to its
/// descriptor for encode/decode. Add a codec by registering a descriptor in DI - no negotiation edits.
/// </summary>
public interface IMediaCodecRegistry
{
    /// <summary>Registered video codecs, in preference order.</summary>
    IReadOnlyList<IVideoCodecDescriptor> VideoCodecs { get; }

    /// <summary>Registered audio codecs, in preference order.</summary>
    IReadOnlyList<IAudioCodecDescriptor> AudioCodecs { get; }

    /// <summary>Find a video codec by its SDP encoding name (case-insensitive), or null.</summary>
    IVideoCodecDescriptor? FindVideo(string rtpName);

    /// <summary>Find an audio codec by its SDP encoding name (case-insensitive), or null.</summary>
    IAudioCodecDescriptor? FindAudio(string rtpName);
}
