using System.Threading.Channels;
using Agash.StreamTransport;

namespace Agash.StreamTransport.Tests;

/// <summary>
/// An in-memory <see cref="ISignalingChannel"/> that delivers everything sent on it to its
/// <see cref="Peer"/>, in order, on a background pump. Two instances cross-linked as peers stand in
/// for the network so a sender and receiver can negotiate in one process.
/// </summary>
internal sealed class LoopbackSignaling : ISignalingChannel
{
    private readonly Channel<object> _inbox = Channel.CreateUnbounded<object>();
    private readonly Task _pump;

    public LoopbackSignaling() => _pump = Task.Run(PumpAsync);

    public LoopbackSignaling? Peer { get; set; }

    public event Func<SessionDescription, Task>? DescriptionReceived;

    public event Func<IceCandidate, Task>? IceCandidateReceived;

    public Task SendAsync(SessionDescription description, CancellationToken cancellationToken = default)
    {
        Peer!._inbox.Writer.TryWrite(description);
        return Task.CompletedTask;
    }

    public Task SendAsync(IceCandidate candidate, CancellationToken cancellationToken = default)
    {
        Peer!._inbox.Writer.TryWrite(candidate);
        return Task.CompletedTask;
    }

    private async Task PumpAsync()
    {
        await foreach (object item in _inbox.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            switch (item)
            {
                case SessionDescription description when DescriptionReceived is { } handler:
                    await handler(description).ConfigureAwait(false);
                    break;
                case IceCandidate candidate when IceCandidateReceived is { } handler:
                    await handler(candidate).ConfigureAwait(false);
                    break;
                default:
                    break;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _inbox.Writer.TryComplete();
        await _pump.ConfigureAwait(false);
    }
}
