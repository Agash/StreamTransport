using Agash.StreamTransport;
using Agash.StreamTransport.Codecs;

namespace Agash.StreamTransport.Tests;

/// <summary>
/// Builds the right hardware HEVC encode backend for a test: <c>hevc_vaapi</c> goes through
/// <see cref="VaapiVideoEncoder"/> (which owns the VAAPI device + frames pool and uploads NV12 into a
/// surface - the plain <see cref="HardwareHevcEncoder"/> can't, as vaapi only encodes VAAPI surfaces),
/// every other vendor through <see cref="HardwareHevcEncoder"/> (system-memory NV12). A VAAPI device or
/// driver that can't initialise is surfaced as <see cref="HardwareEncoderUnavailableException"/> so the
/// existing per-test "inconclusive when the GPU is absent" guards apply uniformly across vendors.
/// </summary>
internal static class TestEncoders
{
    public static IVideoEncoderBackend Open(string encoderName, int width, int height, int fps, long bitrate)
    {
        if (encoderName != "hevc_vaapi")
        {
            return new HardwareHevcEncoder(encoderName, width, height, fps, bitrate);
        }

        try
        {
            return new VaapiVideoEncoder(width, height, fps, bitrate);
        }
        catch (HardwareEncoderUnavailableException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new HardwareEncoderUnavailableException($"hevc_vaapi could not be opened: {ex.Message}");
        }
    }

    /// <summary>Encode one NV12 buffer through the common backend interface; returns the access unit or null.</summary>
    public static byte[]? EncodeNv12(IVideoEncoderBackend encoder, byte[] nv12, int width, int height) =>
        encoder.Encode(VideoFrame.FromPixels(nv12, VideoPixelFormat.Nv12, width, height, 0), out _);
}
