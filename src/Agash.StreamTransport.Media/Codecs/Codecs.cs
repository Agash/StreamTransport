using System.Buffers;
using System.Collections.Immutable;

namespace Agash.StreamTransport.Media;

/// <summary>What an encoder's rate control and latency settings are chosen for.</summary>
public enum EncodeTuning
{
    /// <summary>Lowest delay: a shallow rate-control buffer and no lookahead or reordering.</summary>
    Interactive,

    /// <summary>Screen content: sharper text and more bits per picture, at a little more delay.</summary>
    ScreenContent,

    /// <summary>A lossy, varying uplink: a deeper rate-control buffer that rides out rate swings.</summary>
    LossResilient,
}

/// <summary>
/// A codec with its format parameters, as SDP describes it with <c>a=fmtp</c>: profile, level, tier and
/// the rest, by their SDP names.
/// </summary>
/// <param name="Codec">The codec.</param>
/// <param name="Parameters">The format parameters.</param>
public sealed record VideoCodecFormat(
    VideoCodecId Codec,
    ImmutableSortedDictionary<string, string> Parameters
)
{
    /// <summary>A codec with no parameters.</summary>
    /// <param name="codec">The codec.</param>
    public VideoCodecFormat(VideoCodecId codec)
        : this(codec, ImmutableSortedDictionary<string, string>.Empty) { }

    /// <inheritdoc/>
    public bool Equals(VideoCodecFormat? other) =>
        other is not null
        && Codec == other.Codec
        && Parameters.Count == other.Parameters.Count
        && !Parameters.Except(other.Parameters).Any();

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Codec, Parameters.Count);
}

/// <summary>A rate-control target, reconfigurable while encoding.</summary>
/// <param name="BitsPerSecond">The target bit rate.</param>
/// <param name="FramesPerSecond">The frame rate the rate control plans for.</param>
public readonly record struct RateTarget(long BitsPerSecond, double FramesPerSecond);

/// <summary>What to do with one frame beyond encoding it.</summary>
/// <param name="Keyframe">Make it a keyframe (an IDR), as for a new receiver or loss recovery.</param>
public readonly record struct EncodeRequest(bool Keyframe = false);

/// <summary>How an encoder is set up. The factory makes the backend choices.</summary>
/// <param name="Format">The codec and its parameters.</param>
/// <param name="Size">The picture size.</param>
/// <param name="Rate">The initial rate target.</param>
/// <param name="KeyframeInterval">The longest gap between keyframes; zero for none unless requested.</param>
/// <param name="Tuning">What rate control and latency are tuned for.</param>
/// <param name="Color">The colour the encoded stream signals; frames are converted to it if needed.</param>
/// <param name="Alpha">How the frames' alpha travels: <see cref="AlphaLayout.Layer"/> asks the encoder to code
/// it as the codec's alpha layer; otherwise the encoder codes colour only.</param>
public sealed record VideoEncoderConfiguration(
    VideoCodecFormat Format,
    VideoSize Size,
    RateTarget Rate,
    TimeSpan KeyframeInterval = default,
    EncodeTuning Tuning = EncodeTuning.Interactive,
    VideoColor Color = default,
    AlphaLayout Alpha = AlphaLayout.None
);

/// <summary>What an encoder takes and can do, so a pipeline can feed it without converting.</summary>
/// <param name="ImplementationName">Which implementation, for logs.</param>
/// <param name="IsHardwareAccelerated">Whether it runs on a GPU or media engine.</param>
/// <param name="Input">The frames it takes as they are.</param>
/// <param name="WidthAlignment">The multiple the width must be.</param>
/// <param name="HeightAlignment">The multiple the height must be.</param>
/// <param name="MaximumSize">The largest picture.</param>
/// <param name="ReconfigurableRate">Whether <see cref="IVideoEncoder.Reconfigure"/> takes effect while encoding.</param>
/// <param name="EncodesAlphaLayer">Whether it codes alpha as the codec's alpha layer when asked.</param>
public sealed record VideoEncoderInfo(
    string ImplementationName,
    bool IsHardwareAccelerated,
    VideoConstraints Input,
    int WidthAlignment,
    int HeightAlignment,
    VideoSize MaximumSize,
    bool ReconfigurableRate,
    bool EncodesAlphaLayer = false
);

/// <summary>
/// An encoded video access unit, borrowed from the encoder: valid only for the call it is passed to.
/// </summary>
/// <param name="data">
/// The access unit in the codec's byte-stream format: Annex B for H.264 and H.265, low-overhead OBUs
/// with temporal delimiters for AV1.
/// </param>
/// <param name="codec">The codec.</param>
/// <param name="keyframe">Whether it can be decoded on its own.</param>
/// <param name="timestamp">The timestamp of the frame it encodes.</param>
public readonly ref struct EncodedVideoFrame(
    ReadOnlySpan<byte> data,
    VideoCodecId codec,
    bool keyframe,
    MediaTimestamp timestamp
)
{
    /// <summary>The access unit.</summary>
    public ReadOnlySpan<byte> Data { get; } = data;

    /// <summary>The codec.</summary>
    public VideoCodecId Codec { get; } = codec;

    /// <summary>Whether it can be decoded on its own.</summary>
    public bool Keyframe { get; } = keyframe;

    /// <summary>The timestamp of the frame it encodes.</summary>
    public MediaTimestamp Timestamp { get; } = timestamp;

    /// <summary>Keeps the access unit past the call, in pooled memory.</summary>
    /// <returns>A lease, released exactly once by disposing it.</returns>
    public EncodedVideoFrameLease Retain() => new(in this);
}

/// <summary>An owned access unit in pooled memory.</summary>
public sealed class EncodedVideoFrameLease : IDisposable
{
    private readonly byte[] _buffer;
    private readonly int _length;
    private int _disposed;

    internal EncodedVideoFrameLease(in EncodedVideoFrame frame)
    {
        _buffer = ArrayPool<byte>.Shared.Rent(frame.Data.Length);
        _length = frame.Data.Length;
        frame.Data.CopyTo(_buffer);
        Codec = frame.Codec;
        Keyframe = frame.Keyframe;
        Timestamp = frame.Timestamp;
    }

    /// <summary>The codec.</summary>
    public VideoCodecId Codec { get; }

    /// <summary>Whether it can be decoded on its own.</summary>
    public bool Keyframe { get; }

    /// <summary>The timestamp of the frame it encodes.</summary>
    public MediaTimestamp Timestamp { get; }

    /// <summary>The access unit, valid while the lease is.</summary>
    /// <exception cref="ObjectDisposedException">The lease was released.</exception>
    public EncodedVideoFrame Frame
    {
        get
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return new EncodedVideoFrame(_buffer.AsSpan(0, _length), Codec, Keyframe, Timestamp);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            ArrayPool<byte>.Shared.Return(_buffer);
        }
    }
}

/// <summary>Receives encoded video on the encoder's thread.</summary>
public interface IEncodedVideoConsumer
{
    /// <summary>An access unit; valid only during the call.</summary>
    /// <param name="frame">The access unit.</param>
    void OnEncoded(in EncodedVideoFrame frame);
}

/// <summary>
/// A video encoder. It takes the frames <see cref="VideoEncoderInfo.Input"/> allows as they are, and
/// hands access units to the consumer, possibly later than the frame that produced them.
/// </summary>
public interface IVideoEncoder : IDisposable
{
    /// <summary>What the encoder takes and can do.</summary>
    VideoEncoderInfo Info { get; }

    /// <summary>Encodes a frame.</summary>
    /// <param name="frame">The frame.</param>
    /// <param name="request">Whether it must be a keyframe.</param>
    /// <param name="consumer">Where access units go.</param>
    void Encode(in VideoFrame frame, in EncodeRequest request, IEncodedVideoConsumer consumer);

    /// <summary>Changes the rate target, as congestion control asks.</summary>
    /// <param name="target">The new target.</param>
    void Reconfigure(in RateTarget target);

    /// <summary>Hands out every access unit still inside the encoder.</summary>
    /// <param name="consumer">Where they go.</param>
    void Flush(IEncodedVideoConsumer consumer);
}

/// <summary>What a decoder produces.</summary>
/// <param name="ImplementationName">Which implementation, for logs.</param>
/// <param name="IsHardwareAccelerated">Whether it runs on a GPU or media engine.</param>
/// <param name="Output">The frames it produces.</param>
/// <param name="DecodesAlphaLayer">Whether it decodes the codec's alpha layer into frames with alpha.</param>
public sealed record VideoDecoderInfo(
    string ImplementationName,
    bool IsHardwareAccelerated,
    VideoConstraints Output,
    bool DecodesAlphaLayer = false
);

/// <summary>A video decoder: access units in, frames out to the consumer.</summary>
public interface IVideoDecoder : IDisposable
{
    /// <summary>What the decoder produces.</summary>
    VideoDecoderInfo Info { get; }

    /// <summary>Decodes an access unit.</summary>
    /// <param name="frame">The access unit.</param>
    /// <param name="consumer">Where decoded frames go.</param>
    void Decode(in EncodedVideoFrame frame, IVideoFrameConsumer consumer);
}

/// <summary>
/// Makes video encoders of one family (a vendor's hardware, a platform API, a software library). A
/// pipeline asks every registered factory and takes the best ranked one that can do what it needs.
/// </summary>
public interface IVideoEncoderFactory
{
    /// <summary>How strongly this family is preferred when several can do the job; higher wins.</summary>
    int Rank { get; }

    /// <summary>The codec formats it can encode, for SDP.</summary>
    ImmutableArray<VideoCodecFormat> SupportedFormats { get; }

    /// <summary>What an encoder for a format would take, probed without keeping it; null when it cannot encode it.</summary>
    /// <param name="format">The codec format.</param>
    /// <param name="device">The GPU the input will be on; null for any.</param>
    /// <returns>The encoder's information, or null.</returns>
    VideoEncoderInfo? QueryCapabilities(VideoCodecFormat format, GpuIdentity? device);

    /// <summary>Makes an encoder.</summary>
    /// <param name="configuration">How to set it up.</param>
    /// <param name="device">The GPU its input will be on; null for any.</param>
    /// <returns>The encoder.</returns>
    IVideoEncoder Create(VideoEncoderConfiguration configuration, GpuIdentity? device);
}

/// <summary>Makes video decoders of one family.</summary>
public interface IVideoDecoderFactory
{
    /// <summary>How strongly this family is preferred; higher wins.</summary>
    int Rank { get; }

    /// <summary>The codec formats it can decode, for SDP.</summary>
    ImmutableArray<VideoCodecFormat> SupportedFormats { get; }

    /// <summary>What a decoder for a format would produce; null when it cannot decode it.</summary>
    /// <param name="format">The codec format.</param>
    /// <param name="output">What the consumer of decoded frames accepts.</param>
    /// <returns>The decoder's information, or null.</returns>
    VideoDecoderInfo? QueryCapabilities(VideoCodecFormat format, VideoConstraints output);

    /// <summary>Makes a decoder.</summary>
    /// <param name="format">The codec format.</param>
    /// <param name="output">What the consumer of decoded frames accepts.</param>
    /// <returns>The decoder.</returns>
    IVideoDecoder Create(VideoCodecFormat format, VideoConstraints output);
}

/// <summary>An encoded audio packet, borrowed.</summary>
/// <param name="data">The packet.</param>
/// <param name="codec">The codec.</param>
/// <param name="timestamp">When its first sample was captured.</param>
/// <param name="duration">How long it plays.</param>
public readonly ref struct EncodedAudioFrame(
    ReadOnlySpan<byte> data,
    AudioCodecId codec,
    MediaTimestamp timestamp,
    TimeSpan duration
)
{
    /// <summary>The packet.</summary>
    public ReadOnlySpan<byte> Data { get; } = data;

    /// <summary>The codec.</summary>
    public AudioCodecId Codec { get; } = codec;

    /// <summary>When its first sample was captured.</summary>
    public MediaTimestamp Timestamp { get; } = timestamp;

    /// <summary>How long it plays.</summary>
    public TimeSpan Duration { get; } = duration;
}

/// <summary>Receives encoded audio.</summary>
public interface IEncodedAudioConsumer
{
    /// <summary>A packet; valid only during the call.</summary>
    /// <param name="frame">The packet.</param>
    void OnEncoded(in EncodedAudioFrame frame);
}

/// <summary>An audio encoder.</summary>
public interface IAudioEncoder : IDisposable
{
    /// <summary>The PCM format it takes.</summary>
    AudioFormat Input { get; }

    /// <summary>Encodes audio; packets go to the consumer as whole codec frames fill.</summary>
    /// <param name="frame">The audio.</param>
    /// <param name="consumer">Where packets go.</param>
    void Encode(in AudioFrame frame, IEncodedAudioConsumer consumer);

    /// <summary>Changes the target bit rate.</summary>
    /// <param name="bitsPerSecond">The new target.</param>
    void Reconfigure(int bitsPerSecond);
}

/// <summary>An audio decoder.</summary>
public interface IAudioDecoder : IDisposable
{
    /// <summary>The PCM format it produces.</summary>
    AudioFormat Output { get; }

    /// <summary>Decodes a packet.</summary>
    /// <param name="frame">The packet.</param>
    /// <param name="consumer">Where the audio goes.</param>
    void Decode(in EncodedAudioFrame frame, IAudioFrameConsumer consumer);

    /// <summary>Makes up audio for a lost packet (packet loss concealment).</summary>
    /// <param name="duration">How long the gap is.</param>
    /// <param name="timestamp">When the gap starts.</param>
    /// <param name="consumer">Where the audio goes.</param>
    void Conceal(TimeSpan duration, MediaTimestamp timestamp, IAudioFrameConsumer consumer);
}
