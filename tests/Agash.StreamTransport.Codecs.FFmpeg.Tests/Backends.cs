using Agash.StreamTransport.Codecs.FFmpeg;
using Agash.StreamTransport.Media;

namespace Agash.StreamTransport.Codecs.FFmpeg.Tests;

// The backends this operating system registers, with the codecs the loaded FFmpeg has for each and the GPU
// surfaces each declares, as the package itself lists them: test rows are made from these, so a machine
// runs only rows its platform and its FFmpeg can have. What remains inconclusive is hardware.
internal static class Backends
{
    public static IEnumerable<EncoderBackend> Encoders =>
        EncoderFactories.Select(static f => f.Backend);

    public static IEnumerable<DecoderBackend> Decoders =>
        DecoderFactories.Select(static f => f.Backend);

    public static IEnumerable<(EncoderBackend Backend, VideoCodecId Codec)> EncoderCodecs =>
        from factory in EncoderFactories
        from format in factory.SupportedFormats
        select (factory.Backend, format.Codec);

    public static IEnumerable<(DecoderBackend Backend, VideoCodecId Codec)> DecoderCodecs =>
        from factory in DecoderFactories
        from format in factory.SupportedFormats
        select (factory.Backend, format.Codec);

    public static IEnumerable<(EncoderBackend Backend, VideoStorageKind Storage)> EncoderStorages =>
        from factory in EncoderFactories
        from storage in factory.GpuStorages
        select (factory.Backend, storage);

    public static bool Encodes(EncoderBackend backend, VideoStorageKind storage) =>
        EncoderStorages.Contains((backend, storage));

    public static bool Decodes(DecoderBackend backend, VideoStorageKind storage) =>
        DecoderFactories.Any(f => f.Backend == backend && f.GpuStorages.Contains(storage));

    private static IEnumerable<FFmpegVideoEncoderFactory> EncoderFactories =>
        FFmpegVideoEncoderFactory.CreateAll();

    private static IEnumerable<FFmpegVideoDecoderFactory> DecoderFactories =>
        FFmpegVideoDecoderFactory.CreateAll();
}
