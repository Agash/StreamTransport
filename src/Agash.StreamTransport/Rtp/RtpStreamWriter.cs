using System.Security.Cryptography;
using Agash.StreamTransport.Media;
using Agash.StreamTransport.Sync;
using Agash.StreamTransport.WebRtc.Rtp;
using Agash.StreamTransport.WebRtc.Rtp.PayloadFormats;

namespace Agash.StreamTransport.Rtp;

/// <summary>Takes one RTP payload with its header fields; the payload is valid only during the call.</summary>
/// <param name="payloadType">The RTP payload type.</param>
/// <param name="ssrc">The stream's SSRC.</param>
/// <param name="rtpTimestamp">The RTP timestamp.</param>
/// <param name="marker">The marker bit.</param>
/// <param name="payload">The payload.</param>
/// <param name="captureNtp">The abs-capture-time, or zero for none.</param>
internal delegate void RtpPayloadSink(
    byte payloadType,
    uint ssrc,
    uint rtpTimestamp,
    bool marker,
    ReadOnlySpan<byte> payload,
    ulong captureNtp
);

/// <summary>
/// Turns one stream's encoded frames into RTP payloads: the payload format splits them, the RTP
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
    /// <param name="send">Takes each payload in order.</param>
    public void Write(ReadOnlySpan<byte> frame, MediaTimestamp timestamp, RtpPayloadSink send)
    {
        packetizer.Packetize(frame, _payloads);
        MediaTime origin = timestamp.Origin;
        _first ??= origin;
        uint rtpTimestamp = unchecked(_start + (uint)rate.ToTicks(origin - _first.Value));
        ulong captureNtp = captureClock.ToNtp(origin).Value;
        for (int i = 0; i < _payloads.Count; i++)
        {
            send(
                payloadType,
                ssrc,
                rtpTimestamp,
                i == _payloads.Count - 1,
                _payloads[i].Span,
                i == 0 ? captureNtp : 0
            );
        }
    }
}
