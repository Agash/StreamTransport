using System.Globalization;
using Agash.StreamTransport.WebRtc.Sdp;

namespace Agash.StreamTransport.WebRtc.Rtp;

/// <summary>
/// The <c>rtx</c> codec of RFC 4588: a payload type that carries retransmissions of the media payload
/// type its <c>apt</c> parameter names.
/// </summary>
public static class Rtx
{
    /// <summary>The encoding name: <c>rtx</c>.</summary>
    public const string EncodingName = "rtx";

    /// <summary>Whether a codec is an rtx codec.</summary>
    /// <param name="codec">The codec.</param>
    /// <returns>True for <c>rtx</c>.</returns>
    public static bool IsRtx(SdpCodec codec) =>
        string.Equals(codec.EncodingName, EncodingName, StringComparison.OrdinalIgnoreCase);

    /// <summary>The media payload type an rtx codec repairs, from its <c>apt</c> parameter.</summary>
    /// <param name="codec">The codec.</param>
    /// <returns>The payload type; null when the codec is not rtx or names none.</returns>
    public static int? Repairs(SdpCodec codec)
    {
        if (!IsRtx(codec) || codec.FormatParameters is not { } fmtp)
        {
            return null;
        }

        foreach (string part in fmtp.Split(';', StringSplitOptions.TrimEntries))
        {
            if (
                part.StartsWith("apt=", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(
                    part.AsSpan(4),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int apt
                )
                && apt is >= 0 and <= 127
            )
            {
                return apt;
            }
        }

        return null;
    }

    /// <summary>The rtx codec that repairs a media payload type.</summary>
    /// <param name="payloadType">The rtx codec's payload type.</param>
    /// <param name="repairs">The media payload type it repairs.</param>
    /// <param name="clockRate">The media codec's clock rate.</param>
    /// <returns>The codec.</returns>
    public static SdpCodec For(int payloadType, int repairs, int clockRate = 90_000) =>
        new(
            payloadType,
            EncodingName,
            clockRate,
            null,
            FormattableString.Invariant($"apt={repairs}"),
            []
        );
}
