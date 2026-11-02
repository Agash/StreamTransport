# Agash.StreamTransport.AspNetCore

WHIP (RFC 9725) and WHEP endpoints for [Agash.StreamTransport](https://github.com/Agash/StreamTransport),
mapped onto an existing ASP.NET Core application's routing. The host keeps its own Kestrel, middleware,
authentication and CORS; these are endpoints like any other.

```csharp
builder.Services.AddStreamTransport().AddStreamTransportWebRtc().AddFFmpegCodecs().AddOpusCodecs().AddHttpMediaEndpoints();

app.MapWhip("/whip/{room}", (context, ct) =>
        ValueTask.FromResult<HttpMediaSetup?>(new(new MediaEndpoints { VideoSink = sink, AudioSink = speaker },
            MediaSessionOptions.For(MediaProfile.InteractiveP2P))))
    .RequireAuthorization();

app.MapWhep("/whep/{room}", (context, ct) =>
        ValueTask.FromResult<HttpMediaSetup?>(new(new MediaEndpoints { VideoSource = camera, AudioSource = microphone },
            MediaSessionOptions.For(MediaProfile.InteractiveP2P))));
```

A `POST` of an SDP offer makes a session and answers `201 Created` with the resource's `Location` and the
SDP answer; a `DELETE` of the resource ends it. Candidates travel inside the offer and answer, so the
answer waits briefly for STUN and TURN servers. A handler returning null refuses the offer with `404`.
Sessions end on `DELETE`, on failure, and when the host stops.

A client trickles candidates it gathers later, such as a field device's modem coming up mid-stream, in
a `PATCH` of the resource with an `application/trickle-ice-sdpfrag` body and `If-Match` set to the
resource's `ETag` (RFC 9725 section 4.3.2). The server adds them and answers `204`, then checks the new
addresses itself, which opens its own firewall to them. A `PATCH` without `If-Match` gets `428`, with a
stale tag `412`.

A `PATCH` with new ICE credentials and `If-Match: *` is an ICE restart (section 4.3.3): the session
restarts ICE under them, keeping its DTLS-SRTP keys, and answers `200` with its own new credentials and
candidates and a new `ETag` for the new ICE session. `IMediaSession.RestartTransportAsync` does this from
the client.

The clients, `WhipClient.PublishAsync` and `WhepClient.PlayAsync`, are in `Agash.StreamTransport`; they
trickle late candidates the same way and stop if the server answers that it takes none.
