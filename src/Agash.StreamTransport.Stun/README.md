# Agash.StreamTransport.Stun

An embeddable STUN binding server and ICE-server providers for
[Agash.StreamTransport](https://github.com/Agash/StreamTransport).

## STUN messages

`StunMessageReader` and `StunMessageWriter` parse and build RFC 8489 messages in place over spans:
attributes, XOR-MAPPED-ADDRESS, MESSAGE-INTEGRITY and FINGERPRINT (both verified on read). The ICE
agent, the TURN client and the server below use them; they depend on nothing else in the stack.

```csharp
StunMessageWriter writer = new(buffer, StunMessageClass.Request, StunMethod.Binding, transactionId);
writer.AddFingerprint();
if (StunMessageReader.TryParse(datagram, out StunMessageReader reply)
    && reply.TryGetXorMappedAddress(out IPEndPoint mapped)) { /* ... */ }
```

## STUN server

`StunBindingServer` answers RFC 8489 binding requests with the caller's reflexive address, which is what
ICE needs to gather server-reflexive candidates. It listens on one UDP port, runs on every platform, and
does not detect NAT types (RFC 3489). Run one on a reachable UDP port, or alongside a relay, so peers
discover their public address without a third-party STUN service.

```csharp
await using var stun = new StunBindingServer(new IPEndPoint(IPAddress.Any, 3478));
stun.Start();
```

## ICE-server providers

An `IIceServerProvider` plugged into the signaling router supplies the ICE servers that each peer
receives in its `Welcome`.

- `StaticIceServerProvider` returns a fixed list: STUN only, or TURN with static credentials.
  `StaticIceServerProvider.Stun("stun:stun.example.com:3478")` makes a STUN-only list.
- `CoturnSharedSecretIceServerProvider` advertises an external, self-hosted
  [coturn](https://github.com/coturn/coturn) server configured with `static-auth-secret`. It uses the
  TURN REST API ephemeral-credential scheme: `username = "{unixExpiry}"` and
  `credential = base64(HMAC-SHA1(secret, username))`, so each peer gets fresh, time-limited credentials.

```csharp
var ice = new CoturnSharedSecretIceServerProvider(
    stunUrls: ["stun:turn.example.com:3478"],
    turnUrls: ["turn:turn.example.com:3478?transport=udp"],
    sharedSecret: coturnStaticAuthSecret);
var router = new SignalingRouter(ice);
```

TURN relaying itself is done by the peer connection's TURN client against an external server such as
coturn. This package does not host a relay.
