using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Agash.StreamTransport;

/// <summary>
/// Publishes one media stream to subscribers in a room. A subscriber joining spins up a
/// <see cref="WebRtcMediaSender"/> bound to its per-peer signaling channel (the publisher offers); a
/// subscriber leaving tears it down.
/// </summary>
/// <remarks>
/// Each subscriber currently gets its own sender, which encodes independently - correct for one subscriber.
/// Fanning a single shared encode out to N subscribers (encode once, packetize once, send to N peer
/// connections) is a planned follow-up; until then a single
/// <see cref="IVideoFrameSource"/> cannot be safely consumed by multiple senders at once.
/// </remarks>
public sealed partial class MediaPublisher : IAsyncDisposable
{
    private readonly MediaTransportOptions _options;
    private readonly IMediaTransport _transport;
    private readonly ILogger _logger;
    private readonly IMediaRoom _room;
    private readonly IVideoFrameSource? _videoSource;
    private readonly IAudioFrameSource? _audioSource;
    private readonly nint _gpuDeviceHandle;
    private readonly ConcurrentDictionary<long, IMediaSender> _senders = new();
    private readonly CancellationTokenSource _cts = new();

    /// <summary>Create a publisher over a room joined as <see cref="PeerRole.Publisher"/>.</summary>
    /// <param name="options">The media transport options (codecs, ICE).</param>
    /// <param name="transport">The wire transport that mints a sender per subscriber.</param>
    /// <param name="loggerFactory">The logger factory for the publisher's own diagnostics.</param>
    /// <param name="room">The room, joined as a publisher.</param>
    /// <param name="video">The video source, or null for audio-only.</param>
    /// <param name="audio">The audio source, or null for video-only.</param>
    /// <param name="gpuDeviceHandle">
    /// Optional shared <c>ID3D11Device*</c> (Windows) the video encoder runs on, so a GPU capture source
    /// that produced its textures on the same device encodes them zero-copy. Zero to let the encoder
    /// create its own device.
    /// </param>
    public MediaPublisher(
        MediaTransportOptions options,
        IMediaTransport transport,
        ILoggerFactory loggerFactory,
        IMediaRoom room,
        IVideoFrameSource? video = null,
        IAudioFrameSource? audio = null,
        nint gpuDeviceHandle = 0)
    {
        if (video is null && audio is null)
        {
            throw new ArgumentException("A publisher needs at least a video or an audio source.");
        }

        _options = options with { IceServers = MergeIce(options, room) };
        _transport = transport;
        _logger = loggerFactory.CreateLogger<MediaPublisher>();
        _room = room;
        _videoSource = video;
        _audioSource = audio;
        _gpuDeviceHandle = gpuDeviceHandle;
    }

    /// <summary>Start serving subscribers: wire room events and attach to anyone already present.</summary>
    public void Start()
    {
        _room.PeerJoined += OnPeerJoined;
        _room.PeerLeft += OnPeerLeft;

        // The live roster, not the stale join-time snapshot - a subscriber may have joined and fired
        // PeerJoined before this handler was attached.
        foreach (PeerInfo peer in _room.Peers)
        {
            OnPeerJoined(peer);
        }
    }

    private void OnPeerJoined(PeerInfo peer)
    {
        if (peer.Role == PeerRole.Subscriber)
        {
            _ = AddSubscriberAsync(peer.PeerId);
        }
    }

    private void OnPeerLeft(PeerId peer)
    {
        if (_senders.TryRemove(peer.Value, out IMediaSender? sender))
        {
            LogSubscriberLeft(peer.Value);
            _ = sender.DisposeAsync();
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Subscriber {PeerId} joined; starting a sender.")]
    private partial void LogSubscriberJoined(long peerId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Subscriber {PeerId} left; tearing down its sender.")]
    private partial void LogSubscriberLeft(long peerId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sender for subscriber {PeerId} failed to start.")]
    private partial void LogSenderFailed(long peerId, Exception exception);

    private async Task AddSubscriberAsync(PeerId peer)
    {
        IMediaSender sender = _transport.CreateSender(_options, _videoSource, _audioSource, _gpuDeviceHandle);

        // Claim the peer atomically so a subscriber that appears in both the live roster and a PeerJoined
        // event is only offered to once.
        if (!_senders.TryAdd(peer.Value, sender))
        {
            await sender.DisposeAsync().ConfigureAwait(false);
            return;
        }

        LogSubscriberJoined(peer.Value);

        // Advertise whether this stream carries side-by-side alpha BEFORE the offer, on the same ordered
        // link, so the subscriber adopts it without a flag of its own and has it set before any frame decodes.
        await _room.SendControlAsync(MediaControlTopics.Alpha, _options.PreserveAlpha ? "1" : "0", peer, _cts.Token)
            .ConfigureAwait(false);

        try
        {
            await sender.StartAsync(_room.ChannelFor(peer), _cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogSenderFailed(peer.Value, ex);
            if (_senders.TryRemove(peer.Value, out IMediaSender? failed))
            {
                await failed.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static IReadOnlyList<IceServer> MergeIce(MediaTransportOptions options, IMediaRoom room) =>
        options.IceServers.Count == 0 ? room.IceServers : [.. options.IceServers, .. room.IceServers];

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        _room.PeerJoined -= OnPeerJoined;
        _room.PeerLeft -= OnPeerLeft;
        await _cts.CancelAsync().ConfigureAwait(false);

        foreach (IMediaSender sender in _senders.Values)
        {
            await sender.DisposeAsync().ConfigureAwait(false);
        }

        _senders.Clear();
        _cts.Dispose();
    }
}
