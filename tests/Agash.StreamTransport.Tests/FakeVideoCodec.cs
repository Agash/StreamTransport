using System.Buffers.Binary;

namespace Agash.StreamTransport.Tests;

/// <summary>
/// A hardware-free video codec for system tests: the "encoder" serialises a frame's dimensions into a tiny
/// access unit and the "decoder" reconstructs a frame from it, so the full media orchestration
/// (negotiation -> encode -> packetize -> SRTP -> ICE -> depacketize -> decode -> sink) can be exercised
/// end to end on any machine, with no FFmpeg or GPU. It round-trips width/height so content integrity through
/// the whole pipeline is assertable.
/// </summary>
internal sealed class FakeVideoCodec : IVideoCodecDescriptor
{
    public string RtpName => "x-fake";
    public int ClockRate => 90_000;
    public string? FormatParameters => null;
    public IReadOnlyList<string> RtcpFeedback => ["nack", "nack pli"];
    public int Preference => 1;

    public IVideoEncoder CreateEncoder(VideoEncoderSettings settings) => new FakeEncoder();
    public IVideoDecoder CreateDecoder(VideoDecoderSettings settings) => new FakeDecoder();
    public IRtpPacketizer CreatePacketizer() => new Passthrough();
    public IRtpDepacketizer CreateDepacketizer() => new Passthrough();

    private sealed class FakeEncoder : IVideoEncoder
    {
        public EncodedVideoAccessUnit? Encode(VideoFrame frame)
        {
            byte[] au = new byte[8];
            BinaryPrimitives.WriteInt32LittleEndian(au, frame.Width);
            BinaryPrimitives.WriteInt32LittleEndian(au.AsSpan(4), frame.Height);
            return new EncodedVideoAccessUnit(3000, au, frame.PresentationTimeNs);
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeDecoder : IVideoDecoder
    {
        public bool IsGpuOutput => false;

        public void SetPreserveAlpha(bool value)
        {
        }

        public VideoFrame? Decode(ReadOnlySpan<byte> accessUnit, uint rtpTimestamp, long nowNs, out uint frameRtpTimestamp)
        {
            frameRtpTimestamp = rtpTimestamp;
            if (accessUnit.Length < 8)
            {
                return null;
            }

            int width = BinaryPrimitives.ReadInt32LittleEndian(accessUnit);
            int height = BinaryPrimitives.ReadInt32LittleEndian(accessUnit[4..]);
            return VideoFrame.FromPixels(new byte[width * height * 3 / 2], VideoPixelFormat.Nv12, width, height, nowNs);
        }

        public void Dispose()
        {
        }
    }

    private sealed class Passthrough : IRtpPacketizer, IRtpDepacketizer
    {
        private ReadOnlyMemory<byte> _frame;

        public int Packetize(ReadOnlySpan<byte> frame)
        {
            _frame = frame.ToArray();
            return 1;
        }

        public ReadOnlyMemory<byte> GetPayload(int index) => _frame;

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
}
