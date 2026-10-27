using System.Diagnostics;
using System.Globalization;
using Agash.StreamTransport.Media;
using Microsoft.Extensions.DependencyInjection;
using FF = FFmpeg.Interop;

namespace StreamTransport.Cli;

// Measures this machine's codecs as the registry has them: every registered decoder on a recording,
// every registered encoder on pictures from memory, steady and across rate changes. Each line gives the
// rate, the call times, the CPU the process used and what it allocated per frame, so a change can be
// judged on any platform by the same numbers.
internal static class Bench
{
    public const string Usage = """
        streamtransport bench decode <file>     every decoder on a recording (.h264, .h265, .obu, or any
                                                container FFmpeg reads), such as one made with --record
        streamtransport bench encode [--codec h264|h265|av1] [--size WxH] [--fps n] [--frames n]
                                                every encoder on NV12 from memory, steady and with the rate
                                                changing every second as congestion control moves it
        """;

    private static readonly VideoStorageKind[] s_storages =
    [
        VideoStorageKind.D3D12,
        VideoStorageKind.D3D11,
        VideoStorageKind.DmaBuf,
        VideoStorageKind.IOSurface,
        VideoStorageKind.Cpu,
    ];

    public static int Run(string[] args, IServiceProvider services) =>
        args switch
        {
            ["decode", string file] => Decode(file, services),
            ["encode", .. string[] options] => Encode(options, services),
            _ => Fail(),
        };

    private static int Fail()
    {
        Console.Error.WriteLine(Usage);
        return 2;
    }

    private static int Decode(string file, IServiceProvider services)
    {
        (VideoCodecId codec, List<(byte[] Data, bool Keyframe)> units) = Read(file);
        Console.WriteLine(
            $"{file}: {units.Count} {codec} access units, {units.Count(static u => u.Keyframe)} keyframes"
        );
        VideoCodecFormat format = new(codec);
        VideoConstraints output = new([.. s_storages], [PixelFormat.Nv12], null);
        foreach (IVideoDecoderFactory factory in services.GetServices<IVideoDecoderFactory>())
        {
            if (factory.QueryCapabilities(format, output) is not { } info)
            {
                continue;
            }

            using IVideoDecoder decoder = factory.Create(format, output);
            Counter frames = new();
            Measurement measurement = Measure(
                units.Count,
                i =>
                    decoder.Decode(
                        new EncodedVideoFrame(
                            units[i].Data,
                            codec,
                            units[i].Keyframe,
                            MediaTimestamp.Observed(new MediaTime(i * 16_666_667L))
                        ),
                        frames
                    ),
                () => decoder.Flush(frames)
            );
            Console.WriteLine($"{info.ImplementationName, -48} {measurement} {frames.Count} out");
        }

        return 0;
    }

    private static int Encode(string[] options, IServiceProvider services)
    {
        Dictionary<string, string> named = [];
        for (int i = 0; i + 1 < options.Length; i += 2)
        {
            named[options[i].TrimStart('-')] = options[i + 1];
        }

        VideoCodecId codec = named.TryGetValue("codec", out string? name)
            ? CodecOption(name)
            : VideoCodecId.H264;
        VideoSize size = named.TryGetValue("size", out string? text)
            ? SizeOption(text)
            : new VideoSize(1920, 1080);
        double fps = named.TryGetValue("fps", out string? rate)
            ? double.Parse(rate, CultureInfo.InvariantCulture)
            : 60;
        int count = named.TryGetValue("frames", out string? frames)
            ? int.Parse(frames, CultureInfo.InvariantCulture)
            : 600;

        byte[][] pictures = Pictures(size, (int)fps);
        foreach (IVideoEncoderFactory factory in services.GetServices<IVideoEncoderFactory>())
        {
            if (factory.QueryCapabilities(new VideoCodecFormat(codec), null) is not { } info)
            {
                continue;
            }

            foreach (bool changing in new[] { false, true })
            {
                using IVideoEncoder encoder = factory.Create(
                    new VideoEncoderConfiguration(
                        new VideoCodecFormat(codec),
                        size,
                        new RateTarget(8_000_000, fps)
                    ),
                    device: null
                );
                Counter units = new();
                Measurement measurement = Measure(
                    count,
                    i =>
                    {
                        // The rate swings by two fifths every second, as congestion control moves it.
                        if (changing && i > 0 && i % (int)fps == 0)
                        {
                            encoder.Reconfigure(
                                new RateTarget(i / (int)fps % 2 == 1 ? 4_800_000 : 8_000_000, fps)
                            );
                        }

                        VideoFrame frame = new(
                            new CpuImage(PlaneLayout.Packed(PixelFormat.Nv12, size)),
                            new VideoFormat(PixelFormat.Nv12, size.Width, size.Height),
                            MediaTimestamp.Observed(new MediaTime((long)(i * 1e9 / fps))),
                            pictures[i % pictures.Length]
                        );
                        encoder.Encode(in frame, default, units);
                    }
                );
                encoder.Flush(units);
                Console.WriteLine(
                    $"{info.ImplementationName, -48} {(changing ? "rate changing" : "steady"), -13} {measurement} {units.Keyframes} keyframes ({info.RateChange})"
                );
            }
        }

        return 0;
    }

    // Runs the step once per frame, after a few unmeasured ones that open the codec.
    // finish runs inside the timed span, for work the steps leave in flight.
    private static Measurement Measure(int count, Action<int> step, Action? finish = null)
    {
        const int warmup = 5;
        for (int i = 0; i < Math.Min(warmup, count); i++)
        {
            step(i);
        }

        double[] times = new double[Math.Max(0, count - warmup)];
        var self = Process.GetCurrentProcess();
        self.Refresh();
        TimeSpan cpu = self.TotalProcessorTime;
        long allocated = GC.GetTotalAllocatedBytes(precise: true);
        long started = Stopwatch.GetTimestamp();
        for (int i = warmup; i < count; i++)
        {
            long t = Stopwatch.GetTimestamp();
            step(i);
            times[i - warmup] = Stopwatch.GetElapsedTime(t).TotalMilliseconds;
        }

        finish?.Invoke();
        double seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
        self.Refresh();
        return new Measurement(
            times,
            seconds,
            (self.TotalProcessorTime - cpu).TotalSeconds / seconds,
            (GC.GetTotalAllocatedBytes(precise: true) - allocated)
                / (double)Math.Max(1, times.Length)
        );
    }

    // A recording's access units, read into memory first so the decoders are timed and not the disk.
    private static (VideoCodecId Codec, List<(byte[] Data, bool Keyframe)> Units) Read(string file)
    {
        string? format = Path.GetExtension(file).ToUpperInvariant() switch
        {
            ".H264" or ".264" => "h264",
            ".H265" or ".265" or ".HEVC" => "hevc",
            ".OBU" => "obu",
            _ => null,
        };
        using var reader = FF.MediaReader.Open(file, format: format);
        FF.MediaStream video =
            reader.FindBestStream(FF.MediaType.Video)
            ?? throw new InvalidOperationException($"{file} has no video.");
        FF.CodecId id = video.CodecId;
        VideoCodecId codec =
            id == FF.CodecId.H264 ? VideoCodecId.H264
            : id == FF.CodecId.Hevc ? VideoCodecId.H265
            : id == FF.CodecId.Av1 ? VideoCodecId.AV1
            : throw new NotSupportedException($"{file} is {id}.");
        List<(byte[], bool)> units = [];
        using FF.Packet packet = new();
        while (reader.TryReadPacket(packet))
        {
            if (packet.StreamIndex == video.Index)
            {
                units.Add((packet.Data.ToArray(), packet.IsKeyFrame));
            }

            packet.Reset();
        }

        return (codec, units);
    }

    // A second of pictures with moving detail, so the encoders have something to spend their rate on.
    private static byte[][] Pictures(VideoSize size, int count)
    {
        int area = size.Width * size.Height;
        byte[][] pictures = new byte[count][];
        for (int n = 0; n < count; n++)
        {
            byte[] picture = new byte[area * 3 / 2];
            for (int y = 0; y < size.Height; y++)
            {
                for (int x = 0; x < size.Width; x++)
                {
                    picture[(y * size.Width) + x] = (byte)((x + (n * 4)) ^ (y + (n * 2)));
                }
            }

            picture.AsSpan(area).Fill(128);
            pictures[n] = picture;
        }

        return pictures;
    }

    private static VideoCodecId CodecOption(string name) =>
        name.ToUpperInvariant() switch
        {
            "H264" => VideoCodecId.H264,
            "H265" or "HEVC" => VideoCodecId.H265,
            "AV1" => VideoCodecId.AV1,
            _ => throw new FormatException($"--codec {name}: h264, h265 or av1."),
        };

    private static VideoSize SizeOption(string text) =>
        text.Split('x') is [string w, string h]
            ? new VideoSize(
                int.Parse(w, CultureInfo.InvariantCulture),
                int.Parse(h, CultureInfo.InvariantCulture)
            )
            : throw new FormatException($"--size {text}: WIDTHxHEIGHT.");

    private sealed record Measurement(
        double[] Times,
        double Seconds,
        double CpuCores,
        double BytesPerFrame
    )
    {
        public override string ToString()
        {
            double[] sorted = [.. Times.Order()];
            double median = sorted.Length > 0 ? sorted[sorted.Length / 2] : 0;
            return FormattableString.Invariant(
                $"{Times.Length / Seconds, 6:0} fps, median {median:0.0} ms, p99 {(sorted.Length > 0 ? sorted[sorted.Length * 99 / 100] : 0):0.0} ms, max {(sorted.Length > 0 ? sorted[^1] : 0):0.0} ms, over 2x median {sorted.Count(t => t > 2 * median)}, CPU {CpuCores * 100:0}% of a core, {BytesPerFrame:0} B allocated a frame,"
            );
        }
    }

    private sealed class Counter : IVideoFrameConsumer, IEncodedVideoConsumer
    {
        public int Count { get; private set; }

        public int Keyframes { get; private set; }

        public void OnFrame(in VideoFrame frame) => Count++;

        public void OnEncoded(in EncodedVideoFrame frame)
        {
            Count++;
            if (frame.Keyframe)
            {
                Keyframes++;
            }
        }
    }
}
