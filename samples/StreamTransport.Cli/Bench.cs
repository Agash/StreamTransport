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
        streamtransport bench transcode <file>  every encoder that takes GPU frames, fed by a decoder that
                                                gives them on the same GPU: the zero-copy path a relay or a
                                                receiver re-encoding for OBS takes
        streamtransport bench encode [--codec h264|h265|av1] [--size WxH] [--fps n] [--frames n]
                                     [--input <file>]
                                                every encoder on NV12 from memory, steady and with the rate
                                                changing every second as congestion control moves it; the
                                                pictures are generated, or raw NV12 frames of --size read
                                                from --input (as ffmpeg -pix_fmt nv12 -f rawvideo writes),
                                                to compare with other encoders on the same pictures
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
            ["transcode", string file] => Transcode(file, services),
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

    private static int Transcode(string file, IServiceProvider services)
    {
        (VideoCodecId codec, List<(byte[] Data, bool Keyframe)> units) = Read(file);
        VideoCodecFormat format = new(codec);
        IVideoDecoderFactory[] decoders = [.. services.GetServices<IVideoDecoderFactory>()];
        foreach (IVideoEncoderFactory encoders in services.GetServices<IVideoEncoderFactory>())
        {
            if (encoders.QueryCapabilities(format, null) is not { } encoderInfo)
            {
                continue;
            }

            GpuIdentity? gpu = encoderInfo.Input.Device;
            foreach (
                VideoStorageKind storage in encoderInfo.Input.Storages.Where(static kind =>
                    kind != VideoStorageKind.Cpu
                )
            )
            {
                VideoConstraints output = new([storage], [PixelFormat.Nv12], gpu);
                if (
                    decoders.FirstOrDefault(d => d.QueryCapabilities(format, output) is not null)
                    is not { } decoderFactory
                )
                {
                    continue;
                }

                VideoDecoderInfo decoderInfo = decoderFactory.QueryCapabilities(format, output)!;
                using IVideoDecoder decoder = decoderFactory.Create(format, output);
                using IVideoEncoder encoder = encoders.Create(
                    new VideoEncoderConfiguration(
                        format,
                        new VideoSize(1920, 1080),
                        new RateTarget(8_000_000, 60)
                    ),
                    gpu
                );
                Counter encoded = new();
                Relay relay = new(encoder, encoded);
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
                            relay
                        ),
                    () =>
                    {
                        decoder.Flush(relay);
                        encoder.Flush(encoded);
                    }
                );
                Console.WriteLine(
                    $"{decoderInfo.ImplementationName} -> {encoderInfo.ImplementationName} ({storage}) {measurement} {encoded.Count} out"
                );
            }
        }

        return 0;
    }

    // Decoded frames straight into the encoder, as a relay re-encodes them.
    private sealed class Relay(IVideoEncoder encoder, IEncodedVideoConsumer units)
        : IVideoFrameConsumer
    {
        public void OnFrame(in VideoFrame frame) => encoder.Encode(in frame, default, units);
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

        byte[][] pictures = named.TryGetValue("input", out string? inputFile)
            ? RawPictures(inputFile, size)
            : Pictures(size, (int)fps);
        foreach (IVideoEncoderFactory factory in services.GetServices<IVideoEncoderFactory>())
        {
            if (factory.QueryCapabilities(new VideoCodecFormat(codec), null) is not { } info)
            {
                continue;
            }

            // NV12, or I420 converted before timing for an encoder that takes only that.
            PixelFormat input = info.Input.PixelFormats.Contains(PixelFormat.Nv12)
                ? PixelFormat.Nv12
                : PixelFormat.I420;
            if (!info.Input.PixelFormats.Contains(input))
            {
                Console.WriteLine(
                    $"{info.ImplementationName, -48} takes no 8-bit 4:2:0 frames in memory"
                );
                continue;
            }

            byte[][] given =
                input == PixelFormat.Nv12 ? pictures : [.. pictures.Select(p => ToI420(p, size))];

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
                            new CpuImage(PlaneLayout.Packed(input, size)),
                            new VideoFormat(input, size.Width, size.Height),
                            MediaTimestamp.Observed(new MediaTime((long)(i * 1e9 / fps))),
                            given[i % given.Length]
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

    // NV12's interleaved chroma split into I420's two planes.
    private static byte[] ToI420(byte[] nv12, VideoSize size)
    {
        int area = size.Width * size.Height;
        byte[] i420 = new byte[nv12.Length];
        nv12.AsSpan(0, area).CopyTo(i420);
        int quarter = area / 4;
        for (int i = 0; i < quarter; i++)
        {
            i420[area + i] = nv12[area + (2 * i)];
            i420[area + quarter + i] = nv12[area + (2 * i) + 1];
        }

        return i420;
    }

    // Raw NV12 frames back to back, as many as the file holds.
    private static byte[][] RawPictures(string path, VideoSize size)
    {
        int frame = size.Width * size.Height * 3 / 2;
        byte[] file = File.ReadAllBytes(path);
        if (file.Length < frame)
        {
            throw new FormatException($"{path} holds no whole {size.Width}x{size.Height} frame.");
        }

        byte[][] pictures = new byte[file.Length / frame][];
        for (int i = 0; i < pictures.Length; i++)
        {
            pictures[i] = file.AsSpan(i * frame, frame).ToArray();
        }

        return pictures;
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
