using Agash.StreamTransport.WebRtc;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Agash.StreamTransport.Tests;

/// <summary>
/// The mobility coordinator, tested deterministically against a fake <see cref="INetworkMonitor"/>: a network
/// change must invoke every live recovery callback, registering must start monitoring, and a disposed
/// registration must stop receiving changes. No sockets or timing involved - the abstraction makes it a pure
/// unit test.
/// </summary>
[TestClass]
public sealed class MobilityEngineTests
{
    [TestMethod]
    public void NetworkChange_InvokesEveryRegisteredRecoverer()
    {
        var monitor = new FakeNetworkMonitor();
        using var engine = new MobilityEngine(monitor);
        int a = 0;
        int b = 0;
        using IDisposable ra = engine.Register(() => a++);
        using IDisposable rb = engine.Register(() => b++);

        Assert.IsTrue(monitor.Started, "the first registration should start monitoring.");

        monitor.Raise(new NetworkPathInfo("1", "wlan0", NetworkAdapterType.Wifi, IsUp: true));
        Assert.AreEqual(1, a);
        Assert.AreEqual(1, b);

        monitor.Raise(new NetworkPathInfo("2", "wwan0", NetworkAdapterType.Cellular, IsUp: true));
        Assert.AreEqual(2, a);
        Assert.AreEqual(2, b);
    }

    [TestMethod]
    public void DisposedRegistration_NoLongerInvoked()
    {
        var monitor = new FakeNetworkMonitor();
        using var engine = new MobilityEngine(monitor);
        int count = 0;
        IDisposable registration = engine.Register(() => count++);

        monitor.Raise();
        Assert.AreEqual(1, count);

        registration.Dispose();
        monitor.Raise();
        Assert.AreEqual(1, count, "a disposed registration must stop receiving network changes.");
    }

    private sealed class FakeNetworkMonitor : INetworkMonitor
    {
        public IReadOnlyList<NetworkPathInfo> Current { get; private set; } = [];
        public bool Started { get; private set; }

        public event Action<IReadOnlyList<NetworkPathInfo>>? NetworksChanged;

        public void Start() => Started = true;

        public void Raise(params NetworkPathInfo[] paths)
        {
            Current = paths;
            NetworksChanged?.Invoke(paths);
        }

        public void Dispose()
        {
        }
    }
}
