# StreamTransport relay (Docker)

A single container running the **StreamTransport signaling relay** and **coturn** (STUN + TURN)
together — drop-in self-hosted infrastructure for rooms that need a stable signaling server and/or TURN
relay for symmetric-NAT peers. One image, no compose file.

coturn owns UDP/TCP `3478` and provides STUN **and** TURN; the relay serves WebSocket signaling on
`8080` and advertises coturn to every joining peer with short-lived [TURN REST API][rest] ephemeral
credentials derived from a shared secret. The relay does not run its own STUN in this topology.

[rest]: https://datatracker.ietf.org/doc/html/draft-uberti-behave-turn-rest-00

## Build & run

```bash
# from the repository root
docker build -f docker/Dockerfile -t streamtransport-relay .

docker run --rm \
  -p 8080:8080 \
  -p 3478:3478/udp -p 3478:3478/tcp \
  -p 49160-49200:49160-49200/udp \
  -e STREAMTRANSPORT_ADVERTISED_HOST=relay.example.com \
  -e TURN_EXTERNAL_IP=203.0.113.10 \
  streamtransport-relay
```

Agents then connect with `--relay ws://relay.example.com:8080/ws`.

## Configuration

| Variable | Default | Purpose |
|---|---|---|
| `STREAMTRANSPORT_ADVERTISED_HOST` | `localhost` | Host peers use to reach STUN/TURN (put your public hostname here). |
| `TURN_EXTERNAL_IP` | — | coturn's public IP (set behind NAT so relayed candidates are correct). |
| `STREAMTRANSPORT_TURN_SECRET` | random per start | coturn `static-auth-secret`; the relay mints ephemeral credentials from it. |
| `TURN_REALM` | `streamtransport` | TURN realm. |
| `TURN_MIN_PORT` / `TURN_MAX_PORT` | `49160` / `49200` | UDP relay port range (open these in the firewall). |
| `STREAMTRANSPORT_RELAY_URLS` | `http://0.0.0.0:8080` | Kestrel bind URL(s). |

## Notes on Cloudflare / proxies

The signaling endpoint (`8080`, HTTP/WebSocket) proxies cleanly behind Cloudflare. **TURN media relay is
UDP and does not traverse an HTTP proxy** — for symmetric-NAT fallback the `3478` UDP port and the relay
range must be directly reachable (or run coturn with `turns`/TCP on a TLS port). Many deployments only
need the signaling endpoint public and rely on STUN + direct P2P; TURN matters only when both peers are
behind symmetric NAT with no IPv6.
