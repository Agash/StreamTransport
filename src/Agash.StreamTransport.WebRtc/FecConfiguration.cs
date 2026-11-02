namespace Agash.StreamTransport.WebRtc;

/// <summary>What FlexFEC a negotiation agreed, each way.</summary>
/// <param name="Send">How this endpoint protects its media, or null when it sends no repairs.</param>
/// <param name="Receive">Whose media the peer's repairs protect, or null when it sends none.</param>
internal sealed record FecConfiguration(FecSender? Send, FecReceiver? Receive)
{
    public static FecConfiguration None { get; } = new(null, null);
}

/// <summary>This endpoint's repairs: of which media, on which SSRC, under which payload type.</summary>
/// <param name="ProtectedSsrc">The media SSRC protected.</param>
/// <param name="FecSsrc">The SSRC repairs go on.</param>
/// <param name="PayloadType">The negotiated flexfec payload type.</param>
internal sealed record FecSender(uint ProtectedSsrc, uint FecSsrc, byte PayloadType);

/// <summary>The peer's repairs: of which media, on which SSRC.</summary>
/// <param name="ProtectedSsrc">The peer's media SSRC its repairs protect.</param>
/// <param name="FecSsrc">The SSRC its repairs arrive on.</param>
internal sealed record FecReceiver(uint ProtectedSsrc, uint FecSsrc);
