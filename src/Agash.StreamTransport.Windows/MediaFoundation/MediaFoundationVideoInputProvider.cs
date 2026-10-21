using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Agash.StreamTransport.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.MediaFoundation;
using Windows.Win32.System.Com;

namespace Agash.StreamTransport.Windows.MediaFoundation;

/// <summary>
/// Cameras, USB capture cards and HDMI receivers through Media Foundation, including virtual cameras
/// registered with the Frame Server. Uncompressed modes are delivered as the camera sends them; a camera
/// that sends only MJPEG is left to a fallback provider.
/// </summary>
/// <param name="loggerFactory">Where inputs log.</param>
public sealed unsafe partial class MediaFoundationVideoInputProvider(
    ILoggerFactory? loggerFactory = null
) : IVideoInputProvider
{
    private readonly ILoggerFactory _loggers = loggerFactory ?? NullLoggerFactory.Instance;
    private readonly ILogger _logger = (
        loggerFactory ?? NullLoggerFactory.Instance
    ).CreateLogger<MediaFoundationVideoInputProvider>();

    /// <inheritdoc/>
    public string Name => "mediafoundation";

    /// <inheritdoc/>
    public int Rank => 100;

    /// <inheritdoc/>
    public ValueTask<ImmutableArray<VideoInputInfo>> GetInputsAsync(
        CancellationToken cancellationToken
    )
    {
        MediaFoundationPlatform.Start();
        List<VideoInputInfo> inputs = [];
        IMFAttributes* attributes = null;
        IMFActivate** devices = null;
        uint count = 0;
        try
        {
            Win32.MFCreateAttributes(&attributes, 1).ThrowOnFailure();
            Guid sourceType = Win32.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE;
            Guid videoCapture = Win32.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID;
            attributes->SetGUID(&sourceType, &videoCapture);
            Win32.MFEnumDeviceSources(attributes, &devices, &count).ThrowOnFailure();
            for (uint i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string name = MediaFoundationPlatform.String(
                    devices[i],
                    Win32.MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME
                );
                string link = MediaFoundationPlatform.String(
                    devices[i],
                    Win32.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK
                );
                ImmutableArray<VideoInputMode> modes;
                try
                {
                    modes = MediaFoundationPlatform.Modes(devices[i]);
                }
                catch (COMException exception)
                {
                    // A camera another application holds exclusively cannot be asked its modes now.
                    LogUnreadable(exception, name);
                    continue;
                }

                if (modes.IsEmpty)
                {
                    LogNoModes(name);
                    continue;
                }

                inputs.Add(
                    new VideoInputInfo(Name, link, name, MediaInputKind.Camera, modes)
                    {
                        DeviceKey = link,
                    }
                );
            }
        }
        finally
        {
            for (uint i = 0; i < count; i++)
            {
                _ = devices[i]->Release();
            }

            if (devices is not null)
            {
                Win32.CoTaskMemFree(devices);
            }

            if (attributes is not null)
            {
                _ = attributes->Release();
            }
        }

        return ValueTask.FromResult(inputs.ToImmutableArray());
    }

    /// <inheritdoc/>
    public ValueTask<IVideoInput> OpenAsync(
        VideoInputInfo input,
        VideoInputRequest request,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(request);
        VideoInputMode mode =
            request.Choose(input.Modes)
            ?? throw new NotSupportedException($"{input.Name} has no mode this provider delivers.");
        return ValueTask.FromResult<IVideoInput>(
            new MediaFoundationVideoInput(input, mode, _loggers)
        );
    }

    [LoggerMessage(
        2340,
        LogLevel.Warning,
        "{Camera} could not be asked its modes; it is not listed now."
    )]
    private partial void LogUnreadable(Exception exception, string camera);

    [LoggerMessage(
        2341,
        LogLevel.Information,
        "{Camera} sends no uncompressed format this provider delivers; it is not listed."
    )]
    private partial void LogNoModes(string camera);
}

// Media Foundation set-up and the translations between its media types and the media model.
internal static unsafe class MediaFoundationPlatform
{
    private static readonly Lazy<bool> Started = new(static () =>
    {
        Win32.MFStartup(Win32.MF_VERSION, Win32.MFSTARTUP_NOSOCKET).ThrowOnFailure();
        return true;
    });

    public static void Start() => _ = Started.Value;

    public static string String(IMFActivate* activate, Guid key)
    {
        PWSTR value;
        uint length;
        activate->GetAllocatedString(&key, &value, &length);
        try
        {
            return value.ToString();
        }
        finally
        {
            Win32.CoTaskMemFree(value);
        }
    }

    public static PixelFormat? ToMedia(Guid subtype) =>
        subtype == Win32.MFVideoFormat_NV12 ? PixelFormat.Nv12
        : subtype == Win32.MFVideoFormat_YUY2 ? PixelFormat.Yuy2
        : subtype == Win32.MFVideoFormat_UYVY ? PixelFormat.Uyvy
        : subtype == Win32.MFVideoFormat_I420 ? PixelFormat.I420
        : subtype == Win32.MFVideoFormat_ARGB32 ? PixelFormat.Bgra
        : null;

    // The modes of a device's source: every native type of its first video stream the model has.
    public static ImmutableArray<VideoInputMode> Modes(IMFActivate* activate)
    {
        void* source = activate->ActivateObject(typeof(IMFMediaSource).GUID);
        try
        {
            IMFSourceReader* reader = Reader((IMFMediaSource*)source);
            try
            {
                ImmutableArray<VideoInputMode>.Builder modes =
                    ImmutableArray.CreateBuilder<VideoInputMode>();
                foreach ((VideoInputMode mode, _) in NativeTypes(reader))
                {
                    if (!modes.Contains(mode))
                    {
                        modes.Add(mode);
                    }
                }

                return modes.ToImmutable();
            }
            finally
            {
                _ = reader->Release();
            }
        }
        finally
        {
            activate->ShutdownObject();
        }
    }

    public static IMFSourceReader* Reader(IMFMediaSource* source)
    {
        IMFAttributes* attributes;
        Win32.MFCreateAttributes(&attributes, 1).ThrowOnFailure();
        try
        {
            // The camera's own formats, with no converter inserted behind them.
            Guid disableConverters = Win32.MF_READWRITE_DISABLE_CONVERTERS;
            attributes->SetUINT32(&disableConverters, 1);
            IMFSourceReader* reader;
            Win32.MFCreateSourceReaderFromMediaSource(source, attributes, &reader).ThrowOnFailure();
            return reader;
        }
        finally
        {
            _ = attributes->Release();
        }
    }

    // The native types of the first video stream, with their index.
    public static List<(VideoInputMode Mode, uint Index)> NativeTypes(IMFSourceReader* reader)
    {
        List<(VideoInputMode, uint)> types = [];
        for (uint index = 0; ; index++)
        {
            IMFMediaType* type;
            try
            {
                reader->GetNativeMediaType(FirstVideoStream, index, &type);
            }
            catch (COMException exception)
                when (exception.HResult == MediaFoundationErrors.NoMoreTypes)
            {
                // Deliberately not logged: the end of the list.
                break;
            }

            try
            {
                if (Describe(type) is { } mode)
                {
                    types.Add((mode, index));
                }
            }
            finally
            {
                _ = type->Release();
            }
        }

        return types;
    }

    public static VideoInputMode? Describe(IMFMediaType* type)
    {
        Guid subtypeKey = Win32.MF_MT_SUBTYPE;
        Guid subtype;
        type->GetGUID(&subtypeKey, &subtype);
        if (ToMedia(subtype) is not { } format)
        {
            return null;
        }

        Guid sizeKey = Win32.MF_MT_FRAME_SIZE;
        Guid rateKey = Win32.MF_MT_FRAME_RATE;
        ulong size;
        ulong rate;
        type->GetUINT64(&sizeKey, &size);
        type->GetUINT64(&rateKey, &rate);
        uint numerator = (uint)(rate >> 32);
        uint denominator = (uint)rate;
        return new VideoInputMode(
            format,
            new VideoSize((int)(size >> 32), (int)(uint)size),
            denominator == 0 ? 30 : Math.Round((double)numerator / denominator, 3)
        );
    }

    // The colour a media type states, with the defaults for what it leaves out.
    public static VideoColor Color(IMFMediaType* type, PixelFormat format)
    {
        bool rgb = format is PixelFormat.Bgra or PixelFormat.Rgba;
        Guid matrixKey = Win32.MF_MT_YUV_MATRIX;
        Guid rangeKey = Win32.MF_MT_VIDEO_NOMINAL_RANGE;
        uint matrix = Optional(type, matrixKey);
        uint range = Optional(type, rangeKey);

        // MFVideoTransferMatrix: BT709 1, BT601 2, BT2020 4; MFNominalRange: 0-255 is 1, 16-235 is 2.
        return new VideoColor(
            rgb
                ? ColorMatrix.Identity
                : matrix switch
                {
                    1 => ColorMatrix.Bt709,
                    4 => ColorMatrix.Bt2020,
                    _ => ColorMatrix.Bt601,
                },
            range switch
            {
                1 => ColorRange.Full,
                2 => ColorRange.Limited,
                _ => rgb ? ColorRange.Full : ColorRange.Limited,
            },
            ColorPrimaries.Bt709,
            rgb ? TransferFunction.Srgb : TransferFunction.Bt709
        );
    }

    // An attribute a media type may leave out, as zero.
    private static uint Optional(IMFMediaType* type, Guid key)
    {
        uint value = 0;
        try
        {
            type->GetUINT32(&key, &value);
        }
        catch (COMException)
        {
            // Deliberately not logged: an unstated attribute takes its default.
        }

        return value;
    }

    // MF_SOURCE_READER_FIRST_VIDEO_STREAM.
    public const uint FirstVideoStream = 0xFFFF_FFFC;
}

// The Media Foundation results that are outcomes rather than failures.
internal static class MediaFoundationErrors
{
    // MF_E_NO_MORE_TYPES.
    public const int NoMoreTypes = unchecked((int)0xC00D36B9);
}
