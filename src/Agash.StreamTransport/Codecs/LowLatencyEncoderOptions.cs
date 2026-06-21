using FFmpeg.AutoGen;

namespace Agash.StreamTransport.Codecs;

/// <summary>
/// Applies low-latency, profile-aware encoder settings tuned for real-time streaming. Two halves:
/// <see cref="ConfigureContext"/> sets the codec-context fields common to every hardware encoder (low-delay
/// flag, CBR rate control, VBV depth, intra-refresh GOP); <see cref="Apply"/> sets the per-encoder private
/// AVOptions (each vendor names them differently) before <c>avcodec_open2</c>. Both take the active
/// <see cref="MediaProfile"/> so the same encoder is tuned for latency (interactive), quality (screen share),
/// or loss-resilience (IRL cellular uplink).
/// </summary>
internal static unsafe class LowLatencyEncoderOptions
{
    // VBV (rc_buffer_size) as a fraction of one second of bitrate. A tight buffer keeps end-to-end latency
    // low (the encoder can't run ahead of the budget); a looser one rides out a lossy/variable cellular link.
    private static double VbvSeconds(MediaProfile profile) => profile switch
    {
        MediaProfile.InteractiveP2P => 0.5,
        MediaProfile.ScreenShare => 1.0,
        MediaProfile.IrlContribution => 1.5,
        _ => 0.5,
    };

    /// <summary>
    /// Set the codec-context low-latency + CBR rate-control fields shared by every HW encoder. Call after
    /// width/height/bitrate/gop are set and immediately before <c>avcodec_open2</c>.
    /// </summary>
    public static void ConfigureContext(AVCodecContext* ctx, MediaProfile profile, long bitrate)
    {
        // Emit output without buffering reorder frames. rkmpp keys its async pipeline depth off this flag
        // (H26X_ASYNC_FRAMES 4 -> 0); the other encoders honour or harmlessly ignore it. The decoders set the
        // matching flag, so the whole chain is single-frame.
        ctx->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;

        // CBR from the very first frame (previously only set on the first congestion UpdateBitrate): cap the rate
        // at the target with a profile-sized VBV. Without this the opening ~second runs unbounded VBR. The
        // STX_ENC_VBV env var overrides the VBV depth (seconds) for ad-hoc latency measurement (test-only).
        double vbvSeconds = VbvSeconds(profile);
        if (double.TryParse(Environment.GetEnvironmentVariable("STX_ENC_VBV"),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double vbvOverride) && vbvOverride > 0)
        {
            vbvSeconds = vbvOverride;
        }
        ctx->rc_max_rate = bitrate;
        ctx->rc_buffer_size = (int)Math.Min(int.MaxValue, (long)(bitrate * vbvSeconds));
        // IRL keeps the caller's periodic-IDR GOP (recovery is keyframe-on-PLI + FEC); intra-refresh was measured
        // not to help and is no longer enabled (see Apply). The deeper IRL VBV (1.5x) is latency-free per the sweep.
    }

    /// <summary>Set the per-encoder private AVOptions for <paramref name="encoderName"/> under <paramref name="profile"/>.</summary>
    public static void Apply(AVDictionary** options, string encoderName, MediaProfile profile)
    {
        // NOTE: intra-refresh is intentionally NOT enabled for the IRL profile. Measured (nvenc, one-way sim via
        // STX_NO_NACK + STX_RTP_DROP): it did not improve loss recovery (slightly worse fps/latency at 8-15% loss)
        // and appears to fight the keyframe-on-PLI recovery. Its real benefit (avoiding the IDR bandwidth spike on
        // a bitrate-capped uplink) is unproven on our testbed. Re-enable per encoder via STX_ENC_OPT to retest.
        switch (encoderName)
        {
            case "hevc_nvenc" or "h264_nvenc":
                // p4 = balanced speed/quality; ull (ultra-low-latency: no lookahead/reorder) for the interactive
                // profile, ll otherwise. forced-idr honours our keyframe-on-PLI; rc-lookahead 0 removes the
                // look-ahead delay.
                Set(options,
                    ("preset", "p4"),
                    ("tune", profile == MediaProfile.InteractiveP2P ? "ull" : "ll"),
                    ("rc", "cbr"), ("zerolatency", "1"), ("delay", "0"), ("rc-lookahead", "0"), ("forced-idr", "1"));
                break;

            case "hevc_amf" or "h264_amf":
                Set(options,
                    ("usage", profile == MediaProfile.ScreenShare ? "lowlatency_high_quality" : "ultralowlatency"),
                    ("rc", "cbr"),
                    ("quality", profile == MediaProfile.ScreenShare ? "balanced" : "speed"));
                break;

            case "hevc_qsv" or "h264_qsv":
                Set(options, ("preset", "veryfast"), ("low_delay_brc", "1"), ("async_depth", "1"));
                break;

            case "hevc_vaapi" or "h264_vaapi":
                // No async pipeline (encode one frame, get one packet) - the rest of VAAPI's rate control comes
                // from the context (CBR/VBV in ConfigureContext).
                Set(options, ("async_depth", "1"));
                break;

            case "hevc_videotoolbox" or "h264_videotoolbox":
                // realtime: encode at least as fast as capture; prio_speed: favour speed over quality. The CBR
                // ceiling comes from ConfigureContext (rc_max_rate); the encoder's own constant_bit_rate property
                // is not supported for HEVC VideoToolbox ("not supported by the encoder"), so don't set it.
                Set(options, ("realtime", "1"), ("prio_speed", "1"));
                break;

            case "hevc_rkmpp" or "h264_rkmpp":
                // CBR; the low-delay (async off) comes from AV_CODEC_FLAG_LOW_DELAY in ConfigureContext.
                Set(options, ("rc_mode", "cbr"));
                break;

            default:
                break;
        }

        // Diagnostic override (test-only): STX_ENC_OPT="k1=v1;k2=v2" sets/overrides arbitrary encoder AVOptions on
        // top of the profile defaults (av_dict_set overwrites), so a single knob can be varied for measurement
        // without a rebuild. No effect in production (the env var is unset).
        string? envOpts = Environment.GetEnvironmentVariable("STX_ENC_OPT");
        if (!string.IsNullOrWhiteSpace(envOpts))
        {
            foreach (string pair in envOpts.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                int eq = pair.IndexOf('=');
                if (eq > 0)
                {
                    ffmpeg.av_dict_set(options, pair[..eq].Trim(), pair[(eq + 1)..].Trim(), 0);
                }
            }
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
