using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace Agash.StreamTransport.Codecs;

/// <summary>Turns negative FFmpeg return codes into exceptions with a decoded message.</summary>
internal static unsafe class FFmpegError
{
    public static int ThrowOnError(this int error, string context)
    {
        if (error >= 0)
        {
            return error;
        }

        byte* buffer = stackalloc byte[1024];
        ffmpeg.av_strerror(error, buffer, 1024);
        string message = Marshal.PtrToStringAnsi((nint)buffer) ?? "unknown";
        throw new InvalidOperationException($"FFmpeg error during {context}: {message} ({error}).");
    }
}
