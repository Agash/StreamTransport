using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Agash.StreamTransport.WebRtc.Ice;

/// <summary>
/// An ICE candidate (RFC 8445 §5.1) - a transport address an agent offers as a possible source or
/// destination for media, with its type, priority, and (for reflexive/relayed candidates) the base
/// address it derives from. Serializes to / parses from the SDP <c>a=candidate:</c> form (RFC 8839).
/// </summary>
public readonly record struct IceCandidate
{
    /// <summary>Component 1 (RTP). With rtcp-mux there is no component 2.</summary>
    public const int RtpComponent = 1;

    /// <summary>Creates a candidate.</summary>
    public IceCandidate(
        string foundation,
        int componentId,
        uint priority,
        IPEndPoint endpoint,
        IceCandidateKind kind,
        IPEndPoint? relatedAddress = null)
    {
        Foundation = foundation;
        ComponentId = componentId;
        Priority = priority;
        Endpoint = endpoint;
        Kind = kind;
        RelatedAddress = relatedAddress;
    }

    /// <summary>An identifier shared by candidates of the same type, base, and protocol (RFC 8445 §5.1.1.3).</summary>
    public string Foundation { get; }

    /// <summary>The component this candidate is for (1 = RTP).</summary>
    public int ComponentId { get; }

    /// <summary>The candidate priority (RFC 8445 §5.1.2.1).</summary>
    public uint Priority { get; }

    /// <summary>The candidate transport address.</summary>
    public IPEndPoint Endpoint { get; }

    /// <summary>The candidate type.</summary>
    public IceCandidateKind Kind { get; }

    /// <summary>For reflexive/relayed candidates, the base address they map from; otherwise null.</summary>
    public IPEndPoint? RelatedAddress { get; }

    /// <summary>Formats the candidate as an SDP attribute value (without the leading <c>a=</c>).</summary>
    public string ToSdp()
    {
        string typeName = Kind switch
        {
            IceCandidateKind.Host => "host",
            IceCandidateKind.ServerReflexive => "srflx",
            IceCandidateKind.PeerReflexive => "prflx",
            IceCandidateKind.Relayed => "relay",
            _ => "host",
        };

        string s = string.Create(CultureInfo.InvariantCulture,
            $"candidate:{Foundation} {ComponentId} udp {Priority} {Endpoint.Address} {Endpoint.Port} typ {typeName}");

        if (RelatedAddress is { } rel)
        {
            s = string.Create(CultureInfo.InvariantCulture, $"{s} raddr {rel.Address} rport {rel.Port}");
        }

        return s;
    }

    /// <summary>
    /// Parses an SDP candidate attribute value. Accepts an optional leading <c>candidate:</c> and an
    /// optional <c>a=</c>. Only UDP candidates are supported (TCP ICE is out of scope).
    /// </summary>
    public static bool TryParse(string value, out IceCandidate candidate)
    {
        candidate = default;
        ReadOnlySpan<char> v = value.AsSpan().Trim();
        if (v.StartsWith("a=", StringComparison.Ordinal))
        {
            v = v[2..];
        }

        if (v.StartsWith("candidate:", StringComparison.Ordinal))
        {
            v = v["candidate:".Length..];
        }

        string[] parts = v.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 8 || !parts[6].Equals("typ", StringComparison.Ordinal))
        {
            return false;
        }

        if (!parts[2].Equals("udp", StringComparison.OrdinalIgnoreCase))
        {
            return false; // UDP only.
        }

        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int component)
            || !uint.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint priority)
            || !IPAddress.TryParse(parts[4], out IPAddress? address)
            || !int.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out int port))
        {
            return false;
        }

        IceCandidateKind kind = parts[7] switch
        {
            "host" => IceCandidateKind.Host,
            "srflx" => IceCandidateKind.ServerReflexive,
            "prflx" => IceCandidateKind.PeerReflexive,
            "relay" => IceCandidateKind.Relayed,
            _ => (IceCandidateKind)(-1),
        };
        if ((int)kind < 0)
        {
            return false;
        }

        IPEndPoint? related = null;
        for (int i = 8; i + 1 < parts.Length; i += 2)
        {
            if (parts[i].Equals("raddr", StringComparison.Ordinal)
                && IPAddress.TryParse(parts[i + 1], out IPAddress? raddr))
            {
                int rport = 0;
                if (i + 3 < parts.Length && parts[i + 2].Equals("rport", StringComparison.Ordinal))
                {
                    _ = int.TryParse(parts[i + 3], NumberStyles.Integer, CultureInfo.InvariantCulture, out rport);
                }

                related = new IPEndPoint(raddr, rport);
            }
        }

        candidate = new IceCandidate(parts[0], component, priority, new IPEndPoint(address, port), kind, related);
        return true;
    }

    /// <summary>
    /// Computes a candidate priority (RFC 8445 §5.1.2.1):
    /// <c>2^24·typePref + 2^8·localPref + (256 − componentId)</c>. IPv6 is given a higher local preference
    /// than IPv4 so the IPv6 path is tried first (the symmetric-NAT-traversal goal).
    /// </summary>
    public static uint ComputePriority(IceCandidateKind kind, AddressFamily family, int componentId, int index = 0)
    {
        uint typePref = kind switch
        {
            IceCandidateKind.Host => 126,
            IceCandidateKind.PeerReflexive => 110,
            IceCandidateKind.ServerReflexive => 100,
            IceCandidateKind.Relayed => 0,
            _ => 0,
        };

        // Local preference: IPv6 outranks IPv4 (IPv6-first). `index` keeps multiple candidates of the same
        // family distinct and stable, without overflowing the 16-bit local-preference field.
        bool ipv6 = family == AddressFamily.InterNetworkV6;
        uint localPref = (uint)Math.Clamp((ipv6 ? 60000 : 40000) - index, 1, 65535);

        return (typePref << 24) | (localPref << 8) | (uint)(256 - componentId);
    }

    /// <summary>
    /// Computes the priority of a candidate pair (RFC 8445 §6.1.2.3):
    /// <c>2^32·MIN(G,D) + 2·MAX(G,D) + (G&gt;D?1:0)</c>, where G is the controlling agent's candidate
    /// priority and D the controlled agent's.
    /// </summary>
    public static ulong ComputePairPriority(uint controllingPriority, uint controlledPriority)
    {
        ulong g = controllingPriority;
        ulong d = controlledPriority;
        return ((ulong)Math.Min(g, d) << 32) + (2 * Math.Max(g, d)) + (g > d ? 1UL : 0UL);
    }
}
