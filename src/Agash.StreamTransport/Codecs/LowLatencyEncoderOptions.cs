using FFmpeg.AutoGen;

namespace Agash.StreamTransport.Codecs;

/// <summary>
/// Applies low-latency encoder options tuned for real-time streaming. Each hardware encoder family
/// exposes its own option names, so the settings are selected by encoder rather than shared.
/// </summary>
internal static unsafe class LowLatencyEncoderOptions
{
    public static void Apply(AVDictionary** options, string encoderName)
    {
        switch (encoderName)
        {
            case "hevc_nvenc" or "h264_nvenc":
                Set(options, ("preset", "p4"), ("tune", "ll"), ("rc", "cbr"), ("zerolatency", "1"), ("delay", "0"));
                break;

            case "hevc_amf" or "h264_amf":
                Set(options, ("usage", "ultralowlatency"), ("rc", "cbr"), ("quality", "speed"));
                break;

            case "hevc_qsv" or "h264_qsv":
                Set(options, ("preset", "veryfast"), ("low_delay_brc", "1"), ("async_depth", "1"));
                break;

            case "hevc_videotoolbox" or "h264_videotoolbox":
                Set(options, ("realtime", "1"));
                break;

            case "hevc_rkmpp" or "h264_rkmpp":
                Set(options, ("rc_mode", "cbr"));
                break;

            default:
                break;
        }
    }

    private static void Set(AVDictionary** options, params (string Key, string Value)[] entries)
    {
        foreach ((string key, string value) in entries)
        {
            ffmpeg.av_dict_set(options, key, value, 0);
        }
    }
}
