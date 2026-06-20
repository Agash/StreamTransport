# Agash.StreamTransport.WebRtc.CongestionControl

Send-side congestion control for the [Agash.StreamTransport](https://github.com/Agash/StreamTransport)
WebRTC stack, behind the `INetworkController` seam from `Agash.StreamTransport.WebRtc.Abstractions`.

It ships a **SCReAM** controller (RFC 8298 / the SCReAM v2 draft) - the RMCAT algorithm designed for the
variable, lossy, queue-building behaviour of cellular links, which is the transport's IRL field-agent use case.
The controller consumes RFC 8888 per-packet feedback and produces an encoder target bitrate and a pacing rate.
A Google-Congestion-Control implementation can be added behind the same seam without touching the core.

Loss is run through SCReAM v2's **biased asymmetric loss estimator** (a port of libwebrtc's
`scream/loss_estimator`) rather than backing off on every lost packet: the congestion level steps up per
loss-bearing RTT and down per lossless RTT, so spurious wireless loss (e.g. ~1% uniform) drifts net-negative
and never triggers a back-off, while sustained loss still does. ECN-CE (L4S) marking gets a gentler DCTCP-style
reduction.

> The controller's parameters (queue-delay target, gains, rate bounds) are validated against shaped /
> real cellular links, not just loopback. See ADR-0003 in the repository.
