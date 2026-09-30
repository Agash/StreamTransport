using System.Net;
using Agash.StreamTransport.WebRtc.Ice;
using Agash.StreamTransport.WebRtc.Stun;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agash.StreamTransport.WebRtc.Tests;

/// <summary>
/// Runs <see cref="IceStateMachine"/>s against each other with no sockets, threads or clock: datagrams are
/// delivered the instant they are sent, and time jumps straight to the next moment any machine needs it.
/// A path can be cut, a fraction of datagrams lost (seeded), and datagrams held back to reorder a
/// handshake, so every run of a scenario is the same run.
/// </summary>
internal sealed class IceSwitchboard
{
    private readonly List<Peer> _peers = [];
    private readonly HashSet<IPAddress> _cut = [];
    private readonly List<(Peer From, IceTransmit Transmit)> _held = [];
    private Func<IPEndPoint, byte[], bool>? _hold;
    private Random? _loss;
    private double _lossRate;
    private int _nextPort = 50_000;

    public TimeSpan Now { get; private set; }

    /// <summary>A controlling and a controlled machine on the given addresses, trickling to each other.</summary>
    public (Peer Controlling, Peer Controlled) Pair(
        IPAddress[] controlling,
        IPAddress[] controlled,
        IceTimings? timings = null
    )
    {
        Peer a = Add(IceRole.Controlling, controlling, timings ?? IceTimings.Default);
        Peer b = Add(IceRole.Controlled, controlled, timings ?? IceTimings.Default);
        a.Machine.SetRemoteCredentials(b.Machine.LocalCredentials);
        b.Machine.SetRemoteCredentials(a.Machine.LocalCredentials);
        a.Remote = b;
        b.Remote = a;
        a.Machine.Start(Now);
        b.Machine.Start(Now);
        Exchange();
        return (a, b);
    }

    /// <summary>Drops everything to and from an address from now on.</summary>
    public void Cut(IPAddress address) => _cut.Add(address);

    /// <summary>Restores a cut address.</summary>
    public void Uncut(IPAddress address) => _cut.Remove(address);

    /// <summary>Restarts a peer under new credentials on freshly bound endpoints.</summary>
    public void Restart(Peer peer, IceCredentials credentials, IPAddress[] addresses)
    {
        peer.Machine.Restart(credentials);
        Bind(peer, addresses);
        Exchange();
    }

    /// <summary>Drops a fraction of all datagrams, chosen by a seeded generator.</summary>
    public void SetLossRate(double rate, int seed = 12345)
    {
        _lossRate = rate;
        _loss = new Random(seed);
    }

    /// <summary>Holds back the datagrams the predicate (source, data) matches until released.</summary>
    public void Hold(Func<IPEndPoint, byte[], bool> predicate) => _hold = predicate;

    /// <summary>Delivers everything held, now.</summary>
    public void ReleaseHeld()
    {
        _hold = null;
        (Peer From, IceTransmit Transmit)[] held = [.. _held];
        _held.Clear();
        foreach ((Peer from, IceTransmit transmit) in held)
        {
            Deliver(from, transmit);
        }

        Exchange();
    }

    /// <summary>Advances time until the condition holds or the budget runs out; returns whether it held.</summary>
    public bool RunUntil(Func<bool> condition, TimeSpan budget)
    {
        TimeSpan end = Now + budget;
        while (!condition())
        {
            if (!Step(end))
            {
                return condition();
            }
        }

        return true;
    }

    /// <summary>Advances time by a duration.</summary>
    public void Run(TimeSpan duration)
    {
        TimeSpan end = Now + duration;
        while (Step(end)) { }

        Now = end;
    }

    /// <summary>Sends a media datagram over a peer's selected pair; returns whether it arrived.</summary>
    public bool SendData(Peer from)
    {
        if (from.Machine.Selected is not { } selected)
        {
            return false;
        }

        IPEndPoint source = from.Endpoints[selected.Local];
        return Passes(source, selected.Remote, [0x80]) && Owner(selected.Remote) is not null;
    }

    // Moves time to the next timeout before the end and runs it; false when none is due by then.
    private bool Step(TimeSpan end)
    {
        TimeSpan? next = null;
        foreach (Peer peer in _peers)
        {
            if (peer.Machine.NextTimeout is { } due && (next is null || due < next))
            {
                next = due;
            }
        }

        if (next is not { } at || at > end)
        {
            return false;
        }

        Now = at;
        foreach (Peer peer in _peers)
        {
            peer.Machine.HandleTimeout(Now);
        }

        Exchange();
        return true;
    }

    private Peer Add(IceRole role, IPAddress[] addresses, IceTimings timings)
    {
        Peer peer = new(
            new IceStateMachine(IceCredentials.Generate(), role, timings, NullLogger.Instance)
        );
        Bind(peer, addresses);
        _peers.Add(peer);
        return peer;
    }

    // Handles index the peer's endpoints; a restart's endpoints follow the ones it dropped.
    private void Bind(Peer peer, IPAddress[] addresses)
    {
        foreach (IPAddress address in addresses)
        {
            IPEndPoint bound = new(address, _nextPort++);
            int handle = peer.Endpoints.Count;
            peer.Endpoints.Add(bound);
            peer.Machine.AddLocalEndpoint(handle, bound);
        }
    }

    // Delivers datagrams and events until every machine is quiet.
    private void Exchange()
    {
        bool busy = true;
        while (busy)
        {
            busy = false;
            foreach (Peer peer in _peers)
            {
                while (peer.Machine.TryPollEvent(out IceEvent iceEvent))
                {
                    busy = true;
                    if (iceEvent.Candidate is { } candidate)
                    {
                        peer.Remote?.Machine.AddRemoteCandidate(candidate);
                    }
                    else
                    {
                        peer.States.Add(iceEvent.State);
                    }
                }

                while (peer.Machine.TryPollTransmit(out IceTransmit transmit))
                {
                    busy = true;
                    Deliver(peer, transmit);
                }
            }
        }
    }

    private void Deliver(Peer from, IceTransmit transmit)
    {
        IPEndPoint source = from.Endpoints[transmit.Local];
        if (_hold is not null && _hold(source, transmit.Data))
        {
            _held.Add((from, transmit));
            return;
        }

        if (
            !Passes(source, transmit.Destination, transmit.Data)
            || Owner(transmit.Destination) is not { } to
        )
        {
            return;
        }

        if (StunMessageReader.TryParse(transmit.Data, out StunMessageReader stun))
        {
            to.Peer.Machine.HandleStun(to.Handle, source, stun, Now);
        }
    }

    private bool Passes(IPEndPoint source, IPEndPoint destination, byte[] data) =>
        data.Length > 0
        && !_cut.Contains(source.Address)
        && !_cut.Contains(destination.Address)
        && (_loss is null || _loss.NextDouble() >= _lossRate);

    private (Peer Peer, int Handle)? Owner(IPEndPoint endpoint)
    {
        foreach (Peer peer in _peers)
        {
            int handle = peer.Endpoints.IndexOf(endpoint);
            if (handle >= 0)
            {
                return (peer, handle);
            }
        }

        return null;
    }

    internal sealed class Peer(IceStateMachine machine)
    {
        public IceStateMachine Machine { get; } = machine;

        public List<IPEndPoint> Endpoints { get; } = [];

        public List<IceConnectionState> States { get; } = [];

        public Peer? Remote { get; set; }

        public bool Connected => Machine.State == IceConnectionState.Connected;

        /// <summary>The local address of the selected pair, or null.</summary>
        public IPAddress? SelectedAddress =>
            Machine.Selected is { } selected ? Endpoints[selected.Local].Address : null;
    }
}
