using System.Diagnostics.Metrics;

namespace Agash.StreamTransport.Signaling;

/// <summary>
/// The meter the signaling router reports on. Nothing is recorded until something listens: add
/// <see cref="MeterName"/> to an OpenTelemetry provider or watch it with <c>dotnet-counters</c>.
/// </summary>
public static class SignalingDiagnostics
{
    /// <summary>The meter's name: <c>Agash.StreamTransport.Signaling</c>.</summary>
    public const string MeterName = "Agash.StreamTransport.Signaling";
}

// The instruments. Room codes are invitations, so no tag ever carries one.
internal sealed class SignalingMetrics
{
    public SignalingMetrics(IMeterFactory? meterFactory)
    {
        Meter meter =
            meterFactory?.Create(SignalingDiagnostics.MeterName)
            ?? new Meter(SignalingDiagnostics.MeterName);
        RoomsActive = meter.CreateUpDownCounter<long>(
            "streamtransport.signaling.rooms.active",
            "{room}",
            "Rooms open on the router."
        );
        PeersActive = meter.CreateUpDownCounter<long>(
            "streamtransport.signaling.peers.active",
            "{peer}",
            "Peers joined to a room, by role."
        );
        JoinsRefused = meter.CreateCounter<long>(
            "streamtransport.signaling.joins.refused",
            "{join}",
            "Hellos refused, by reason: invalid, version or room_not_found."
        );
        MessagesRouted = meter.CreateCounter<long>(
            "streamtransport.signaling.messages.routed",
            "{message}",
            "Messages forwarded between peers, by type: sdp, ice or control."
        );
        MessagesDropped = meter.CreateCounter<long>(
            "streamtransport.signaling.messages.dropped",
            "{message}",
            "Messages not delivered, by reason: no_target, link_broken or invalid."
        );
    }

    public UpDownCounter<long> RoomsActive { get; }

    public UpDownCounter<long> PeersActive { get; }

    public Counter<long> JoinsRefused { get; }

    public Counter<long> MessagesRouted { get; }

    public Counter<long> MessagesDropped { get; }

    public static KeyValuePair<string, object?> Reason(string reason) =>
        new("streamtransport.reason", reason);

    public static KeyValuePair<string, object?> Type(string type) =>
        new("streamtransport.signaling.message_type", type);

    public static KeyValuePair<string, object?> Role(PeerRole role) =>
        new(
            "streamtransport.signaling.role",
            role == PeerRole.Publisher ? "publisher" : "subscriber"
        );
}
