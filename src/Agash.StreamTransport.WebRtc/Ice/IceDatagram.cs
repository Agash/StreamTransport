using System.Net;

namespace Agash.StreamTransport.WebRtc.Ice;

/// <summary>
/// A datagram the ICE agent received for the layers above it (DTLS, SRTP), with what ICE knows about how
/// it came.
/// </summary>
/// <param name="Data">
/// The datagram, in the agent's reused receive buffer: borrowed for the handler's synchronous duration,
/// which may read and change it in place but copies what it keeps.
/// </param>
/// <param name="Source">Where it came from.</param>
/// <param name="Ecn">Its two ECN bits; zero where the platform does not surface them.</param>
/// <param name="PreviousSession">
/// Whether it came over a socket of the ICE session a restart replaced, still open while the peer moves:
/// the 5-tuple a DTLS association made before the restart lives on (RFC 8842 section 6).
/// </param>
public readonly record struct IceDatagram(
    Memory<byte> Data,
    IPEndPoint Source,
    byte Ecn,
    bool PreviousSession
);

/// <summary>Handles a datagram the ICE agent received.</summary>
/// <param name="datagram">The datagram, borrowed for the call.</param>
public delegate void IceDatagramHandler(in IceDatagram datagram);
