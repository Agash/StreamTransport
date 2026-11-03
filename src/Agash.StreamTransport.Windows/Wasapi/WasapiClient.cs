using System.Runtime.InteropServices;
using Agash.StreamTransport.Media;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.Audio;
using Windows.Win32.System.Com;
using Windows.Win32.System.Com.StructuredStorage;
using Windows.Win32.System.Variant;
using Windows.Win32.UI.Shell.PropertiesSystem;

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

    public WasapiClient(WasapiEndpoint endpoint, string? deviceId = null)
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
            if (deviceId is not null)
            {
                fixed (char* id = deviceId)
                {
                    ((IMMDeviceEnumerator*)enumerator)->GetDevice(id, &device);
                }
            }
            else
            {
                ((IMMDeviceEnumerator*)enumerator)->GetDefaultAudioEndpoint(
                    endpoint == WasapiEndpoint.DefaultCapture
                        ? EDataFlow.eCapture
                        : EDataFlow.eRender,
                    ERole.eMultimedia,
                    &device
                );
            }
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

    /// <summary>The active output devices: each one's ID, which the constructor opens, and its name.</summary>
    /// <returns>The devices; the default one is flagged.</returns>
    public static List<(string Id, string Name, bool IsDefault)> ListOutputs()
    {
        // COM on this thread, which the caller does not own: a pool thread is in the multithreaded apartment.
        HRESULT initialized = Win32.CoInitializeEx(null, COINIT.COINIT_MULTITHREADED);
        Guid enumeratorClass = typeof(MMDeviceEnumerator).GUID;
        Guid enumeratorId = typeof(IMMDeviceEnumerator).GUID;
        void* enumerator;
        Win32
            .CoCreateInstance(&enumeratorClass, null, CLSCTX.CLSCTX_ALL, &enumeratorId, &enumerator)
            .ThrowOnFailure();
        List<(string, string, bool)> outputs = [];
        try
        {
            var devices = (IMMDeviceEnumerator*)enumerator;
            string? defaultId = null;
            try
            {
                IMMDevice* fallback;
                devices->GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, &fallback);
                defaultId = DeviceId(fallback);
                _ = fallback->Release();
            }
            catch (COMException)
            {
                // Deliberately not logged: with no output device there is no default to flag.
            }

            IMMDeviceCollection* collection;
            devices->EnumAudioEndpoints(
                EDataFlow.eRender,
                DEVICE_STATE.DEVICE_STATE_ACTIVE,
                &collection
            );
            try
            {
                uint count;
                collection->GetCount(&count);
                for (uint index = 0; index < count; index++)
                {
                    IMMDevice* device;
                    collection->Item(index, &device);
                    try
                    {
                        string id = DeviceId(device);
                        outputs.Add((id, FriendlyName(device) ?? id, id == defaultId));
                    }
                    finally
                    {
                        _ = device->Release();
                    }
                }
            }
            finally
            {
                _ = collection->Release();
            }
        }
        finally
        {
            _ = ((IUnknown*)enumerator)->Release();
            if (initialized.Succeeded)
            {
                Win32.CoUninitialize();
            }
        }

        return outputs;
    }

    private static string DeviceId(IMMDevice* device)
    {
        PWSTR id;
        device->GetId(&id);
        try
        {
            return id.ToString();
        }
        finally
        {
            Win32.CoTaskMemFree(id);
        }
    }

    private static string? FriendlyName(IMMDevice* device)
    {
        IPropertyStore* store;
        device->OpenPropertyStore(STGM.STGM_READ, &store);
        try
        {
            PROPVARIANT value;
            PROPERTYKEY key = Win32.PKEY_Device_FriendlyName;
            store->GetValue(&key, &value);
            try
            {
                return value.Anonymous.Anonymous.vt == VARENUM.VT_LPWSTR
                    ? value.Anonymous.Anonymous.Anonymous.pwszVal.ToString()
                    : null;
            }
            finally
            {
                _ = Win32.PropVariantClear(&value);
            }
        }
        finally
        {
            _ = store->Release();
        }
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
