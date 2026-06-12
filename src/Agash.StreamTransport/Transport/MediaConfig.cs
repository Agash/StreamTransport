using System.Net;
using Agash.StreamTransport.WebRtc;
using Agash.StreamTransport.WebRtc.Sdp;

namespace Agash.StreamTransport.Transport;

/// <summary>
/// Shared payload-type / SSRC / clock-rate constants and the <see cref="PeerConnectionOptions"/> builder for
/// the native transport path, so the sender and receiver agree on the negotiated media.
/// </summary>
internal static class MediaConfig
{
    public const byte VideoRtxPayloadType = 97;
    public const int VideoClockRate = 90_000;

    // Dynamic payload-type ranges (RFC 3551): video from 96 (skipping the reserved RTX PT), audio from 111.
    private const int FirstDynamicPayloadType = 96;
    private const int FirstAudioPayloadType = 111;

    public const uint VideoSsrc = 0x5EED_0001;
    public const uint VideoRtxSsrc = 0x5EED_0002;
    public const uint AudioSsrc = 0x5EED_0011;

    /// <summary>
    /// Builds the peer-connection media configuration from the codec registry: one BUNDLEd audio and/or video
    /// line, each advertising every registered codec (ordered by the profile's preference for video) with a
    /// distinct dynamic payload type. The peer's answer selects the mutually-supported codec.
    /// </summary>
    public static PeerConnectionOptions Build(IMediaCodecRegistry registry, MediaTransportOptions options, bool audio, bool video)
    {
        var lines = new List<MediaLine>(2);

        if (audio && registry.AudioCodecs.Count > 0)
        {
            int pt = FirstAudioPayloadType;
            var codecs = new List<SdpCodec>(registry.AudioCodecs.Count);
            foreach (IAudioCodecDescriptor a in registry.AudioCodecs)
            {
                codecs.Add(new SdpCodec(pt++, a.RtpName, a.ClockRate, a.Channels, a.FormatParameters, []));
            }

            lines.Add(new MediaLine("0", SdpMediaKind.Audio, AudioSsrc, codecs));
        }

        if (video && registry.VideoCodecs.Count > 0)
        {
            int pt = FirstDynamicPayloadType;
            var codecs = new List<SdpCodec>(registry.VideoCodecs.Count);
            foreach (IVideoCodecDescriptor v in OrderVideo(registry.VideoCodecs, options.VideoCodecs))
            {
                if (pt == VideoRtxPayloadType)
                {
                    pt++; // reserve 97 for RTX.
                }

                codecs.Add(new SdpCodec(pt++, v.RtpName, v.ClockRate, null, v.FormatParameters, v.RtcpFeedback));
            }

            lines.Add(new MediaLine("1", SdpMediaKind.Video, VideoSsrc, codecs)
            {
                RtxSsrc = VideoRtxSsrc,
                RtxPayloadType = VideoRtxPayloadType,
            });
        }

        // Loopback candidates let two agents on one host connect for the local comparison; harmless across machines.
        return new PeerConnectionOptions
        {
            Media = lines,
            StunServers = ResolveStun(options.StunServers),
            IncludeLoopback = true,
            EnableFec = options.EnableFec && video,
            FecProtectedSsrc = VideoSsrc,
        };
    }

    // Order the registered video codecs by the profile's preference (matched by encoding name), then by the
    // descriptor's own Preference. Codecs not named in the preference list still follow, so a registered codec
    // is always offered - the preference is a hint, not a filter (custom codecs have no VideoCodec enum value).
    private static IEnumerable<IVideoCodecDescriptor> OrderVideo(
        IReadOnlyList<IVideoCodecDescriptor> available, IReadOnlyList<VideoCodec> preference)
    {
        if (preference.Count == 0)
        {
            return available;
        }

        string[] preferred = [.. preference.Select(RtpNameOf)];
        return available
            .OrderBy(c =>
            {
                int index = Array.FindIndex(preferred, n => string.Equals(n, c.RtpName, StringComparison.OrdinalIgnoreCase));
                return index < 0 ? int.MaxValue : index;
            })
            .ThenBy(c => c.Preference);
    }

    private static string RtpNameOf(VideoCodec codec) => codec switch
    {
        VideoCodec.H264 => "H264",
        VideoCodec.H265 => "H265",
        VideoCodec.Av1 => "AV1",
        _ => string.Empty,
    };

    private static IReadOnlyList<IPEndPoint> ResolveStun(IReadOnlyList<string> stunUrls)
    {
        if (stunUrls.Count == 0)
        {
            return [];
        }

        var endpoints = new List<IPEndPoint>();
        foreach (string url in stunUrls)
        {
            // Accept "stun:host:port" or "host:port"; default to the STUN port if none.
            string hostPort = url.StartsWith("stun:", StringComparison.OrdinalIgnoreCase) ? url[5..] : url;
            int colon = hostPort.LastIndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            string host = hostPort[..colon];
            int port = int.TryParse(hostPort[(colon + 1)..], out int p) ? p : 3478;
            try
            {
                foreach (IPAddress address in Dns.GetHostAddresses(host))
                {
                    endpoints.Add(new IPEndPoint(address, port));
                }
            }
            catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ArgumentException)
            {
                // Unresolvable STUN host — skip it; host candidates still allow LAN/direct connectivity.
            }
        }

        return endpoints;
    }

    /// <summary>Converts a monotonic capture timestamp (ns) to a UQ32.32 NTP value for abs-capture-time.</summary>
    public static ulong CaptureNsToNtp(long captureNs)
    {
        ulong seconds = (ulong)(captureNs / 1_000_000_000L);
        ulong fraction = (ulong)(((captureNs % 1_000_000_000L) << 32) / 1_000_000_000L);
        return (seconds << 32) | (fraction & 0xFFFF_FFFF);
    }
}
