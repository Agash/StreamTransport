using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Agash.StreamTransport.WebRtc.Sdp;

namespace Agash.StreamTransport.WebRtc.Rtp.PayloadFormats;

/// <summary>
/// The RTP payload formats an endpoint supports, found by encoding name. Built once from the formats a
/// host registers; later registrations of a name replace earlier ones.
/// </summary>
public sealed class RtpPayloadFormatRegistry
{
    private readonly FrozenDictionary<string, RtpPayloadFormat> _byName;

    /// <summary>A registry of the given formats.</summary>
    /// <param name="formats">The formats; a later one with the same encoding name wins.</param>
    public RtpPayloadFormatRegistry(IEnumerable<RtpPayloadFormat> formats)
    {
        ArgumentNullException.ThrowIfNull(formats);
        Dictionary<string, RtpPayloadFormat> byName = new(StringComparer.OrdinalIgnoreCase);
        foreach (RtpPayloadFormat format in formats)
        {
            ArgumentNullException.ThrowIfNull(format, nameof(formats));
            byName[format.EncodingName] = format;
        }

        _byName = byName.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        Formats = [.. _byName.Values];
    }

    /// <summary>The formats this library implements: H.264, H.265, AV1 and Opus.</summary>
    public static RtpPayloadFormatRegistry BuiltIn { get; } =
        new([
            H264PayloadFormat.Instance,
            H265PayloadFormat.Instance,
            Av1PayloadFormat.Instance,
            OpusPayloadFormat.Instance,
        ]);

    /// <summary>Every registered format.</summary>
    public ImmutableArray<RtpPayloadFormat> Formats { get; }

    /// <summary>Finds the format with an encoding name.</summary>
    /// <param name="encodingName">The <c>a=rtpmap</c> encoding name.</param>
    /// <param name="format">The format when found.</param>
    /// <returns>True when a format has the name.</returns>
    public bool TryGet(string encodingName, [NotNullWhen(true)] out RtpPayloadFormat? format) =>
        _byName.TryGetValue(encodingName, out format);

    /// <summary>Finds the format of a negotiated codec: its encoding name and clock rate.</summary>
    /// <param name="codec">The codec from a session description.</param>
    /// <param name="format">The format when found.</param>
    /// <returns>True when a format matches the codec.</returns>
    public bool TryGet(SdpCodec codec, [NotNullWhen(true)] out RtpPayloadFormat? format)
    {
        if (TryGet(codec.EncodingName, out format) && format.Matches(codec))
        {
            return true;
        }

        format = null;
        return false;
    }
}
