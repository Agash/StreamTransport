using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace Agash.StreamTransport.Codecs;

/// <summary>
/// Selects the libavdevice input format for the current OS. The same code path captures a camera + mic
/// on every platform - dshow on Windows, AVFoundation on macOS, Video4Linux2 / ALSA on Linux (including
/// the rockchip IRL board's v4l2 camera) - so the agent has one capture story everywhere.
/// </summary>
public static class CaptureBackend
{
    /// <summary>The libavdevice input-format name for camera capture on this OS.</summary>
    public static string VideoInputFormat =>
        OperatingSystem.IsWindows() ? "dshow" : OperatingSystem.IsMacOS() ? "avfoundation" : "v4l2";

    /// <summary>The libavdevice input-format name for audio capture on this OS.</summary>
    public static string AudioInputFormat =>
        OperatingSystem.IsWindows() ? "dshow" : OperatingSystem.IsMacOS() ? "avfoundation" : "alsa";

    /// <summary>
    /// Whether one input can carry both video and audio on this OS. dshow and AVFoundation expose a
    /// combined device URL; v4l2/ALSA need two separate inputs.
    /// </summary>
    public static bool SupportsCombinedInput => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
}

/// <summary>
/// Captures live camera and/or microphone input through FFmpeg's <c>libavdevice</c>, decodes it, and
/// exposes it as an <see cref="IVideoFrameSource"/> (NV12, ready for hardware HEVC encode) and an
/// <see cref="IAudioFrameSource"/> (16-bit PCM at 48 kHz). Both streams are stamped from one monotonic
/// clock so WebRTC lip-syncs them on the receiver. A background reader pulls packets; the sources hand
/// out the latest video frame and queued audio frames without blocking the encode pumps.
/// </summary>
public sealed unsafe class FFmpegCaptureDevice : IDisposable
{
    private readonly AVFormatContext* _format;
    private readonly AVPacket* _packet;
    private readonly AVFrame* _decoded;
    private readonly CancellationTokenSource _cts = new();
    private readonly VideoStream? _video;
    private readonly AudioStream? _audio;
    private Task? _reader;
    private bool _disposed;

    private FFmpegCaptureDevice(AVFormatContext* format, VideoStream? video, AudioStream? audio)
    {
        _format = format;
        _video = video;
        _audio = audio;
        _packet = ffmpeg.av_packet_alloc();
        _decoded = ffmpeg.av_frame_alloc();
    }

    /// <summary>The captured video as a frame source, or null when the input has no video stream.</summary>
    public IVideoFrameSource? Video => _video;

    /// <summary>The captured audio as a frame source, or null when the input has no audio stream.</summary>
    public IAudioFrameSource? Audio => _audio;

    /// <summary>
    /// Open a capture device. <paramref name="url"/> is the libavdevice device URL for
    /// <paramref name="inputFormat"/> (e.g. <c>"video=Integrated Camera:audio=Microphone"</c> for dshow,
    /// <c>"0:0"</c> for AVFoundation, <c>"/dev/video0"</c> for v4l2). <paramref name="options"/> are passed
    /// to the device (e.g. <c>framerate</c>, <c>video_size</c>, <c>pixel_format</c>).
    /// </summary>
    public static FFmpegCaptureDevice Open(
        string inputFormat, string url, IReadOnlyDictionary<string, string>? options = null)
    {
        FFmpegLibrary.EnsureLoaded();
        ffmpeg.avdevice_register_all();

        AVInputFormat* format = ffmpeg.av_find_input_format(inputFormat);
        if (format is null)
        {
            throw new NotSupportedException($"libavdevice input format '{inputFormat}' is not available in this FFmpeg build.");
        }

        AVDictionary* opts = null;
        if (options is not null)
        {
            foreach ((string key, string value) in options)
            {
                ffmpeg.av_dict_set(&opts, key, value, 0);
            }
        }

        AVFormatContext* ctx = null;
        int open = ffmpeg.avformat_open_input(&ctx, url, format, &opts);
        ffmpeg.av_dict_free(&opts);
        if (open < 0)
        {
            throw new InvalidOperationException($"Could not open capture device '{url}' (FFmpeg error {open}).");
        }

        if (ffmpeg.avformat_find_stream_info(ctx, null) < 0)
        {
            ffmpeg.avformat_close_input(&ctx);
            throw new InvalidOperationException($"Could not read stream info from '{url}'.");
        }

        VideoStream? video = null;
        AudioStream? audio = null;
        for (int i = 0; i < (int)ctx->nb_streams; i++)
        {
            AVStream* stream = ctx->streams[i];
            AVMediaType type = stream->codecpar->codec_type;
            if (type == AVMediaType.AVMEDIA_TYPE_VIDEO && video is null)
            {
                video = VideoStream.Create(i, stream);
            }
            else if (type == AVMediaType.AVMEDIA_TYPE_AUDIO && audio is null)
            {
                audio = AudioStream.Create(i, stream);
            }
        }

        if (video is null && audio is null)
        {
            ffmpeg.avformat_close_input(&ctx);
            throw new InvalidOperationException($"Capture device '{url}' exposed no decodable audio or video stream.");
        }

        var device = new FFmpegCaptureDevice(ctx, video, audio);
        device.Start();
        return device;
    }

    private void Start() => _reader = Task.Run(() => ReadLoop(_cts.Token));

    private void ReadLoop(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            int read = ffmpeg.av_read_frame(_format, _packet);
            if (read < 0)
            {
                if (read == ffmpeg.AVERROR_EOF)
                {
                    return;
                }

                continue; // device hiccup; keep reading.
            }

            try
            {
                if (_video is not null && _packet->stream_index == _video.Index)
                {
                    _video.Decode(_packet, _decoded);
                }
                else if (_audio is not null && _packet->stream_index == _audio.Index)
                {
                    _audio.Decode(_packet, _decoded);
                }
            }
            finally
            {
                ffmpeg.av_packet_unref(_packet);
            }
        }
    }

    internal static long NowNs() => Stopwatch.GetTimestamp() * (1_000_000_000L / Stopwatch.Frequency);

    /// <summary>Stop the reader and release the device, decoders, and resampler.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();
        try
        {
            _reader?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            // Reader teardown races are benign.
        }

        AVPacket* packet = _packet;
        ffmpeg.av_packet_free(&packet);

        AVFrame* frame = _decoded;
        ffmpeg.av_frame_free(&frame);

        AVFormatContext* format = _format;
        ffmpeg.avformat_close_input(&format);

        _video?.Dispose();
        _audio?.Dispose();
        _cts.Dispose();
    }

    /// <summary>Decodes a camera stream to tightly-packed NV12 and publishes the latest frame.</summary>
    private sealed class VideoStream : IVideoFrameSource, IDisposable
    {
        private readonly AVCodecContext* _codec;
        private SwsContext* _sws;
        private readonly Lock _gate = new();
        private byte[]? _latest;
        private int _width;
        private int _height;
        private long _timeNs;
        private bool _hasNew;

        private VideoStream(int index, AVCodecContext* codec)
        {
            Index = index;
            _codec = codec;
        }

        public int Index { get; }

        public static VideoStream? Create(int index, AVStream* stream)
        {
            AVCodec* codec = ffmpeg.avcodec_find_decoder(stream->codecpar->codec_id);
            if (codec is null)
            {
                return null;
            }

            AVCodecContext* ctx = ffmpeg.avcodec_alloc_context3(codec);
            ffmpeg.avcodec_parameters_to_context(ctx, stream->codecpar);
            if (ffmpeg.avcodec_open2(ctx, codec, null) < 0)
            {
                ffmpeg.avcodec_free_context(&ctx);
                return null;
            }

            return new VideoStream(index, ctx);
        }

        public void Decode(AVPacket* packet, AVFrame* frame)
        {
            if (ffmpeg.avcodec_send_packet(_codec, packet) < 0)
            {
                return;
            }

            while (ffmpeg.avcodec_receive_frame(_codec, frame) == 0)
            {
                Convert(frame);
                ffmpeg.av_frame_unref(frame);
            }
        }

        private void Convert(AVFrame* frame)
        {
            int width = frame->width;
            int height = frame->height;
            _sws = ffmpeg.sws_getCachedContext(
                _sws, width, height, (AVPixelFormat)frame->format,
                width, height, AVPixelFormat.AV_PIX_FMT_NV12, (int)SwsFlags.SWS_BILINEAR, null, null, null);
            if (_sws is null)
            {
                return;
            }

            byte[] nv12 = new byte[width * height * 3 / 2];
            fixed (byte* dst = nv12)
            {
                byte_ptrArray4 dstData = default;
                int_array4 dstStride = default;
                dstData[0] = dst;
                dstData[1] = dst + (width * height);
                dstStride[0] = width;
                dstStride[1] = width;
                ffmpeg.sws_scale(_sws, frame->data, frame->linesize, 0, height, dstData, dstStride);
            }

            lock (_gate)
            {
                _latest = nv12;
                _width = width;
                _height = height;
                _timeNs = NowNs();
                _hasNew = true;
            }
        }

        public bool TryGetFrame(out VideoFrame frame)
        {
            lock (_gate)
            {
                if (!_hasNew || _latest is null)
                {
                    frame = default;
                    return false;
                }

                _hasNew = false;
                frame = VideoFrame.FromPixels(_latest, VideoPixelFormat.Nv12, _width, _height, _timeNs);
                return true;
            }
        }

        public void Dispose()
        {
            if (_sws is not null)
            {
                ffmpeg.sws_freeContext(_sws);
            }

            AVCodecContext* codec = _codec;
            ffmpeg.avcodec_free_context(&codec);
        }
    }

    /// <summary>Decodes a microphone stream to interleaved 16-bit PCM at 48 kHz and queues 20 ms frames.</summary>
    private sealed class AudioStream : IAudioFrameSource, IDisposable
    {
        private const int OutRate = 48_000;
        private readonly AVCodecContext* _codec;
        private readonly int _channels;
        private SwrContext* _swr;
        private readonly ConcurrentQueue<AudioFrame> _queue = new();

        private AudioStream(int index, AVCodecContext* codec, int channels)
        {
            Index = index;
            _codec = codec;
            _channels = channels;
        }

        public int Index { get; }

        public static AudioStream? Create(int index, AVStream* stream)
        {
            AVCodec* codec = ffmpeg.avcodec_find_decoder(stream->codecpar->codec_id);
            if (codec is null)
            {
                return null;
            }

            AVCodecContext* ctx = ffmpeg.avcodec_alloc_context3(codec);
            ffmpeg.avcodec_parameters_to_context(ctx, stream->codecpar);
            if (ffmpeg.avcodec_open2(ctx, codec, null) < 0)
            {
                ffmpeg.avcodec_free_context(&ctx);
                return null;
            }

            int channels = Math.Max(1, ctx->ch_layout.nb_channels);
            return new AudioStream(index, ctx, channels);
        }

        public void Decode(AVPacket* packet, AVFrame* frame)
        {
            if (ffmpeg.avcodec_send_packet(_codec, packet) < 0)
            {
                return;
            }

            while (ffmpeg.avcodec_receive_frame(_codec, frame) == 0)
            {
                Convert(frame);
                ffmpeg.av_frame_unref(frame);
            }
        }

        private void Convert(AVFrame* frame)
        {
            EnsureResampler(frame);
            if (_swr is null)
            {
                return;
            }

            int outSamples = (int)ffmpeg.av_rescale_rnd(
                ffmpeg.swr_get_delay(_swr, frame->sample_rate) + frame->nb_samples,
                OutRate, frame->sample_rate, AVRounding.AV_ROUND_UP);
            if (outSamples <= 0)
            {
                return;
            }

            byte[] pcm = new byte[outSamples * _channels * 2];
            int converted;
            fixed (byte* dst = pcm)
            {
                byte* outPtr = dst;
                converted = ffmpeg.swr_convert(_swr, &outPtr, outSamples, frame->extended_data, frame->nb_samples);
            }

            if (converted <= 0)
            {
                return;
            }

            int bytes = converted * _channels * 2;
            byte[] trimmed = bytes == pcm.Length ? pcm : pcm[..bytes];
            _queue.Enqueue(new AudioFrame(trimmed, AudioSampleFormat.S16, OutRate, _channels, NowNs()));
        }

        private void EnsureResampler(AVFrame* frame)
        {
            if (_swr is not null)
            {
                return;
            }

            AVChannelLayout outLayout = default;
            ffmpeg.av_channel_layout_default(&outLayout, _channels);
            AVChannelLayout inLayout = frame->ch_layout;
            SwrContext* swr = null;
            int rc = ffmpeg.swr_alloc_set_opts2(
                &swr,
                &outLayout, AVSampleFormat.AV_SAMPLE_FMT_S16, OutRate,
                &inLayout, (AVSampleFormat)frame->format, frame->sample_rate,
                0, null);
            if (rc < 0 || swr is null || ffmpeg.swr_init(swr) < 0)
            {
                return;
            }

            _swr = swr;
        }

        public bool TryGetFrame(out AudioFrame frame) => _queue.TryDequeue(out frame);

        public void Dispose()
        {
            if (_swr is not null)
            {
                SwrContext* swr = _swr;
                ffmpeg.swr_free(&swr);
            }

            AVCodecContext* codec = _codec;
            ffmpeg.avcodec_free_context(&codec);
        }
    }
}
