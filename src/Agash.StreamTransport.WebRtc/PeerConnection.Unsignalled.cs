using System.Collections.Frozen;
using Microsoft.Extensions.Logging;

namespace Agash.StreamTransport.WebRtc;

/// <summary>
/// Repairs for streams the peer did not name in its description, as SFU-style WHEP servers answer: no
/// <c>a=ssrc</c> lines, so the SSRCs are learned from the payload types the section negotiated. The first
/// packet on a media payload type binds the stream's SSRC, which then takes NACKs; the first packet on that
/// payload type's RTX type binds the retransmission stream to it.
/// </summary>
public sealed partial class PeerConnection
{
    private readonly Lock _unsignalledGate = new();

    // Learns an unsignalled stream's SSRC from a packet of one of its payload types; true when it did.
    private bool LearnUnsignalled(uint ssrc, byte payloadType)
    {
        RtxConfiguration config = Volatile.Read(ref _rtx);
        if (
            config.Unsignalled.IsDefaultOrEmpty
            || config.Repairable.Contains(ssrc)
            || config.Receive.ContainsKey(ssrc)
        )
        {
            return false;
        }

        lock (_unsignalledGate)
        {
            config = Volatile.Read(ref _rtx);
            for (int i = 0; i < config.Unsignalled.Length; i++)
            {
                UnsignalledRepair stream = config.Unsignalled[i];
                if (stream.MediaSsrc is null && stream.MediaPayloadTypes.Contains(payloadType))
                {
                    Volatile.Write(
                        ref _rtx,
                        config with
                        {
                            Repairable = stream.Nack
                                ? [.. config.Repairable, ssrc]
                                : config.Repairable,
                            Unsignalled = config.Unsignalled.SetItem(
                                i,
                                stream with
                                {
                                    MediaSsrc = ssrc,
                                }
                            ),
                        }
                    );
                    LogLearnedSsrc(_logger, ssrc, stream.Mid, "media");
                    return true;
                }

                if (
                    stream is { MediaSsrc: { } media, RtxBound: false }
                    && stream.OriginalPayloadTypes.ContainsKey(payloadType)
                )
                {
                    Dictionary<uint, RtxReceiver> receive = new(config.Receive)
                    {
                        [ssrc] = new RtxReceiver(media, stream.OriginalPayloadTypes),
                    };
                    Volatile.Write(
                        ref _rtx,
                        config with
                        {
                            Receive = receive.ToFrozenDictionary(),
                            Unsignalled = config.Unsignalled.SetItem(
                                i,
                                stream with
                                {
                                    RtxBound = true,
                                }
                            ),
                        }
                    );
                    LogLearnedSsrc(_logger, ssrc, stream.Mid, "retransmission");
                    return true;
                }
            }
        }

        return false;
    }

    // A section whose peer named no SSRCs: its media payload types, and each RTX type with its original.
    private sealed record UnsignalledRepair(
        string Mid,
        FrozenSet<byte> MediaPayloadTypes,
        FrozenDictionary<byte, byte> OriginalPayloadTypes,
        uint? MediaSsrc = null,
        bool RtxBound = false,
        bool Nack = false
    );

    [LoggerMessage(
        EventId = 1178,
        Level = LogLevel.Information,
        Message = "Ignored remote {Candidate}: not a UDP address this stack can check (an mDNS name or TCP); a peer behind it is learned from its checks"
    )]
    private partial void LogIgnoredCandidate(string candidate);

    [LoggerMessage(
        EventId = 1177,
        Level = LogLevel.Information,
        Message = "Learned SSRC {Ssrc} as the {Stream} stream of section {Mid}, which the peer did not name"
    )]
    private static partial void LogLearnedSsrc(
        ILogger logger,
        uint ssrc,
        string mid,
        string stream
    );
}
