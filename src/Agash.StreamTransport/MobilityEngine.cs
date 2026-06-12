using System.Collections.Concurrent;
using Agash.StreamTransport.WebRtc;

namespace Agash.StreamTransport;

/// <summary>
/// The mobility coordinator: it watches the host's network interfaces (via
/// <see cref="INetworkMonitor"/>) and, when they change - a Wi-Fi↔cellular transition, a modem reconnect, a
/// default-route flip - tells every active connection to re-probe and fail over its path, proactively rather
/// than waiting for consent to time out. It owns no transport; connections register a recovery callback while
/// they are live. Reactive consent-loss recovery still runs inside each <see cref="PeerConnection"/>; this is
/// the fast, proactive trigger on top.
/// </summary>
public sealed class MobilityEngine : IDisposable
{
    private readonly INetworkMonitor _monitor;
    private readonly ConcurrentDictionary<Registration, byte> _recoverers = new();
    private readonly Lock _gate = new();
    private bool _started;

    /// <summary>Create the engine over a network monitor (resolved from DI).</summary>
    public MobilityEngine(INetworkMonitor monitor) => _monitor = monitor;

    /// <summary>
    /// Register a recovery callback (typically <see cref="PeerConnection.TriggerNetworkRecovery"/>), invoked
    /// on every network change. Dispose the returned token when the connection goes away.
    /// </summary>
    public IDisposable Register(Action onNetworkChange)
    {
        ArgumentNullException.ThrowIfNull(onNetworkChange);
        var registration = new Registration(this, onNetworkChange);
        _recoverers[registration] = 0;

        // Start monitoring lazily on the first registration.
        lock (_gate)
        {
            if (!_started)
            {
                _started = true;
                _monitor.NetworksChanged += OnNetworksChanged;
                _monitor.Start();
            }
        }

        return registration;
    }

    private void OnNetworksChanged(IReadOnlyList<NetworkPathInfo> paths)
    {
        foreach (Registration registration in _recoverers.Keys)
        {
            registration.Invoke();
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_started)
            {
                _monitor.NetworksChanged -= OnNetworksChanged;
                _started = false;
            }
        }

        _recoverers.Clear();
    }

    private sealed class Registration(MobilityEngine owner, Action callback) : IDisposable
    {
        public void Invoke() => callback();

        public void Dispose() => owner._recoverers.TryRemove(this, out _);
    }
}
