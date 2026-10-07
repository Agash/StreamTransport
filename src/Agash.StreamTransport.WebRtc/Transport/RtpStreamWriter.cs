using System.Security.Cryptography;
using Agash.StreamTransport.Media;
using Agash.StreamTransport.WebRtc.Rtp;
using Agash.StreamTransport.WebRtc.Rtp.PayloadFormats;

namespace Agash.StreamTransport.WebRtc.Transport;

/// <summary>
/// Turns one stream's encoded frames into RTP on a connection: the payload format splits them, the RTP
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
    ClockRate rate
)
{
    private readonly RtpPayloadWriter _payloads = new();
    private readonly Lock _gate = new();
    private readonly uint _start = unchecked(
        (uint)RandomNumberGenerator.GetInt32(int.MinValue, int.MaxValue)
    );
    private MediaTime? _first;

    /// <summary>The SSRC the stream sends on.</summary>
    public uint Ssrc => ssrc;

    /// <summary>Packetizes an encoded frame and queues its packets on the connection, in order.</summary>
    /// <param name="connection">The connection.</param>
    /// <param name="frame">The encoded frame.</param>
    /// <param name="timestamp">When it was made.</param>
    /// <param name="capture">When it was captured, on this side's wall clock.</param>
    /// <returns>False when the connection could not take the packets.</returns>
    public bool Write(
        PeerConnection connection,
        ReadOnlySpan<byte> frame,
        MediaTimestamp timestamp,
        NtpTime capture
    )
    {
        lock (_gate)
        {
            packetizer.Packetize(frame, connection.MaximumRtpPayloadSize, _payloads);
            MediaTime origin = timestamp.Origin;
            _first ??= origin;
            uint rtpTimestamp = unchecked(_start + (uint)rate.ToTicks(origin - _first.Value));
            bool sent = true;
            for (int i = 0; i < _payloads.Count; i++)
            {
                sent &= connection.TrySendRtp(
                    payloadType,
                    ssrc,
                    rtpTimestamp,
                    i == _payloads.Count - 1,
                    _payloads[i].Span,
                    i == 0 ? capture.Value : 0
                );
            }

            return sent;
        }
    }
}
