# Agash.StreamTransport.WebRtc.DependencyInjection

`Microsoft.Extensions.DependencyInjection` wiring for the
[Agash.StreamTransport](https://github.com/Agash/StreamTransport) WebRTC stack. One call registers the
DTLS-SRTP factory (BouncyCastle, isolated in `.Dtls`), the SCReAM congestion controller, and a
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

The core transport never depends on a particular DTLS implementation or congestion-control algorithm —
both are resolved here, so they can be swapped without touching the transport. See ADR-0003.
