using System.Collections.Concurrent;
using System.Collections.Immutable;
using Agash.StreamTransport.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using static FFmpeg.Interop.VulkanExtensions;
using FF = FFmpeg.Interop;

namespace Agash.StreamTransport.Codecs.FFmpeg;

/// <summary>
/// Makes FFmpeg video decoders of one <see cref="DecoderBackend"/>. Decoded frames stay on the GPU in
/// the backend's storage when the consumer takes it on that GPU, and are downloaded to system memory
/// once otherwise. Whether a hardware backend decodes a codec on a GPU is found out by decoding a
/// short stream through it, once per codec and GPU.
/// </summary>
public sealed partial class FFmpegVideoDecoderFactory : IVideoDecoderFactory
{
    private static readonly ConcurrentDictionary<
        (DecoderBackend, VideoCodecId, string),
        bool
    > s_probes = new();

    private static readonly ImmutableArray<PixelFormat> s_cpuFormats =
    [
        PixelFormat.Nv12,
        PixelFormat.I420,
        PixelFormat.P010,
        PixelFormat.Bgra,
        PixelFormat.Rgba,
        PixelFormat.Yuva420,
    ];

    private readonly DecoderSpec _spec;
    private readonly FFmpegCodecOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly FFmpegCodecMetrics _metrics;
    private readonly ILogger _logger;

    /// <summary>A factory for one backend.</summary>
    /// <param name="backend">The backend.</param>
    /// <param name="options">Shared options; defaults when null.</param>
    public FFmpegVideoDecoderFactory(DecoderBackend backend, FFmpegCodecOptions? options = null)
    {
        _spec = DecoderCatalog.Get(backend);
        _options = options ?? new FFmpegCodecOptions();
        _loggerFactory = _options.LoggerFactory ?? NullLoggerFactory.Instance;
        _metrics = _options.MeterFactory is { } meters
            ? new FFmpegCodecMetrics(meters)
            : FFmpegCodecMetrics.Shared;
        _logger = _loggerFactory.CreateLogger<FFmpegVideoDecoderFactory>();
    }

    /// <summary>The backend.</summary>
    public DecoderBackend Backend => _spec.Backend;

    /// <inheritdoc/>
    public int Rank => _spec.Rank;

    /// <inheritdoc/>
    public ImmutableArray<VideoCodecFormat> SupportedFormats =>
        Rank == 0
            ? []
            :
            [
                .. VideoCodecId
                    .BuiltIn.Where(codec =>
                        _spec.DecoderFor(codec) is { } name && FF.Codec.TryFindDecoder(name, out _)
                    )
                    .Select(static codec => new VideoCodecFormat(codec)),
            ];

    /// <summary>A factory for every backend this platform has, most preferred first.</summary>
    /// <param name="options">Shared options; defaults when null.</param>
    /// <returns>The factories.</returns>
    public static ImmutableArray<FFmpegVideoDecoderFactory> CreateAll(
        FFmpegCodecOptions? options = null
    ) =>
        [
            .. DecoderCatalog
                .All.Where(static spec => spec.Rank > 0)
                .OrderByDescending(static spec => spec.Rank)
                .Select(spec => new FFmpegVideoDecoderFactory(spec.Backend, options)),
        ];

    /// <inheritdoc/>
    public VideoDecoderInfo? QueryCapabilities(VideoCodecFormat format, VideoConstraints output)
    {
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(output);
        return Resolve(format, output)?.Info;
    }

    /// <inheritdoc/>
    /// <exception cref="NotSupportedException">The backend cannot decode the format into that output.</exception>
    public IVideoDecoder Create(VideoCodecFormat format, VideoConstraints output)
    {
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(output);
        Resolved resolved =
            Resolve(format, output)
            ?? throw new NotSupportedException(
                $"{_spec.Backend} cannot decode {format.Codec} into what the consumer takes."
            );
        FF.HardwareDevice? device = OpenDevice(resolved.Adapter);
        try
        {
            return new FFmpegVideoDecoder(
                resolved.Codec,
                format.Codec,
                device,
                resolved.Identity,
                resolved.Storage,
                resolved.CpuFormat,
                DrmModifiers(resolved.Storage, output),
                resolved.Info,
                _loggerFactory.CreateLogger<FFmpegVideoDecoder>()
            );
        }
        catch
        {
            device?.Dispose();
            throw;
        }
    }

    private Resolved? Resolve(VideoCodecFormat format, VideoConstraints output)
    {
        if (
            Rank == 0
            || _spec.DecoderFor(format.Codec) is not { } name
            || !FF.Codec.TryFindDecoder(name, out FF.Codec codec)
        )
        {
            return null;
        }

        FF.GpuAdapter? adapter = null;
        if (_spec.DeviceType is not null)
        {
            // A consumer that takes a GPU frame on one GPU gets the decoder there; one that takes
            // only system memory lets the decoder use the best GPU.
            adapter = Adapters.For(vendor: null, output.Device);
            if (adapter is null && !OperatingSystem.IsMacOS())
            {
                return null;
            }
        }

        GpuIdentity? identity = adapter is null ? null : Adapters.IdentityOf(adapter);
        VideoStorageKind? gpu = _spec.GpuStorages.FirstOrDefault(storage =>
            output.Storages.Contains(storage)
            && (output.Device is null || output.Device == identity)
            && SupportedHere(storage)
        );
        PixelFormat? cpu = s_cpuFormats.FirstOrDefault(output.PixelFormats.Contains);
        bool cpuAllowed =
            output.Storages.Contains(VideoStorageKind.Cpu)
            && output.PixelFormats.Any(s_cpuFormats.Contains);
        VideoStorageKind storage;
        if (
            gpu is { } chosen
            && _spec.GpuStorages.Contains(chosen)
            && output.PixelFormats.Any(static f => f is PixelFormat.Nv12 or PixelFormat.P010)
        )
        {
            storage = chosen;
        }
        else if (cpuAllowed)
        {
            storage = VideoStorageKind.Cpu;
        }
        else
        {
            return null;
        }

        string key = adapter?.ToString() ?? "default";
        if (
            !s_probes.GetOrAdd(
                (_spec.Backend, format.Codec, key),
                _ => CountedProbe(codec, format.Codec, adapter)
            )
        )
        {
            return null;
        }

        VideoDecoderInfo info = new(
            $"{codec.Name} ({_spec.Backend}{(adapter is null ? "" : $", {adapter.Name}")})",
            _spec.DeviceType is not null,
            new VideoConstraints(
                [storage],
                storage == VideoStorageKind.Cpu
                    ? [cpu!.Value]
                    : [PixelFormat.Nv12, PixelFormat.P010],
                storage == VideoStorageKind.Cpu ? null : identity
            ),
            // FFmpeg's own HEVC decoder decodes the alpha layer; hardware decoding takes the base
            // layer only.
            DecodesAlphaLayer: _spec.Backend == DecoderBackend.Software
                && format.Codec == VideoCodecId.H265
        );
        return new Resolved(codec, adapter, identity, storage, cpu ?? PixelFormat.Nv12, info);
    }

    // A Vulkan decoder shares its pictures as DMA-BUFs only when it decodes into a modifier the consumer
    // reads: the consumer's, or linear when it names none. VA-API surfaces export as they are.
    private ImmutableArray<ulong> DrmModifiers(VideoStorageKind storage, VideoConstraints output) =>
        storage == VideoStorageKind.DmaBuf && _spec.DeviceType == FF.HardwareDeviceType.Vulkan
            ? output.DrmModifiers.IsDefaultOrEmpty
                ? [DmaBufImage.LinearModifier]
                : output.DrmModifiers
            : [];

    private static bool SupportedHere(VideoStorageKind storage) =>
        storage switch
        {
            VideoStorageKind.D3D12 => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240),
            VideoStorageKind.D3D11 => OperatingSystem.IsWindows(),
            VideoStorageKind.DmaBuf => OperatingSystem.IsLinux(),
            VideoStorageKind.IOSurface => OperatingSystem.IsMacOS(),
            _ => true,
        };

    private FF.HardwareDevice? OpenDevice(FF.GpuAdapter? adapter) =>
        _spec.DeviceType is not { } type ? null
        : adapter is null ? FF.HardwareDevice.Create(type)
        : FF.HardwareDevice.Create(type, adapter);

    private bool CountedProbe(FF.Codec codec, VideoCodecId id, FF.GpuAdapter? adapter)
    {
        bool works = RunProbe(codec, id, adapter);
        _metrics.Probes.Add(
            1,
            FFmpegCodecMetrics.Codec(codec.Name),
            FFmpegCodecMetrics.Outcome(works)
        );
        return works;
    }

    // Encodes a few frames and decodes them through this backend; a hardware backend
    // passes only if its frames come out on the GPU.
    private bool RunProbe(FF.Codec codec, VideoCodecId id, FF.GpuAdapter? adapter)
    {
        // Decoders this machine lacks fail loudly in FFmpeg's log; the outcome is logged here instead.
        using FF.FFmpegLogging.DemotionScope demoted = FF.FFmpegLogging.Demote();
        try
        {
            ImmutableArray<byte[]> stream = SampleStream(id);
            using FF.HardwareDevice? device = OpenDevice(adapter);
            if (
                device is not null
                && device.Type == FF.HardwareDeviceType.Vulkan
                && !device.CanVulkanDecode(Formats.ToCodecId(id))
            )
            {
                LogProbeFailed(
                    _logger,
                    codec.Name,
                    $"the GPU has no Vulkan Video decode for {id}",
                    null
                );
                return false;
            }

            using var decoder = FF.Decoder.Create(
                codec,
                new FF.DecoderOptions { HardwareDevice = device, LowDelay = true }
            );
            using FF.Packet packet = new();
            using FF.Frame frame = new();
            int frames = 0;
            bool hardware = true;
            foreach (byte[] unit in stream)
            {
                packet.CopyFrom(unit);
                foreach (FF.Frame decoded in decoder.Decode(packet, frame))
                {
                    frames++;
                    hardware &= decoded.IsHardwareFrame;
                }
            }

            foreach (FF.Frame decoded in decoder.Decode(null, frame))
            {
                frames++;
                hardware &= decoded.IsHardwareFrame;
            }

            bool works = frames == stream.Length && (device is null || hardware);
            if (works)
            {
                LogProbeSucceeded(_logger, codec.Name, adapter?.Name ?? "default");
            }
            else
            {
                LogProbeFailed(
                    _logger,
                    codec.Name,
                    $"{frames} of {stream.Length} frames decoded, on the GPU: {hardware}",
                    null
                );
            }

            return works;
        }
        catch (Exception ex)
            when (ex
                    is FF.FFmpegException
                        or NotSupportedException
                        or InvalidOperationException
                        or ArgumentException
            )
        {
            LogProbeFailed(_logger, codec.Name, ex.Message, ex);
            return false;
        }
    }

    // A few frames of the codec, shared by every probe of the codec.
    private static readonly ConcurrentDictionary<VideoCodecId, ImmutableArray<byte[]>> s_samples =
        new();

    private static ImmutableArray<byte[]> SampleStream(VideoCodecId id) =>
        s_samples.GetOrAdd(
            id,
            static codec =>
            {
                VideoSize size = new(256, 256);
                using IVideoEncoder encoder = AnyEncoder(codec, size);
                PixelFormat format = encoder.Info.Input.PixelFormats.First(static f =>
                    f is PixelFormat.Nv12 or PixelFormat.I420
                );
                byte[] pixels = new byte[PlaneLayout.PackedSize(format, size)];
                pixels.AsSpan().Fill(96);
                SampleCollector collector = new();
                for (int i = 0; i < 3; i++)
                {
                    VideoFrame frame = new(
                        new CpuImage(PlaneLayout.Packed(format, size)),
                        new VideoFormat(format, size.Width, size.Height),
                        MediaTimestamp.Observed(new MediaTime(i * 33_333_333L)),
                        pixels
                    );
                    encoder.Encode(in frame, new EncodeRequest(Keyframe: i == 0), collector);
                }

                encoder.Flush(collector);
                return [.. collector.Units];
            }
        );

    // An encoder for the codec to make probe streams with: software first, since it depends on no GPU,
    // then whatever hardware this machine has (macOS FFmpeg builds carry no software H.264 or H.265).
    private static IVideoEncoder AnyEncoder(VideoCodecId codec, VideoSize size)
    {
        VideoEncoderConfiguration configuration = new(
            new VideoCodecFormat(codec),
            size,
            new RateTarget(500_000, 30)
        );
        foreach (
            FFmpegVideoEncoderFactory factory in FFmpegVideoEncoderFactory
                .CreateAll()
                .OrderBy(static f => f.IsHardwareAccelerated)
        )
        {
            if (
                factory.QueryCapabilities(configuration.Format, device: null) is { } info
                && info.Input.PixelFormats.Any(static f =>
                    f is PixelFormat.Nv12 or PixelFormat.I420
                )
            )
            {
                return factory.Create(configuration, device: null);
            }
        }

        throw new NotSupportedException(
            $"No encoder on this machine makes {codec} to probe decoders with."
        );
    }

    private sealed record Resolved(
        FF.Codec Codec,
        FF.GpuAdapter? Adapter,
        GpuIdentity? Identity,
        VideoStorageKind Storage,
        PixelFormat CpuFormat,
        VideoDecoderInfo Info
    );

    private sealed class SampleCollector : IEncodedVideoConsumer
    {
        public List<byte[]> Units { get; } = [];

        public void OnEncoded(in EncodedVideoFrame frame) => Units.Add(frame.Data.ToArray());
    }

    [LoggerMessage(
        EventId = 1020,
        Level = LogLevel.Debug,
        Message = "{Decoder} works on {Adapter}"
    )]
    private static partial void LogProbeSucceeded(ILogger logger, string decoder, string adapter);

    [LoggerMessage(
        EventId = 1021,
        Level = LogLevel.Debug,
        Message = "{Decoder} is unavailable: {Reason}"
    )]
    private static partial void LogProbeFailed(
        ILogger logger,
        string decoder,
        string reason,
        Exception? exception
    );
}
