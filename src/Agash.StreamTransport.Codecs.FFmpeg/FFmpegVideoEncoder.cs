using System.Collections.Immutable;
using Agash.StreamTransport.Media;
using Microsoft.Extensions.Logging;
using FF = FFmpeg.Interop;

namespace Agash.StreamTransport.Codecs.FFmpeg;

// One FFmpeg encoder for every backend. It opens on the first frame, when the frame's storage and
// device are known, with the input adapter for that storage; later frames must come the same way. A
// rate change the encoder cannot take while running reopens it on the next frame with a keyframe, so
// congestion control reaches every backend.
internal sealed partial class FFmpegVideoEncoder : IVideoEncoder
{
    // Timestamps go through FFmpeg on the RTP video clock.
    private static readonly FF.Rational TimeBase = new(1, 90_000);

    private readonly BackendSpec _spec;
    private readonly FF.Codec _codec;
    private readonly VideoCodecId _codecId;
    private readonly FF.GpuAdapter? _adapter;
    private readonly ImmutableDictionary<string, string> _privateOptions;
    private readonly bool _forcesKeyframes;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly TimestampRing _timestamps = new();

    // GPU frames the encoder reads after Encode returns, kept until the packet made from each comes out.
    private readonly Queue<(long Pts, VideoFrameLease Frame)> _reading = new();
    private VideoEncoderConfiguration _configuration;

    // The rate the encoder works to, apart from the configuration: congestion control moves it every few
    // feedback reports, and a copied configuration each time would be garbage.
    private RateTarget _rate;

    // When the last rate change that cost a keyframe was made, as a timestamp of the encoder's clock.
    private long _keyframedRateChange = long.MinValue;

    // Since when the target has stood above the rate the encoder was opened at by enough to reopen.
    private long _risingSince = long.MinValue;

    // What the encoder sends, in bits per second: an exponentially weighted sum of the bits it emitted,
    // with a time constant of OutputWindow, which a steady rate settles to.
    private double _outputBits;
    private long _outputAt = long.MinValue;
    private static readonly TimeSpan OutputWindow = TimeSpan.FromSeconds(1);
    private Session? _session;

    // Why the encoder reopens on the next frame (rate, frame_rate); null when it does not.
    private string? _reopen;
    private readonly FFmpegCodecMetrics? _metrics;
    private readonly TimeProvider _time;
    private bool _keyframePending;
    private long _lastPts = long.MinValue;
    private bool _disposed;

    // A rate the encoder takes only when opened is applied by opening the next encoder alongside the
    // running one, on a worker, and swapping it in at the next frame once it is ready: the running encoder
    // keeps encoding meanwhile, so a rate change costs a keyframe and no stall.
    private Prepared? _prepared;
    private bool _preparing;
    private string? _preparedAgain;
    private readonly ManualResetEventSlim _prepareOpened = new(initialState: true);

    public FFmpegVideoEncoder(
        BackendSpec spec,
        FF.Codec codec,
        VideoEncoderConfiguration configuration,
        VideoEncoderInfo info,
        FF.GpuAdapter? adapter,
        ImmutableDictionary<string, string> privateOptions,
        bool forcesKeyframes,
        ILogger logger,
        FFmpegCodecMetrics? metrics = null,
        TimeProvider? timeProvider = null
    )
    {
        _forcesKeyframes = forcesKeyframes;
        _metrics = metrics;
        _time = timeProvider ?? TimeProvider.System;
        _spec = spec;
        _codec = codec;
        _codecId = configuration.Format.Codec;
        _configuration = configuration;
        _rate = configuration.Rate;
        _adapter = adapter;
        _privateOptions = privateOptions;
        _logger = logger;
        Info = info;
    }

    public VideoEncoderInfo Info { get; }

    public void Encode(
        in VideoFrame frame,
        in EncodeRequest request,
        IEncodedVideoConsumer consumer
    )
    {
        ArgumentNullException.ThrowIfNull(consumer);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!Info.Input.Accepts(frame.Storage, frame.Format.PixelFormat))
            {
                throw new ArgumentException(
                    $"{Info.ImplementationName} does not take {frame.Storage.Kind} {frame.Format.PixelFormat} frames.",
                    nameof(frame)
                );
            }

            VideoSize visible = new(
                frame.Format.VisibleRect.Width,
                frame.Format.VisibleRect.Height
            );
            if (visible != _configuration.Size)
            {
                throw new ArgumentException(
                    $"The frame is {visible.Width}x{visible.Height}; the encoder was configured for {_configuration.Size.Width}x{_configuration.Size.Height}.",
                    nameof(frame)
                );
            }

            // An encoder that ignores a forced picture type starts a new one for a keyframe: a new
            // encoder's first frame is always an IDR.
            bool keyframeByReopen =
                !_forcesKeyframes && request.Keyframe && _session is { FramesSent: > 0 };
            if (_session is { } open)
            {
                if (open.Storage != frame.Storage.Kind)
                {
                    // Another storage needs another input: everything is opened again.
                    Drain(open, consumer);
                    CloseSession(open);
                    _session = Open(in frame, "storage");
                }
                else if (_prepared is { } prepared)
                {
                    _prepared = null;
                    Drain(open, consumer);
                    open.Replace(prepared.Encoder, prepared.BitsPerSecond);
                    _keyframePending = true;
                }
                else if (_reopen is not null || keyframeByReopen)
                {
                    // A keyframe now, or a prepared encoder that failed to open: the input stays and
                    // the encoder is opened here.
                    string reason = _reopen ?? "keyframe";
                    Drain(open, consumer);
                    long started = _time.GetTimestamp();
                    RateTarget rate = _rate;
                    open.Replace(CreateEncoder(open.Input, rate, open.Color), rate.BitsPerSecond);
                    Opened(reason, open.Storage, rate, started);
                    _keyframePending = true;
                }
            }

            Session session = _session ??= Open(in frame, "start");
            _reopen = null;

            long pts = ToPts(frame.Timestamp.Time);
            if (pts <= _lastPts)
            {
                pts = _lastPts + 1;
            }

            // A GPU frame is read after this call returns, so the encoder reads the frame it keeps: the
            // producer's own buffer where the producer can lend it, a copy only where the producer must
            // overwrite it in place.
            session.Frame.Reset();
            if (frame.Storage.Kind == VideoStorageKind.Cpu)
            {
                session.Input.Prepare(in frame, pts, session.Frame);
            }
            else
            {
                VideoFrameLease kept = frame.Retain();
                try
                {
                    VideoFrame read = kept.Frame;
                    session.Input.Prepare(in read, pts, session.Frame);
                }
                catch
                {
                    kept.Dispose();
                    throw;
                }

                _reading.Enqueue((pts, kept));
            }

            _lastPts = pts;
            _timestamps.Add(pts, frame.Timestamp);
            session.Frame.PresentationTimestamp = pts;
            session.Frame.TimeBase = TimeBase;
            session.Frame.PictureType =
                request.Keyframe || _keyframePending ? FF.PictureType.I : FF.PictureType.None;
            _keyframePending = false;
            session.FramesSent++;

            foreach (FF.Packet packet in session.Encoder.Encode(session.Frame, session.Packet))
            {
                Emit(packet, consumer);
            }
        }
    }

    public void Reconfigure(in RateTarget target)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(target.BitsPerSecond);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            double openedFrames = _rate.FramesPerSecond;
            _rate = target;
            if (_session is not { } session)
            {
                return;
            }

            if (target.BitsPerSecond > session.BitsPerSecond * 1.3)
            {
                _risingSince = _risingSince == long.MinValue ? _time.GetTimestamp() : _risingSince;
            }
            else
            {
                _risingSince = long.MinValue;
            }

            // Frame rate is set when an encoder opens; a source that changed pace reopens it.
            if (
                target.FramesPerSecond < openedFrames * 0.8
                || target.FramesPerSecond > openedFrames * 1.2
            )
            {
                LogRateReopen(_logger, Info.ImplementationName, target.BitsPerSecond);
                Prepare(session, "frame_rate");
            }
            // A change that costs a keyframe waits, as a reopen does, until the target has moved well away.
            else if (
                session.Encoder.SupportsRateControlChanges
                && (
                    Info.RateChange == RateChange.InPlace
                    || WorthReopening(
                        session.BitsPerSecond,
                        target.BitsPerSecond,
                        SinceKeyframedRateChange,
                        RisingFor,
                        OutputBitsPerSecond,
                        target.FramesPerSecond
                    )
                )
            )
            {
                if (Info.RateChange != RateChange.InPlace)
                {
                    _keyframedRateChange = _time.GetTimestamp();
                }

                session.Encoder.SetRateControl(
                    target.BitsPerSecond,
                    target.BitsPerSecond,
                    BufferSize(target, _configuration.Tuning)
                );
                session.Retargeted(target.BitsPerSecond);
                LogRateChanged(_logger, Info.ImplementationName, target.BitsPerSecond);
            }
            else if (
                WorthReopening(
                    session.BitsPerSecond,
                    target.BitsPerSecond,
                    SinceKeyframedRateChange,
                    RisingFor,
                    OutputBitsPerSecond,
                    target.FramesPerSecond
                )
            )
            {
                _keyframedRateChange = _time.GetTimestamp();
                _risingSince = long.MinValue;
                LogRateReopen(_logger, Info.ImplementationName, target.BitsPerSecond);
                Prepare(session, "rate");
            }
        }
    }

    // A reopen costs a keyframe and the encoder's start-up, and the congestion controller moves the
    // target a few percent at a time; an encoder that applies its rate only when opened reopens when the
    // target has moved well away from it. A fall is followed sooner than a rise: sending above a lowered
    // target is what fills the bottleneck queue. Shortly after a change that cost a keyframe, the target
    // is still pulled down by that keyframe (draft-ietf-ccwg-rfc8298bis-screamv2-01 section 4.4): it
    // fills the sender queue, whose delay the rate adjustment integrates, and it is far above the nominal
    // frame size, which frame_size_dev tracks. Following that fall would open the next keyframe, so only
    // a collapse reopens until the keyframe's effect has passed. A rise is followed only once it has
    // lasted as long: the target swings back up as each keyframe's effect fades, and a reopen for each
    // swing is a keyframe for each. And a rate binds only when the encoder sends close to it: one
    // sending far less (a still picture, a static screen) is held back by neither rate, and then the
    // target follows the round trip alone (ref_wnd / s_rtt with the window application-limited), so a
    // reopen would buy nothing but a keyframe.
    internal static bool WorthReopening(
        long opened,
        long target,
        TimeSpan sinceKeyframedChange,
        TimeSpan risingFor,
        double output,
        double framesPerSecond
    )
    {
        TimeSpan settle = KeyframeSettle(framesPerSecond);
        bool aboveTarget = output > target * 0.85;
        bool heldBack = output > opened * 0.7;
        return sinceKeyframedChange < settle
            ? target < opened * 0.5 && aboveTarget
            : (target < opened * 0.85 && aboveTarget)
                || (target > opened * 1.3 && risingFor >= settle && heldBack);
    }

    // frame_size_dev decays by 1 - FRAME_SIZE_DEV_ALPHA (1/64) a frame: from its 0.2 cap, which a
    // keyframe reaches, to a tenth of that takes ln(0.1) / ln(63/64), about 146 frames. The rate
    // adjustment recovers sooner, within a few dozen frames.
    internal static TimeSpan KeyframeSettle(double framesPerSecond) =>
        TimeSpan.FromSeconds(Math.Max(2, 150 / Math.Max(1, framesPerSecond)));

    private double OutputBitsPerSecond => Decayed(_time.GetTimestamp()) / OutputWindow.TotalSeconds;

    private double Decayed(long now) =>
        _outputAt == long.MinValue
            ? 0
            : _outputBits
                * Math.Exp(
                    -_time.GetElapsedTime(_outputAt, now).TotalSeconds / OutputWindow.TotalSeconds
                );

    private TimeSpan RisingFor =>
        _risingSince == long.MinValue ? TimeSpan.Zero : _time.GetElapsedTime(_risingSince);

    private TimeSpan SinceKeyframedRateChange =>
        _keyframedRateChange == long.MinValue
            ? TimeSpan.MaxValue
            : _time.GetElapsedTime(_keyframedRateChange);

    public void Flush(IEncodedVideoConsumer consumer)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_session is { } session)
            {
                // An encoder that has seen end of stream takes no more frames; the next frame opens a
                // new one.
                Drain(session, consumer);
                CloseSession(session);
                _session = null;
                _keyframePending = true;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_session is { } session)
            {
                CloseSession(session);
            }

            _session = null;
            ReleaseRead(long.MaxValue);
            _prepareOpened.Dispose();
        }
    }

    // The encoder input the backend declares for the frame's storage.
    private EncoderInput GpuInputFor(in VideoFrame frame, PixelFormat format, VideoSize size) =>
        _spec.InputFor(frame.Storage.Kind) switch
        {
            GpuInput.D3D12Texture
                when OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)
                    && frame.Storage.TryGetValue(out D3D12Image d3d12) => new D3D12Input(
                d3d12,
                format,
                size
            ),
            GpuInput.D3D11Texture
                when OperatingSystem.IsWindowsVersionAtLeast(6, 1)
                    && frame.Storage.TryGetValue(out D3D11Image d3d11) => new D3D11Input(
                d3d11,
                format,
                size
            ),
            GpuInput.VulkanImport when OperatingSystem.IsLinux() => new VulkanDmaBufInput(
                CreateDevice(),
                format,
                size,
                modifier => LogDmaBufCopied(_logger, Info.ImplementationName, modifier)
            ),
            GpuInput.VaapiMap when OperatingSystem.IsLinux() => new DmaBufInput(
                CreateDevice(),
                format,
                size
            ),
            GpuInput.IOSurface when OperatingSystem.IsMacOS() => new IOSurfaceInput(
                CreateDevice(),
                format,
                size
            ),
            _ => throw new NotSupportedException(
                $"{_spec.Backend} does not take {frame.Storage.Kind} frames on this platform."
            ),
        };

    private Session Open(in VideoFrame frame, string reason)
    {
        long started = _time.GetTimestamp();
        VideoSize size = _configuration.Size;
        PixelFormat format = frame.Format.PixelFormat;
        EncoderInput input =
            frame.Storage.Kind == VideoStorageKind.Cpu
                ? CpuInput(format, size)
                : GpuInputFor(in frame, format, size);

        try
        {
            RateTarget rate = _rate;
            // The colour the stream signals: the configuration's, else the first frame's, else BT.709
            // in video range, which HD capture produces and every WebRTC peer assumes.
            // The coded picture is Y'CbCr 4:2:0 whatever the input, so an RGB input's colour is signalled
            // as what the encoder converted it to.
            VideoColor color = (
                _configuration.Color != default ? _configuration.Color
                : frame.Color != default ? frame.Color
                : VideoColor.Bt709
            ).ConvertedTo(PixelFormat.I420);
            FF.Encoder encoder = CreateEncoder(input, rate, color);
            Opened(reason, frame.Storage.Kind, rate, started);
            return new Session(encoder, input, frame.Storage.Kind, rate.BitsPerSecond, color);
        }
        catch
        {
            input.Dispose();
            throw;
        }
    }

    // A codec context on an input, at a rate. It holds no state of this instance's, so a worker opens one
    // while another encodes.
    private FF.Encoder CreateEncoder(EncoderInput input, RateTarget rate, VideoColor color)
    {
        VideoSize size = _configuration.Size;
        return FF.Encoder.Create(
            _codec,
            new FF.VideoEncoderOptions
            {
                Width = size.Width,
                Height = size.Height,
                PixelFormat = input.ContextFormat,
                TimeBase = TimeBase,
                FrameRate = FrameRate(rate.FramesPerSecond),
                GopSize = EncoderSettings.GopSize(
                    _configuration.KeyframeInterval,
                    _rate.FramesPerSecond
                ),
                MaxBFrames = 0,
                BitRate = rate.BitsPerSecond,
                MaxRate = rate.BitsPerSecond,
                BufferSize = BufferSize(rate, _configuration.Tuning),
                LowDelay =
                    _configuration.Alpha != AlphaLayout.Layer
                    || EncoderSettings.LowDelayWithAlphaLayer(_codec.Name),
                HardwareFrames = input.Pool,
                HardwareDevice = input.Pool is null ? input.Device : null,
                // The range describes the samples the encoder is handed: RGB is full range, and
                // the encoder converts it to the coded range itself (VideoToolbox has no BGRA in
                // any other range).
                ColorRange =
                    Formats.FromFFmpeg(input.ContextFormat) is PixelFormat.Bgra or PixelFormat.Rgba
                        ? Formats.ToFFmpeg(ColorRange.Full)
                    : color.Range == ColorRange.Unspecified ? null
                    : Formats.ToFFmpeg(color.Range),
                ColorPrimaries =
                    color.Primaries == ColorPrimaries.Unspecified
                        ? null
                        : Formats.ToFFmpeg(color.Primaries),
                ColorTransfer =
                    color.Transfer == TransferFunction.Unspecified
                        ? null
                        : Formats.ToFFmpeg(color.Transfer),
                ColorSpace =
                    color.Matrix == ColorMatrix.Unspecified ? null : Formats.ToFFmpeg(color.Matrix),
                CodecOptions = _privateOptions,
            }
        );
    }

    private void Opened(string reason, VideoStorageKind storage, RateTarget rate, long started)
    {
        LogOpened(
            _logger,
            Info.ImplementationName,
            storage,
            _configuration.Size.Width,
            _configuration.Size.Height,
            rate.BitsPerSecond,
            EncoderSettings.Describe(_privateOptions)
        );
        if (_metrics is { } metrics)
        {
            metrics.EncoderOpens.Add(
                1,
                FFmpegCodecMetrics.Codec(_codec.Name),
                FFmpegCodecMetrics.Reason(reason)
            );
            metrics.EncoderOpenDuration.Record(
                _time.GetElapsedTime(started).TotalSeconds,
                FFmpegCodecMetrics.Codec(_codec.Name)
            );
        }
    }

    // Opens the next encoder at the configured rate on a worker. One preparation runs at a time; a target
    // that moves meanwhile is prepared once that one is done, from the newest target.
    private void Prepare(Session session, string reason)
    {
        if (_preparing)
        {
            _preparedAgain = reason;
            return;
        }

        _preparing = true;
        _prepareOpened.Reset();
        EncoderInput input = session.Input;
        VideoColor color = session.Color;
        RateTarget rate = _rate;
        _ = ThreadPool.UnsafeQueueUserWorkItem(
            _ =>
            {
                long started = _time.GetTimestamp();
                FF.Encoder? encoder = null;
                Exception? failure = null;
                try
                {
                    encoder = CreateEncoder(input, rate, color);
                }
                catch (Exception ex)
                    when (ex
                            is FF.FFmpegException
                                or NotSupportedException
                                or InvalidOperationException
                                or ArgumentException
                    )
                {
                    failure = ex;
                }
                finally
                {
                    // Before the lock: a session closing under it waits for this, not for the lock.
                    _prepareOpened.Set();
                }

                Finished(input, reason, rate, started, encoder, failure);
            },
            null
        );
    }

    private void Finished(
        EncoderInput input,
        string reason,
        RateTarget rate,
        long started,
        FF.Encoder? encoder,
        Exception? failure
    )
    {
        lock (_gate)
        {
            _preparing = false;
            if (_disposed || _session is not { } session || session.Input != input)
            {
                encoder?.Dispose();
                return;
            }

            if (encoder is null)
            {
                // Opened on the next frame instead, where a failure reaches the caller.
                LogPrepareFailed(_logger, failure, Info.ImplementationName, rate.BitsPerSecond);
                _reopen = reason;
                return;
            }

            Opened(reason, session.Storage, rate, started);
            _prepared?.Encoder.Dispose();
            _prepared = new Prepared(encoder, rate.BitsPerSecond);
            if (_preparedAgain is { } again)
            {
                _preparedAgain = null;
                if (
                    WorthReopening(
                        rate.BitsPerSecond,
                        _rate.BitsPerSecond,
                        SinceKeyframedRateChange,
                        RisingFor,
                        OutputBitsPerSecond,
                        _rate.FramesPerSecond
                    )
                )
                {
                    Prepare(session, again);
                }
            }
        }
    }

    // Closes a session once no worker is opening an encoder on its input.
    private void CloseSession(Session session)
    {
        _prepareOpened.Wait();
        _prepared?.Encoder.Dispose();
        _prepared = null;
        _preparedAgain = null;
        _reopen = null;
        session.Dispose();
    }

    // System memory goes straight in when the encoder takes it, uploaded into the device's surfaces
    // when it takes only surfaces; a hardware encoder that takes system memory still runs on the GPU
    // the factory chose.
    private EncoderInput CpuInput(PixelFormat format, VideoSize size)
    {
        FF.PixelFormat software = Formats.ToFFmpeg(format);
        if (!Takes(_codec, software) && _spec.DeviceType is not null)
        {
            return new UploadInput(CreateDevice(), format, size);
        }

        return _spec.DeviceType is null
            ? new SystemMemoryInput(format)
            : new SystemMemoryInput(format, CreateDevice());
    }

    internal static bool Takes(FF.Codec codec, FF.PixelFormat format)
    {
        ReadOnlySpan<FF.PixelFormat> formats = codec.PixelFormats;
        if (formats.IsEmpty)
        {
            return true;
        }

        foreach (FF.PixelFormat candidate in formats)
        {
            if (
                candidate == format
                || (format == FF.PixelFormat.Bgra && candidate == Formats.Bgr0)
                || (format == FF.PixelFormat.Rgba && candidate == Formats.Rgb0)
            )
            {
                return true;
            }
        }

        return false;
    }

    private FF.HardwareDevice CreateDevice() =>
        _spec.DeviceType is { } type
            ? SharedDevices.Open(type, _adapter)
            : throw new InvalidOperationException($"{_spec.Backend} encoders run on no device.");

    private void Drain(Session session, IEncodedVideoConsumer consumer)
    {
        foreach (FF.Packet packet in session.Encoder.Encode(null, session.Packet))
        {
            Emit(packet, consumer);
        }

        ReleaseRead(long.MaxValue);
    }

    // Releases the GPU frames of every packet up to and including a pts: the encoder has read them.
    private void ReleaseRead(long pts)
    {
        _session?.Input.Read(pts);
        while (_reading.TryPeek(out (long Pts, VideoFrameLease Frame) oldest) && oldest.Pts <= pts)
        {
            _ = _reading.Dequeue();
            oldest.Frame.Dispose();
        }
    }

    private void Emit(FF.Packet packet, IEncodedVideoConsumer consumer)
    {
        long pts = packet.PresentationTimestamp ?? _lastPts;
        MediaTimestamp timestamp = _timestamps.Take(pts);
        long now = _time.GetTimestamp();
        _outputBits = Decayed(now) + (packet.Data.Length * 8.0);
        _outputAt = now;
        consumer.OnEncoded(
            new EncodedVideoFrame(packet.Data, _codecId, packet.IsKeyFrame, timestamp)
        );
        ReleaseRead(pts);
    }

    private static long ToPts(MediaTime time) =>
        (long)((Int128)time.Nanoseconds * TimeBase.Denominator / 1_000_000_000);

    private static FF.Rational FrameRate(double framesPerSecond) =>
        framesPerSecond <= 0 ? new FF.Rational(30, 1)
        : Math.Abs(framesPerSecond - Math.Round(framesPerSecond)) < 1e-6
            ? new FF.Rational((int)Math.Round(framesPerSecond), 1)
        : new FF.Rational((int)Math.Round(framesPerSecond * 1000), 1000);

    private static int BufferSize(RateTarget rate, EncodeTuning tuning) =>
        (int)Math.Min(int.MaxValue, rate.BitsPerSecond * EncoderSettings.BufferSeconds(tuning));

    // The encoder and what feeds it, replaced together when the encoder reopens.
    // An encoder opened alongside the running one, at the rate it holds.
    private sealed record Prepared(FF.Encoder Encoder, long BitsPerSecond);

    private sealed class Session(
        FF.Encoder encoder,
        EncoderInput input,
        VideoStorageKind storage,
        long bitsPerSecond,
        VideoColor color
    ) : IDisposable
    {
        public FF.Encoder Encoder { get; private set; } = encoder;

        // The rate the encoder holds: the one it was opened at, or last took in place.
        public long BitsPerSecond { get; private set; } = bitsPerSecond;

        // The colour the stream signals, kept across reopens.
        public VideoColor Color { get; } = color;

        // The rate the running encoder took in place.
        public void Retargeted(long bitsPerSecond) => BitsPerSecond = bitsPerSecond;

        // The next encoder on the same input, after the running one has been drained. The drained one is
        // closed on a worker: closing waits for its GPU work, and it holds its own references to the
        // input's device and frames.
        public void Replace(FF.Encoder encoder, long bitsPerSecond)
        {
            _ = ThreadPool.UnsafeQueueUserWorkItem(
                static drained => drained.Dispose(),
                Encoder,
                false
            );
            Encoder = encoder;
            BitsPerSecond = bitsPerSecond;
            FramesSent = 0;
        }

        public EncoderInput Input { get; } = input;

        public VideoStorageKind Storage { get; } = storage;

        public FF.Frame Frame { get; } = new();

        public FF.Packet Packet { get; } = new();

        public int FramesSent { get; set; }

        public void Dispose()
        {
            Encoder.Dispose();
            Frame.Dispose();
            Packet.Dispose();
            Input.Dispose();
        }
    }

    // Capture timestamps by the pts they went into FFmpeg with, for the packets that come out. Output
    // is in input order without B-frames, so the ring is short.
    private sealed class TimestampRing
    {
        private readonly (long Pts, MediaTimestamp Timestamp)[] _entries = new (
            long,
            MediaTimestamp
        )[64];
        private int _next;

        public void Add(long pts, MediaTimestamp timestamp)
        {
            _entries[_next] = (pts, timestamp);
            _next = (_next + 1) % _entries.Length;
        }

        public MediaTimestamp Take(long pts)
        {
            foreach ((long entryPts, MediaTimestamp timestamp) in _entries)
            {
                if (entryPts == pts)
                {
                    return timestamp;
                }
            }

            return MediaTimestamp.Observed(
                new MediaTime(pts * 1_000_000_000 / TimeBase.Denominator)
            );
        }
    }

    [LoggerMessage(
        EventId = 1000,
        Level = LogLevel.Information,
        Message = "Opened {Encoder} for {Storage} input at {Width}x{Height}, {BitsPerSecond} b/s ({Options})"
    )]
    private static partial void LogOpened(
        ILogger logger,
        string encoder,
        VideoStorageKind storage,
        int width,
        int height,
        long bitsPerSecond,
        string options
    );

    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Debug,
        Message = "{Encoder} retargeted to {BitsPerSecond} b/s while running"
    )]
    private static partial void LogRateChanged(ILogger logger, string encoder, long bitsPerSecond);

    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Debug,
        Message = "{Encoder} cannot change rate while running; reopening at {BitsPerSecond} b/s on the next frame"
    )]
    private static partial void LogRateReopen(ILogger logger, string encoder, long bitsPerSecond);

    [LoggerMessage(
        EventId = 1004,
        Level = LogLevel.Warning,
        Message = "{Encoder} did not open at {BitsPerSecond} b/s alongside the running encoder; it reopens on the next frame"
    )]
    private static partial void LogPrepareFailed(
        ILogger logger,
        Exception? exception,
        string encoder,
        long bitsPerSecond
    );

    [LoggerMessage(
        EventId = 1003,
        Level = LogLevel.Information,
        Message = "{Encoder} cannot read DMA-BUFs with modifier 0x{Modifier:x16} in place on this GPU; each picture is copied on the GPU"
    )]
    internal static partial void LogDmaBufCopied(ILogger logger, string encoder, ulong modifier);
}
