using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Agash.StreamTransport.Stun;

/// <summary>Hands every joining peer the same fixed set of ICE servers.</summary>
/// <remarks>
/// Use this for STUN-only deployments, or to advertise a TURN server that issues long-lived static
/// credentials. For coturn with a shared secret (ephemeral, time-limited credentials), use
/// <see cref="CoturnSharedSecretIceServerProvider"/> instead.
/// </remarks>
public sealed class StaticIceServerProvider(IReadOnlyList<IceServer> iceServers) : IIceServerProvider
{
    private readonly IReadOnlyList<IceServer> _iceServers = iceServers ?? throw new ArgumentNullException(nameof(iceServers));

    /// <summary>Advertise only the given STUN URLs (no TURN).</summary>
    public static StaticIceServerProvider Stun(params string[] stunUrls) =>
        new([new IceServer(stunUrls)]);

    /// <inheritdoc/>
    public IReadOnlyList<IceServer> GetIceServersForPeer() => _iceServers;
}

/// <summary>
/// Advertises STUN plus an external TURN server (typically self-hosted <c>coturn</c> configured with a
/// <c>static-auth-secret</c>) using the coturn / TURN REST API ephemeral-credential scheme: the username
/// is <c>"{unixExpiry}"</c> (optionally <c>"{unixExpiry}:{name}"</c>) and the credential is
/// <c>base64(HMAC-SHA1(secret, username))</c>. Each call mints a fresh pair valid for the configured
/// lifetime, so credentials cannot be replayed indefinitely. This is the "bring your own TURN" path: no
/// native TURN server runs in-process.
/// </summary>
public sealed class CoturnSharedSecretIceServerProvider : IIceServerProvider
{
    private readonly IReadOnlyList<string> _stunUrls;
    private readonly IReadOnlyList<string> _turnUrls;
    private readonly byte[] _secret;
    private readonly TimeSpan _credentialLifetime;
    private readonly string? _user;
    private readonly TimeProvider _time;

    /// <summary>Create a provider for an external coturn server.</summary>
    /// <param name="stunUrls">STUN URLs to advertise (e.g. the same coturn's <c>stun:</c> URL).</param>
    /// <param name="turnUrls">TURN URLs to advertise (e.g. <c>turn:turn.example.com:3478?transport=udp</c>).</param>
    /// <param name="sharedSecret">The coturn <c>static-auth-secret</c>.</param>
    /// <param name="credentialLifetime">How long minted credentials stay valid. Defaults to 10 minutes.</param>
    /// <param name="user">Optional name suffix appended to the username as <c>"{expiry}:{user}"</c>.</param>
    /// <param name="timeProvider">Clock source; defaults to <see cref="TimeProvider.System"/>.</param>
    public CoturnSharedSecretIceServerProvider(
        IReadOnlyList<string> stunUrls,
        IReadOnlyList<string> turnUrls,
        string sharedSecret,
        TimeSpan? credentialLifetime = null,
        string? user = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(turnUrls);
        ArgumentException.ThrowIfNullOrEmpty(sharedSecret);
        _stunUrls = stunUrls ?? [];
        _turnUrls = turnUrls;
        _secret = Encoding.UTF8.GetBytes(sharedSecret);
        _credentialLifetime = credentialLifetime ?? TimeSpan.FromMinutes(10);
        _user = user;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc/>
    public IReadOnlyList<IceServer> GetIceServersForPeer()
    {
        (string username, string credential) = MintCredential();
        var servers = new List<IceServer>(2);
        if (_stunUrls.Count > 0)
        {
            servers.Add(new IceServer(_stunUrls));
        }

        servers.Add(new IceServer(_turnUrls, username, credential));
        return servers;
    }

    private (string Username, string Credential) MintCredential()
    {
        long expiry = _time.GetUtcNow().ToUnixTimeSeconds() + (long)_credentialLifetime.TotalSeconds;
        string username = _user is null
            ? expiry.ToString(CultureInfo.InvariantCulture)
            : string.Create(CultureInfo.InvariantCulture, $"{expiry}:{_user}");

        byte[] mac = HMACSHA1.HashData(_secret, Encoding.UTF8.GetBytes(username));
        return (username, Convert.ToBase64String(mac));
    }
}
