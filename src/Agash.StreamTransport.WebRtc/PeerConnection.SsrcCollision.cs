using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Frozen;
using System.Collections.Immutable;
using Agash.StreamTransport.WebRtc.Rtcp;
using Agash.StreamTransport.WebRtc.Sdp;
using Agash.StreamTransport.WebRtc.Srtp;
using Microsoft.Extensions.Logging;

namespace Agash.StreamTransport.WebRtc;

/// <summary>
/// SSRC collisions (RFC 3550 section 8.2, RFC 5576 section 5), which WebRTC endpoints detect and resolve
/// (RFC 8834 section 4.8). A collision is the peer sending RTP, or a sender or receiver report, from one of
/// this side's SSRCs, or naming one in its description; this side then says goodbye on that SSRC and goes
/// on with a new random one that neither side has used, and its descriptions name the new one with the old
/// as <c>previous-ssrc</c> (RFC 5576 section 6.2). Streams keep the SSRC they were set up with as their
/// identity: the new one replaces it as packets leave, and feedback about the new one is mapped back.
/// </summary>
public sealed partial class PeerConnection
{
    private readonly Lock _ssrcGate = new();
    private readonly HashSet<uint> _retiredSsrcs = [];
    private FrozenDictionary<uint, uint> _wireSsrcs = [];
    private FrozenDictionary<uint, uint> _setupSsrcs = [];

    /// <summary>
    /// Raised when a collision made this side change one of its SSRCs, with the old and the new one. The
    /// next description this side writes names the new one; an application that can renegotiate sends one
    /// (RFC 5576 section 5 recommends it).
    /// </summary>
    public event Action<uint, uint>? LocalSsrcChanged;

    // The SSRC a stream set up with goes out as, and back.
    internal uint WireSsrc(uint setup) =>
        _wireSsrcs.TryGetValue(setup, out uint wire) ? wire : setup;

    private uint SetupSsrc(uint wire) =>
        _setupSsrcs.TryGetValue(wire, out uint setup) ? setup : wire;

    // The SSRCs this side sends from, as they go out.
    private bool IsLocalWireSsrc(uint ssrc)
    {
        if (ssrc == _rtcpSenderSsrc)
        {
            return true;
        }

        foreach (MediaLine line in _options.Media)
        {
            if (
                (line.LocalSsrc != 0 && WireSsrc(line.LocalSsrc) == ssrc)
                || (line.RtxSsrc is { } rtx && WireSsrc(rtx) == ssrc)
                || (line.FecSsrc is { } fec && WireSsrc(fec) == ssrc)
            )
            {
                return true;
            }
        }

        return false;
    }

    // The SSRCs a description written now names as previous ones for a stream (section 6.2).
    private ImmutableArray<uint> PreviousSsrcs(uint setup)
    {
        if (!_wireSsrcs.ContainsKey(setup))
        {
            return [];
        }

        lock (_ssrcGate)
        {
            ImmutableArray<uint>.Builder previous = ImmutableArray.CreateBuilder<uint>();
            foreach ((uint retired, uint from) in _retiredFrom)
            {
                if (from == setup)
                {
                    previous.Add(retired);
                }
            }

            return previous.DrainToImmutable();
        }
    }

    // Each retired SSRC, with the SSRC its stream set up with.
    private readonly Dictionary<uint, uint> _retiredFrom = [];

    // Section 8.2: a packet from the peer carrying one of this side's SSRCs.
    private void CheckCollision(uint ssrc)
    {
        if (IsLocalWireSsrc(ssrc))
        {
            ResolveCollision(ssrc, "the peer sends from it");
        }
    }

    // RFC 5576 section 5: an SSRC the peer's description names is not used here; two signalled sources
    // that collide are acted on at once.
    private void AvoidSignalledCollisions(SdpDescription remote)
    {
        foreach (SdpMediaDescription media in remote.Media)
        {
            foreach (uint? ssrc in (uint?[])[media.Ssrc, media.RtxSsrc, media.FecSsrc])
            {
                if (ssrc is { } signalled && IsLocalWireSsrc(signalled))
                {
                    ResolveCollision(signalled, "the peer's description names it");
                }
            }
        }
    }

    // Section 8.2: goodbye on the colliding SSRC, and a new random one neither side has used.
    private void ResolveCollision(uint colliding, string reason)
    {
        uint replacement;
        lock (_ssrcGate)
        {
            if (!IsLocalWireSsrc(colliding))
            {
                return;
            }

            do
            {
                replacement = SecureRandom.Ssrc();
            } while (
                IsLocalWireSsrc(replacement)
                || _retiredSsrcs.Contains(replacement)
                || IsRemoteSsrc(replacement)
            );

            uint setup = SetupSsrc(colliding);
            _ = _retiredSsrcs.Add(colliding);
            _retiredFrom[colliding] = setup;
            Dictionary<uint, uint> wire = new(_wireSsrcs) { [setup] = replacement };
            Dictionary<uint, uint> back = [];
            foreach ((uint from, uint to) in wire)
            {
                back[to] = from;
            }

            _wireSsrcs = wire.ToFrozenDictionary();
            _setupSsrcs = back.ToFrozenDictionary();
            if (_rtcpSenderSsrc == colliding)
            {
                _rtcpSenderSsrc = replacement;
            }
        }

        lock (_reportGate)
        {
            _ = _sendStatistics.Remove(colliding);
        }

        ForgetBreaker(colliding);
        BuildSectionTables();
        LogSsrcCollision(_logger, colliding, replacement, reason);
        _ = SayGoodbyeAsync(colliding);
        LocalSsrcChanged?.Invoke(colliding, replacement);
    }

    // An SSRC the peer has signalled or sent from.
    private bool IsRemoteSsrc(uint ssrc)
    {
        foreach (SdpMediaDescription media in _remoteDescription?.Media ?? [])
        {
            if (media.Ssrc == ssrc || media.RtxSsrc == ssrc || media.FecSsrc == ssrc)
            {
                return true;
            }
        }

        lock (_reportGate)
        {
            return _remoteMembers.ContainsKey(ssrc) || _receiveStatistics.ContainsKey(ssrc);
        }
    }

    // A BYE for the retired SSRC (RFC 3550 section 6.6), behind a receiver report unless reduced-size
    // RTCP was agreed.
    private async Task SayGoodbyeAsync(uint ssrc)
    {
        if (_srtp is not { } srtp || _iceAgent is not { } agent)
        {
            return;
        }

        // Built behind the headroom a relay frames into, so the packet goes out in place.
        byte[] buffer = ArrayPool<byte>.Shared.Rent(
            Ice.IceAgent.SendHeadroom
                + Ice.IceAgent.SendTailroom
                + FeedbackPrefixCapacity
                + 16
                + SrtpSession.MaxRtcpProtectionOverhead
        );
        Span<byte> rtcp = buffer.AsSpan(Ice.IceAgent.SendHeadroom);
        try
        {
            int prefix = WriteFeedbackPrefix(rtcp);
            int length = prefix + RtcpSourceDescription.WriteGoodbye(rtcp[prefix..], [ssrc]);
            int protectedLength = srtp.ProtectRtcp(rtcp, length);
            CountRtcpSent(protectedLength);
            await agent
                .SendInPlaceAsync(
                    buffer,
                    Ice.IceAgent.SendHeadroom,
                    protectedLength,
                    default,
                    default
                )
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            LogGoodbyeFailed(_logger, exception, ssrc);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // A packet leaving on a stream whose SSRC changed carries the new one.
    private void RewriteSsrc(Span<byte> rtp)
    {
        if (_wireSsrcs.Count == 0)
        {
            return;
        }

        uint setup = BinaryPrimitives.ReadUInt32BigEndian(rtp[8..]);
        uint wire = WireSsrc(setup);
        if (wire != setup)
        {
            BinaryPrimitives.WriteUInt32BigEndian(rtp[8..], wire);
        }
    }

    [LoggerMessage(
        EventId = 1196,
        Level = LogLevel.Warning,
        Message = "SSRC {Ssrc} collided ({Reason}); sending from {Replacement} (RFC 3550 section 8.2)"
    )]
    private static partial void LogSsrcCollision(
        ILogger logger,
        uint ssrc,
        uint replacement,
        string reason
    );

    [LoggerMessage(
        EventId = 1197,
        Level = LogLevel.Debug,
        Message = "The BYE for retired SSRC {Ssrc} could not be sent"
    )]
    private static partial void LogGoodbyeFailed(ILogger logger, Exception exception, uint ssrc);
}
