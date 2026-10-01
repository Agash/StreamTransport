using System.Collections.Concurrent;
using System.Collections.Immutable;
using Agash.StreamTransport.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using FF = FFmpeg.Interop;

namespace Agash.StreamTransport.Codecs.FFmpeg;

/// <summary>
/// Makes FFmpeg video encoders of one <see cref="EncoderBackend"/>. Whether the backend works on a GPU
/// is found out by opening a small encoder and encoding a frame through it, once per encoder and GPU;
/// a backend whose encoder is in the FFmpeg build but whose hardware, driver or codec support is
/// missing reports no capabilities.
/// </summary>
public sealed partial class FFmpegVideoEncoderFactory : IVideoEncoderFactory
{
    private static readonly ConcurrentDictionary<
        (string Encoder, string Adapter),
        Probe?
    > s_probes = new();

    // The Media formats a probe tries, most preferred first.
    private static readonly ImmutableArray<PixelFormat> s_candidates =
    [
        PixelFormat.Nv12,
        PixelFormat.I420,
        PixelFormat.P010,
        PixelFormat.Bgra,
        PixelFormat.Rgba,
    ];

    private readonly BackendSpec _spec;
    private readonly FFmpegCodecOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;

    /// <summary>A factory for one backend.</summary>
    /// <param name="backend">The backend.</param>
    /// <param name="options">Shared options; defaults when null.</param>
    public FFmpegVideoEncoderFactory(EncoderBackend backend, FFmpegCodecOptions? options = null)
    {
        _spec = BackendCatalog.Get(backend);
        _options = options ?? new FFmpegCodecOptions();
        _loggerFactory = _options.LoggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<FFmpegVideoEncoderFactory>();
    }

    /// <summary>The backend.</summary>
    public EncoderBackend Backend => _spec.Backend;

    /// <inheritdoc/>
    public int Rank => _spec.Rank;

    /// <summary>Whether the backend runs on a GPU or media engine.</summary>
    public bool IsHardwareAccelerated => _spec.IsHardware;

    /// <inheritdoc/>
    /// <remarks>The codecs whose encoder is in the loaded FFmpeg; whether the hardware can run them is
    /// <see cref="QueryCapabilities"/>'s question.</remarks>
    public ImmutableArray<VideoCodecFormat> SupportedFormats
    {
        get
        {
            if (Rank == 0)
            {
                return [];
            }

            ImmutableArray<VideoCodecFormat>.Builder formats =
                ImmutableArray.CreateBuilder<VideoCodecFormat>();
            foreach ((VideoCodecId codec, string encoder) in _spec.Encoders)
            {
                if (FF.Codec.TryFindEncoder(encoder, out _))
                {
                    formats.Add(new VideoCodecFormat(codec));
                }
            }

            return formats.DrainToImmutable();
        }
    }

    /// <summary>A factory for every backend this platform has, most preferred first.</summary>
    /// <param name="options">Shared options; defaults when null.</param>
    /// <returns>The factories.</returns>
    public static ImmutableArray<FFmpegVideoEncoderFactory> CreateAll(
        FFmpegCodecOptions? options = null
    ) =>
        [
            .. BackendCatalog
                .All.Where(static spec => spec.Rank > 0)
                .OrderByDescending(static spec => spec.Rank)
                .Select(spec => new FFmpegVideoEncoderFactory(spec.Backend, options)),
        ];

    /// <inheritdoc/>
    public VideoEncoderInfo? QueryCapabilities(VideoCodecFormat format, GpuIdentity? device)
    {
        ArgumentNullException.ThrowIfNull(format);
        return Resolve(format, device) is { } resolved ? resolved.Info : null;
    }

    /// <inheritdoc/>
    /// <exception cref="NotSupportedException">The backend cannot encode the format on that GPU.</exception>
    public IVideoEncoder Create(VideoEncoderConfiguration configuration, GpuIdentity? device)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        Resolved resolved =
            Resolve(configuration.Format, device)
            ?? throw new NotSupportedException(
                $"{_spec.Backend} cannot encode {configuration.Format.Codec} on this machine{(device is null ? "" : " on that GPU")}."
            );
        return new FFmpegVideoEncoder(
            _spec,
            resolved.Codec,
            configuration,
            resolved.Info,
            resolved.Adapter,
            PrivateOptions(resolved.Codec.Name, configuration.Tuning),
            resolved.ForcesKeyframes,
            _loggerFactory.CreateLogger<FFmpegVideoEncoder>()
        );
    }

    private Resolved? Resolve(VideoCodecFormat format, GpuIdentity? device)
    {
        if (Rank == 0 || _spec.EncoderFor(format.Codec) is not { } name)
        {
            return null;
        }

        if (!FF.Codec.TryFindEncoder(name, out FF.Codec codec))
        {
            return null;
        }

        FF.GpuAdapter? adapter = null;
        if (_spec.DeviceType is not null)
        {
            adapter = Adapters.For(_spec.Backend, device);
            if (adapter is null && !OperatingSystem.IsMacOS())
            {
                return null;
            }
        }

        string adapterKey = adapter?.ToString() ?? "default";
        Probe? probe = s_probes.GetOrAdd(
            (name, adapterKey),
            static (_, state) => state.Self.RunProbe(state.Codec, state.Id, state.Adapter),
            (Self: this, Codec: codec, Id: format.Codec, Adapter: adapter)
        );
        if (probe is null)
        {
            return null;
        }

        GpuIdentity? identity = adapter is null ? null : Adapters.IdentityOf(adapter);
        ImmutableArray<VideoStorageKind> storages =
        [
            VideoStorageKind.Cpu,
            .. _spec.GpuStorages.Where(static s => SupportedHere(s)),
        ];
        VideoEncoderInfo info = new(
            $"{name} ({_spec.Backend}{(adapter is null ? "" : $", {adapter.Name}")})",
            _spec.IsHardware,
            new VideoConstraints(storages, probe.Formats, identity),
            WidthAlignment: 2,
            HeightAlignment: 2,
            probe.MaximumSize,
            probe.ReconfigurableRate
        );
        return new Resolved(codec, adapter, info, probe.ForcesKeyframes);
    }

    private static bool SupportedHere(VideoStorageKind storage) =>
        storage switch
        {
            VideoStorageKind.D3D12 => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240),
            VideoStorageKind.D3D11 => OperatingSystem.IsWindows(),
            VideoStorageKind.DmaBuf => OperatingSystem.IsLinux(),
            VideoStorageKind.IOSurface => OperatingSystem.IsMacOS(),
            _ => true,
        };

    // Opens the encoder for real and encodes a few frames through the system-memory path, which every
    // backend takes; a driver without the codec, a missing GPU and an absent runtime all fail here.
    // An encoder that takes only surfaces is offered the formats its device holds, which are more than
    // it encodes (a Vulkan device holds BGRA, a Vulkan H.264 encoder takes only YUV), so each of those
    // is tried and only the ones that encode are kept; an encoder with its own formats is tried on its
    // first that works.
    private Probe? RunProbe(FF.Codec codec, VideoCodecId id, FF.GpuAdapter? adapter)
    {
        ImmutableArray<PixelFormat> candidates = FormatsFor(codec, adapter);
        if (candidates.IsEmpty)
        {
            LogProbeFailed(_logger, codec.Name, "it takes none of the Media pixel formats", null);
            return null;
        }

        bool surfacesOnly = !candidates.Any(f =>
            FFmpegVideoEncoder.Takes(codec, Formats.ToFFmpeg(f))
        );
        ImmutableArray<PixelFormat>.Builder working = ImmutableArray.CreateBuilder<PixelFormat>();
        bool? forcesKeyframes = null;
        Exception? failure = null;
        foreach (PixelFormat format in candidates)
        {
            try
            {
                bool forces = Exercise(codec, id, adapter, format);
                forcesKeyframes ??= forces;
                working.Add(format);
                if (!surfacesOnly)
                {
                    break;
                }
            }
            catch (Exception ex)
                when (ex
                        is FF.FFmpegException
                            or NotSupportedException
                            or InvalidOperationException
                            or ArgumentException
                )
            {
                LogFormatRefused(_logger, codec.Name, format, ex.Message);
                failure ??= ex;
            }
        }

        if (forcesKeyframes is not { } forced)
        {
            LogProbeFailed(_logger, codec.Name, failure?.Message ?? "no format encoded", failure);
            return null;
        }

        bool reconfigurable = codec.SupportsRateControlChanges;
        LogProbeSucceeded(_logger, codec.Name, adapter?.Name ?? "default", reconfigurable, forced);
        return new Probe(
            surfacesOnly
                ? working.DrainToImmutable()
                : [.. candidates.SkipWhile(f => !working.Contains(f))],
            MaximumSize(adapter),
            reconfigurable,
            forced
        );
    }

    // Encodes three frames of one format; the third asks for a keyframe, to learn whether the encoder
    // honours a forced picture type or has to be reopened for one.
    private bool Exercise(
        FF.Codec codec,
        VideoCodecId id,
        FF.GpuAdapter? adapter,
        PixelFormat format
    )
    {
        const int size = 256;
        VideoSize frameSize = new(size, size);
        byte[] pixels = new byte[PlaneLayout.PackedSize(format, frameSize)];
        if (format is PixelFormat.Nv12 or PixelFormat.I420)
        {
            pixels.AsSpan(0, size * size).Fill(16);
            pixels.AsSpan(size * size).Fill(128);
        }

        VideoEncoderConfiguration configuration = new(
            new VideoCodecFormat(id),
            frameSize,
            new RateTarget(1_000_000, 30)
        );
        VideoEncoderInfo provisional = new(
            codec.Name,
            _spec.IsHardware,
            VideoConstraints.Cpu(format),
            2,
            2,
            frameSize,
            ReconfigurableRate: false
        );
        using FFmpegVideoEncoder encoder = new(
            _spec,
            codec,
            configuration,
            provisional,
            adapter,
            PrivateOptions(codec.Name, EncodeTuning.Interactive),
            forcesKeyframes: true,
            _loggerFactory.CreateLogger<FFmpegVideoEncoder>()
        );
        KeyframeRecorder consumer = new();
        for (int i = 0; i < 3; i++)
        {
            VideoFrame frame = new(
                new CpuImage(PlaneLayout.Packed(format, frameSize)),
                new VideoFormat(format, size, size),
                MediaTimestamp.Observed(new MediaTime(i * 33_333_333L)),
                pixels
            );
            encoder.Encode(in frame, new EncodeRequest(Keyframe: i != 1), consumer);
        }

        encoder.Flush(consumer);
        return consumer.Keyframes is [true, false, true];
    }

    // The Media formats the encoder takes: its own software formats, or for an encoder that takes
    // only surfaces, the formats its device's surfaces hold.
    private ImmutableArray<PixelFormat> FormatsFor(FF.Codec codec, FF.GpuAdapter? adapter)
    {
        ImmutableArray<PixelFormat>.Builder formats = ImmutableArray.CreateBuilder<PixelFormat>();
        foreach (PixelFormat candidate in s_candidates)
        {
            if (FFmpegVideoEncoder.Takes(codec, Formats.ToFFmpeg(candidate)))
            {
                formats.Add(candidate);
            }
        }

        if (formats.Count > 0 || _spec.DeviceType is not { } type)
        {
            return formats.DrainToImmutable();
        }

        try
        {
            using FF.HardwareDevice device = adapter is null
                ? FF.HardwareDevice.Create(type)
                : FF.HardwareDevice.Create(type, adapter);
            FF.HardwareFrameConstraints constraints = device.GetFrameConstraints();
            foreach (PixelFormat candidate in s_candidates)
            {
                if (constraints.SoftwareFormats.Contains(Formats.ToFFmpeg(candidate)))
                {
                    formats.Add(candidate);
                }
            }
        }
        catch (FF.FFmpegException ex)
        {
            LogProbeFailed(_logger, codec.Name, ex.Message, ex);
        }

        return formats.DrainToImmutable();
    }

    private VideoSize MaximumSize(FF.GpuAdapter? adapter)
    {
        const int fallback = 8192;
        if (_spec.DeviceType is not { } type)
        {
            return new VideoSize(fallback, fallback);
        }

        try
        {
            using FF.HardwareDevice device = adapter is null
                ? FF.HardwareDevice.Create(type)
                : FF.HardwareDevice.Create(type, adapter);
            FF.HardwareFrameConstraints constraints = device.GetFrameConstraints();
            return new VideoSize(
                constraints.MaxWidth > 0 ? constraints.MaxWidth : fallback,
                constraints.MaxHeight > 0 ? constraints.MaxHeight : fallback
            );
        }
        catch (FF.FFmpegException ex)
        {
            LogProbeFailed(_logger, _spec.Backend.ToString(), ex.Message, ex);
            return new VideoSize(fallback, fallback);
        }
    }

    private ImmutableDictionary<string, string> PrivateOptions(string encoder, EncodeTuning tuning)
    {
        ImmutableDictionary<string, string> options = EncoderSettings.PrivateOptions(
            encoder,
            tuning
        );
        return _options.EncoderOptions.TryGetValue(
            encoder,
            out ImmutableDictionary<string, string>? extra
        )
            ? options.SetItems(extra)
            : options;
    }

    private sealed record Probe(
        ImmutableArray<PixelFormat> Formats,
        VideoSize MaximumSize,
        bool ReconfigurableRate,
        bool ForcesKeyframes
    );

    private sealed record Resolved(
        FF.Codec Codec,
        FF.GpuAdapter? Adapter,
        VideoEncoderInfo Info,
        bool ForcesKeyframes
    );

    private sealed class KeyframeRecorder : IEncodedVideoConsumer
    {
        public List<bool> Keyframes { get; } = [];

        public void OnEncoded(in EncodedVideoFrame frame) => Keyframes.Add(frame.Keyframe);
    }

    [LoggerMessage(
        EventId = 1010,
        Level = LogLevel.Debug,
        Message = "{Encoder} works on {Adapter} (rate changes while running: {Reconfigurable}, forced keyframes: {ForcesKeyframes})"
    )]
    private static partial void LogProbeSucceeded(
        ILogger logger,
        string encoder,
        string adapter,
        bool reconfigurable,
        bool forcesKeyframes
    );

    [LoggerMessage(
        EventId = 1012,
        Level = LogLevel.Debug,
        Message = "{Encoder} does not encode {Format}: {Reason}"
    )]
    private static partial void LogFormatRefused(
        ILogger logger,
        string encoder,
        PixelFormat format,
        string reason
    );

    [LoggerMessage(
        EventId = 1011,
        Level = LogLevel.Debug,
        Message = "{Encoder} is unavailable: {Reason}"
    )]
    private static partial void LogProbeFailed(
        ILogger logger,
        string encoder,
        string reason,
        Exception? exception
    );
}
