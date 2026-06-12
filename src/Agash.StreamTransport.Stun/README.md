# Agash.StreamTransport.Stun

Embeddable **STUN binding server** and **ICE-server providers** for
[Agash.StreamTransport](https://github.com/Agash/StreamTransport).

## STUN server

`StunBindingServer` answers RFC 5389 binding requests with the caller's
reflexive address — everything WebRTC ICE needs to gather server-reflexive
candidates. Single UDP port, cross-platform, no RFC 3489 NAT-type detection.

```csharp
await using var stun = new StunBindingServer(new IPEndPoint(IPAddress.Any, 3478));
stun.Start();
```

This is what the light agent ships with: run one on a reachable UDP port (or
co-host it with the relay) so peers discover their public address without
depending on a third-party STUN service.

## ICE-server providers

Plug an `IIceServerProvider` into the signaling router; the ICE servers it
returns are shipped to each peer in its `Welcome`.

- `StaticIceServerProvider` — a fixed list (STUN-only, or TURN with static
  credentials). `StaticIceServerProvider.Stun("stun:stun.example.com:3478")`.
- `CoturnSharedSecretIceServerProvider` — **bring your own TURN.** Advertises an
  external self-hosted [coturn](https://github.com/coturn/coturn) (configured
  with `static-auth-secret`) using the TURN REST API ephemeral-credential
  scheme: `username = "{unixExpiry}"`, `credential = base64(HMAC-SHA1(secret,
  username))`. Fresh, time-limited credentials per peer.

```csharp
var ice = new CoturnSharedSecretIceServerProvider(
    stunUrls: ["stun:turn.example.com:3478"],
    turnUrls: ["turn:turn.example.com:3478?transport=udp"],
    sharedSecret: coturnStaticAuthSecret);
var router = new SignalingRouter(ice);
```

A **native, in-process TURN relay** (`Agash.StreamTransport.Turn`) is a separate,
later package. Most deployments don't host TURN — the agent is STUN-only and
relies on DevTunnels for signaling reachability — so TURN stays optional and
external until then.
