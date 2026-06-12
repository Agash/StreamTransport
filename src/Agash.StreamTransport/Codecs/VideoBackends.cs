using System.Runtime.Versioning;

namespace Agash.StreamTransport.Codecs;

/// <summary>
/// A HEVC encode backend - implemented directly by each concrete encoder (<see cref="HardwareHevcEncoder"/>,
/// <see cref="VaapiVideoEncoder"/>, the D3D11 and VideoToolbox encoders). There is no wrapper layer: the
/// encoder class <i>is</i> the backend, so encode behaviour and any per-codec tuning live in one place. The
/// send pipeline creates the right one lazily on the first frame (dimensions known then) and drives it through
/// <see cref="Encode"/>. Alpha packing / colour conversion are applied by the pipeline's surface transform
/// before the frame arrives here.
/// </summary>
internal interface IVideoEncoderBackend : IDisposable
{
    /// <summary>The native device the zero-copy encoder runs on (ID3D11Device* on Windows), or 0.</summary>
    nint NativeDevice { get; }

    /// <summary>
    /// Encode one prepared frame, returning the Annex-B HEVC access unit or null if output was withheld. The
    /// frame's <see cref="VideoFrame.PresentationTimeNs"/> is threaded through FFmpeg as the packet PTS, so
    /// <paramref name="capturePtsNs"/> comes back as the capture time of the frame that actually produced this
    /// access unit. The encoder holds a pipeline (and, with B-frames, reorders output), so that producing frame
    /// is earlier than - and not necessarily adjacent to - the one just submitted; its capture time, not the
    /// submitted one, is what the RTP timestamp and abs-capture-time must describe.
    /// </summary>
    byte[]? Encode(in VideoFrame frame, out long capturePtsNs);

    /// <summary>
    /// Retune the running encoder to a new target bitrate without tearing it down (congestion backpressure).
    /// Best-effort: a backend that cannot retune a live encoder leaves this as a no-op.
    /// </summary>
    void UpdateBitrate(long bitrateBps) { }
}

/// <summary>
/// A HEVC decode backend - implemented directly by each concrete decoder (<see cref="HevcDecoder"/>, the D3D11
/// and VideoToolbox decoders). The decoder class <i>is</i> the backend (no wrapper). It reports the surface
/// kind it decodes into: <see cref="StreamInteropKind.None"/> for CPU pixel frames, or a GPU surface kind for
/// a zero-copy publish. Alpha unpack / colour conversion are applied by the pipeline around the backend.
/// </summary>
internal interface IVideoDecoderBackend : IDisposable
{
    /// <summary>The surface kind decoded frames carry: <see cref="StreamInteropKind.None"/> = CPU pixels.</summary>
    StreamInteropKind OutputSurfaceKind { get; }

    /// <summary>The native device the GPU output surface lives on (ID3D11Device* on Windows), or 0 for CPU.</summary>
    nint NativeDevice { get; }

    /// <summary>
    /// Decode one access unit; returns true and fills <paramref name="frame"/> when a frame is produced.
    /// <paramref name="rtpTimestamp"/> is the 90 kHz RTP timestamp of the submitted access unit; it is threaded
    /// through FFmpeg as the packet PTS so <paramref name="frameRtpTimestamp"/> comes back as the RTP timestamp
    /// of the access unit that actually produced this frame (a decoder may hold a one-frame pipeline, so the two
    /// differ) - that is the timestamp playout must schedule against, never the just-submitted one.
    /// </summary>
    bool TryDecode(ReadOnlySpan<byte> accessUnit, uint rtpTimestamp, long presentationTimeNs, out VideoFrame frame, out uint frameRtpTimestamp);
}

/// <summary>Selects the decode backend for the requested output mode, falling back to CPU when a GPU backend can't open.</summary>
internal static class VideoDecoderBackendFactory
{
    public static IVideoDecoderBackend Create(bool preferGpuOutput)
    {
#if WINDOWS_HEAD
        if (preferGpuOutput)
        {
            try
            {
                return new D3D11VideoDecoder();
            }
            catch (Exception)
            {
                // No D3D11VA decode device; fall back to CPU decode below.
            }
        }
#endif
        if (preferGpuOutput && OperatingSystem.IsMacOS())
        {
            try
            {
                return CreateVideoToolboxDecoder();
            }
            catch (Exception)
            {
                // No VideoToolbox decode session; fall back to CPU decode below.
            }
        }

        return new HevcDecoder();
    }

    [SupportedOSPlatform("macos")]
    private static IVideoDecoderBackend CreateVideoToolboxDecoder() => new VideoToolboxVideoDecoder();
}
