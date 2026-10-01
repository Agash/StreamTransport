using System.Collections.Immutable;
using Agash.StreamTransport.Media;

namespace Agash.StreamTransport.Sessions;

/// <summary>The <c>a=fmtp</c> parameter list: <c>name=value</c> pairs separated by semicolons.</summary>
internal static class FormatParameters
{
    /// <summary>
    /// The ways of carrying alpha a side supports for a codec, most preferred first, comma-separated: what
    /// a sender can produce, or what a receiver can take. The answer's first way the offer also lists is
    /// the one used. Peers that do not know the parameter ignore it, as RFC 8866 has them do with unknown
    /// parameters, and get opaque video.
    /// </summary>
    public const string AlphaName = "x-alpha";

    /// <summary>Alpha side by side with the colour in a frame twice as wide.</summary>
    public const string SideBySide = "side-by-side";

    /// <summary>Alpha as the codec's own alpha layer (H.265's auxiliary alpha layer).</summary>
    public const string Layer = "layer";

    /// <summary>The ways of carrying alpha an <c>a=fmtp</c> value lists, in its order.</summary>
    /// <param name="value">The value, or null.</param>
    /// <returns>The layouts; empty when it lists none.</returns>
    public static ImmutableArray<AlphaLayout> AlphaLayouts(string? value) =>
        Parse(value).TryGetValue(AlphaName, out string? list)
            ?
            [
                .. list.Split(
                        ',',
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
                    )
                    .Select(static way =>
                        string.Equals(way, Layer, StringComparison.OrdinalIgnoreCase)
                            ? AlphaLayout.Layer
                        : string.Equals(way, SideBySide, StringComparison.OrdinalIgnoreCase)
                            ? AlphaLayout.PackSideBySide
                        : AlphaLayout.None
                    )
                    .Where(static layout => layout != AlphaLayout.None),
            ]
            : [];

    /// <summary>The value of <see cref="AlphaName"/> for a list of ways.</summary>
    /// <param name="layouts">The ways, most preferred first.</param>
    /// <returns>The comma-separated value.</returns>
    public static string AlphaValue(IEnumerable<AlphaLayout> layouts) =>
        string.Join(',', layouts.Select(static l => l == AlphaLayout.Layer ? Layer : SideBySide));

    /// <summary>The way alpha travels: the answer's first way the offer also lists; none without one.</summary>
    /// <param name="offer">The offer's ways.</param>
    /// <param name="answer">The answer's ways.</param>
    /// <returns>The way, or <see cref="AlphaLayout.None"/>.</returns>
    public static AlphaLayout ChooseAlpha(
        ImmutableArray<AlphaLayout> offer,
        ImmutableArray<AlphaLayout> answer
    ) => answer.FirstOrDefault(offer.Contains);

    /// <summary>The parameters of an <c>a=fmtp</c> value, by name.</summary>
    /// <param name="value">The value, or null.</param>
    /// <returns>The parameters.</returns>
    public static ImmutableSortedDictionary<string, string> Parse(string? value)
    {
        ImmutableSortedDictionary<string, string>.Builder parameters =
            ImmutableSortedDictionary.CreateBuilder<string, string>(
                StringComparer.OrdinalIgnoreCase
            );
        foreach (
            string pair in (value ?? string.Empty).Split(
                ';',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            )
        )
        {
            int equals = pair.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0)
            {
                parameters[pair[..equals].Trim()] = pair[(equals + 1)..].Trim();
            }
        }

        return parameters.ToImmutable();
    }

    /// <summary>An <c>a=fmtp</c> value with one more parameter, or null when there are none.</summary>
    /// <param name="value">The value, or null.</param>
    /// <param name="name">The parameter's name.</param>
    /// <param name="parameter">Its value.</param>
    /// <returns>The combined value.</returns>
    public static string Append(string? value, string name, string parameter) =>
        string.IsNullOrEmpty(value) ? $"{name}={parameter}" : $"{value};{name}={parameter}";
}
