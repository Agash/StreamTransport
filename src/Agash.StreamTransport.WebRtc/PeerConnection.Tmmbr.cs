using System.Buffers;
using Agash.StreamTransport.WebRtc.Rtcp;
using Agash.StreamTransport.WebRtc.Sdp;
using Agash.StreamTransport.WebRtc.Srtp;
using Microsoft.Extensions.Logging;

namespace Agash.StreamTransport.WebRtc;

/// <summary>
/// Temporary maximum bit rate requests (RFC 5104 section 4.2), which WebRTC endpoints support (RFC 8834
/// section 5.1.6) where <c>ccm tmmbr</c> was agreed: a request from the peer about one of this side's
/// streams caps what this side sends, beside the description's limits, and is confirmed with a TMMBN; this
/// side can ask the peer to cap one of its streams the same way, which a receiver uses to change what it
/// takes while the session runs.
/// </summary>
public sealed partial class PeerConnection
{
    // The overhead a request here counts in its limit: IPv4, UDP, RTP and the SRTP tag (RFC 5104 section
    // 4.2.1.2).
    private const int RequestOverhead = 20 + 8 + 12 + 16;

    // RFC 5104 section 4.2.1.1: the largest bit rate the fields hold, which takes a limit back off.
    private const long NoLimit = 0x1FFFFL << 46;

    private readonly Dictionary<uint, long> _tmmbrRequests = [];
    private long _tmmbrLimit = long.MaxValue;

    /// <summary>
    /// The most the peer last asked this side to send with TMMBR, in bits per second; null when it has not
    /// asked or has taken its request back.
    /// </summary>
    public long? PeerRequestedLimitBitsPerSecond
    {
        get
        {
            long limit = Interlocked.Read(ref _tmmbrLimit);
            return limit == long.MaxValue ? null : limit;
        }
    }

    /// <summary>
    /// Asks the peer to keep one of its streams under a bit rate (TMMBR), or to stop limiting it.
    /// </summary>
    /// <param name="mediaSsrc">The peer's stream.</param>
    /// <param name="bitsPerSecond">The limit; null takes it back.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    /// <returns>False when <c>ccm tmmbr</c> was not agreed or nothing could be sent yet.</returns>
    public async ValueTask<bool> RequestSendLimitAsync(
        uint mediaSsrc,
        long? bitsPerSecond,
        CancellationToken cancellationToken = default
    )
    {
        if (!FeedbackAgreed(SdpMediaKind.Video, "ccm tmmbr"))
        {
            return false;
        }

        long limit = bitsPerSecond ?? NoLimit;
        return await SendTmmbAsync(
                (buffer, sender) =>
                    RtcpFeedback.BuildTmmbr(buffer, sender, mediaSsrc, limit, RequestOverhead),
                FeedbackSenderFor(mediaSsrc),
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    // A request from the peer: its entries for this side's streams set its limit, the lowest of all
    // requesters' limits caps what is sent, and the request is confirmed with the bounding set (section
    // 4.2.2; with one peer the bounding set is its request).
    private void OnTmmbr(uint requester, List<TmmbEntry> entries)
    {
        if (!FeedbackAgreed(SdpMediaKind.Video, "ccm tmmbr"))
        {
            return;
        }

        long requested = long.MaxValue;
        int overhead = 0;
        bool ours = false;
        foreach (TmmbEntry entry in entries)
        {
            if (IsOwnSource(entry.Ssrc) || entry.Ssrc == _rtcpSenderSsrc)
            {
                requested = Math.Min(requested, entry.BitsPerSecond);
                overhead = entry.Overhead;
                ours = true;
            }
        }

        if (!ours)
        {
            return;
        }

        long limit;
        lock (_tmmbrRequests)
        {
            if (requested >= NoLimit)
            {
                _ = _tmmbrRequests.Remove(requester);
            }
            else
            {
                _tmmbrRequests[requester] = requested;
            }

            limit = long.MaxValue;
            foreach (long each in _tmmbrRequests.Values)
            {
                limit = Math.Min(limit, each);
            }
        }

        _ = Interlocked.Exchange(ref _tmmbrLimit, limit);
        LogTmmbr(_logger, requester, requested);
        if (_controller is { } controller)
        {
            OnEstimate(controller.Current);
        }

        long confirmed = requested;
        _ = SendTmmbAsync(
            (buffer, sender) =>
                RtcpFeedback.BuildTmmbn(
                    buffer,
                    sender,
                    [new TmmbEntry(requester, confirmed, overhead)]
                ),
            _rtcpSenderSsrc,
            CancellationToken.None
        );
    }

    private async ValueTask<bool> SendTmmbAsync(
        Func<Span<byte>, uint, int> build,
        uint senderSsrc,
        CancellationToken cancellationToken
    )
    {
        if (_srtp is not { } srtp || _iceAgent is not { } agent)
        {
            return false;
        }

        // Built behind the headroom a relay frames into, so the packet goes out in place.
        byte[] buffer = ArrayPool<byte>.Shared.Rent(
            Ice.IceAgent.SendHeadroom
                + Ice.IceAgent.SendTailroom
                + FeedbackPrefixCapacity
                + 32
                + SrtpSession.MaxRtcpProtectionOverhead
        );
        Span<byte> rtcp = buffer.AsSpan(Ice.IceAgent.SendHeadroom);
        try
        {
            int prefix = WriteFeedbackPrefix(rtcp);
            int length = prefix + build(rtcp[prefix..], senderSsrc);
            int protectedLength = srtp.ProtectRtcp(rtcp, length);
            CountRtcpSent(protectedLength);
            await agent
                .SendInPlaceAsync(
                    buffer,
                    Ice.IceAgent.SendHeadroom,
                    protectedLength,
                    default,
                    cancellationToken
                )
                .ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            LogTmmbFailed(_logger, exception);
            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    [LoggerMessage(
        EventId = 1194,
        Level = LogLevel.Information,
        Message = "Source {Requester} asked for at most {BitsPerSecond} bit/s (TMMBR, RFC 5104 section 4.2.1)"
    )]
    private static partial void LogTmmbr(ILogger logger, uint requester, long bitsPerSecond);

    [LoggerMessage(
        EventId = 1195,
        Level = LogLevel.Debug,
        Message = "A TMMBR or TMMBN could not be sent"
    )]
    private static partial void LogTmmbFailed(ILogger logger, Exception exception);
}
