using System.Buffers;
using Agash.StreamTransport.WebRtc.Rtcp;
using Agash.StreamTransport.WebRtc.Srtp;

namespace Agash.StreamTransport.WebRtc;

/// <summary>
/// Receive-side loss recovery for <see cref="PeerConnection"/>: a NACK requester modelled on libwebrtc's
/// <c>modules/video_coding/nack_requester</c>. For each media stream that has an RTX partner configured (so the
/// peer can serve retransmissions), it tracks RTP sequence continuity, sends a Generic NACK (RFC 4585) the
/// instant a forward gap appears, resends still-missing sequences on a coarse interval, and - when a gap cannot
/// be recovered within the retry budget or the missing set overflows - falls back to a Picture Loss Indication
/// so the sender emits a fresh keyframe. Inbound RTX packets are unwrapped in <see cref="ReceiveRtp"/>; this
/// half decides <i>what</i> to ask for.
/// </summary>
public sealed partial class PeerConnection
{
    private readonly Lock _nackGate = new();
    private readonly Dictionary<uint, NackStream> _nackStreams = [];
    private readonly Dictionary<uint, byte> _remoteMediaPayloadType = [];
    private readonly List<ushort> _nackScratch = [];

    // libwebrtc parity: bound the missing set, give up to a keyframe after a fixed retry budget, and treat a
    // very large forward jump as a stream reset rather than a 60k-packet gap.
    private const int MaxNackEntries = 1000;            // kMaxNackPackets
    private const int MaxNackRetries = 100;             // kMaxNackRetries (a packet is NACKed at most this often)
    private const int MaxNackPacketAge = 10_000;        // a forward jump beyond this is a reset, not loss (kMaxPacketAge)
    private const long NackResendIntervalMicros = 100_000; // resend a still-missing seq at most every RTT (kDefaultRtt)

    // The original media payload type to restore when unwrapping an RTX packet (RFC 4588 carries only the
    // original sequence number, not the PT). Learned from the media stream's own packets, which always precede
    // any retransmission of them.
    private void RememberMediaPayloadType(uint mediaSsrc, byte payloadType)
    {
        lock (_nackGate)
        {
            _remoteMediaPayloadType[mediaSsrc] = payloadType;
        }
    }

    // Recognize an inbound RTX packet: its SSRC + PT match a configured RTX stream. SSRC config is symmetric
    // (both peers use the same constants), so _rtx - built for our send side - also describes the RTX stream the
    // peer retransmits on. Returns the media SSRC the retransmission belongs to and the PT to restore.
    private bool TryRecognizeRtx(uint ssrc, byte payloadType, out uint mediaSsrc, out byte originalPayloadType)
    {
        foreach ((uint media, RtxState rtx) in _rtx)
        {
            if (rtx.Ssrc == ssrc && rtx.PayloadType == payloadType)
            {
                mediaSsrc = media;
                lock (_nackGate)
                {
                    return _remoteMediaPayloadType.TryGetValue(media, out originalPayloadType);
                }
            }
        }

        mediaSsrc = 0;
        originalPayloadType = 0;
        return false;
    }

    // Note a received media sequence number. Fires an immediate NACK for any newly detected gap and a keyframe
    // request if the missing set overflows. Called on the receive thread for NACK-eligible media (has RTX).
    private void OnMediaSequence(uint mediaSsrc, ushort sequence)
    {
        bool keyframe;
        _nackScratch.Clear();
        lock (_nackGate)
        {
            if (!_nackStreams.TryGetValue(mediaSsrc, out NackStream? stream))
            {
                stream = new NackStream();
                _nackStreams[mediaSsrc] = stream;
            }

            stream.OnReceived(sequence, NowMicros(), _nackScratch, out keyframe);
        }

        if (_nackScratch.Count > 0)
        {
            SendNack(mediaSsrc, _nackScratch);
        }

        if (keyframe)
        {
            _ = RequestKeyframeAsync(mediaSsrc);
        }
    }

    // Timer tick (driven from the congestion-feedback timer): resend still-missing sequences whose resend
    // interval has elapsed, and request a keyframe for any stream whose retry budget is exhausted.
    private void ProcessNackResends()
    {
        long now = NowMicros();
        lock (_nackGate)
        {
            foreach ((uint mediaSsrc, NackStream stream) in _nackStreams)
            {
                _nackScratch.Clear();
                stream.GetDueResends(now, _nackScratch, out bool keyframe);
                if (_nackScratch.Count > 0)
                {
                    SendNack(mediaSsrc, _nackScratch);
                }

                if (keyframe)
                {
                    _ = RequestKeyframeAsync(mediaSsrc);
                }
            }
        }
    }

    private void SendNack(uint mediaSsrc, List<ushort> sequences)
    {
        if (_srtp is not { } srtp || _iceAgent is not { } agent)
        {
            return;
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(12 + (sequences.Count * 4) + SrtpSession.RtcpProtectionOverhead);
        try
        {
            int length = RtcpFeedback.BuildNack(buffer, _rtcpSenderSsrc, mediaSsrc,
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(sequences));
            int protectedLength = srtp.ProtectRtcp(buffer, length);
            Interlocked.Add(ref _nackSequencesRequested, sequences.Count);
            _ = agent.SendAsync(buffer.AsMemory(0, protectedLength));
        }
        catch (Exception)
        {
            // NACK is best-effort; a send race during teardown is benign.
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Per-stream NACK state: the highest in-order sequence seen and the set of sequences known missing, each
    /// with its retry count and last-NACKed time. Mirrors libwebrtc's NackRequester accounting; not thread-safe
    /// (callers hold <see cref="_nackGate"/>).
    /// </summary>
    private sealed class NackStream
    {
        private readonly Dictionary<ushort, NackEntry> _missing = [];
        private int _highest = -1;

        public void OnReceived(ushort sequence, long nowMicros, List<ushort> nackNow, out bool keyframe)
        {
            keyframe = false;
            if (_highest < 0)
            {
                _highest = sequence;
                return;
            }

            ushort forwardDistance = (ushort)(sequence - (ushort)_highest);
            if (forwardDistance == 0)
            {
                return; // duplicate of the highest sequence.
            }

            if (forwardDistance < 0x8000)
            {
                // Forward progress. A jump larger than the age cap is a reset/seek, not 60k lost packets.
                if (forwardDistance > MaxNackPacketAge)
                {
                    _missing.Clear();
                    _highest = sequence;
                    keyframe = true;
                    return;
                }

                for (ushort missing = (ushort)(_highest + 1); missing != sequence; missing++)
                {
                    if (_missing.TryAdd(missing, new NackEntry(0, nowMicros)))
                    {
                        nackNow.Add(missing);
                    }
                }

                _highest = sequence;
            }
            else
            {
                // A sequence at or behind the highest: a late, reordered, or RTX-recovered packet. Clear it from
                // the missing set - it is no longer lost.
                _missing.Remove(sequence);
            }

            if (_missing.Count > MaxNackEntries)
            {
                // The receiver has fallen too far behind to recover packet-by-packet; ask for a fresh keyframe.
                _missing.Clear();
                keyframe = true;
            }
        }

        public void GetDueResends(long nowMicros, List<ushort> resend, out bool keyframe)
        {
            keyframe = false;
            if (_missing.Count == 0)
            {
                return;
            }

            List<ushort>? exhausted = null;
            foreach ((ushort sequence, NackEntry entry) in _missing)
            {
                if (nowMicros - entry.LastSentMicros < NackResendIntervalMicros)
                {
                    continue;
                }

                if (entry.Retries >= MaxNackRetries)
                {
                    (exhausted ??= []).Add(sequence);
                    keyframe = true;
                    continue;
                }

                _missing[sequence] = new NackEntry(entry.Retries + 1, nowMicros);
                resend.Add(sequence);
            }

            if (exhausted is not null)
            {
                foreach (ushort sequence in exhausted)
                {
                    _missing.Remove(sequence);
                }
            }
        }
    }

    private readonly record struct NackEntry(int Retries, long LastSentMicros);
}
