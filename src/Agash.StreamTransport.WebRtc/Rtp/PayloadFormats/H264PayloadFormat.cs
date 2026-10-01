using System.Collections.Immutable;
using Agash.StreamTransport.WebRtc.Sdp;

namespace Agash.StreamTransport.WebRtc.Rtp.PayloadFormats;

/// <summary>
/// The H.264 RTP payload format (RFC 6184) in non-interleaved mode (packetization-mode=1), offered as
/// Constrained High and then Constrained Baseline, each at level 5.2 with level asymmetry allowed. A
/// payload type matches a peer's when the packetization mode and the profile agree; the levels may
/// differ, each side receiving up to its own.
/// </summary>
public sealed class H264PayloadFormat : RtpPayloadFormat
{
    internal const int StapA = 24;
    internal const int FuA = 28;
    private const int Idr = 5;
    private const int Sps = 7;
    private const int Pps = 8;

    private H264PayloadFormat() { }

    /// <summary>The format.</summary>
    public static H264PayloadFormat Instance { get; } = new();

    /// <inheritdoc/>
    public override string EncodingName => "H264";

    /// <inheritdoc/>
    public override SdpMediaKind Kind => SdpMediaKind.Video;

    /// <inheritdoc/>
    public override int ClockRate => 90_000;

    /// <inheritdoc/>
    public override ImmutableArray<string?> FormatParameterSets =>
        [
            "level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=640c34",
            "level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42e034",
        ];

    /// <inheritdoc/>
    public override bool AreCompatible(string? local, string? remote) =>
        H264Parameters.PacketizationMode(local) == H264Parameters.PacketizationMode(remote)
        && H264Parameters.Profile(local) is { } profile
        && H264Parameters.Profile(remote) == profile;

    /// <inheritdoc/>
    public override ImmutableArray<string> RtcpFeedback => ["nack", "nack pli"];

    /// <inheritdoc/>
    public override RtpPayloadTraits KeyframeRequires =>
        RtpPayloadTraits.SequenceParameters | RtpPayloadTraits.PictureParameters;

    /// <inheritdoc/>
    public override IRtpPacketizer CreatePacketizer(int maxPayloadSize) =>
        new H264Packetizer(maxPayloadSize);

    /// <inheritdoc/>
    public override IRtpDepacketizer CreateDepacketizer() => new H264Depacketizer();

    /// <inheritdoc/>
    public override RtpPayloadTraits Inspect(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty)
        {
            return RtpPayloadTraits.None;
        }

        switch (payload[0] & 0x1F)
        {
            case StapA:
                RtpPayloadTraits traits = RtpPayloadTraits.None;
                foreach (ReadOnlySpan<byte> nal in new AggregatedNalUnits(payload[1..]))
                {
                    traits |= Classify(nal[0] & 0x1F);
                }

                return traits;
            case FuA:
                // Only the first fragment names the NAL unit's type.
                return payload.Length >= 2 && (payload[1] & 0x80) != 0
                    ? Classify(payload[1] & 0x1F)
                    : RtpPayloadTraits.None;
            case int type:
                return Classify(type);
        }
    }

    private static RtpPayloadTraits Classify(int nalType) =>
        nalType switch
        {
            Idr => RtpPayloadTraits.Keyframe,
            Sps => RtpPayloadTraits.SequenceParameters | RtpPayloadTraits.SequenceStart,
            Pps => RtpPayloadTraits.PictureParameters,
            _ => RtpPayloadTraits.None,
        };
}

/// <summary>
/// Packetizes H.264 access units per RFC 6184 in non-interleaved mode: consecutive small NAL units share
/// a STAP-A packet, a NAL unit that fits alone is a single-NAL-unit packet, and a larger one is split into
/// FU-A fragments.
/// </summary>
/// <param name="maxPayloadSize">The largest RTP payload in bytes; at least 3.</param>
public sealed class H264Packetizer(int maxPayloadSize)
    : NalUnitPacketizer(maxPayloadSize, nalHeaderSize: 1, fragmentHeaderSize: 2)
{
    /// <inheritdoc/>
    /// <remarks>STAP-A: the F bit of any unit and the highest NRI, with type 24.</remarks>
    protected override void WriteAggregationHeader(Span<byte> header, ReadOnlySpan<byte> run)
    {
        int forbidden = 0;
        int nri = 0;
        foreach (Range range in new AnnexBNalUnits(run, startsWithNal: true))
        {
            forbidden |= run[range.Start] & 0x80;
            nri = Math.Max(nri, run[range.Start] & 0x60);
        }

        header[0] = (byte)(forbidden | nri | H264PayloadFormat.StapA);
    }

    /// <inheritdoc/>
    /// <remarks>FU indicator: the NAL's F and NRI bits with type 28. FU header: start, end, and the NAL's type.</remarks>
    protected override void WriteFragmentHeader(
        Span<byte> header,
        ReadOnlySpan<byte> nalHeader,
        bool first,
        bool last
    )
    {
        header[0] = (byte)((nalHeader[0] & 0xE0) | H264PayloadFormat.FuA);
        header[1] = (byte)((first ? 0x80 : 0) | (last ? 0x40 : 0) | (nalHeader[0] & 0x1F));
    }
}

/// <summary>
/// Reassembles H.264 access units per RFC 6184 in non-interleaved mode: single NAL unit packets, STAP-A
/// and FU-A, fed in sequence order. The access unit comes out in Annex-B form on the marker bit. A
/// fragmented NAL unit missing its first or last fragment is dropped.
/// </summary>
public sealed class H264Depacketizer : IRtpDepacketizer
{
    private readonly AnnexBAssembler _assembler = new();

    /// <inheritdoc/>
    public bool TryPush(ReadOnlySpan<byte> payload, bool marker, out EncodedFrameBuffer frame)
    {
        if (!payload.IsEmpty)
        {
            switch (payload[0] & 0x1F)
            {
                case H264PayloadFormat.StapA:
                    foreach (ReadOnlySpan<byte> nal in new AggregatedNalUnits(payload[1..]))
                    {
                        _assembler.AppendNal(nal);
                    }

                    break;
                case H264PayloadFormat.FuA when payload.Length >= 2:
                    byte header = payload[1];
                    if ((header & 0x80) != 0)
                    {
                        _assembler.StartFragment([(byte)((payload[0] & 0xE0) | (header & 0x1F))]);
                    }

                    _assembler.AppendFragment(payload[2..]);
                    if ((header & 0x40) != 0)
                    {
                        _assembler.EndFragment();
                    }

                    break;
                case > 0 and < H264PayloadFormat.StapA:
                    _assembler.AppendNal(payload);
                    break;
                default:
                    // STAP-B, MTAP and FU-B belong to the interleaved mode, which is never negotiated.
                    break;
            }
        }

        return _assembler.TryComplete(marker, out frame);
    }

    /// <inheritdoc/>
    public void Dispose() => _assembler.Dispose();
}
