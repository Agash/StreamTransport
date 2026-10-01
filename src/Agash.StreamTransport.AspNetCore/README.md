# Agash.StreamTransport.AspNetCore

WHIP (RFC 9725) and WHEP endpoints for [Agash.StreamTransport](https://github.com/Agash/StreamTransport),
mapped onto an existing ASP.NET Core application's routing. The host keeps its own Kestrel, middleware,
authentication and CORS; these are endpoints like any other.

```csharp
builder.Services.AddStreamTransport().AddFFmpegCodecs().AddOpusCodecs().AddHttpMediaEndpoints();

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
answer waits briefly for STUN and TURN servers; trickle (`PATCH`) is answered `405`. A handler returning
null refuses the offer with `404`. Sessions end on `DELETE`, on failure, and when the host stops.

The clients, `WhipClient.PublishAsync` and `WhepClient.PlayAsync`, are in `Agash.StreamTransport`.
