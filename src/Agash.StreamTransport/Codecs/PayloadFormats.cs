using Agash.StreamTransport.WebRtc.Rtp;
using Agash.StreamTransport.WebRtc.Rtp.PayloadFormats;

namespace Agash.StreamTransport.Codecs;

/// <summary>H.265 RTP payload format (RFC 7798): fragments an Annex-B access unit into FU packets.</summary>
internal sealed class H265RtpPacketizer : IRtpPacketizer
{
    private readonly RtpPayloadWriter _writer = new();

    public int Packetize(ReadOnlySpan<byte> frame)
    {
        H265Packetizer.Packetize(frame, _writer);
        return _writer.Count;
    }

    public ReadOnlyMemory<byte> GetPayload(int index) => _writer[index];
}

/// <summary>H.265 RTP depacketizer (RFC 7798): reassembles FU packets into an access unit.</summary>
internal sealed class H265RtpDepacketizer : IRtpDepacketizer
{
    private readonly H265Depacketizer _inner = new();

    public PooledBuffer? Push(ReadOnlySpan<byte> payload, bool marker) =>
        _inner.Push(payload, marker, out byte[] accessUnit, out int length) ? new PooledBuffer(accessUnit, length) : null;

    public void Dispose() => _inner.Dispose();
}

/// <summary>
/// Identity payload format: one encoded frame per RTP packet, the inverse on receive. Correct for Opus
/// (one packet per frame) and any codec whose access unit always fits one packet.
/// </summary>
internal sealed class PassthroughPacketizer : IRtpPacketizer
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
internal sealed class PassthroughDepacketizer : IRtpDepacketizer
{
    public PooledBuffer? Push(ReadOnlySpan<byte> payload, bool marker)
    {
        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(payload.Length);
        payload.CopyTo(buffer);
        return new PooledBuffer(buffer, payload.Length);
    }

    public void Dispose()
    {
    }
}
