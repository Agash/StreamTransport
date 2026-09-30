using Agash.StreamTransport.WebRtc.Rtp;
using Agash.StreamTransport.WebRtc.Rtp.PayloadFormats;

namespace Agash.StreamTransport.Codecs;

/// <summary>H.265 RTP payload format (RFC 7798) behind the composition's packetizer contract.</summary>
internal sealed class H265RtpPacketizer : Agash.StreamTransport.IRtpPacketizer
{
    private readonly WebRtc.Rtp.PayloadFormats.IRtpPacketizer _inner =
        H265PayloadFormat.Instance.CreatePacketizer(1100);
    private readonly RtpPayloadWriter _writer = new();

    public int Packetize(ReadOnlySpan<byte> frame)
    {
        _inner.Packetize(frame, _writer);
        return _writer.Count;
    }

    public ReadOnlyMemory<byte> GetPayload(int index) => _writer[index];
}

/// <summary>H.265 RTP depacketizer (RFC 7798) behind the composition's depacketizer contract.</summary>
internal sealed class H265RtpDepacketizer : Agash.StreamTransport.IRtpDepacketizer
{
    private readonly WebRtc.Rtp.PayloadFormats.IRtpDepacketizer _inner =
        H265PayloadFormat.Instance.CreateDepacketizer();

    public PooledBuffer? Push(ReadOnlySpan<byte> payload, bool marker)
    {
        if (!_inner.TryPush(payload, marker, out EncodedFrameBuffer frame))
        {
            return null;
        }

        using (frame)
        {
            byte[] copy = System.Buffers.ArrayPool<byte>.Shared.Rent(frame.Length);
            frame.Span.CopyTo(copy);
            return new PooledBuffer(copy, frame.Length);
        }
    }

    public void Dispose() => _inner.Dispose();
}

/// <summary>
/// Identity payload format: one encoded frame per RTP packet, the inverse on receive. Correct for Opus
/// (one packet per frame) and any codec whose access unit always fits one packet.
/// </summary>
internal sealed class PassthroughPacketizer : Agash.StreamTransport.IRtpPacketizer
{
    private ReadOnlyMemory<byte> _frame;

    public int Packetize(ReadOnlySpan<byte> frame)
    {
        _frame = frame.ToArray();
        return 1;
    }

    public ReadOnlyMemory<byte> GetPayload(int index) => _frame;
}

/// <summary>The receive side of <see cref="PassthroughPacketizer"/>: each payload is a complete frame.</summary>
internal sealed class PassthroughDepacketizer : Agash.StreamTransport.IRtpDepacketizer
{
    public PooledBuffer? Push(ReadOnlySpan<byte> payload, bool marker)
    {
        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(payload.Length);
        payload.CopyTo(buffer);
        return new PooledBuffer(buffer, payload.Length);
    }

    public void Dispose() { }
}
