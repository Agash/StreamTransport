# Agash.StreamTransport.Adaptation

Transport-independent media adaptation for Agash.StreamTransport. The types here take what was sent and
what feedback said about it, and produce a send rate, a pacing schedule and a loss-repair plan. They do
not touch a socket, so a transport that owns its socket (the WebRTC package) composes them, and a
transport that adapts by itself (QUIC, SRT) reports its own capacity instead. Every type runs on a
caller-supplied clock reading, and none is thread-safe: the transport serializes calls.

## ScreamCongestionController

`ScreamCongestionController` implements `ICongestionController` with SCReAMv2
(draft-ietf-ccwg-rfc8298bis-screamv2). A reference window bounds the bytes in flight and sets the target
rate. The window is reduced at most once per virtual round trip on congestion and grows between
congestion events. Loss backs off by a fixed factor once the average loss rate shows it is not link-layer
noise, classic ECN by a smaller one, and L4S marks and queue delay in proportion to the congestion they
show. `Current` gives the latest `CapacityEstimate`, `Ecn` the codepoint that suits its response, and
`ScreamOptions` holds the tunables.

## DeliveryTracker

`DeliveryTracker` resolves what became of each sent packet from feedback, the same way for every
transport. A packet reported missing is lost only when it stays missing for the reordering window after a
later packet arrived; a packet no feedback mentions is lost by the RACK time rule (RFC 8985). A packet
is resolved once, however many overlapping reports cover it. The reordering window adapts to the delay
that late packets showed, because delay variation on cellular and Wi-Fi links is not congestion.

## Pacer

`Pacer` spreads outgoing packets over time at the congestion controller's pacing rate, so a keyframe does
not leave as one burst. Packets carry a `TrafficClass`: audio goes first and is never held back, and
retransmissions, video and FEC repair follow in that order and share one budget. A `Gate` callback
applies the controller's send window. `SentBytes` counts what each class sent, from which
`MediaRateAllocator` measures repair overhead. Sends run one at a time in queue order.

## PathMtuDiscovery

`PathMtuDiscovery` is datagram packetization layer path MTU discovery (RFC 8899) for an acknowledged
transport. It confirms the base size (1200 bytes by default), then runs a binary search up to a maximum
(1452 bytes by default) with one probe in flight. Probes are judged by the transport's own loss
detection, and black holes are detected from bursts of lost packets above the base size, which drop
`Current` back to the base. `PathMtuOptions` holds the sizes and timers.

## EcnValidator

`EcnValidator` validates ECN on a path before relying on it and keeps checking while using it
(RFC 6679, RFC 9000 section 13.4.2). While testing, it marks a fraction of media packets. When feedback
shows enough of them arriving with their mark (or CE), it marks all media. If marked packets arrive
cleared or rewritten, or are lost while unmarked ones arrive, it stops marking and retries after an
interval, or at once when the path changes. `State` reports where it stands.

## CircuitBreaker

`CircuitBreaker` implements the RTP circuit breakers of RFC 8083, apart from congestion control. It
trips on three silent reporting intervals, on reports that keep showing nothing arriving, or on a send
rate far above what TCP would get. Congestion first holds media to a tenth of the rate that tripped it,
and if that does not help within the next window, transmission ceases until `TryReset` succeeds.
`CircuitBreakerOptions` holds the intervals, defaulting to RFC 8083's values.

## RecoveryPolicy

`RecoveryPolicy` decides how a sender repairs loss from the path and the media, in the manner of
libwebrtc's hybrid NACK/FEC. Retransmission alone is used on a short round trip. Otherwise FEC is added
with a group size derived from the loss: the largest group whose chance of losing two or more packets,
which one XOR repair cannot recover, stays under a target. Small frames get no FEC on a moderate round
trip. `Plan` takes `RecoveryInputs` and returns a `RecoveryPlan` with the FEC group size and whether to
retransmit.

## MediaRateAllocator

`MediaRateAllocator` splits the rate a transport allows between audio, recovery traffic and video.
Retransmission and repair bytes count against the same budget as the media (RFC 4588 section 7), so
`VideoBitsPerSecond` returns what remains after audio and the measured recovery rate, never below a
floor. It reads the bytes sent per `TrafficClass`, typically from the pacer.
