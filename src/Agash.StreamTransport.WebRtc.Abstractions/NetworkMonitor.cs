namespace Agash.StreamTransport.WebRtc;

/// <summary>The kind of physical link a network interface represents, as far as we can classify it.</summary>
public enum NetworkAdapterType
{
    /// <summary>Unclassified.</summary>
    Unknown,

    /// <summary>Wired Ethernet.</summary>
    Ethernet,

    /// <summary>Wi-Fi (802.11).</summary>
    Wifi,

    /// <summary>Mobile broadband / cellular (WWAN).</summary>
    Cellular,

    /// <summary>Loopback.</summary>
    Loopback,

    /// <summary>Some other interface (tunnel, virtual, …).</summary>
    Other,
}

/// <summary>A snapshot of one operational network interface (the bits mobility cares about).</summary>
/// <param name="Id">The interface id (stable across the process).</param>
/// <param name="Name">The interface name, for diagnostics.</param>
/// <param name="AdapterType">The classified link type.</param>
/// <param name="IsUp">Whether the interface is operational.</param>
public readonly record struct NetworkPathInfo(string Id, string Name, NetworkAdapterType AdapterType, bool IsUp);

/// <summary>
/// Observes the host's network interfaces and reports when they change - an interface added/removed, a link
/// going up/down, a Wi-Fi↔cellular transition. The default implementation rides the BCL's
/// <c>NetworkChange.NetworkAddressChanged</c> (netlink-backed on Linux), so it is cross-platform and
/// allocation-free in steady state - it only does work when a change actually fires, which is rare. The
/// mobility layer consumes this to pre-warm and fail over paths; on its own it is observability.
/// </summary>
public interface INetworkMonitor : IDisposable
{
    /// <summary>The current operational interfaces.</summary>
    IReadOnlyList<NetworkPathInfo> Current { get; }

    /// <summary>Raised when the set of operational interfaces changes, carrying the new snapshot.</summary>
    event Action<IReadOnlyList<NetworkPathInfo>>? NetworksChanged;

    /// <summary>Begin monitoring (idempotent). Populates <see cref="Current"/> and starts raising changes.</summary>
    void Start();
}
