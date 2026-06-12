#if WINDOWS_HEAD
using Agash.StreamTransport;
using Agash.StreamTransport.Codecs;
using Spout2.NET;
using Vortice.Direct3D11;

namespace StreamTransport.Agent;

/// <summary>
/// Publishes decoded GPU frames to a Spout sender (Windows) so downstream apps (OBS) can pick them up.
/// The receiver decodes straight into an NV12 D3D11 texture; this sink converts it to BGRA on the GPU and
/// hands it to Spout - no CPU readback. It initialises lazily from the device the decoded texture lives
/// on, so it always shares the decoder's device.
/// </summary>
internal sealed class SpoutVideoPublishSink : IVideoFrameSink, IDisposable
{
    private readonly string _senderName;
    private readonly Lock _gate = new();
    private volatile bool _alpha;
    private ID3D11Device? _device;
    private INv12ToBgra? _converter;
    private IAlphaUnpacker? _unpacker;
    private SpoutSender? _sender;
    private bool _disposed;

    public SpoutVideoPublishSink(string senderName, bool alpha = false)
    {
        _senderName = senderName;
        _alpha = alpha;
    }

    /// <summary>
    /// Adopt the publisher's negotiated side-by-side-alpha setting. Safe to call before the first frame
    /// (the converter/unpacker is created lazily per the current value), so the receiver needs no flag.
    /// </summary>
    public void SetPreserveAlpha(bool value) => _alpha = value;

    public void Submit(VideoFrame frame)
    {
        if (frame.InteropKind != StreamInteropKind.Spout || frame.Surface == 0)
        {
            return; // only GPU (zero-copy decode) frames publish through this path.
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            EnsureInitialized(frame.Surface);

            // Alpha: the decoded surface is the packed 2W x H NV12; the unpacker splits it back to W x H
            // BGRA-with-alpha. Otherwise the converter does plain NV12 -> BGRA. Both are GPU surface transforms
            // (IAlphaUnpacker / INv12ToBgra), created on demand for the negotiated mode (set before the first
            // frame), so they stay on the GPU and a late SetPreserveAlpha works.
            VideoFrame bgra = _alpha
                ? (_unpacker ??= new D3D11AlphaUnpacker(_device!)).UnpackAlpha(frame, frame.PresentationTimeNs)
                : (_converter ??= new D3D11Nv12ToBgraConverter(_device!)).Nv12ToBgra(frame, frame.PresentationTimeNs);

            _sender!.Send(bgra.Surface);
        }
    }

    private void EnsureInitialized(nint texture)
    {
        if (_sender is not null)
        {
            return;
        }

        // The decoded texture knows its own device; share it for the converter and the Spout sender.
        using var tex = new ID3D11Texture2D(texture);
        tex.AddRef();
        _device = tex.Device;
        _sender = new SpoutSender(_senderName, _device.NativePointer);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _sender?.Dispose();
            (_converter as IDisposable)?.Dispose();
            (_unpacker as IDisposable)?.Dispose();
            _device?.Dispose();
        }
    }
}
#endif
