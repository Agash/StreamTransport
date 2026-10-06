# Agash.StreamTransport.WebRtc.DependencyInjection

`Microsoft.Extensions.DependencyInjection` wiring for the
[Agash.StreamTransport](https://github.com/Agash/StreamTransport) WebRTC stack. One call registers the
DTLS certificate, the SCReAM congestion controller, a `PeerConnectionFactory`, network-change recovery,
and the WebRTC `IMediaTransportFactory` that media sessions run over:

```csharp
services
    .AddStreamTransport()
    .AddStreamTransportWebRtc(scream =>
    {
        scream.MaxBitrateBps = 12_000_000;
        scream.QueueDelayTargetMs = 60;
    });
```

A session's WebRTC settings (ICE policy, address preferences, loopback candidates) go in
`MediaSessionOptions.Transport` as a `WebRtcTransportOptions`.

The congestion-control algorithm is resolved here, so it can be swapped without touching the transport.
Register an `RtcCertificate` before calling it to keep one identity across runs.
