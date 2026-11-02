using System.Runtime.InteropServices;
using Agash.StreamTransport.Media;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.Audio;
using Windows.Win32.System.Com;

namespace Agash.StreamTransport.Windows.Wasapi;

/// <summary>Which audio endpoint a WASAPI stream opens.</summary>
public enum WasapiEndpoint
{
    /// <summary>The default capture device, such as a microphone.</summary>
    DefaultCapture,

    /// <summary>What the default output device plays, captured as it leaves (loopback).</summary>
    DefaultOutputLoopback,

    /// <summary>The default output device, for rendering.</summary>
    DefaultOutput,
}

/// <summary>
/// One shared-mode WASAPI stream in the format StreamTransport's audio uses: 48 kHz, stereo, 32-bit
/// float, which WASAPI converts to and from the device's own mix format. It wakes a thread of its own
/// through an event. Create, use and dispose it on that thread, which it initialises for COM.
/// </summary>
internal sealed unsafe class WasapiClient : IDisposable
{
    public static readonly AudioFormat Format = new(SampleFormat.F32, 48_000, 2);

    // Twenty milliseconds of buffer: the device period of most shared-mode endpoints, twice over.
    private const long BufferDuration = 200_000;

    private readonly IAudioClient* _client;

    // KSDATAFORMAT_SUBTYPE_IEEE_FLOAT, from ksmedia.h.
    private static readonly Guid FloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");

    private readonly AutoResetEvent _ready = new(false);
    private readonly SafeHandle _mmcss;

    public WasapiClient(WasapiEndpoint endpoint)
    {
        Win32.CoInitializeEx(null, COINIT.COINIT_MULTITHREADED).ThrowOnFailure();
        uint task = 0;
        _mmcss = Win32.AvSetMmThreadCharacteristics("Pro Audio", ref task);

        Guid enumeratorClass = typeof(MMDeviceEnumerator).GUID;
        Guid enumeratorId = typeof(IMMDeviceEnumerator).GUID;
        void* enumerator;
        Win32
            .CoCreateInstance(&enumeratorClass, null, CLSCTX.CLSCTX_ALL, &enumeratorId, &enumerator)
            .ThrowOnFailure();
        IMMDevice* device;
        try
        {
            ((IMMDeviceEnumerator*)enumerator)->GetDefaultAudioEndpoint(
                endpoint == WasapiEndpoint.DefaultCapture ? EDataFlow.eCapture : EDataFlow.eRender,
                ERole.eMultimedia,
                &device
            );
        }
        finally
        {
            _ = ((IUnknown*)enumerator)->Release();
        }

        try
        {
            Guid clientId = typeof(IAudioClient).GUID;
            void* client;
            device->Activate(&clientId, CLSCTX.CLSCTX_ALL, null, &client);
            _client = (IAudioClient*)client;
        }
        finally
        {
            _ = device->Release();
        }

        WAVEFORMATEXTENSIBLE format = new()
        {
            Format = new WAVEFORMATEX
            {
                wFormatTag = (ushort)Win32.WAVE_FORMAT_EXTENSIBLE,
                nChannels = 2,
                nSamplesPerSec = 48_000,
                wBitsPerSample = 32,
                nBlockAlign = 8,
                nAvgBytesPerSec = 48_000 * 8,
                cbSize = (ushort)(sizeof(WAVEFORMATEXTENSIBLE) - sizeof(WAVEFORMATEX)),
            },
            dwChannelMask = Win32.SPEAKER_FRONT_LEFT | Win32.SPEAKER_FRONT_RIGHT,
            SubFormat = FloatSubFormat,
        };
        format.Samples.wValidBitsPerSample = 32;
        uint flags =
            Win32.AUDCLNT_STREAMFLAGS_EVENTCALLBACK
            | Win32.AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM
            | Win32.AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY
            | (
                endpoint == WasapiEndpoint.DefaultOutputLoopback
                    ? Win32.AUDCLNT_STREAMFLAGS_LOOPBACK
                    : 0
            );
        _client->Initialize(
            AUDCLNT_SHAREMODE.AUDCLNT_SHAREMODE_SHARED,
            flags,
            BufferDuration,
            0,
            (WAVEFORMATEX*)&format,
            null
        );
        uint frames;
        _client->GetBufferSize(&frames);
        BufferFrames = frames;
        _client->SetEventHandle(new HANDLE(_ready.SafeWaitHandle.DangerousGetHandle()));
    }

    /// <summary>The endpoint buffer's size in frames.</summary>
    public uint BufferFrames { get; }

    /// <summary>How long audio takes through the stream: its latency and its buffer.</summary>
    public TimeSpan Latency
    {
        get
        {
            long latency;
            _client->GetStreamLatency(&latency);
            return TimeSpan.FromTicks(latency + BufferDuration);
        }
    }

    public void Start() => _client->Start();

    /// <summary>Set by the device each period: data to take, or room to fill.</summary>
    public WaitHandle Ready => _ready;

    /// <summary>A service of the stream, such as its capture or render client.</summary>
    public T* Service<T>()
        where T : unmanaged
    {
        Guid id = typeof(T).GUID;
        void* service;
        _client->GetService(&id, &service);
        return (T*)service;
    }

    /// <summary>Frames queued in the endpoint buffer and not yet played.</summary>
    public uint Padding
    {
        get
        {
            uint padding;
            _client->GetCurrentPadding(&padding);
            return padding;
        }
    }

    public void Dispose()
    {
        _client->Stop();
        _ = _client->Release();
        _ready.Dispose();
        _mmcss.Dispose();

        Win32.CoUninitialize();
    }
}
