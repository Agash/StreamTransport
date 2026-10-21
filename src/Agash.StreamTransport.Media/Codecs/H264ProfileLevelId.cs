using System.Globalization;

namespace Agash.StreamTransport.Media;

/// <summary>The H.264 profiles a session negotiates.</summary>
public enum H264Profile
{
    /// <summary>Constrained Baseline: no B-slices, no CABAC, no FMO or ASO. Every decoder takes it.</summary>
    ConstrainedBaseline,

    /// <summary>Baseline.</summary>
    Baseline,

    /// <summary>Main.</summary>
    Main,

    /// <summary>Constrained High: High with no B-slices and progressive frames only.</summary>
    ConstrainedHigh,

    /// <summary>High.</summary>
    High,

    /// <summary>High 4:4:4 Predictive.</summary>
    PredictiveHigh444,
}

/// <summary>
/// An H.264 profile and level as the format parameter <c>profile-level-id</c> carries them (RFC 6184
/// section 8.1): three bytes in hex, <c>profile_idc</c>, the constraint flags and <c>level_idc</c>.
/// </summary>
/// <param name="Profile">The profile.</param>
/// <param name="Level">The level as <c>level_idc</c>: 31 for 3.1, 52 for 5.2; 9 for level 1b.</param>
public readonly record struct H264ProfileLevelId(H264Profile Profile, int Level)
{
    /// <summary>The format parameter's name.</summary>
    public const string ParameterName = "profile-level-id";

    /// <summary>What a stream is when the parameter is absent: Baseline at level 1 (RFC 6184).</summary>
    public static H264ProfileLevelId Default { get; } = new(H264Profile.Baseline, 10);

    // profile_idc with the constraint flags that tell the profiles apart, after libwebrtc's table, except
    // that Main may also declare constraint_set4 and constraint_set5 (frames only, no B-slices), as
    // hardware encoders do. A pattern is eight characters for the flag byte, most significant first: 1,
    // 0, or x for either.
    private static readonly (byte ProfileIdc, string Flags, H264Profile Profile)[] Patterns =
    [
        (0x42, "x1xx0000", H264Profile.ConstrainedBaseline),
        (0x4D, "1xxx0000", H264Profile.ConstrainedBaseline),
        (0x58, "11xx0000", H264Profile.ConstrainedBaseline),
        (0x42, "x0xx0000", H264Profile.Baseline),
        (0x58, "10xx0000", H264Profile.Baseline),
        (0x4D, "0x0xxx00", H264Profile.Main),
        (0x64, "00000000", H264Profile.High),
        (0x64, "00001100", H264Profile.ConstrainedHigh),
        (0xF4, "00000000", H264Profile.PredictiveHigh444),
    ];

    /// <summary>Reads the profile and level from codec format parameters.</summary>
    /// <param name="parameters">The format parameters, by their SDP names.</param>
    /// <param name="value">The profile and level; <see cref="Default"/> when the parameter is absent.</param>
    /// <returns>False when the parameter is present but not a profile this knows.</returns>
    public static bool TryRead(
        IReadOnlyDictionary<string, string> parameters,
        out H264ProfileLevelId value
    )
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (!parameters.TryGetValue(ParameterName, out string? text))
        {
            value = Default;
            return true;
        }

        return TryParse(text, out value);
    }

    /// <summary>Parses a <c>profile-level-id</c> value.</summary>
    /// <param name="text">Six hex digits.</param>
    /// <param name="value">The profile and level.</param>
    /// <returns>Whether the value is a profile this knows.</returns>
    public static bool TryParse(string? text, out H264ProfileLevelId value)
    {
        value = default;
        if (
            text is not { Length: 6 }
            || !uint.TryParse(
                text,
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out uint bytes
            )
        )
        {
            return false;
        }

        byte profileIdc = (byte)(bytes >> 16);
        byte flags = (byte)(bytes >> 8);
        int level = (byte)bytes;
        foreach ((byte idc, string pattern, H264Profile profile) in Patterns)
        {
            if (idc == profileIdc && Fits(flags, pattern))
            {
                // Level 1b is level_idc 11 with constraint_set3 in the Baseline family (RFC 6184).
                bool level1b =
                    level == 11
                    && (flags & 0x10) != 0
                    && profile
                        is H264Profile.ConstrainedBaseline
                            or H264Profile.Baseline
                            or H264Profile.Main;
                value = new H264ProfileLevelId(profile, level1b ? 9 : level);
                return true;
            }
        }

        return false;
    }

    /// <summary>The <c>profile-level-id</c> value: six lowercase hex digits.</summary>
    /// <returns>The value.</returns>
    public override string ToString()
    {
        (byte idc, byte flags) = Profile switch
        {
            H264Profile.ConstrainedBaseline => ((byte)0x42, (byte)0xE0),
            H264Profile.Baseline => ((byte)0x42, (byte)0x00),
            H264Profile.Main => ((byte)0x4D, (byte)0x00),
            H264Profile.ConstrainedHigh => ((byte)0x64, (byte)0x0C),
            H264Profile.High => ((byte)0x64, (byte)0x00),
            _ => ((byte)0xF4, (byte)0x00),
        };
        int level = Level;
        if (level == 9)
        {
            // Level 1b: level_idc 11 with constraint_set3 for the Baseline family, 9 for the others.
            if (
                Profile
                is H264Profile.ConstrainedBaseline
                    or H264Profile.Baseline
                    or H264Profile.Main
            )
            {
                flags |= 0x10;
                level = 11;
            }
        }

        return FormattableString.Invariant($"{idc:x2}{flags:x2}{level:x2}");
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
