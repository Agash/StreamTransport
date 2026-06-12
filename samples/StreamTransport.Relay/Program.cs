using System.Net;
using System.Net.WebSockets;
using Agash.StreamTransport;
using Agash.StreamTransport.Signaling;
using Agash.StreamTransport.Stun;

// StreamTransport.Relay - a self-hostable WebRTC signaling relay with an embedded STUN server.
//
// Media never flows through here: peers negotiate peer-to-peer and the relay only routes the SDP/ICE
// handshake and provisions ICE servers. Configuration is via environment variables (sensible local-dev
// defaults), mirroring how a small relay is run in a container or systemd unit.
//
//   STREAMTRANSPORT_RELAY_URLS       Kestrel bind URL(s).        default http://0.0.0.0:8080
//   STREAMTRANSPORT_STUN_PORT        Embedded STUN UDP port.     default 3478
//   STREAMTRANSPORT_ADVERTISED_HOST  Host peers reach STUN/TURN. default localhost
//   STREAMTRANSPORT_TURN_URLS        Optional external coturn TURN URLs (comma-separated).
//   STREAMTRANSPORT_TURN_SECRET      coturn static-auth-secret (required to advertise TURN).

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

string advertisedHost = Env("STREAMTRANSPORT_ADVERTISED_HOST") ?? "localhost";
int stunPort = int.TryParse(Env("STREAMTRANSPORT_STUN_PORT"), out int p) ? p : 3478;
string stunUrl = $"stun:{advertisedHost}:{stunPort}";

// When co-deployed with coturn (the Docker image), coturn owns 3478 and provides STUN+TURN; the relay
// must not bind its own STUN there. Set STREAMTRANSPORT_STUN_DISABLED=1 in that topology.
bool stunDisabled = Env("STREAMTRANSPORT_STUN_DISABLED") is "1" or "true";

IIceServerProvider iceProvider = BuildIceProvider(stunUrl);

builder.Services.AddSingleton(iceProvider);
builder.Services.AddSingleton<ISignalingRouter>(sp => new SignalingRouter(sp.GetRequiredService<IIceServerProvider>()));
if (!stunDisabled)
{
    builder.Services.AddSingleton(_ => new StunBindingServer(new IPEndPoint(IPAddress.Any, stunPort)));
}

string urls = Env("STREAMTRANSPORT_RELAY_URLS") ?? "http://0.0.0.0:8080";
builder.WebHost.UseUrls(urls);

WebApplication app = builder.Build();
app.UseWebSockets();

// Start the embedded STUN server unless coturn is providing it.
if (!stunDisabled)
{
    StunBindingServer stun = app.Services.GetRequiredService<StunBindingServer>();
    stun.Start();
    app.Logger.LogInformation("STUN server listening on UDP {Endpoint}", stun.ListenEndPoint);
}
else
{
    app.Logger.LogInformation("Embedded STUN disabled; expecting an external STUN/TURN (coturn).");
}

app.MapGet("/health", () => Results.Ok("ok"));

// Pre-mint a room code (e.g. for a UI to show before the publisher connects). Publishers may also just
// pick a code and connect; the router creates it on the publisher's hello.
app.MapGet("/api/new-room", (ISignalingRouter router) => Results.Ok(new { code = router.CreateRoom().Value }));

// The signaling endpoint. One WebSocket per peer; the room router does the routing.
app.Map("/ws", async (HttpContext context, ISignalingRouter router, ILoggerFactory loggerFactory) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    ILogger logger = loggerFactory.CreateLogger("Signaling");
    using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
    var transport = new WebSocketSignalingTransport(socket);
    await using ISignalingSession session = router.Connect(transport);
    transport.MessageReceived += message => session.ReceiveAsync(message).AsTask();

    try
    {
        await transport.RunAsync(context.RequestAborted);
    }
    catch (OperationCanceledException)
    {
        // Client disconnected; fall through to cleanup.
    }
    catch (Exception ex)
    {
        logger.LogDebug(ex, "signaling socket faulted");
    }
});

app.Logger.LogInformation("StreamTransport relay listening on {Urls}", urls);
await app.RunAsync();

static string? Env(string name)
{
    string? value = Environment.GetEnvironmentVariable(name);
    return string.IsNullOrWhiteSpace(value) ? null : value;
}

static IIceServerProvider BuildIceProvider(string stunUrl)
{
    string? turnUrls = Env("STREAMTRANSPORT_TURN_URLS");
    string? turnSecret = Env("STREAMTRANSPORT_TURN_SECRET");
    if (turnUrls is null || turnSecret is null)
    {
        // STUN-only: advertise this relay's own embedded STUN server.
        return StaticIceServerProvider.Stun(stunUrl);
    }

    // Bring-your-own external coturn: advertise STUN + TURN with ephemeral credentials.
    string[] urls = turnUrls.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    return new CoturnSharedSecretIceServerProvider([stunUrl], urls, turnSecret);
}
