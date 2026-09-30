using System.Collections.Immutable;
using Agash.StreamTransport.WebRtc.Sdp;

namespace Agash.StreamTransport.WebRtc.Rtp.PayloadFormats;

/// <summary>
/// The H.265 RTP payload format (RFC 7798) without decoding order numbers (sprop-max-don-diff=0), Main
/// profile by default.
/// </summary>
public sealed class H265PayloadFormat : RtpPayloadFormat
{
    internal const int AggregationPacket = 48;
    internal const int FragmentationUnit = 49;
    internal const int PaciPacket = 50;
    private const int IrapFirst = 16;
    private const int IrapLast = 23;
    private const int Vps = 32;
    private const int Sps = 33;
    private const int Pps = 34;

    private H265PayloadFormat() { }

    /// <summary>The format.</summary>
    public static H265PayloadFormat Instance { get; } = new();

    /// <inheritdoc/>
    public override string EncodingName => "H265";

    /// <inheritdoc/>
    public override SdpMediaKind Kind => SdpMediaKind.Video;

    /// <inheritdoc/>
    public override int ClockRate => 90_000;

    /// <inheritdoc/>
    public override ImmutableArray<string> RtcpFeedback => ["nack", "nack pli"];

    /// <inheritdoc/>
    public override RtpPayloadTraits KeyframeRequires =>
        RtpPayloadTraits.VideoParameters
        | RtpPayloadTraits.SequenceParameters
        | RtpPayloadTraits.PictureParameters;

    /// <inheritdoc/>
    public override IRtpPacketizer CreatePacketizer(int maxPayloadSize) =>
        new H265Packetizer(maxPayloadSize);

    /// <inheritdoc/>
    public override IRtpDepacketizer CreateDepacketizer() => new H265Depacketizer();

    /// <inheritdoc/>
    public override RtpPayloadTraits Inspect(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2)
        {
            return RtpPayloadTraits.None;
        }

        switch (NalType(payload[0]))
        {
            case AggregationPacket:
                RtpPayloadTraits traits = RtpPayloadTraits.None;
                foreach (ReadOnlySpan<byte> nal in new AggregatedNalUnits(payload[2..]))
                {
                    traits |= Classify(NalType(nal[0]));
                }

                return traits;
            case FragmentationUnit:
                // Only the first fragment names the NAL unit's type.
                return payload.Length >= 3 && (payload[2] & 0x80) != 0
                    ? Classify(payload[2] & 0x3F)
                    : RtpPayloadTraits.None;
            case int type:
                return Classify(type);
        }
    }

    internal static int NalType(byte header) => (header >> 1) & 0x3F;

    private static RtpPayloadTraits Classify(int nalType) =>
        nalType switch
        {
            >= IrapFirst and <= IrapLast => RtpPayloadTraits.Keyframe,
            Vps => RtpPayloadTraits.VideoParameters | RtpPayloadTraits.SequenceStart,
            Sps => RtpPayloadTraits.SequenceParameters,
            Pps => RtpPayloadTraits.PictureParameters,
            _ => RtpPayloadTraits.None,
        };
}

/// <summary>
/// Packetizes H.265 access units per RFC 7798: consecutive small NAL units share an Aggregation Packet,
/// a NAL unit that fits alone is a single-NAL-unit packet, and a larger one is split into Fragmentation
/// Units.
/// </summary>
/// <param name="maxPayloadSize">The largest RTP payload in bytes; at least 4.</param>
public sealed class H265Packetizer(int maxPayloadSize)
    : NalUnitPacketizer(maxPayloadSize, nalHeaderSize: 2, fragmentHeaderSize: 3)
{
    /// <inheritdoc/>
    /// <remarks>The F bit of any unit and the lowest LayerId and TemporalId of all of them, with type 48.</remarks>
    protected override void WriteAggregationHeader(Span<byte> header, ReadOnlySpan<byte> run)
    {
        int forbidden = 0;
        int layerId = 0x3F;
        int temporalId = 0x7;
        foreach (Range range in new AnnexBNalUnits(run, startsWithNal: true))
        {
            ReadOnlySpan<byte> nal = run[range];
            forbidden |= nal[0] & 0x80;
            layerId = Math.Min(layerId, ((nal[0] & 0x1) << 5) | (nal[1] >> 3));
            temporalId = Math.Min(temporalId, nal[1] & 0x7);
        }

        header[0] = (byte)(forbidden | (H265PayloadFormat.AggregationPacket << 1) | (layerId >> 5));
        header[1] = (byte)(((layerId & 0x1F) << 3) | temporalId);
    }

    /// <inheritdoc/>
    /// <remarks>PayloadHdr: the NAL header with type 49. FU header: start, end, and the NAL's type.</remarks>
    protected override void WriteFragmentHeader(
        Span<byte> header,
        ReadOnlySpan<byte> nalHeader,
        bool first,
        bool last
    )
    {
        header[0] = (byte)((nalHeader[0] & 0x81) | (H265PayloadFormat.FragmentationUnit << 1));
        header[1] = nalHeader[1];
        header[2] = (byte)(
            (first ? 0x80 : 0) | (last ? 0x40 : 0) | H265PayloadFormat.NalType(nalHeader[0])
        );
    }
}

/// <summary>
/// Reassembles H.265 access units per RFC 7798 without decoding order numbers: single NAL unit packets,
/// Aggregation Packets and Fragmentation Units, fed in sequence order. The access unit comes out in
/// Annex-B form on the marker bit. A fragmented NAL unit missing its first or last fragment is dropped.
/// </summary>
public sealed class H265Depacketizer : IRtpDepacketizer
{
    private readonly AnnexBAssembler _assembler = new();

    /// <inheritdoc/>
    public bool TryPush(ReadOnlySpan<byte> payload, bool marker, out EncodedFrameBuffer frame)
    {
        if (payload.Length >= 2)
        {
            switch (H265PayloadFormat.NalType(payload[0]))
            {
                case H265PayloadFormat.AggregationPacket:
                    foreach (ReadOnlySpan<byte> nal in new AggregatedNalUnits(payload[2..]))
                    {
                        _assembler.AppendNal(nal);
                    }

                    break;
                case H265PayloadFormat.FragmentationUnit when payload.Length >= 3:
                    byte header = payload[2];
                    if ((header & 0x80) != 0)
                    {
                        // The NAL header is the PayloadHdr with the original type restored.
                        _assembler.StartFragment(
                            [(byte)((payload[0] & 0x81) | ((header & 0x3F) << 1)), payload[1]]
                        );
                    }

                    _assembler.AppendFragment(payload[3..]);
                    if ((header & 0x40) != 0)
                    {
                        _assembler.EndFragment();
                    }

                    break;
                case H265PayloadFormat.PaciPacket:
                    // PACI packets carry layered-coding information that is never negotiated.
                    break;
                default:
                    _assembler.AppendNal(payload);
                    break;
            }
        }

        return _assembler.TryComplete(marker, out frame);
    }

    /// <inheritdoc/>
    public void Dispose() => _assembler.Dispose();
}
