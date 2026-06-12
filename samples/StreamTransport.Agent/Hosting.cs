using System.Net;
using System.Net.WebSockets;
using Agash.StreamTransport;
using Agash.StreamTransport.Signaling;
using Agash.StreamTransport.Stun;
using DevTunnels.Client;
using DevTunnels.Client.Authentication;
using DevTunnels.Client.Hosting;
using DevTunnels.Client.Ports;
using DevTunnels.Client.Tunnels;

namespace StreamTransport.Agent;

/// <summary>
/// A self-contained signaling relay embedded in the sender: an <see cref="HttpListener"/> WebSocket
/// endpoint backed by the room router. This is the "host your own signaling" path - the sender owns the
/// room, and a DevTunnel (or any tunnel) exposes this local endpoint so a remote peer can reach it
/// without a separate relay server.
/// </summary>
internal sealed class EmbeddedRelay : IAsyncDisposable
{
    private readonly HttpListener _listener;
    private readonly SignalingRouter _router;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _accept;

    private EmbeddedRelay(HttpListener listener, SignalingRouter router, int port)
    {
        _listener = listener;
        _router = router;
        Port = port;
        _accept = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    /// <summary>The local WebSocket URL the sender's own publisher connects to.</summary>
    public Uri LocalWebSocketUri => new($"ws://localhost:{Port}/ws");

    /// <summary>
    /// Start an embedded relay. ICE servers are public STUN by default (reachable by a remote peer over a
    /// tunnel); for a LAN-only host you can advertise the relay's own STUN instead.
    /// </summary>
    public static EmbeddedRelay Start(string stunUrl = "stun:stun.l.google.com:19302")
    {
        int port = FreePort();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        var router = new SignalingRouter(StaticIceServerProvider.Stun(stunUrl));
        return new EmbeddedRelay(listener, router, port);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                return;
            }

            if (!context.Request.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                context.Response.Close();
                continue;
            }

            _ = HandlePeerAsync(context);
        }
    }

    private async Task HandlePeerAsync(HttpListenerContext context)
    {
        HttpListenerWebSocketContext ws = await context.AcceptWebSocketAsync(null).ConfigureAwait(false);
        var transport = new WebSocketSignalingTransport(ws.WebSocket);
        await using ISignalingSession session = _router.Connect(transport);
        transport.MessageReceived += message => session.ReceiveAsync(message).AsTask();
        try
        {
            await transport.RunAsync(_cts.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Peer disconnected.
        }
    }

    private static int FreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        _listener.Close();
        try
        {
            await _accept.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Teardown race.
        }

        _cts.Dispose();
    }
}

/// <summary>
/// A DevTunnel that exposes the embedded relay's local port publicly, using our
/// <see cref="DevTunnelsClient"/> (which drives the authenticated <c>devtunnel</c> CLI). Hands back the
/// public <c>wss://</c> signaling URL a remote peer connects to.
/// </summary>
internal sealed class SignalingTunnel : IAsyncDisposable
{
    // A stable per-account tunnel id (reused via create-or-update) which, unlike a fresh random id per run,
    // never accumulates orphaned tunnels.
    private const string TunnelId = "streamtransport-agent";

    private readonly IDevTunnelHostSession _session;

    private SignalingTunnel(IDevTunnelHostSession session, Uri publicWebSocketUri)
    {
        _session = session;
        PublicWebSocketUri = publicWebSocketUri;
    }

    /// <summary>The public <c>wss://.../ws</c> URL a remote receiver connects to.</summary>
    public Uri PublicWebSocketUri { get; }

    public static async Task<SignalingTunnel> StartAsync(int localPort, CancellationToken cancellationToken)
    {
        var client = new DevTunnelsClient();

        // Respect the existing CLI session (the user may be logged in via GitHub). Only trigger a login
        // if there is none - never force a provider, which could re-authenticate as a different identity.
        DevTunnelLoginStatus status = await client.GetLoginStatusAsync(cancellationToken).ConfigureAwait(false);
        if (!status.IsLoggedIn)
        {
            await client.EnsureLoggedInAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        // AllowAnonymous on the tunnel and port grants anonymous client access; no separate access call is
        // needed (and adding one is what made the host start reject with an opaque error).
        await client.CreateOrUpdateTunnelAsync(
            TunnelId,
            new DevTunnelOptions { AllowAnonymous = true, Description = "StreamTransport agent signaling" },
            cancellationToken).ConfigureAwait(false);
        await client.CreateOrReplacePortAsync(
            TunnelId, localPort, new DevTunnelPortOptions { Protocol = "http", AllowAnonymous = true }, cancellationToken)
            .ConfigureAwait(false);

        IDevTunnelHostSession session = await client.StartHostSessionAsync(
            new DevTunnelHostStartOptions
            {
                TunnelId = TunnelId,
                PortNumber = localPort,
                ReadyTimeout = TimeSpan.FromSeconds(30),
            },
            cancellationToken).ConfigureAwait(false);
        await session.WaitForReadyAsync(cancellationToken).ConfigureAwait(false);

        Uri publicUrl = session.PublicUrl
            ?? throw new InvalidOperationException($"DevTunnel did not report a public URL ({session.FailureReason}).");
        var wss = new Uri($"wss://{publicUrl.Authority}/ws");
        return new SignalingTunnel(session, wss);
    }

    public async ValueTask DisposeAsync()
    {
        // Stop the host session but leave the (stable, reusable) tunnel in place for the next run.
        await _session.StopAsync().ConfigureAwait(false);
        await _session.DisposeAsync().ConfigureAwait(false);
    }
}
