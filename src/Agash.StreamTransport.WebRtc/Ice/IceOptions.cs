namespace Agash.StreamTransport.WebRtc.Ice;

/// <summary>The ICE options an agent signals (<c>a=ice-options</c>, RFC 8839 section 5.6).</summary>
[Flags]
public enum IceOptions
{
    /// <summary>None.</summary>
    None = 0,

    /// <summary>Trickle ICE (RFC 8838): candidates may follow the description.</summary>
    Trickle = 1,

    /// <summary>The agent follows RFC 8445 rather than RFC 5245 (RFC 8445 section 10).</summary>
    Ice2 = 2,

    /// <summary>
    /// Renomination (draft-thatcher-ice-renomination): the controlling agent may nominate another pair at
    /// any time, numbered with the NOMINATION attribute, and the controlled agent takes the highest. Without
    /// it a pair is nominated once, and moving takes an ICE restart (RFC 8445 section 8.1.1).
    /// </summary>
    Renomination = 4,

    /// <summary>What this stack signals: every option above.</summary>
    Supported = Trickle | Ice2 | Renomination,
}

internal static class IceOptionsText
{
    public static IceOptions Parse(string value)
    {
        IceOptions options = IceOptions.None;
        foreach (string token in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            options |= token switch
            {
                "trickle" => IceOptions.Trickle,
                "ice2" => IceOptions.Ice2,
                "renomination" => IceOptions.Renomination,
                _ => IceOptions.None,
            };
        }

        return options;
    }

    public static string Format(IceOptions options) =>
        string.Join(
            ' ',
            new[]
            {
                (IceOptions.Trickle, "trickle"),
                (IceOptions.Ice2, "ice2"),
                (IceOptions.Renomination, "renomination"),
            }
                .Where(option => options.HasFlag(option.Item1))
                .Select(static option => option.Item2)
        );
}
