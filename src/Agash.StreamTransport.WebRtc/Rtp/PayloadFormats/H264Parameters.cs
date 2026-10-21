using System.Globalization;

namespace Agash.StreamTransport.WebRtc.Rtp.PayloadFormats;

// The H.264 format parameters that decide whether two payload types carry the same stream (RFC 6184
// section 8.1). Profiles are told apart by profile_idc and the constraint flags, after libwebrtc's
// table, with Main also allowed constraint_set4 and constraint_set5; the Media package reads the same
// parameter for encoders with H264ProfileLevelId.
internal static class H264Parameters
{
    private static readonly (byte ProfileIdc, string Flags, string Profile)[] Patterns =
    [
        (0x42, "x1xx0000", "ConstrainedBaseline"),
        (0x4D, "1xxx0000", "ConstrainedBaseline"),
        (0x58, "11xx0000", "ConstrainedBaseline"),
        (0x42, "x0xx0000", "Baseline"),
        (0x58, "10xx0000", "Baseline"),
        (0x4D, "0x0xxx00", "Main"),
        (0x64, "00000000", "High"),
        (0x64, "00001100", "ConstrainedHigh"),
        (0xF4, "00000000", "PredictiveHigh444"),
    ];

    // packetization-mode, 0 when absent.
    public static string PacketizationMode(string? fmtp) =>
        Value(fmtp, "packetization-mode") ?? "0";

    // The profile profile-level-id names, Baseline when absent (RFC 6184), null when not a known one.
    public static string? Profile(string? fmtp)
    {
        string? text = Value(fmtp, "profile-level-id") ?? "42000a";
        if (
            text.Length != 6
            || !uint.TryParse(
                text,
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out uint bytes
            )
        )
        {
            return null;
        }

        byte idc = (byte)(bytes >> 16);
        byte flags = (byte)(bytes >> 8);
        foreach ((byte profileIdc, string pattern, string profile) in Patterns)
        {
            if (profileIdc == idc && Fits(flags, pattern))
            {
                return profile;
            }
        }

        return null;
    }

    private static string? Value(string? fmtp, string name)
    {
        if (fmtp is null)
        {
            return null;
        }

        foreach (string part in fmtp.Split(';', StringSplitOptions.TrimEntries))
        {
            int equals = part.IndexOf('=', StringComparison.Ordinal);
            if (
                equals > 0
                && part.AsSpan(0, equals).Trim().Equals(name, StringComparison.OrdinalIgnoreCase)
            )
            {
                return part[(equals + 1)..].Trim();
            }
        }

        return null;
    }

    private static bool Fits(byte flags, string pattern)
    {
        for (int bit = 0; bit < 8; bit++)
        {
            bool set = (flags & (0x80 >> bit)) != 0;
            if ((pattern[bit] == '1' && !set) || (pattern[bit] == '0' && set))
            {
                return false;
            }
        }

        return true;
    }
}
