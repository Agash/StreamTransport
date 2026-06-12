using System.Net.NetworkInformation;

namespace Agash.StreamTransport.WebRtc;

/// <summary>
/// The default <see cref="INetworkMonitor"/>, riding the BCL's <see cref="NetworkChange.NetworkAddressChanged"/>
/// (netlink-backed on Linux, IP Helper notifications on Windows). It allocates nothing in steady state - it
/// only enumerates interfaces when a change actually fires, which is rare - and raises
/// <see cref="INetworkMonitor.NetworksChanged"/> only when the operational set genuinely differs.
/// </summary>
public sealed class NetworkChangeMonitor : INetworkMonitor
{
    private readonly Lock _gate = new();
    private NetworkPathInfo[] _current = [];
    private bool _started;

    /// <inheritdoc/>
    public IReadOnlyList<NetworkPathInfo> Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <inheritdoc/>
    public event Action<IReadOnlyList<NetworkPathInfo>>? NetworksChanged;

    /// <inheritdoc/>
    public void Start()
    {
        lock (_gate)
        {
            if (_started)
            {
                return;
            }

            _started = true;
        }

        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        Refresh();
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e) => Refresh();

    private void Refresh()
    {
        NetworkPathInfo[] snapshot = Snapshot();
        bool changed;
        lock (_gate)
        {
            changed = !snapshot.AsSpan().SequenceEqual(_current);
            if (changed)
            {
                _current = snapshot;
            }
        }

        if (changed)
        {
            NetworksChanged?.Invoke(snapshot);
        }
    }

    private static NetworkPathInfo[] Snapshot()
    {
        NetworkInterface[] all = NetworkInterface.GetAllNetworkInterfaces();
        var operational = new List<NetworkPathInfo>(all.Length);
        foreach (NetworkInterface ni in all)
        {
            if (ni.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            operational.Add(new NetworkPathInfo(ni.Id, ni.Name, Classify(ni.NetworkInterfaceType), IsUp: true));
        }

        return [.. operational];
    }

    private static NetworkAdapterType Classify(NetworkInterfaceType type) => type switch
    {
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT
            or NetworkInterfaceType.FastEthernetFx => NetworkAdapterType.Ethernet,
        NetworkInterfaceType.Wireless80211 => NetworkAdapterType.Wifi,
        NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2 => NetworkAdapterType.Cellular,
        NetworkInterfaceType.Loopback => NetworkAdapterType.Loopback,
        _ => NetworkAdapterType.Other,
    };

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_gate)
        {
            if (!_started)
            {
                return;
            }

            _started = false;
        }

        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
    }
}
