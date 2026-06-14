using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace Agash.StreamTransport.Codecs;

// Diagnostic-only: routes FFmpeg's log to a file so VAAPI/driver errors (which otherwise vanish - the default
// callback's stderr is not captured under the test host) are visible. Enabled by setting STX_DMABUF_DEBUG to
// a file path. Most VAAPI error messages carry no printf args, so logging the raw format string identifies them.
internal static unsafe class FfmpegLog
{
    private static av_log_set_callback_callback? s_callback;
    private static string? s_path;

    public static void InstallIfRequested()
    {
        string? path = Environment.GetEnvironmentVariable("STX_DMABUF_DEBUG");
        if (string.IsNullOrEmpty(path) || s_callback is not null)
        {
            return;
        }

        s_path = path;
        s_callback = LogCallback;
        ffmpeg.av_log_set_level(ffmpeg.AV_LOG_VERBOSE);
        ffmpeg.av_log_set_callback(s_callback);
    }

    private static void LogCallback(void* avcl, int level, string fmt, byte* vl)
    {
        if (level > ffmpeg.AV_LOG_VERBOSE || s_path is null)
        {
            return;
        }

        try
        {
            File.AppendAllText(s_path, $"[ff {level}] {fmt}");
        }
        catch (IOException)
        {
            // best-effort diagnostics; ignore log write races
        }
    }
}
