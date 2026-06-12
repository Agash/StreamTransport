namespace Agash.StreamTransport;

/// <summary>
/// The wire-transport seam: mints the per-peer <see cref="IMediaSender"/> / <see cref="IMediaReceiver"/>
/// that negotiate over an <see cref="ISignalingChannel"/> and move media to and from a remote peer. The
/// built-in implementation is WebRTC (ICE + DTLS-SRTP + RTP/RTCP); an alternative transport - for example a
/// QUIC-based one reusing the same capture, codec, STUN, and negotiation pieces - is plugged in by replacing
/// this single registration, leaving the publisher / subscriber orchestration and the capture/codec seams
/// untouched.
/// </summary>
public interface IMediaTransport
{
    /// <summary>
    /// Create a sender for one peer. At least one of <paramref name="video"/> / <paramref name="audio"/> must
    /// be non-null. <paramref name="gpuDeviceHandle"/> is an optional shared GPU device for zero-copy encode.
    /// </summary>
    IMediaSender CreateSender(
        MediaTransportOptions options,
        IVideoFrameSource? video = null,
        IAudioFrameSource? audio = null,
        nint gpuDeviceHandle = 0);

    /// <summary>
    /// Create a receiver for one peer. At least one of <paramref name="video"/> / <paramref name="audio"/>
    /// must be non-null.
    /// </summary>
    IMediaReceiver CreateReceiver(
        MediaTransportOptions options,
        IVideoFrameSink? video = null,
        IAudioFrameSink? audio = null);
}
