# Single-container StreamTransport relay: our WebSocket signaling room router + coturn (STUN + TURN) in
# one image. coturn owns UDP/TCP 3478 and provides STUN+TURN; the relay advertises it with ephemeral
# credentials and does NOT run its own STUN. Build from the repository root:
#
#   docker build -f docker/Dockerfile -t streamtransport-relay .
#   docker run -p 8080:8080 -p 3478:3478/udp -p 3478:3478/tcp -p 49160-49200:49160-49200/udp \
#     -e STREAMTRANSPORT_ADVERTISED_HOST=relay.example.com -e TURN_EXTERNAL_IP=<public-ip> \
#     streamtransport-relay

# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
# The relay only references the signaling + STUN packages (no media/FFmpeg), so this is a lean publish.
RUN dotnet publish samples/StreamTransport.Relay/StreamTransport.Relay.csproj -c Release -o /app

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update \
    && apt-get install -y --no-install-recommends coturn \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
COPY docker/entrypoint.sh /usr/local/bin/entrypoint.sh
RUN chmod +x /usr/local/bin/entrypoint.sh

# 8080 signaling (HTTP/WebSocket); 3478 STUN+TURN; 49160-49200 TURN relay range.
EXPOSE 8080 3478/udp 3478/tcp 49160-49200/udp
ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]
