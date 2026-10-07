using System.Buffers;
using System.Net;
using Agash.StreamTransport.Adaptation;
using Agash.StreamTransport.WebRtc.Ice;
using Agash.StreamTransport.WebRtc.Rtp;
using Agash.StreamTransport.WebRtc.Srtp;
using Microsoft.Extensions.Logging;

namespace Agash.StreamTransport.WebRtc;

/// <summary>
/// Path MTU discovery for <see cref="PeerConnection"/> (RFC 8899), the RTP adapter onto
/// <see cref="PathMtuDiscovery"/>. With RFC 8888 feedback RTP is an acknowledged transport, so the search
/// runs as QUIC's does (RFC 9000 section 14.3): RTCP feedback acknowledges or loses every packet, probes
/// included, and no probe timer is needed. A probe is an RTX retransmission of a recent media packet padded
/// up to the size under test (RFC 8899 section 4.1, probing with application data and padding): the receiver
/// takes it as a duplicate and drops it, as it does libwebrtc's padding on the RTX stream. Probing needs RTX
/// and congestion-control feedback agreed with the peer; without them media stays at the base size.
/// </summary>
/// <remarks>
/// <para>
/// A probe is an ordinary sent packet of class <see cref="TrafficClass.Probe"/>, as QUIC treats its PMTU
/// probes (RFC 9000 section 14.4): the pacer sends it after all media, as libwebrtc's pacer sends padding
/// last; the congestion window governs it and counts it in flight. Only its loss is set apart: a probe lost
/// to its size is no sign of congestion (RFC 8899 section 3, item 7), so the controller, the ECN check and the
/// loss rate pass over it.
/// </para>
/// <para>
/// The search runs per pair of local and remote addresses (RFC 9000 section 14.2): when ICE moves media to a
/// pair it has used before, what was learned there still holds, and a new pair starts at the base size.
/// </para>
/// </remarks>
public sealed partial class PeerConnection
{
    // RTP padding: a count octet holds at most this many bytes, which caps how far a probe reaches beyond
    // the packet it is built from.
    private const int MaxRtpPadding = 255;

    // An RTX retransmission's original sequence number, which the media payload leaves room for.
    private const int RtxRoom = 2;

    // A relayed pair's datagrams travel inside TURN framing, at most a Send indication: the STUN header, an
    // IPv6 XOR-PEER-ADDRESS, the DATA attribute header and its padding (RFC 8656 sections 10 and 11).
    private const int TurnFramingRoom = 52;

    // How many of a stream's newest packets a probe may be built from: the largest of them, since a frame's
    // last fragment, often the newest packet, is usually small.
    private const int ProbeSourceWindow = 32;

    private readonly PathMtuOptions? _pathMtuOptions;

    // Every pair's search, and the active pair's, under _feedbackGate with the controller.
    private readonly Dictionary<
        (IPEndPoint Local, IPEndPoint Remote),
        PathMtuDiscovery
    > _pathMtus = [];
    private PathMtuDiscovery? _pathMtu;
    private int _datagramLimit;

    /// <summary>
    /// The largest datagram the connection sends: what path MTU discovery has confirmed on the current
    /// path, or the base size while it has confirmed nothing larger or is not running.
    /// </summary>
    public int MaximumDatagramSize => Volatile.Read(ref _datagramLimit);

    /// <summary>
    /// The largest RTP payload that fits <see cref="MaximumDatagramSize"/> with everything a packet and its
    /// repairs add: the header and the largest extension block the negotiated extensions make, the SRTP
    /// tag, an RTX sequence number, a FlexFEC header.
    /// Packetizers read it per frame, so media follows the path MTU as it changes.
    /// </summary>
    public int MaximumRtpPayloadSize =>
        MaximumDatagramSize
        - RtpPacket.FixedHeaderLength
        - Volatile.Read(ref _extensions).MaximumBlockLength
        - SrtpSession.MaxProtectionOverhead
        - RtxRoom
        - (FecEnabled ? FlexFec.HeaderLength : 0);

    /// <summary>Raised with the new size when <see cref="MaximumDatagramSize"/> changes.</summary>
    public event Action<int>? MaximumDatagramSizeChanged;

    // ICE settled on a pair: its own search takes over, new or as it was left.
    private void SelectPathMtu(IcePath path)
    {
        if (_pathMtuOptions is not { } options)
        {
            return;
        }

        lock (_feedbackGate)
        {
            if (!_pathMtus.TryGetValue((path.Local, path.Remote), out PathMtuDiscovery? search))
            {
                search = new PathMtuDiscovery(
                    path.LocalKind == IceCandidateKind.Relayed
                        ? options with
                        {
                            MaximumSize = Math.Max(
                                options.BaseSize,
                                options.MaximumSize - TurnFramingRoom
                            ),
                        }
                        : options
                );
                _pathMtus[(path.Local, path.Remote)] = search;
            }

            // A probe still out on the old pair is answered, if at all, on that pair's feedback.
            _pathMtu?.OnProbeAbandoned();
            _pathMtu = search;
            UpdateDatagramLimit();
        }
    }

    // On the process tick: sends a probe when the search wants one and a packet to build it from is at hand.
    private void ProbePathMtu(TimeSpan now)
    {
        if (!_ccfbNegotiated || _srtp is not { } srtp)
        {
            return;
        }

        foreach ((uint mediaSsrc, RtxSender rtx) in Volatile.Read(ref _rtx).Send)
        {
            if (
                !_sendHistory.TryGetValue(mediaSsrc, out RtpSendHistory? history)
                || !history.TryGetLargestRecent(
                    ProbeSourceWindow,
                    out ReadOnlyMemory<byte> original
                )
                || original.Length < 2
                || !rtx.PayloadTypes.TryGetValue(
                    (byte)(original.Span[1] & 0x7F),
                    out byte rtxPayloadType
                )
            )
            {
                continue;
            }

            int overhead = srtp.ProtectionOverhead;
            int unpadded = original.Length + RtxRoom;
            int? target;
            lock (_feedbackGate)
            {
                target = _pathMtu?.TakeProbe(now, unpadded + MaxRtpPadding + overhead);
            }

            if (target is { } size)
            {
                // Media keeps within the confirmed size, so the padding is at least one octet; were it not,
                // the probe would only test a larger size than asked, which confirms that one too.
                SendProbe(
                    original.Span,
                    rtx,
                    rtxPayloadType,
                    Math.Max(1, size - overhead - unpadded)
                );
            }

            return;
        }
    }

    private void SendProbe(
        ReadOnlySpan<byte> original,
        RtxSender rtx,
        byte rtxPayloadType,
        int padding
    )
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(
            original.Length + RtxRoom + padding + SrtpSession.MaxProtectionOverhead
        );
        if (
            !RtxStream.TryWrap(
                original,
                buffer,
                rtxPayloadType,
                rtx.Ssrc,
                rtx.NextSequence(),
                out int length
            )
        )
        {
            ArrayPool<byte>.Shared.Return(buffer);
            AbandonProbe();
            return;
        }

        // RFC 3550 section 5.1: the P bit, then padding whose last octet counts it.
        buffer[0] |= 0x20;
        buffer.AsSpan(length, padding - 1).Clear();
        buffer[length + padding - 1] = (byte)padding;
        _pacer.Enqueue(new PacedPacket(buffer, length + padding, TrafficClass.Probe));
    }

    // A probe the pacer was handed but the connection did not send.
    private void AbandonProbe()
    {
        lock (_feedbackGate)
        {
            _pathMtu?.OnProbeAbandoned();
        }
    }

    // Under _feedbackGate, from RecordSent: the probe went out under this packet number.
    private void NoteProbeSent(long packetId) => _pathMtu?.OnProbeSent(packetId);

    // Under _feedbackGate, with every batch of resolved packets: each one acknowledged or lost, for the
    // search and for black hole detection.
    private void ObservePathMtu(ReadOnlySpan<PacketObservation> observations, TimeSpan now)
    {
        if (_pathMtu is not { } search || observations.IsEmpty)
        {
            return;
        }

        bool grew = false;
        foreach (PacketObservation observation in observations)
        {
            SentPacket packet = observation.Packet;
            if (observation.Outcome == PacketOutcome.Lost)
            {
                search.OnLost(packet.Id, packet.Size, now);
            }
            else
            {
                grew |= search.OnAcknowledged(packet.Id, packet.Size);
            }
        }

        if (search.CheckBlackHole(now))
        {
            LogPathMtuBlackHole(search.Current);
            UpdateDatagramLimit();
        }
        else if (grew)
        {
            UpdateDatagramLimit();
        }
    }

    // Under _feedbackGate.
    private void UpdateDatagramLimit()
    {
        int size = _pathMtu?.Current ?? Volatile.Read(ref _datagramLimit);
        if (Interlocked.Exchange(ref _datagramLimit, size) != size)
        {
            LogPathMtu(size);
            MaximumDatagramSizeChanged?.Invoke(size);
        }
    }

    [LoggerMessage(
        EventId = 1170,
        Level = LogLevel.Information,
        Message = "Path MTU: datagrams up to {Size} bytes"
    )]
    private partial void LogPathMtu(int size);

    [LoggerMessage(
        EventId = 1171,
        Level = LogLevel.Warning,
        Message = "Packets above the base size are being lost as a smaller path MTU would lose them; back to {Size}-byte datagrams"
    )]
    private partial void LogPathMtuBlackHole(int size);
}
