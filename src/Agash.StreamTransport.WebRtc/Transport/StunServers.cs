using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Agash.StreamTransport.WebRtc.Turn;

namespace Agash.StreamTransport.WebRtc.Transport;

/// <summary>
/// Reads ICE server URLs: <c>stun:</c> URLs (RFC 7064) resolve to endpoints here, and <c>turn:</c> and
/// <c>turns:</c> URLs (RFC 7065) become TURN servers, which resolve when they allocate.
/// </summary>
internal static class StunServers
{
    private const int DefaultPort = 3478;

    /// <summary>The endpoints of every <c>stun:</c> URL; other schemes are left to their own clients.</summary>
    /// <param name="servers">The ICE servers.</param>
    /// <param name="cancellationToken">Cancels resolution.</param>
    /// <returns>The resolved endpoints, and the URLs that did not resolve.</returns>
    public static async Task<(List<IPEndPoint> Endpoints, List<string> Unresolved)> ResolveAsync(
        IEnumerable<IceServer> servers,
        CancellationToken cancellationToken
    )
    {
        List<IPEndPoint> endpoints = [];
        List<string> unresolved = [];
        foreach (string url in servers.SelectMany(static s => s.Urls))
        {
            if (!TryParse(url, out string host, out int port))
            {
                continue;
            }

            try
            {
                IPAddress[] addresses = IPAddress.TryParse(host, out IPAddress? literal)
                    ? [literal]
                    : await Dns.GetHostAddressesAsync(host, cancellationToken)
                        .ConfigureAwait(false);
                endpoints.AddRange(addresses.Select(address => new IPEndPoint(address, port)));
            }
            catch (SocketException)
            {
                // Reported to the caller, which logs it with the session.
                unresolved.Add(url);
            }
        }

        return (endpoints, unresolved);
    }

    /// <summary>The TURN servers among the ICE servers, with their credentials.</summary>
    /// <param name="servers">The ICE servers.</param>
    /// <returns>Every <c>turn:</c> or <c>turns:</c> URL this client supports.</returns>
    public static List<TurnServer> Turn(IEnumerable<IceServer> servers)
    {
        List<TurnServer> turn = [];
        foreach (IceServer server in servers)
        {
            foreach (string url in server.Urls)
            {
                if (
                    TurnServer.TryParse(
                        url,
                        server.Username ?? string.Empty,
                        server.Credential ?? string.Empty,
                        out TurnServer parsed
                    )
                )
                {
                    turn.Add(parsed);
                }
            }
        }

        return turn;
    }

    // stun:host[:port], with a bracketed IPv6 literal allowed.
    private static bool TryParse(string url, out string host, out int port)
    {
        host = string.Empty;
        port = DefaultPort;
        if (!url.StartsWith("stun:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string rest = url[5..];
        int queryStart = rest.IndexOf('?', StringComparison.Ordinal);
        if (queryStart >= 0)
        {
            rest = rest[..queryStart];
        }

        int portStart = rest.StartsWith('[')
            ? rest.IndexOf("]:", StringComparison.Ordinal) + 1
            : rest.LastIndexOf(':');
        if (
            portStart > 0
            && int.TryParse(
                rest[(portStart + 1)..],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int parsed
            )
        )
        {
            port = parsed;
            rest = rest[..portStart];
        }

        host = rest.Trim('[', ']');
        return host.Length > 0;
    }
}
