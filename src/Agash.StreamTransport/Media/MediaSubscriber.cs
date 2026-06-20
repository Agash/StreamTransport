using Microsoft.Extensions.Logging;

namespace Agash.StreamTransport;

/// <summary>
/// Subscribes to a room's published stream: joins as a <see cref="PeerRole.Subscriber"/>, finds the
/// publisher, and decodes its audio/video into the supplied sinks. The publisher offers; this side
/// answers over the publisher's per-peer channel. One publisher per room is expected.
/// </summary>
public sealed partial class MediaSubscriber : IAsyncDisposable
{
    private readonly MediaTransportOptions _options;
    private readonly IMediaTransport _transport;
    private readonly ILogger _logger;
    private readonly IMediaRoom _room;
    private readonly IVideoFrameSink? _videoSink;
    private readonly IAudioFrameSink? _audioSink;
    private readonly Lock _gate = new();
    private IMediaReceiver? _receiver;
    private bool? _negotiatedAlpha;
    private long _outputLatencyOffsetNs;

    /// <summary>
    /// Raised when the publisher advertises whether its stream carries side-by-side alpha (over the signaling
    /// control channel, before the offer). The library applies it to its own receive pipeline automatically;
    /// a host that does its own GPU unpack downstream (e.g. the agent's Spout/Syphon publish sink) subscribes
    /// to this to adopt the value without needing a flag of its own.
    /// </summary>
    public event Action<bool>? AlphaNegotiated;

    /// <summary>
    /// The most recently advertised side-by-side-alpha value, or null if none has arrived yet. The publisher
    /// sends it on the ordered control channel before its offer, so a host wiring a downstream GPU sink can read
    /// this <i>immediately after subscribing to <see cref="AlphaNegotiated"/></i> to adopt a value that already
    /// arrived (the event is fire-and-forget and would otherwise be missed), closing the first-frame race where
    /// the sink commits its output-pool format before learning alpha.
    /// </summary>
    public bool? NegotiatedAlpha
    {
        get { lock (_gate) { return _negotiatedAlpha; } }
    }

    /// <summary>Create a subscriber over a room joined as <see cref="PeerRole.Subscriber"/>.</summary>
    /// <param name="options">The media transport options (codecs, ICE, playout).</param>
    /// <param name="transport">The wire transport that mints the receiver.</param>
    /// <param name="loggerFactory">The logger factory for the subscriber's own diagnostics.</param>
    /// <param name="room">The room, joined as a subscriber.</param>
    /// <param name="video">The video sink, or null for audio-only.</param>
    /// <param name="audio">The audio sink, or null for video-only.</param>
    public MediaSubscriber(
        MediaTransportOptions options,
        IMediaTransport transport,
        ILoggerFactory loggerFactory,
        IMediaRoom room,
        IVideoFrameSink? video = null,
        IAudioFrameSink? audio = null)
    {
        if (video is null && audio is null)
        {
            throw new ArgumentException("A subscriber needs at least a video or an audio sink.");
        }

        _options = options with { IceServers = options.IceServers.Count == 0 ? room.IceServers : options.IceServers };
        _transport = transport;
        _logger = loggerFactory.CreateLogger<MediaSubscriber>();
        _room = room;
        _videoSink = video;
        _audioSink = audio;
    }

    /// <summary>Start receiving: attach to the publisher (present now or when it joins).</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _room.PeerJoined += OnPeerJoined;
        _room.ControlReceived += OnControl;

        foreach (PeerInfo peer in _room.Peers)
        {
            if (peer.Role == PeerRole.Publisher)
            {
                await AttachAsync(peer.PeerId, cancellationToken).ConfigureAwait(false);
                break;
            }
        }
    }

    private void OnPeerJoined(PeerInfo peer)
    {
        if (peer.Role == PeerRole.Publisher)
        {
            _ = AttachAsync(peer.PeerId, CancellationToken.None);
        }
    }

    // The publisher advertises stream.alpha before its offer. Buffer it (the receiver may not exist yet) and
    // push it onto a live receiver, so the value is set before the negotiated stream delivers any frame.
    private void OnControl(PeerControlMessage message)
    {
        if (message.Topic != MediaControlTopics.Alpha)
        {
            return;
        }

        bool alpha = message.Payload == "1";
        IMediaReceiver? receiver;
        lock (_gate)
        {
            _negotiatedAlpha = alpha;
            receiver = _receiver;
        }

        LogAlphaNegotiated(alpha);
        receiver?.SetPreserveAlpha(alpha);
        AlphaNegotiated?.Invoke(alpha);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Attaching to publisher {PeerId}.")]
    private partial void LogAttaching(long peerId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Publisher negotiated side-by-side alpha = {Alpha}.")]
    private partial void LogAlphaNegotiated(bool alpha);

    /// <summary>
    /// Set the differential output-path latency (<c>Lv - La</c>) used to lip-sync at the output boundary rather
    /// than scheduler release (#14). Stored and applied to the receiver as soon as it exists; safe to call before
    /// or after attaching to a publisher. 0 (the default) keeps scheduler-aligned playout.
    /// </summary>
    public void SetOutputLatencyOffset(long differentialNs)
    {
        IMediaReceiver? receiver;
        lock (_gate)
        {
            _outputLatencyOffsetNs = differentialNs;
            receiver = _receiver;
        }

        receiver?.SetOutputLatencyOffset(differentialNs);
    }

    private async Task AttachAsync(PeerId publisher, CancellationToken cancellationToken)
    {
        IMediaReceiver receiver;
        long offsetNs;
        lock (_gate)
        {
            if (_receiver is not null)
            {
                return; // already attached to a publisher.
            }

            receiver = _transport.CreateReceiver(_options, _videoSink, _audioSink);
            _receiver = receiver;
            offsetNs = _outputLatencyOffsetNs;
        }

        if (offsetNs != 0)
        {
            receiver.SetOutputLatencyOffset(offsetNs);
        }

        LogAttaching(publisher.Value);

        // Wiring the receiver subscribes its handlers to the publisher's channel; any offer/ICE that
        // arrived first was buffered by the channel and flushes now.
        await receiver.StartAsync(_room.ChannelFor(publisher), cancellationToken).ConfigureAwait(false);

        // If the publisher's stream.alpha already arrived (control before attach), apply it now that the
        // receiver's video pipeline exists; later control messages flow through OnControl.
        bool? negotiated;
        lock (_gate)
        {
            negotiated = _negotiatedAlpha;
        }

        if (negotiated is { } alpha)
        {
            receiver.SetPreserveAlpha(alpha);
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        _room.PeerJoined -= OnPeerJoined;
        _room.ControlReceived -= OnControl;
        IMediaReceiver? receiver;
        lock (_gate)
        {
            receiver = _receiver;
            _receiver = null;
        }

        if (receiver is not null)
        {
            await receiver.DisposeAsync().ConfigureAwait(false);
        }
    }
}
