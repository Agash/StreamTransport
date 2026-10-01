using System.Globalization;
using System.Net.Security;

namespace Agash.StreamTransport.WebRtc.Turn;

/// <summary>How a TURN client reaches its server (RFC 8656 section 3.1).</summary>
public enum TurnTransport
{
    /// <summary>UDP, the default and the lowest overhead.</summary>
    Udp,

    /// <summary>TCP, for networks that block UDP.</summary>
    Tcp,

    /// <summary>TLS over TCP (<c>turns:</c>), for networks that admit only HTTPS-like traffic.</summary>
    Tls,
}

/// <summary>
/// A TURN server and the long-term credentials to allocate on it. The relayed transport to peers is
/// always UDP; <see cref="Transport"/> is only how this client talks to the server.
/// </summary>
/// <param name="Host">The server's host name or address literal.</param>
/// <param name="Port">The server's port.</param>
/// <param name="Transport">How to reach the server.</param>
/// <param name="Username">The long-term credential username.</param>
/// <param name="Credential">The long-term credential password.</param>
public sealed record TurnServer(
    string Host,
    int Port,
    TurnTransport Transport,
    string Username,
    string Credential
)
{
    /// <summary>The default port for <c>turn:</c> (3478).</summary>
    public const int DefaultPort = 3478;

    /// <summary>The default port for <c>turns:</c> (5349).</summary>
    public const int DefaultTlsPort = 5349;

    /// <summary>
    /// Validates the server's TLS certificate for <see cref="TurnTransport.Tls"/>; the platform's chain
    /// and host-name validation when null.
    /// </summary>
    public RemoteCertificateValidationCallback? CertificateValidation { get; init; }

    /// <summary>
    /// Parses a <c>turn:</c> or <c>turns:</c> URI (RFC 7065): <c>turn:host[:port][?transport=udp|tcp]</c>,
    /// with a bracketed IPv6 literal allowed.
    /// </summary>
    /// <param name="uri">The URI.</param>
    /// <param name="username">The credential username.</param>
    /// <param name="credential">The credential password.</param>
    /// <param name="server">The parsed server.</param>
    /// <returns>Whether <paramref name="uri"/> is a TURN URI this client supports.</returns>
    public static bool TryParse(
        string uri,
        string username,
        string credential,
        out TurnServer server
    )
    {
        ArgumentNullException.ThrowIfNull(uri);
        server = null!;
        bool secure;
        ReadOnlySpan<char> rest;
        if (uri.StartsWith("turns:", StringComparison.OrdinalIgnoreCase))
        {
            secure = true;
            rest = uri.AsSpan(6);
        }
        else if (uri.StartsWith("turn:", StringComparison.OrdinalIgnoreCase))
        {
            secure = false;
            rest = uri.AsSpan(5);
        }
        else
        {
            return false;
        }

        TurnTransport transport = secure ? TurnTransport.Tls : TurnTransport.Udp;
        int query = rest.IndexOf('?');
        if (query >= 0)
        {
            ReadOnlySpan<char> parameter = rest[(query + 1)..];
            rest = rest[..query];
            if (parameter.Equals("transport=tcp", StringComparison.OrdinalIgnoreCase))
            {
                transport = secure ? TurnTransport.Tls : TurnTransport.Tcp;
            }
            else if (!parameter.Equals("transport=udp", StringComparison.OrdinalIgnoreCase) || secure)
            {
                // TURN over DTLS (turns: with transport=udp) is not implemented.
                return false;
            }
        }

        int port = secure ? DefaultTlsPort : DefaultPort;
        int portStart = rest.StartsWith("[") ? rest.IndexOf("]:") + 1 : rest.LastIndexOf(':');
        if (portStart > 0)
        {
            if (
                !int.TryParse(
                    rest[(portStart + 1)..],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out port
                )
                || port is <= 0 or > 65535
            )
            {
                return false;
            }

            rest = rest[..portStart];
        }

        string host = rest.Trim("[]").ToString();
        if (host.Length == 0)
        {
            return false;
        }

        server = new TurnServer(host, port, transport, username, credential);
        return true;
    }
}
