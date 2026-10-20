using System.Collections.Concurrent;
using System.Collections.Frozen;
using Agash.StreamTransport.WebRtc.Rtcp;
using Agash.StreamTransport.WebRtc.Rtp;
using Agash.StreamTransport.WebRtc.Sdp;
using Microsoft.Extensions.Logging;

namespace Agash.StreamTransport.WebRtc;

/// <summary>
/// Streams and their media sections under BUNDLE (RFC 9143 sections 9.2 and 15). Each local stream names
/// its section with the MID header extension until the peer's reports show it was received, and with an
/// SDES MID item in every report. Each received stream is associated with a section by the MID it
/// carries, then by its SSRC, then by a payload type only one section uses; a stream that fits no
/// section is not decoded.
/// </summary>
public sealed partial class PeerConnection
{
    private readonly Lock _sectionGate = new();
    private readonly Dictionary<uint, SectionBinding> _streamSections = [];
    private readonly HashSet<uint> _undecodable = [];
    private readonly ConcurrentDictionary<uint, byte> _midAcknowledged = new();
    private SectionTables _sections = SectionTables.Empty;

    // The tables the negotiated sections give: MID, local SSRC, payload type and remote SSRC to section.
    private void BuildSectionTables()
    {
        Dictionary<RtpMid, string> mids = [];
        Dictionary<string, FrozenSet<byte>> payloadTypes = new(StringComparer.Ordinal);
        Dictionary<byte, string?> byPayloadType = [];
        Dictionary<uint, RtpMid> localMids = [];
        foreach (NegotiatedMediaInfo section in NegotiatedMedia)
        {
            if (RtpMid.TryCreate(section.Mid, out RtpMid tag))
            {
                mids[tag] = section.Mid;
            }

            FrozenSet<byte> types =
            [
                .. section.Codecs.Select(static c => (byte)c.PayloadType),
                .. section.RemoteCodecs.Select(static c => (byte)c.PayloadType),
            ];
            payloadTypes[section.Mid] = types;

            // A payload type more than one section receives says nothing about the section (section 9.2).
            foreach (byte type in types)
            {
                byPayloadType[type] = byPayloadType.ContainsKey(type) ? null : section.Mid;
            }

            if (
                _options.Media.FirstOrDefault(l => l.Mid == section.Mid) is { } line
                && tag.Length > 0
            )
            {
                foreach (uint? ssrc in (uint?[])[line.LocalSsrc, line.RtxSsrc, line.FecSsrc])
                {
                    if (ssrc is { } local and not 0)
                    {
                        localMids[WireSsrc(local)] = tag;
                    }
                }
            }
        }

        Volatile.Write(
            ref _sections,
            new SectionTables(
                mids.ToFrozenDictionary(),
                payloadTypes.ToFrozenDictionary(StringComparer.Ordinal),
                byPayloadType
                    .Where(static p => p.Value is not null)
                    .ToFrozenDictionary(static p => p.Key, static p => p.Value!),
                localMids.ToFrozenDictionary()
            )
        );

        // The SSRCs the peer signalled for each section.
        lock (_sectionGate)
        {
            foreach (SdpMediaDescription remote in _remoteDescription?.Media ?? [])
            {
                foreach (uint? ssrc in (uint?[])[remote.Ssrc, remote.RtxSsrc, remote.FecSsrc])
                {
                    if (ssrc is { } signalled && !_streamSections.ContainsKey(signalled))
                    {
                        _streamSections[signalled] = new SectionBinding(remote.Mid, null);
                    }
                }
            }
        }
    }

    // Section 15.2: the MID goes on a stream's packets until a report from the peer covers the stream.
    private RtpExtensionValues WithMid(uint ssrc, RtpExtensionValues extensions) =>
        Volatile.Read(ref _extensions).Mid != 0
        && !_midAcknowledged.ContainsKey(ssrc)
        && Volatile.Read(ref _sections).LocalMids.TryGetValue(ssrc, out RtpMid mid)
            ? extensions with
            {
                Mid = mid,
            }
            : extensions;

    // A reception report about one of this side's streams: the peer has it, MID and all.
    private void OnStreamReported(uint ssrc) => _midAcknowledged.TryAdd(ssrc, 0);

    // The SDES chunk for a local source, with its section's MID (section 15.1).
    private SdesSource Described(uint ssrc) =>
        new(
            ssrc,
            Volatile.Read(ref _sections).LocalMids.TryGetValue(ssrc, out RtpMid mid) ? mid : default
        );

    // Section 9.2: the section a received packet's stream belongs to, or null when it fits none.
    internal string? AssociateSection(in RtpHeader header)
    {
        SectionTables tables = Volatile.Read(ref _sections);
        lock (_sectionGate)
        {
            _ = _streamSections.TryGetValue(header.Ssrc, out SectionBinding? known);
            if (header.Extensions.Mid is { } carried)
            {
                if (!tables.Mids.TryGetValue(carried, out string? named))
                {
                    NoteUndecodable(header.Ssrc, "its MID names no section");
                    return null;
                }

                // A MID updates the stream's section only from a packet newer than the last update
                // (RFC 7941 section 4.2.6).
                if (
                    known is null
                    || known.Sequence is not { } last
                    || (short)(header.SequenceNumber - last) > 0
                )
                {
                    known = new SectionBinding(named, header.SequenceNumber);
                    _streamSections[header.Ssrc] = known;
                }
            }

            if (known is not null)
            {
                return
                    tables.PayloadTypes.TryGetValue(known.Mid, out FrozenSet<byte>? types)
                    && types.Contains(header.PayloadType)
                    ? known.Mid
                    : NoteUndecodable(header.Ssrc, "its payload type is not its section's");
            }

            if (tables.ByPayloadType.TryGetValue(header.PayloadType, out string? section))
            {
                _streamSections[header.Ssrc] = new SectionBinding(section, null);
                return section;
            }

            return NoteUndecodable(header.Ssrc, "no MID, SSRC or payload type places it");
        }
    }

    // The section a remote stream was associated with, when it has been.
    private string? SectionOf(uint ssrc)
    {
        lock (_sectionGate)
        {
            return _streamSections.TryGetValue(ssrc, out SectionBinding? binding)
                ? binding.Mid
                : null;
        }
    }

    private string? NoteUndecodable(uint ssrc, string reason)
    {
        if (_undecodable.Add(ssrc))
        {
            LogStreamNotDecoded(_logger, ssrc, reason);
        }

        return null;
    }

    [LoggerMessage(
        EventId = 1189,
        Level = LogLevel.Information,
        Message = "RTP stream {Ssrc} is not decoded: {Reason} (RFC 9143 section 9.2)"
    )]
    private static partial void LogStreamNotDecoded(ILogger logger, uint ssrc, string reason);

    // A stream's section, and the sequence number of the packet whose MID set it (null when the SSRC
    // or a payload type set it).
    private sealed record SectionBinding(string Mid, ushort? Sequence);

    private sealed record SectionTables(
        FrozenDictionary<RtpMid, string> Mids,
        FrozenDictionary<string, FrozenSet<byte>> PayloadTypes,
        FrozenDictionary<byte, string> ByPayloadType,
        FrozenDictionary<uint, RtpMid> LocalMids
    )
    {
        public static SectionTables Empty { get; } = new([], [], [], []);
    }
}
