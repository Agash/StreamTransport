using System.Text;

namespace Agash.StreamTransport.Http;

/// <summary>
/// ICE servers as HTTP <c>Link</c> header values (RFC 9725 section 4.6): how a WHIP or WHEP server
/// tells its clients which STUN and TURN servers to gather with.
/// </summary>
public static class IceServerLinks
{
    /// <summary>The link values for ICE servers, one per URL.</summary>
    /// <param name="servers">The servers.</param>
    /// <returns>Values such as <c>&lt;turn:host?transport=udp&gt;; rel="ice-server"; username="u"; credential="p"; credential-type="password"</c>.</returns>
    public static IEnumerable<string> Format(IEnumerable<IceServer> servers)
    {
        ArgumentNullException.ThrowIfNull(servers);
        foreach (IceServer server in servers)
        {
            foreach (string url in server.Urls)
            {
                StringBuilder link = new($"<{url}>; rel=\"ice-server\"");
                if (server.Username is { } username && server.Credential is { } credential)
                {
                    link.Append($"; username=\"{Escape(username)}\"; credential=\"{Escape(credential)}\"; credential-type=\"password\"");
                }

                yield return link.ToString();
            }
        }
    }

    /// <summary>The ICE servers among link values; links of other relations are skipped.</summary>
    /// <param name="values">The <c>Link</c> header values, each possibly holding several links.</param>
    /// <returns>The servers.</returns>
    public static List<IceServer> Parse(IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        List<IceServer> servers = [];
        foreach (string value in values)
        {
            foreach (string link in Split(value, ','))
            {
                string[] parts = Split(link, ';');
                string target = parts[0].Trim();
                if (!target.StartsWith('<') || !target.EndsWith('>'))
                {
                    continue;
                }

                Dictionary<string, string> parameters = new(StringComparer.OrdinalIgnoreCase);
                foreach (string parameter in parts.Skip(1))
                {
                    int equals = parameter.IndexOf('=', StringComparison.Ordinal);
                    if (equals > 0)
                    {
                        parameters[parameter[..equals].Trim()] = Unquote(parameter[(equals + 1)..].Trim());
                    }
                }

                if (parameters.TryGetValue("rel", out string? rel) && rel == "ice-server")
                {
                    servers.Add(
                        new IceServer(
                            [target[1..^1]],
                            parameters.GetValueOrDefault("username"),
                            parameters.GetValueOrDefault("credential")
                        )
                    );
                }
            }
        }

        return servers;
    }

    // Splits on a separator outside quotes and angle brackets.
    private static string[] Split(string value, char separator)
    {
        List<string> parts = [];
        bool quoted = false;
        bool bracketed = false;
        int start = 0;
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c == '"' && (i == 0 || value[i - 1] != '\\'))
            {
                quoted = !quoted;
            }
            else if (!quoted && c == '<')
            {
                bracketed = true;
            }
            else if (!quoted && c == '>')
            {
                bracketed = false;
            }
            else if (!quoted && !bracketed && c == separator)
            {
                parts.Add(value[start..i]);
                start = i + 1;
            }
        }

        parts.Add(value[start..]);
        return [.. parts];
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private static string Unquote(string value) =>
        value.Length >= 2 && value[0] == '"' && value[^1] == '"'
            ? value[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal)
            : value;
}
