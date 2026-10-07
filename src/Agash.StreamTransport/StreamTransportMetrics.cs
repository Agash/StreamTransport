using System.Diagnostics;
using System.Diagnostics.Metrics;
using Agash.StreamTransport.Media;

namespace Agash.StreamTransport;

// The library's instruments on the meter named StreamTransportDiagnostics.MeterName. Made from the
// host's IMeterFactory when there is one, so the meter belongs to the container that made it and a test
// reads its own; a meter of its own otherwise. Tags are low-cardinality: codec, implementation, role,
// outcome, reason; never a session, peer or address.
internal sealed class StreamTransportMetrics : IDisposable
{
    private readonly Meter _meter;
    private readonly bool _ownsMeter;

    public StreamTransportMetrics(IMeterFactory? meterFactory)
    {
        _ownsMeter = meterFactory is null;
        _meter =
            meterFactory?.Create(StreamTransportDiagnostics.MeterName)
            ?? new Meter(StreamTransportDiagnostics.MeterName);

        SessionsActive = _meter.CreateUpDownCounter<long>(
            "streamtransport.sessions.active",
            "{session}",
            "Media sessions started and not yet disposed, by role."
        );
        SessionConnectDuration = _meter.CreateHistogram<double>(
            "streamtransport.session.connect.duration",
            "s",
            "From starting a session to media flowing, or to its failure, by role and outcome."
        );
        VideoFramesSent = _meter.CreateCounter<long>(
            "streamtransport.video.frames.sent",
            "{frame}",
            "Encoded video frames handed to the link, by codec."
        );
        VideoFramesDropped = _meter.CreateCounter<long>(
            "streamtransport.video.frames.dropped",
            "{frame}",
            "Source video frames not encoded, by codec and reason."
        );
        VideoEncodeDuration = _meter.CreateHistogram<double>(
            "streamtransport.video.encode.duration",
            "s",
            "Time to encode one video frame, by codec and encoder.",
            advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = DurationBuckets }
        );
        VideoEncoderFailures = _meter.CreateCounter<long>(
            "streamtransport.video.encoder.failures",
            "{failure}",
            "Encoders that failed before producing a frame and were passed over, by codec and encoder."
        );
        VideoTargetBitrate = _meter.CreateGauge<long>(
            "streamtransport.video.target_bitrate",
            "bit/s",
            "The latest rate congestion control asked a video encoder for, by codec."
        );
        VideoFramesDecoded = _meter.CreateCounter<long>(
            "streamtransport.video.frames.decoded",
            "{frame}",
            "Received video frames decoded, by codec."
        );
        VideoFramesFailed = _meter.CreateCounter<long>(
            "streamtransport.video.frames.failed",
            "{frame}",
            "Received video frames the decoder rejected, by codec."
        );
        VideoFramesSkipped = _meter.CreateCounter<long>(
            "streamtransport.video.frames.skipped",
            "{frame}",
            "Received video frames dropped undecoded because decoding fell behind, by codec."
        );
        VideoDecodeDuration = _meter.CreateHistogram<double>(
            "streamtransport.video.decode.duration",
            "s",
            "Time to decode one video frame, by codec.",
            advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = DurationBuckets }
        );
        KeyframeRequests = _meter.CreateCounter<long>(
            "streamtransport.video.keyframe_requests",
            "{request}",
            "Keyframe requests, by direction: sent to the peer or received from it."
        );
        AudioFramesSent = _meter.CreateCounter<long>(
            "streamtransport.audio.frames.sent",
            "{frame}",
            "Encoded audio frames handed to the link, by codec."
        );
        AudioFramesDecoded = _meter.CreateCounter<long>(
            "streamtransport.audio.frames.decoded",
            "{frame}",
            "Received audio frames decoded, by codec."
        );
        AudioFramesRepaired = _meter.CreateCounter<long>(
            "streamtransport.audio.frames.repaired",
            "{frame}",
            "Lost audio frames, by how they were repaired: concealed or recovered from redundancy."
        );
        PlayoutDelay = _meter.CreateHistogram<double>(
            "streamtransport.playout.delay",
            "s",
            "The synced playout buffer depth each time it is set."
        );
        AvSyncOffset = _meter.CreateHistogram<double>(
            "streamtransport.playout.av_offset",
            "s",
            "Lip sync at presentation: the capture instant of the video frame shown less that of the audio heard with it; positive when audio lags video."
        );
    }

    // Encode and decode take from a fraction of a millisecond (GPU) to tens (software 4K).
    private static readonly double[] DurationBuckets =
    [
        0.0005,
        0.001,
        0.002,
        0.004,
        0.008,
        0.016,
        0.033,
        0.066,
        0.133,
        0.25,
        0.5,
    ];

    public UpDownCounter<long> SessionsActive { get; }

    public Histogram<double> SessionConnectDuration { get; }

    public Counter<long> VideoFramesSent { get; }

    public Counter<long> VideoFramesDropped { get; }

    public Histogram<double> VideoEncodeDuration { get; }

    public Counter<long> VideoEncoderFailures { get; }

    public Gauge<long> VideoTargetBitrate { get; }

    public Counter<long> VideoFramesDecoded { get; }

    public Counter<long> VideoFramesFailed { get; }

    public Counter<long> VideoFramesSkipped { get; }

    public Histogram<double> VideoDecodeDuration { get; }

    public Counter<long> KeyframeRequests { get; }

    public Counter<long> AudioFramesSent { get; }

    public Counter<long> AudioFramesDecoded { get; }

    public Counter<long> AudioFramesRepaired { get; }

    public Histogram<double> PlayoutDelay { get; }

    public Histogram<double> AvSyncOffset { get; }

    public static KeyValuePair<string, object?> Codec(VideoCodecId codec) =>
        new("streamtransport.codec", codec.Name);

    public static KeyValuePair<string, object?> Codec(AudioCodecId codec) =>
        new("streamtransport.codec", codec.Name);

    public static KeyValuePair<string, object?> Implementation(string name) =>
        new("streamtransport.implementation", name);

    public static KeyValuePair<string, object?> Role(MediaSessionRole role) =>
        new(
            "streamtransport.session.role",
            role == MediaSessionRole.Offerer ? "offerer" : "answerer"
        );

    public static KeyValuePair<string, object?> Reason(string reason) =>
        new("streamtransport.reason", reason);

    public static KeyValuePair<string, object?> Outcome(string outcome) =>
        new("streamtransport.outcome", outcome);

    public static KeyValuePair<string, object?> Direction(string direction) =>
        new("streamtransport.direction", direction);

    public void Dispose()
    {
        if (_ownsMeter)
        {
            _meter.Dispose();
        }
    }
}
