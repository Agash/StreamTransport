using System.Runtime.InteropServices;
using Agash.StreamTransport.Media;
using AVFoundation;
using Foundation;

namespace Agash.StreamTransport.MacOS.Audio;

/// <summary>
/// Turns captured buffers, in whatever format the device delivers, into the transport's 48 kHz stereo
/// float, stamped with when their first sample was captured on the media clock. A mono device is heard
/// on both channels. Used from one thread at a time.
/// </summary>
internal sealed class CapturedAudio : IDisposable
{
    private readonly AVAudioFormat _output = new(
        AVAudioCommonFormat.PCMFloat32,
        CoreAudioSink.Format.SampleRate,
        2,
        true
    );
    private AVAudioConverter? _converter;
    private AVAudioFormat? _input;

    /// <summary>Converts a buffer and hands the result to a consumer.</summary>
    /// <param name="captured">The buffer as the device delivered it.</param>
    /// <param name="when">When its first sample was captured.</param>
    /// <param name="consumer">Where the converted audio goes.</param>
    /// <param name="failure">Why the conversion failed, when it did.</param>
    /// <returns>False when the conversion failed.</returns>
    public unsafe bool TryConvert(
        AVAudioPcmBuffer captured,
        AVAudioTime when,
        IAudioFrameConsumer consumer,
        out string? failure
    )
    {
        failure = null;
        AVAudioConverter converter = ConverterFor(captured.Format);
        uint capacity =
            (uint)
                Math.Ceiling(captured.FrameLength * _output.SampleRate / captured.Format.SampleRate)
            + 16;
        using AVAudioPcmBuffer converted = new(_output, capacity);
        bool supplied = false;
        AVAudioConverterOutputStatus status = converter.ConvertToBuffer(
            converted,
            out NSError? error,
            (uint _, out AVAudioConverterInputStatus input) =>
            {
                input = supplied
                    ? AVAudioConverterInputStatus.NoDataNow
                    : AVAudioConverterInputStatus.HaveData;
                supplied = true;
                return captured;
            }
        );
        if (status == AVAudioConverterOutputStatus.Error)
        {
            failure = error?.LocalizedDescription ?? status.ToString();
            return false;
        }

        if (converted.FrameLength > 0)
        {
            float* samples = *(float**)converted.FloatChannelData;
            AudioFrame frame = new(
                MemoryMarshal.AsBytes(
                    new ReadOnlySpan<float>(samples, (int)converted.FrameLength * 2)
                ),
                CoreAudioSink.Format,
                MediaTimestamp.Captured(CaptureTime(when))
            );
            consumer.OnFrame(in frame);
        }

        return true;
    }

    public void Dispose()
    {
        _converter?.Dispose();
        _input?.Dispose();
        _output.Dispose();
    }

    // Core Audio's host time is the monotonic clock the media clock runs on.
    private static MediaTime CaptureTime(AVAudioTime when) =>
        when.HostTimeValid
            ? new MediaTime((long)(AVAudioTime.SecondsForHostTime(when.HostTime) * 1e9))
            : MediaClock.System.Now;

    // The converter for a captured format, made again when the format changes.
    private AVAudioConverter ConverterFor(AVAudioFormat captured)
    {
        if (_converter is { } current && _input is { } known && known.IsEqual(captured))
        {
            return current;
        }

        _converter?.Dispose();
        _input?.Dispose();
        _input = captured;
        _converter = new AVAudioConverter(captured, _output);
        if (captured.ChannelCount == 1)
        {
            _converter.ChannelMap = [NSNumber.FromInt32(0), NSNumber.FromInt32(0)];
        }

        return _converter;
    }
}
