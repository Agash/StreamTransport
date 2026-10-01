using System.Reflection;
using Agash.StreamTransport.Codecs.FFmpeg;
using Agash.StreamTransport.Media;
using FFmpeg.Interop;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agash.StreamTransport.Tests;

[TestClass]
public sealed class LoggingTests
{
    [TestMethod]
    public void EventIds_AreExplicitAndUniqueAcrossTheLibrary()
    {
        List<(int Id, string Method)> ids = [];
        foreach (
            string path in Directory.GetFiles(
                AppContext.BaseDirectory,
                "Agash.StreamTransport*.dll"
            )
        )
        {
            if (Path.GetFileName(path).Contains(".Tests", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (Type type in Assembly.LoadFrom(path).GetTypes())
            {
                foreach (
                    MethodInfo method in type.GetMethods(
                        BindingFlags.Instance
                            | BindingFlags.Static
                            | BindingFlags.Public
                            | BindingFlags.NonPublic
                            | BindingFlags.DeclaredOnly
                    )
                )
                {
                    if (method.GetCustomAttribute<LoggerMessageAttribute>() is { } attribute)
                    {
                        string name = $"{type.FullName}.{method.Name}";
                        Assert.AreNotEqual(-1, attribute.EventId, $"{name} has no event id");
                        ids.Add((attribute.EventId, name));
                    }
                }
            }
        }

        Assert.IsGreaterThan(
            50,
            ids.Count,
            $"the library's assemblies were found ({ids.Count} log methods)"
        );
        string[] duplicates =
        [
            .. ids.GroupBy(static i => i.Id)
                .Where(static g => g.Count() > 1)
                .Select(static g =>
                    $"{g.Key}: {string.Join(", ", g.Select(static i => i.Method))}"
                ),
        ];
        Assert.IsEmpty(duplicates, string.Join("; ", duplicates));
    }

    [TestMethod]
    public void AddFFmpegCodecs_RoutesFFmpegsLogUntilTheContainerIsDisposed()
    {
        CapturingLoggerFactory logs = new();
        ServiceCollection services = new();
        services.AddSingleton<ILoggerFactory>(logs);
        services.AddFFmpegCodecs();
        using (ServiceProvider provider = services.BuildServiceProvider())
        {
            _ = provider.GetServices<IVideoDecoderFactory>().ToList();
            FeedGarbage();
            Assert.Contains("AVCodecContext: h264:", logs.Dump());
        }

        string routed = logs.Dump();
        FeedGarbage();
        Assert.AreEqual(routed, logs.Dump(), "the disposed container no longer gets FFmpeg's log");
    }

    private static void FeedGarbage()
    {
        using var decoder = Decoder.Create(
            Codec.FindDecoder(CodecId.H264),
            new DecoderOptions { ThreadCount = 1 }
        );
        using var packet = new Packet();
        packet.CopyFrom([0, 0, 0, 1, 0x65, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]);
        try
        {
            _ = decoder.TrySend(packet);
        }
        catch (FFmpegException)
        {
            // Expected: decoding on this thread reports the garbage at once.
        }
    }
}
