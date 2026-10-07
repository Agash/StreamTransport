using System.Text;

namespace Agash.StreamTransport.Http;

/// <summary>
/// An <c>application/trickle-ice-sdpfrag</c> body (RFC 8840 section 9): the ICE credentials, a pseudo
/// <c>m=</c> line with the <c>mid</c> it stands for, and the candidates that belong to it. WHIP and WHEP
/// carry trickled candidates and ICE restarts in it as HTTP <c>PATCH</c> bodies (RFC 9725 section 4.3).
/// </summary>
/// <param name="UsernameFragment">The ICE username fragment, if the fragment carries it.</param>
/// <param name="Password">The ICE password, if the fragment carries it.</param>
/// <param name="MediaLine">
/// The pseudo <c>m=</c> line, without the <c>m=</c>; the receiver ignores what it says beyond standing for
/// the section <paramref name="Mid"/> names.
/// </param>
/// <param name="Mid">The identification tag of the media section the candidates belong to.</param>
/// <param name="Candidates">The candidates, each as its attribute value (<c>candidate:...</c>).</param>
/// <param name="EndOfCandidates">Whether the fragment says no more candidates follow.</param>
public sealed record TrickleIceFragment(
    string? UsernameFragment,
    string? Password,
    string MediaLine,
    string? Mid,
    IReadOnlyList<string> Candidates,
    bool EndOfCandidates = false
)
{
    /// <summary>The media type of the body.</summary>
    public const string MediaType = "application/trickle-ice-sdpfrag";

    /// <summary>
    /// The pseudo <c>m=</c> line a sender uses when it does not know the real one (RFC 8840 section 4.4).
    /// </summary>
    public const string DefaultMediaLine = "audio 9 RTP/AVP 0";

    /// <summary>
    /// A fragment for the first media section of a session description: its ICE credentials, its
    /// <c>m=</c> line and <c>mid</c>, as a WHIP or WHEP client sends for the offerer-tagged section of its
    /// bundle (RFC 9725 section 4.3.2).
    /// </summary>
    /// <param name="sdp">The session description.</param>
    /// <param name="candidates">The candidates to carry.</param>
    /// <returns>The fragment.</returns>
    public static TrickleIceFragment ForFirstSection(string sdp, IReadOnlyList<string> candidates)
    {
        ArgumentNullException.ThrowIfNull(sdp);
        ArgumentNullException.ThrowIfNull(candidates);
        string? ufrag = null;
        string? password = null;
        string? mediaLine = null;
        string? mid = null;
        foreach (string raw in sdp.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.StartsWith("m=", StringComparison.Ordinal))
            {
                if (mediaLine is not null)
                {
                    break;
                }

                mediaLine = line[2..];
            }
            else if (line.StartsWith("a=ice-ufrag:", StringComparison.Ordinal))
            {
                ufrag ??= line["a=ice-ufrag:".Length..];
            }
            else if (line.StartsWith("a=ice-pwd:", StringComparison.Ordinal))
            {
                password ??= line["a=ice-pwd:".Length..];
            }
            else if (mediaLine is not null && line.StartsWith("a=mid:", StringComparison.Ordinal))
            {
                mid ??= line["a=mid:".Length..];
            }
        }

        return new TrickleIceFragment(
            ufrag,
            password,
            mediaLine ?? DefaultMediaLine,
            mid,
            candidates
        );
    }

    /// <summary>
    /// A fragment of the first media section of a session description with that section's own
    /// candidates: what an ICE restart sends (RFC 9725 section 4.3.3).
    /// </summary>
    /// <param name="sdp">The session description.</param>
    /// <returns>The fragment.</returns>
    public static TrickleIceFragment FromFirstSection(string sdp)
    {
        ArgumentNullException.ThrowIfNull(sdp);
        List<string> candidates = [];
        bool inFirst = false;
        foreach (string raw in sdp.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.StartsWith("m=", StringComparison.Ordinal))
            {
                if (inFirst)
                {
                    break;
                }

                inFirst = true;
            }
            else if (inFirst && line.StartsWith("a=candidate:", StringComparison.Ordinal))
            {
                candidates.Add(line[2..]);
            }
        }

        return ForFirstSection(sdp, candidates);
    }

    /// <summary>
    /// A session description with this fragment's ICE in place of its own: every section's credentials
    /// replaced, every candidate dropped and this fragment's candidates put in the first section. How
    /// either end of an ICE restart over HTTP turns the fragment back into the description it stands for,
    /// the rest of which still applies (RFC 9725 section 4.3.3).
    /// </summary>
    /// <param name="sdp">The description negotiated before.</param>
    /// <returns>The description with this fragment's ICE.</returns>
    public string ApplyTo(string sdp)
    {
        ArgumentNullException.ThrowIfNull(sdp);
        StringBuilder result = new();
        int sections = 0;
        bool written = false;
        foreach (string raw in sdp.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (
                line.Length == 0
                || line.StartsWith("a=candidate:", StringComparison.Ordinal)
                || line == "a=end-of-candidates"
            )
            {
                continue;
            }

            if (line.StartsWith("m=", StringComparison.Ordinal) && ++sections == 2)
            {
                WriteCandidates(result);
                written = true;
            }

            if (
                line.StartsWith("a=ice-ufrag:", StringComparison.Ordinal)
                && UsernameFragment is { } ufrag
            )
            {
                line = "a=ice-ufrag:" + ufrag;
            }
            else if (
                line.StartsWith("a=ice-pwd:", StringComparison.Ordinal) && Password is { } password
            )
            {
                line = "a=ice-pwd:" + password;
            }

            result.Append(line).Append("\r\n");
        }

        if (!written)
        {
            WriteCandidates(result);
        }

        return result.ToString();
    }

    private void WriteCandidates(StringBuilder body)
    {
        foreach (string candidate in Candidates)
        {
            body.Append("a=").Append(candidate).Append("\r\n");
        }
    }

    /// <summary>Reads a fragment body.</summary>
    /// <param name="body">The body.</param>
    /// <param name="fragment">The fragment, when the body is one.</param>
    /// <returns>Whether the body is a fragment: SDP lines with candidates only after an <c>m=</c> line.</returns>
    public static bool TryParse(string body, out TrickleIceFragment fragment)
    {
        ArgumentNullException.ThrowIfNull(body);
        fragment = null!;
        string? ufrag = null;
        string? password = null;
        string? mediaLine = null;
        string? mid = null;
        bool end = false;
        List<string> candidates = [];
        foreach (string raw in body.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            if (line.Length < 2 || line[1] != '=')
            {
                return false;
            }

            if (line.StartsWith("m=", StringComparison.Ordinal))
            {
                mediaLine ??= line[2..];
            }
            else if (line.StartsWith("a=ice-ufrag:", StringComparison.Ordinal))
            {
                ufrag = line["a=ice-ufrag:".Length..];
            }
            else if (line.StartsWith("a=ice-pwd:", StringComparison.Ordinal))
            {
                password = line["a=ice-pwd:".Length..];
            }
            else if (line.StartsWith("a=mid:", StringComparison.Ordinal))
            {
                mid ??= line["a=mid:".Length..];
            }
            else if (line.StartsWith("a=candidate:", StringComparison.Ordinal))
            {
                if (mediaLine is null)
                {
                    return false;
                }

                candidates.Add(line[2..]);
            }
            else if (line == "a=end-of-candidates")
            {
                end = true;
            }
        }

        if (candidates.Count > 0 && mediaLine is null)
        {
            return false;
        }

        fragment = new TrickleIceFragment(
            ufrag,
            password,
            mediaLine ?? DefaultMediaLine,
            mid,
            candidates,
            end
        );
        return true;
    }

    /// <summary>The body, CRLF line endings.</summary>
    /// <returns>The body.</returns>
    public string Format()
    {
        StringBuilder body = new();
        if (UsernameFragment is { } ufrag)
        {
            body.Append("a=ice-ufrag:").Append(ufrag).Append("\r\n");
        }

        if (Password is { } password)
        {
            body.Append("a=ice-pwd:").Append(password).Append("\r\n");
        }

        body.Append("m=").Append(MediaLine).Append("\r\n");
        if (Mid is { } mid)
        {
            body.Append("a=mid:").Append(mid).Append("\r\n");
        }

        foreach (string candidate in Candidates)
        {
            body.Append("a=").Append(candidate).Append("\r\n");
        }

        if (EndOfCandidates)
        {
            body.Append("a=end-of-candidates\r\n");
        }

        return body.ToString();
    }
}
