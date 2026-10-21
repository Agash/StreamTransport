using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.Audio;
using Windows.Win32.Media.Audio.Endpoints;
using Windows.Win32.System.Com;

namespace Agash.StreamTransport.Windows.Tests;

/// <summary>
/// The default output's volume, read for a test and its mute undone for the test's duration.
/// </summary>
/// <remarks>
/// Loopback capture hears the mix after the endpoint's software volume, so what a test reads back
/// depends on where the machine's volume sits. Disposing restores the mute state as it was.
/// </remarks>
internal sealed unsafe class DefaultOutputVolume : IDisposable
{
    private IAudioEndpointVolume* _volume;
    private readonly bool _wasMuted;

    private DefaultOutputVolume(IAudioEndpointVolume* volume)
    {
        _volume = volume;
        BOOL muted;
        volume->GetMute(&muted);
        _wasMuted = muted;
        float level;
        volume->GetMasterVolumeLevel(&level);
        LevelDecibels = level;
    }

    /// <summary>The output's level, in dB: 0 at full volume.</summary>
    public float LevelDecibels { get; }

    /// <summary>What the volume does to an amplitude: 1 at full volume.</summary>
    public double Gain => Math.Pow(10, LevelDecibels / 20);

    /// <summary>Opens the default render endpoint's volume and unmutes it until disposed.</summary>
    public static DefaultOutputVolume Unmuted()
    {
        TestWin32.CoInitializeEx(null, COINIT.COINIT_MULTITHREADED);
        Guid enumeratorClass = typeof(MMDeviceEnumerator).GUID;
        Guid enumeratorId = typeof(IMMDeviceEnumerator).GUID;
        void* enumerator;
        TestWin32
            .CoCreateInstance(&enumeratorClass, null, CLSCTX.CLSCTX_ALL, &enumeratorId, &enumerator)
            .ThrowOnFailure();
        IMMDevice* device;
        try
        {
            ((IMMDeviceEnumerator*)enumerator)->GetDefaultAudioEndpoint(
                EDataFlow.eRender,
                ERole.eMultimedia,
                &device
            );
        }
        finally
        {
            _ = ((IUnknown*)enumerator)->Release();
        }

        void* volume;
        try
        {
            Guid volumeId = typeof(IAudioEndpointVolume).GUID;
            device->Activate(&volumeId, CLSCTX.CLSCTX_ALL, null, &volume);
        }
        finally
        {
            _ = device->Release();
        }

        DefaultOutputVolume opened = new((IAudioEndpointVolume*)volume);
        if (opened._wasMuted)
        {
            opened._volume->SetMute(false, null);
        }

        return opened;
    }

    public void Dispose()
    {
        if (_volume is null)
        {
            return;
        }

        if (_wasMuted)
        {
            _volume->SetMute(true, null);
        }

        _ = _volume->Release();
        _volume = null;
    }
}
