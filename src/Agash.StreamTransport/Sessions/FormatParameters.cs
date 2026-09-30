using System.Collections.Immutable;

namespace Agash.StreamTransport.Sessions;

/// <summary>The <c>a=fmtp</c> parameter list: <c>name=value</c> pairs separated by semicolons.</summary>
internal static class FormatParameters
{
    /// <summary>
    /// How this library says a video stream carries alpha side by side. Peers that do not know it
    /// ignore it, as RFC 8866 has them do with unknown parameters.
    /// </summary>
    public const string AlphaName = "x-alpha";

    /// <summary>The value of <see cref="AlphaName"/> for side-by-side alpha.</summary>
    public const string SideBySide = "side-by-side";

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
