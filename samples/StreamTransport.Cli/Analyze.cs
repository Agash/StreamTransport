using System.Numerics.Tensors;
using Agash.StreamTransport.Media;
using Agash.StreamTransport.TestSignal;
using FFmpeg.Interop;
using FFmpegMediaType = FFmpeg.Interop.MediaType;

namespace StreamTransport.Cli;

// Measures the test signal in a recording, such as OBS's: each flash and click at its presentation time,
// so the offset is what a viewer of the recording sees and hears.
internal static class Analyze
{
    public static int Run(string path, string? ffmpeg)
    {
        if (ffmpeg is not null)
        {
            FFmpegLibraries.SearchDirectory = ffmpeg;
        }

        // A recording's level is whatever the capture made it; clicks are found at a third of its loudest.
        float peak = AudioPeak(path);
        if (peak <= 0)
        {
            throw new InvalidOperationException($"{path} is silent.");
        }

        using var reader = MediaReader.Open(path);
        MediaStream video =
            reader.FindBestStream(FFmpegMediaType.Video)
            ?? throw new InvalidOperationException($"{path} has no video stream.");
        MediaStream audio =
            reader.FindBestStream(FFmpegMediaType.Audio)
            ?? throw new InvalidOperationException($"{path} has no audio stream.");
        using Decoder videoDecoder = video.CreateDecoder();
        using Decoder audioDecoder = audio.CreateDecoder();
        using Packet packet = new();
        using Frame frame = new();
        TestSignalAnalyzer analyzer = new() { ClickThreshold = peak / 3 };
        byte[] luma = [];
        long pictures = 0;
        while (reader.TryReadPacket(packet))
        {
            if (packet.StreamIndex == video.Index)
            {
                foreach (Frame picture in videoDecoder.Decode(packet, frame))
                {
                    ReadOnlyImagePlane plane = picture.GetPlane(0);
                    int size = plane.Stride * plane.Height;
                    if (luma.Length < size)
                    {
                        luma = new byte[size];
                    }

                    plane.CopyTo(luma);
                    analyzer.ObserveVideo(
                        luma.AsSpan(0, size),
                        plane.Stride,
                        new VideoSize(picture.Width, picture.Height),
                        At(picture, video),
                        received: null
                    );
                    pictures++;
                }
            }
            else if (packet.StreamIndex == audio.Index)
            {
                foreach (Frame samples in audioDecoder.Decode(packet, frame))
                {
                    // Planar audio holds the first channel in plane 0; interleaved holds all of them.
                    bool planar = samples.SampleFormat.IsPlanar;
                    int channels = planar ? 1 : samples.ChannelLayout.ChannelCount;
                    if (samples.SampleFormat.ToInterleaved().BytesPerSample != sizeof(float))
                    {
                        throw new InvalidOperationException(
                            $"The audio decodes to {samples.SampleFormat.Name}; the analysis reads float samples."
                        );
                    }

                    analyzer.ObserveAudio(
                        samples.GetSamples<float>(0)[..(samples.SampleCount * channels)],
                        channels,
                        samples.SampleRate,
                        At(samples, audio)
                    );
                }
            }
        }

        TestSignalMeasurement measured = analyzer.Measure();
        Console.WriteLine(
            FormattableString.Invariant(
                $"{path}: {pictures} pictures; A/V offset {measured.MeanOffset.TotalMilliseconds:0.0} ms ({measured.MinOffset.TotalMilliseconds:0.0} to {measured.MaxOffset.TotalMilliseconds:0.0}) over {measured.Pairs} clicks, positive when audio lags"
            )
        );
        return measured.Pairs > 0 ? 0 : 1;
    }

    // The loudest sample of the first audio channel.
    private static float AudioPeak(string path)
    {
        using var reader = MediaReader.Open(path);
        MediaStream audio =
            reader.FindBestStream(FFmpegMediaType.Audio)
            ?? throw new InvalidOperationException($"{path} has no audio stream.");
        using Decoder decoder = audio.CreateDecoder();
        using Packet packet = new();
        using Frame frame = new();
        float peak = 0;
        while (reader.TryReadPacket(packet))
        {
            if (packet.StreamIndex != audio.Index)
            {
                continue;
            }

            foreach (Frame samples in decoder.Decode(packet, frame))
            {
                int channels = samples.SampleFormat.IsPlanar
                    ? 1
                    : samples.ChannelLayout.ChannelCount;
                ReadOnlySpan<float> first = samples.GetSamples<float>(0)[
                    ..(samples.SampleCount * channels)
                ];
                peak = Math.Max(peak, TensorPrimitives.MaxMagnitude(first));
            }
        }

        return peak;
    }

    // A decoded frame's presentation time, on the media clock's scale from the file's start. A frame
    // without one (a decoder that lost track) counts at the stream's start, which no pairing matches.
    private static MediaTime At(Frame frame, MediaStream stream) =>
        MediaTime.FromTimeSpan(stream.TimeBase.ToTimeSpan(frame.PresentationTimestamp ?? 0));
}
