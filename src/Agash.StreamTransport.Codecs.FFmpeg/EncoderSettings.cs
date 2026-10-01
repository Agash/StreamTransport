using System.Collections.Immutable;
using System.Globalization;
using Agash.StreamTransport.Media;

namespace Agash.StreamTransport.Codecs.FFmpeg;

// Real-time settings per encoder. Every encoder opens with no reordering, no lookahead and constant
// bit rate under a rate-control buffer sized by the tuning; the private options below are each
// vendor's names for the same intent. FFmpeg rejects an option the encoder did not consume, so a name
// that drifts between FFmpeg releases fails the encoder's open.
internal static class EncoderSettings
{
    // The rate-control buffer, in seconds of the target bit rate. A shallow buffer keeps latency low
    // because the encoder cannot run ahead of the budget; a deep one rides out a varying uplink.
    public static double BufferSeconds(EncodeTuning tuning) =>
        tuning switch
        {
            EncodeTuning.ScreenContent => 1.0,
            EncodeTuning.LossResilient => 1.5,
            _ => 0.5,
        };

    // Frames between keyframes when the configuration asks for none: long enough never to matter,
    // since receivers ask for keyframes when they need them.
    public const int NoKeyframeInterval = 600 * 60;

    public static int GopSize(VideoEncoderConfiguration configuration)
    {
        if (configuration.KeyframeInterval <= TimeSpan.Zero)
        {
            return NoKeyframeInterval;
        }

        double frames =
            configuration.KeyframeInterval.TotalSeconds * configuration.Rate.FramesPerSecond;
        return (int)Math.Clamp(Math.Round(frames), 1, NoKeyframeInterval);
    }

    public static ImmutableDictionary<string, string> PrivateOptions(
        string encoder,
        EncodeTuning tuning
    )
    {
        bool interactive = tuning == EncodeTuning.Interactive;
        bool screen = tuning == EncodeTuning.ScreenContent;
        return encoder switch
        {
            // p4 balances speed and quality; ull drops lookahead and reordering outright.
            "h264_nvenc" or "hevc_nvenc" or "av1_nvenc" => Options(
                ("preset", "p4"),
                ("tune", interactive ? "ull" : "ll"),
                ("rc", "cbr"),
                ("zerolatency", "1"),
                ("delay", "0"),
                ("rc-lookahead", "0"),
                ("forced-idr", "1")
            ),
            // forced_idr: a requested keyframe is an IDR, which a receiver can start decoding from.
            "h264_amf" or "hevc_amf" or "av1_amf" => Options(
                ("usage", screen ? "lowlatency_high_quality" : "ultralowlatency"),
                ("rc", "cbr"),
                ("quality", screen ? "balanced" : "speed"),
                ("forced_idr", "1")
            ),
            "h264_qsv" or "hevc_qsv" or "av1_qsv" => Options(
                ("preset", "veryfast"),
                ("low_delay_brc", "1"),
                ("async_depth", "1")
            ),
            "h264_d3d12va" or "hevc_d3d12va" or "av1_d3d12va" => Options(
                ("rc_mode", "CBR"),
                ("async_depth", "1")
            ),
            "h264_vulkan" or "hevc_vulkan" or "av1_vulkan" => Options(
                ("rc_mode", "cbr"),
                ("tune", interactive ? "ull" : "ll"),
                ("usage", interactive ? "conference" : "stream"),
                ("content", screen ? "desktop" : "camera"),
                ("async_depth", "1")
            ),
            "h264_vaapi" or "hevc_vaapi" or "av1_vaapi" => Options(
                ("rc_mode", "CBR"),
                ("async_depth", "1")
            ),
            // realtime encodes at least as fast as capture. VideoToolbox's constant_bit_rate is not
            // supported for HEVC, so the ceiling comes from the rate-control buffer instead.
            "h264_videotoolbox" or "hevc_videotoolbox" => Options(
                ("realtime", "1"),
                ("prio_speed", "1")
            ),
            "h264_mf" or "hevc_mf" or "av1_mf" => Options(
                ("rate_control", "cbr"),
                ("scenario", screen ? "display_remoting" : "video_conference")
            ),
            "libopenh264" => Options(("rc_mode", "bitrate")),
            // gop=0: P-frames only, so output is in capture order.
            "libkvazaar" => Options(("kvazaar-params", "preset=ultrafast,gop=0")),
            // rtc selects SVT-AV1's real-time mode: low-delay prediction and rate control for it.
            "libsvtav1" => Options(("preset", "10"), ("svtav1-params", "rtc=1")),
            _ => [],
        };
    }

    // The options that make an encoder code the frames' alpha as the codec's alpha layer, or null when it
    // cannot. VideoToolbox codes H.265 with an alpha layer from BGRA when given an alpha quality; edges
    // of a keyed or rendered subject want it high. Other FFmpeg encoders take BGRA but drop its alpha.
    public static ImmutableDictionary<string, string>? AlphaLayerOptions(string encoder) =>
        encoder switch
        {
            "hevc_videotoolbox" => Options(("alpha_quality", "0.9")),
            _ => null,
        };

    // Whether the encoder keeps its low-latency rate control while coding an alpha layer. VideoToolbox
    // cannot create an HEVC-with-alpha session in low-latency mode (kVTParameterErr); without it, with no
    // B-frames and in real time, it still outputs each frame as it is encoded.
    public static bool LowDelayWithAlphaLayer(string encoder) => encoder != "hevc_videotoolbox";

    private static ImmutableDictionary<string, string> Options(
        params ReadOnlySpan<(string Key, string Value)> options
    )
    {
        ImmutableDictionary<string, string>.Builder builder = ImmutableDictionary.CreateBuilder<
            string,
            string
        >(StringComparer.Ordinal);
        foreach ((string key, string value) in options)
        {
            builder[key] = value;
        }

        return builder.ToImmutable();
    }

    public static string Describe(ImmutableDictionary<string, string> options) =>
        string.Join(
            ' ',
            options
                .OrderBy(static o => o.Key, StringComparer.Ordinal)
                .Select(static o =>
                    string.Create(CultureInfo.InvariantCulture, $"{o.Key}={o.Value}")
                )
        );
}
