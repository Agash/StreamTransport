namespace Agash.StreamTransport.Codecs;

/// <summary>
/// The built-in H.265 video codec: hardware HEVC through FFmpeg (<see cref="VideoSendPipeline"/> /
/// <see cref="VideoReceivePipeline"/>) with the RFC 7798 payload format. Registered by default; a host swaps
/// or supplements it by registering another <see cref="IVideoCodecDescriptor"/>.
/// </summary>
internal sealed class H265VideoCodecDescriptor : IVideoCodecDescriptor
{
    public string RtpName => "H265";
    public int ClockRate => 90_000;
    public string? FormatParameters => null;
    public IReadOnlyList<string> RtcpFeedback => ["nack", "nack pli"];
    public int Preference => 10;

    public IVideoEncoder CreateEncoder(VideoEncoderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new VideoSendPipeline(
            settings.Fps, settings.Bitrate, settings.EncoderName, settings.GpuDeviceHandle, settings.PreserveAlpha, settings.MaxBFrames, settings.Profile);
    }

    public IVideoDecoder CreateDecoder(VideoDecoderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new VideoReceivePipeline(settings.PreferGpuOutput, settings.PreserveAlpha);
    }

    public IRtpPacketizer CreatePacketizer() => new H265RtpPacketizer();
    public IRtpDepacketizer CreateDepacketizer() => new H265RtpDepacketizer();
}

/// <summary>
/// The built-in Opus audio codec: pure-managed Concentus (<see cref="AudioPipeline"/>) with one packet per
/// RTP frame. Registered by default.
/// </summary>
internal sealed class OpusAudioCodecDescriptor : IAudioCodecDescriptor
{
    public string RtpName => "opus";
    public int ClockRate => 48_000;
    public int Channels => 2;
    public string? FormatParameters => "minptime=10;useinbandfec=1";
    public int Preference => 10;

    public IAudioEncoder CreateEncoder() => new AudioPipeline(AudioCodec.Opus);
    public IAudioDecoder CreateDecoder() => new AudioPipeline(AudioCodec.Opus);
    public IRtpPacketizer CreatePacketizer() => new PassthroughPacketizer();
    public IRtpDepacketizer CreateDepacketizer() => new PassthroughDepacketizer();
}
