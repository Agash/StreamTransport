using System.Runtime.InteropServices;
using Agash.StreamTransport.Media;
using FF = FFmpeg.Interop;

namespace Agash.StreamTransport.Codecs.FFmpeg.Tests;

[TestClass]
public static class Natives
{
    // The FFmpeg 9 shared libraries eng/fetch-ffmpeg.ps1 puts under native/ffmpeg/<rid>.
    [AssemblyInitialize]
    public static void Load(TestContext context)
    {
        _ = context;
        string? directory = AppContext.BaseDirectory;
        while (
            directory is not null && !File.Exists(Path.Combine(directory, "StreamTransport.slnx"))
        )
        {
            directory = Path.GetDirectoryName(directory);
        }

        string natives = Path.Combine(
            directory ?? throw new InvalidOperationException("The repository root was not found."),
            "native",
            "ffmpeg",
            RuntimeInformation.RuntimeIdentifier
        );
        if (Directory.Exists(natives))
        {
            FF.FFmpegLibraries.SearchDirectory = natives;
        }
    }
}

// Synthetic pictures whose content moves every frame, and the means to check what an encoder made.
internal static class Pictures
{
    public const int Width = 320;
    public const int Height = 240;

    public static VideoSize Size => new(Width, Height);

    // A diagonal gradient that slides by a few pixels per frame, with 4:2:0 chroma in NV12 or I420.
    public static byte[] Make(PixelFormat format, int index)
    {
        byte[] pixels = new byte[PlaneLayout.PackedSize(format, Size)];
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                pixels[(y * Width) + x] = Luma(index, x, y);
            }
        }

        pixels.AsSpan(Width * Height).Fill(128);
        return pixels;
    }

    public static byte Luma(int index, int x, int y) => (byte)(32 + ((x + y + (index * 3)) % 192));

    public static double MeanLuma(int index)
    {
        long sum = 0;
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                sum += Luma(index, x, y);
            }
        }

        return sum / (double)(Width * Height);
    }

    public static MediaTimestamp Timestamp(int index) =>
        MediaTimestamp.Captured(new MediaTime(1_000_000_000L + (index * 33_333_333L)));

    public static VideoFrame CpuFrame(PixelFormat format, byte[] pixels, int index) =>
        new(
            new CpuImage(PlaneLayout.Packed(format, Size)),
            new VideoFormat(format, Width, Height),
            Timestamp(index),
            pixels
        );
}

// Keeps every access unit an encoder hands out.
internal sealed class Collector : IEncodedVideoConsumer
{
    public List<(byte[] Data, bool Keyframe, MediaTimestamp Timestamp)> Units { get; } = [];

    public void OnEncoded(in EncodedVideoFrame frame) =>
        Units.Add((frame.Data.ToArray(), frame.Keyframe, frame.Timestamp));

    public long Bytes(int from, int to) =>
        Units.Skip(from).Take(to - from).Sum(static unit => (long)unit.Data.Length);
}

// Decodes access units with FFmpeg's software decoder for the codec, independent of the encoder.
internal static class Reference
{
    public static List<(int Width, int Height, double MeanLuma)> Decode(
        VideoCodecId codec,
        IEnumerable<byte[]> units
    )
    {
        string name = codec switch
        {
            VideoCodecId.H264 => "h264",
            VideoCodecId.H265 => "hevc",
            _ => "libdav1d",
        };
        List<(int, int, double)> frames = [];
        using var decoder = FF.Decoder.Create(FF.Codec.FindDecoder(name));
        using FF.Packet packet = new();
        using FF.Frame output = new();
        using FF.Frame yuv = new();
        using FF.Scaler scaler = new();
        void Collect(FF.Frame frame)
        {
            yuv.Width = frame.Width;
            yuv.Height = frame.Height;
            yuv.PixelFormat = FF.PixelFormat.Yuv420P;
            scaler.Scale(frame, yuv);
            FF.ReadOnlyImagePlane luma = yuv.GetPlane(0);
            long sum = 0;
            for (int row = 0; row < luma.Height; row++)
            {
                foreach (byte value in luma.GetRow(row))
                {
                    sum += value;
                }
            }

            frames.Add((frame.Width, frame.Height, sum / (double)(luma.Height * luma.RowLength)));
        }

        foreach (byte[] unit in units)
        {
            packet.CopyFrom(unit);
            foreach (FF.Frame frame in decoder.Decode(packet, output))
            {
                Collect(frame);
            }
        }

        foreach (FF.Frame frame in decoder.Decode(null, output))
        {
            Collect(frame);
        }

        return frames;
    }
}

internal static class Streams
{
    // The moving gradient, encoded in software where it can be.
    public static List<(byte[] Data, MediaTimestamp Timestamp)> Encode(VideoCodecId codec, int frameCount)
    {
        VideoEncoderConfiguration configuration = new(
            new VideoCodecFormat(codec),
            Pictures.Size,
            new RateTarget(4_000_000, 30)
        );
        FFmpegVideoEncoderFactory factory =
            FFmpegVideoEncoderFactory
                .CreateAll()
                .OrderBy(static f => f.IsHardwareAccelerated)
                .FirstOrDefault(f =>
                    f.QueryCapabilities(configuration.Format, device: null) is { } info
                    && info.Input.PixelFormats.Any(static p =>
                        p is PixelFormat.Nv12 or PixelFormat.I420
                    )
                )
            ?? throw new AssertInconclusiveException($"Nothing on this machine encodes {codec}.");
        using IVideoEncoder encoder = factory.Create(configuration, device: null);
        PixelFormat format = encoder.Info.Input.PixelFormats.First(static f =>
            f is PixelFormat.Nv12 or PixelFormat.I420
        );
        Collector collector = new();
        for (int i = 0; i < frameCount; i++)
        {
            byte[] pixels = Pictures.Make(format, i);
            VideoFrame frame = Pictures.CpuFrame(format, pixels, i);
            encoder.Encode(in frame, new EncodeRequest(Keyframe: i == 0), collector);
        }

        encoder.Flush(collector);
        return [.. collector.Units.Select(static u => (u.Data, u.Timestamp))];
    }
}
