# Agash.StreamTransport.Signaling

Transport-agnostic WebRTC **signaling room router** for
[Agash.StreamTransport](https://github.com/Agash/StreamTransport).

The router owns the room registry, mints peer ids, routes SDP/ICE between peers
in a room, and announces joins and leaves. It carries **no media** - peers
negotiate WebRTC peer-to-peer and the encoded media never touches this process.
It depends only on `Agash.StreamTransport.Abstractions` (no WebRTC, codec, or
ASP.NET dependency), so the same router backs both:

- the standalone **relay** over a raw WebSocket endpoint, and
- a **host application**, bound to its existing SignalR hub.

## Binding it to a transport

Implement `ISignalingPeerTransport` per connection (push a message down to that
peer) and drive a session:

```csharp
var router = new SignalingRouter(iceServerProvider); // singleton

// per connection:
await using ISignalingSession session = router.Connect(myTransport);
// for each inbound message decoded from the socket:
await session.ReceiveAsync(message);
// session disposes on disconnect -> peer-left is announced, empty rooms GC'd
```

`SignalingJson` (in the abstractions package) gives the canonical wire format so
the WebSocket relay, a SignalR hub, and the room-aware client all agree.

## Join rule

A `Publisher` creates its room if the code is unknown (so it owns the room and
survives reconnects); a `Subscriber` joining an unknown code is rejected with
`RoomNotFound`.
