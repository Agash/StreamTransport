using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Agash.StreamTransport.Sessions;

/// <summary>Resolves the <c>stun:</c> URLs of ICE servers (RFC 7064) to endpoints.</summary>
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
