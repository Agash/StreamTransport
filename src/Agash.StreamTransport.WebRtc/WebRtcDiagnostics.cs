using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net.Sockets;
using Agash.StreamTransport.WebRtc.Ice;

namespace Agash.StreamTransport.WebRtc;

/// <summary>
/// The names the WebRTC stack reports metrics and traces under. Nothing is recorded or emitted until
/// something listens: add them to an OpenTelemetry provider, or watch the meter with
/// <c>dotnet-counters</c>. DTLS reports on Dtls.Core's own meter.
/// </summary>
public static class WebRtcDiagnostics
{
    /// <summary>The meter's name: <c>Agash.StreamTransport.WebRtc</c>.</summary>
    public const string MeterName = "Agash.StreamTransport.WebRtc";

    /// <summary>The activity source's name: <c>Agash.StreamTransport.WebRtc</c>.</summary>
    public const string ActivitySourceName = "Agash.StreamTransport.WebRtc";

    internal static ActivitySource ActivitySource { get; } =
        new(ActivitySourceName, typeof(WebRtcDiagnostics).Assembly.GetName().Version?.ToString());
}

// The instruments. Tags are low-cardinality: candidate kinds, address family, outcome; never an address.
internal sealed class WebRtcMetrics
{
    public WebRtcMetrics(IMeterFactory? meterFactory)
    {
        Meter meter =
            meterFactory?.Create(WebRtcDiagnostics.MeterName)
            ?? new Meter(WebRtcDiagnostics.MeterName);
        ConnectionsActive = meter.CreateUpDownCounter<long>(
            "streamtransport.webrtc.connections.active",
            "{connection}",
            "Peer connections created and not yet disposed."
        );
        ConnectDuration = meter.CreateHistogram<double>(
            "streamtransport.webrtc.connect.duration",
            "s",
            "From starting ICE to the DTLS-SRTP transport being up, or to the failure, by outcome."
        );
        SelectedPaths = meter.CreateCounter<long>(
            "streamtransport.webrtc.ice.selected_paths",
            "{path}",
            "Candidate pairs ICE selected, by local and remote candidate kind and address family: how often media goes direct, through NAT or through a relay."
        );
        ConsentLost = meter.CreateCounter<long>(
            "streamtransport.webrtc.ice.consent_lost",
            "{event}",
            "Selected paths that stopped answering consent checks."
        );
        Restarts = meter.CreateCounter<long>(
            "streamtransport.webrtc.ice.restarts",
            "{restart}",
            "ICE restarts, by this side or the peer."
        );
        TurnAllocations = meter.CreateCounter<long>(
            "streamtransport.webrtc.turn.allocations",
            "{allocation}",
            "TURN allocations attempted, by transport and outcome."
        );
        PacketsSent = meter.CreateCounter<long>(
            "streamtransport.webrtc.rtp.packets.sent",
            "{packet}",
            "Media RTP packets sent, not counting retransmissions."
        );
        NackedSequences = meter.CreateCounter<long>(
            "streamtransport.webrtc.nack.sequences",
            "{packet}",
            "Lost packets asked for again with NACK."
        );
        Retransmissions = meter.CreateCounter<long>(
            "streamtransport.webrtc.rtx.packets",
            "{packet}",
            "RTX retransmissions, by direction: sent, or received and recovered."
        );
    }

    // For connections made without dependency injection.
    public static WebRtcMetrics Shared { get; } = new(meterFactory: null);

    public UpDownCounter<long> ConnectionsActive { get; }

    public Histogram<double> ConnectDuration { get; }

    public Counter<long> SelectedPaths { get; }

    public Counter<long> ConsentLost { get; }

    public Counter<long> Restarts { get; }

    public Counter<long> TurnAllocations { get; }

    public Counter<long> PacketsSent { get; }

    public Counter<long> NackedSequences { get; }

    public Counter<long> Retransmissions { get; }

    public static WebRtcMetrics For(IMeterFactory? meterFactory) =>
        meterFactory is null ? Shared : new WebRtcMetrics(meterFactory);

    public static KeyValuePair<string, object?> Outcome(string outcome) =>
        new("streamtransport.outcome", outcome);

    public static KeyValuePair<string, object?> Direction(string direction) =>
        new("streamtransport.direction", direction);

    public static KeyValuePair<string, object?> LocalKind(IceCandidateKind kind) =>
        new("streamtransport.webrtc.ice.local_kind", Name(kind));

    public static KeyValuePair<string, object?> RemoteKind(IceCandidateKind kind) =>
        new("streamtransport.webrtc.ice.remote_kind", Name(kind));

    public static KeyValuePair<string, object?> Family(AddressFamily family) =>
        new("network.type", family == AddressFamily.InterNetworkV6 ? "ipv6" : "ipv4");

    public static KeyValuePair<string, object?> Transport(string transport) =>
        new("network.transport", transport);

    // The SDP names (RFC 8839): host, srflx, prflx, relay.
    public static string Name(IceCandidateKind kind) =>
        kind switch
        {
            IceCandidateKind.Host => "host",
            IceCandidateKind.ServerReflexive => "srflx",
            IceCandidateKind.PeerReflexive => "prflx",
            _ => "relay",
        };
}
