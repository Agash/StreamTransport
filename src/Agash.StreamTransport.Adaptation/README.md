# Agash.StreamTransport.Adaptation

Transport-independent media adaptation for Agash.StreamTransport.

- `Pacer` spreads outgoing packets over time at the congestion controller's pacing rate. Packets carry
  a `TrafficClass`: audio goes first and is never held back; retransmissions, video and FEC repair
  follow in that order and share one budget, so recovery traffic cannot exceed the rate the network
  allows. The pacer counts what each class sent, from which the media side measures repair overhead.

A transport adapter that owns its socket (the WebRTC package) composes the pacer; an adapter over a
transport that paces itself does not need it.
