# Agash.StreamTransport.WebRtc.DependencyInjection

`Microsoft.Extensions.DependencyInjection` wiring for the
[Agash.StreamTransport](https://github.com/Agash/StreamTransport) WebRTC stack. One call registers the
DTLS certificate, the SCReAM congestion controller, and a
`PeerConnectionFactory`:

```csharp
services.AddStreamTransportWebRtc(scream =>
{
    scream.MaxBitrateBps = 12_000_000;
    scream.QueueDelayTargetMs = 60;
});

// then, from DI:
PeerConnection pc = factory.Create(new PeerConnectionOptions { /* media lines */ });
```

The congestion-control algorithm is resolved here, so it can be swapped without touching the transport.
Register an `RtcCertificate` before calling it to keep one identity across runs.
