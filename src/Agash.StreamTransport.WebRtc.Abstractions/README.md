# Agash.StreamTransport.WebRtc.Abstractions

Leaf contracts for the first-party WebRTC stack used by
[`Agash.StreamTransport`](https://github.com/Agash/StreamTransport): ICE and DTLS roles, ICE candidate
kinds, network change monitoring (`INetworkMonitor`), and the transport health and loss statistics a peer
connection reports.

It depends on the BCL only. Congestion control is transport-independent and lives in
`Agash.StreamTransport.Adaptation`.
