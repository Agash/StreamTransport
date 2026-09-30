using System.Buffers;
using System.Security.Cryptography;
using Agash.StreamTransport.Media;
using Agash.StreamTransport.Sync;
using Agash.StreamTransport.WebRtc.Rtp;
using Agash.StreamTransport.WebRtc.Rtp.PayloadFormats;

namespace Agash.StreamTransport.Rtp;

/// <summary>
/// Turns one stream's encoded frames into paced RTP packets: the payload format splits them, the RTP
/// timestamp is the frame's origin on the stream's clock from a random start (RFC 3550), and the first
/// packet of each frame carries its abs-capture-time.
/// </summary>
/// <remarks>
/// Timestamps come from each frame's own time, not from adding up durations, so frames an encoder emits
/// out of capture order and gaps in the source both keep their true spacing.
/// </remarks>
internal sealed class RtpStreamWriter(
    IRtpPacketizer packetizer,
    byte payloadType,
    uint ssrc,
    ClockRate rate,
    CaptureClock captureClock
)
{
    private readonly RtpPayloadWriter _payloads = new();
    private readonly uint _start = unchecked(
        (uint)RandomNumberGenerator.GetInt32(int.MinValue, int.MaxValue)
    );
    private MediaTime? _first;

    /// <summary>The SSRC the stream sends on.</summary>
    public uint Ssrc => ssrc;

    /// <summary>Packetizes an encoded frame and hands its packets on in order.</summary>
    /// <param name="frame">The encoded frame.</param>
    /// <param name="timestamp">When it was made.</param>
    /// <param name="send">Takes each packet, and with it the packet's rented buffer.</param>
    public void Write(ReadOnlySpan<byte> frame, MediaTimestamp timestamp, Action<PacedPacket> send)
    {
        packetizer.Packetize(frame, _payloads);
        MediaTime origin = timestamp.Origin;
        _first ??= origin;
        uint rtpTimestamp = unchecked(_start + (uint)rate.ToTicks(origin - _first.Value));
        ulong captureNtp = captureClock.ToNtp(origin).Value;
        for (int i = 0; i < _payloads.Count; i++)
        {
            ReadOnlySpan<byte> payload = _payloads[i].Span;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(payload.Length);
            payload.CopyTo(buffer);
            send(
                new PacedPacket(
                    payloadType,
                    ssrc,
                    rtpTimestamp,
                    Marker: i == _payloads.Count - 1,
                    buffer,
                    payload.Length,
                    i == 0 ? captureNtp : 0
                )
            );
        }
    }
}
