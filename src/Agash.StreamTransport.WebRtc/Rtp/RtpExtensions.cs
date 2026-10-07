using System.Buffers.Binary;
using Agash.StreamTransport.WebRtc.Sdp;

namespace Agash.StreamTransport.WebRtc.Rtp;

/// <summary>The RTP header extensions this endpoint understands, by the URI that names each in SDP.</summary>
public static class RtpExtensionUris
{
    /// <summary>When the frame was captured, on the sender's NTP clock (WebRTC abs-capture-time).</summary>
    public const string AbsoluteCaptureTime =
        "http://www.webrtc.org/experiments/rtp-hdrext/abs-capture-time";

    /// <summary>Per-frame sender timing: encode, packetization and pacer exit (WebRTC video-timing).</summary>
    public const string VideoTiming = "http://www.webrtc.org/experiments/rtp-hdrext/video-timing";

    /// <summary>The playout delay the sender asks the receiver to keep within (WebRTC playout-delay).</summary>
    public const string PlayoutDelay = "http://www.webrtc.org/experiments/rtp-hdrext/playout-delay";
}

/// <summary>
/// Sender timing of one video frame, as the video-timing extension carries it on the frame's last packet:
/// milliseconds after capture at which encoding started and finished, packetization finished and the last
/// packet left the pacer, and two slots a relay on the path may stamp. Each saturates at 65535 ms.
/// </summary>
/// <param name="Flags">Why the frame is timed: <see cref="ByTimer"/>, <see cref="BySize"/>, or both.</param>
/// <param name="EncodeStart">When encoding started.</param>
/// <param name="EncodeFinish">When encoding finished.</param>
/// <param name="PacketizationFinish">When the frame's packets went to the pacer.</param>
/// <param name="PacerExit">When the frame's last packet left the pacer.</param>
/// <param name="Network">A relay's stamp.</param>
/// <param name="Network2">A second relay's stamp.</param>
public readonly record struct VideoTiming(
    byte Flags,
    ushort EncodeStart,
    ushort EncodeFinish,
    ushort PacketizationFinish,
    ushort PacerExit,
    ushort Network,
    ushort Network2
)
{
    /// <summary>The frame is timed because the periodic timer chose it.</summary>
    public const byte ByTimer = 0x01;

    /// <summary>The frame is timed because it was larger than usual.</summary>
    public const byte BySize = 0x02;

    /// <summary>The extension's length on the wire.</summary>
    public const int Length = 13;

    /// <summary>Where <see cref="PacerExit"/> sits in the value, for the pacer to stamp it in place.</summary>
    public const int PacerExitOffset = 7;

    /// <summary>A delta from capture in whole milliseconds, saturated to the field.</summary>
    /// <param name="sinceCapture">The time since capture.</param>
    /// <returns>The field value.</returns>
    public static ushort Delta(TimeSpan sinceCapture) =>
        (ushort)Math.Clamp((long)sinceCapture.TotalMilliseconds, 0, ushort.MaxValue);

    internal void Write(Span<byte> value)
    {
        value[0] = Flags;
        BinaryPrimitives.WriteUInt16BigEndian(value[1..], EncodeStart);
        BinaryPrimitives.WriteUInt16BigEndian(value[3..], EncodeFinish);
        BinaryPrimitives.WriteUInt16BigEndian(value[5..], PacketizationFinish);
        BinaryPrimitives.WriteUInt16BigEndian(value[7..], PacerExit);
        BinaryPrimitives.WriteUInt16BigEndian(value[9..], Network);
        BinaryPrimitives.WriteUInt16BigEndian(value[11..], Network2);
    }

    internal static VideoTiming? Read(ReadOnlySpan<byte> value) =>
        value.Length == Length
            ? new VideoTiming(
                value[0],
                BinaryPrimitives.ReadUInt16BigEndian(value[1..]),
                BinaryPrimitives.ReadUInt16BigEndian(value[3..]),
                BinaryPrimitives.ReadUInt16BigEndian(value[5..]),
                BinaryPrimitives.ReadUInt16BigEndian(value[7..]),
                BinaryPrimitives.ReadUInt16BigEndian(value[9..]),
                BinaryPrimitives.ReadUInt16BigEndian(value[11..])
            )
            : null;
}

/// <summary>
/// The playout delay a sender asks of the receiver, from capture to render: zero for both asks to render
/// as soon as possible. Carried in 10 ms units of 12 bits each, so at most 40.95 s.
/// </summary>
/// <param name="Minimum">The least delay.</param>
/// <param name="Maximum">The most delay.</param>
public readonly record struct PlayoutDelay(TimeSpan Minimum, TimeSpan Maximum)
{
    /// <summary>The extension's length on the wire.</summary>
    public const int Length = 3;

    private static readonly TimeSpan Unit = TimeSpan.FromMilliseconds(10);

    internal void Write(Span<byte> value)
    {
        uint raw = (Units(Minimum) << 12) | Units(Maximum);
        value[0] = (byte)(raw >> 16);
        value[1] = (byte)(raw >> 8);
        value[2] = (byte)raw;
    }

    internal static PlayoutDelay? Read(ReadOnlySpan<byte> value)
    {
        if (value.Length != Length)
        {
            return null;
        }

        uint raw = ((uint)value[0] << 16) | ((uint)value[1] << 8) | value[2];
        return new PlayoutDelay(Unit * (raw >> 12), Unit * (raw & 0xFFF));
    }

    private static uint Units(TimeSpan delay) => (uint)Math.Clamp(delay / Unit, 0, 0xFFF);
}

/// <summary>The header extension values one RTP packet carries; null where it carries none.</summary>
/// <param name="AbsoluteCaptureTimeNtp">The capture time as a UQ32.32 NTP timestamp.</param>
/// <param name="VideoTiming">The frame's sender timing.</param>
/// <param name="PlayoutDelay">The playout delay the sender asks for.</param>
public readonly record struct RtpExtensionValues(
    ulong? AbsoluteCaptureTimeNtp = null,
    VideoTiming? VideoTiming = null,
    PlayoutDelay? PlayoutDelay = null
)
{
    /// <summary>Whether the packet carries none.</summary>
    public bool IsEmpty =>
        AbsoluteCaptureTimeNtp is null && VideoTiming is null && PlayoutDelay is null;
}

/// <summary>
/// The local identifiers the two sides agreed for each extension (RFC 8285), one map for the whole
/// connection: BUNDLE requires an identifier to mean the same extension in every section (RFC 8843
/// section 9.1). Zero means the extension is not in use. Identifiers 1 to 14 travel in the one-byte form;
/// a map with a larger one writes every packet in the two-byte form, which needs no
/// <c>extmap-allow-mixed</c> since nothing is mixed (RFC 8285 section 4.1.2).
/// </summary>
/// <param name="AbsoluteCaptureTime">The abs-capture-time identifier.</param>
/// <param name="VideoTiming">The video-timing identifier.</param>
/// <param name="PlayoutDelay">The playout-delay identifier.</param>
public sealed record RtpExtensionMap(int AbsoluteCaptureTime, int VideoTiming, int PlayoutDelay)
{
    private const ushort OneByteProfile = 0xBEDE;
    private const ushort TwoByteProfile = 0x1000;

    /// <summary>No extensions.</summary>
    public static RtpExtensionMap None { get; } = new(0, 0, 0);

    /// <summary>The identifiers this endpoint offers.</summary>
    public static RtpExtensionMap Offered { get; } = new(1, 2, 3);

    /// <summary>Whether any extension is in use.</summary>
    public bool IsEmpty => AbsoluteCaptureTime == 0 && VideoTiming == 0 && PlayoutDelay == 0;

    /// <summary>The most bytes the extension block of a packet takes, its 4-byte header included.</summary>
    public int MaximumBlockLength
    {
        get
        {
            int element = TwoByte ? 2 : 1;
            int data =
                (AbsoluteCaptureTime != 0 ? element + 8 : 0)
                + (VideoTiming != 0 ? element + Rtp.VideoTiming.Length : 0)
                + (PlayoutDelay != 0 ? element + Rtp.PlayoutDelay.Length : 0);
            return data == 0 ? 0 : 4 + ((data + 3) / 4 * 4);
        }
    }

    private bool TwoByte => AbsoluteCaptureTime > 14 || VideoTiming > 14 || PlayoutDelay > 14;

    /// <summary>The identifiers of the extensions this endpoint understands among SDP's mappings.</summary>
    /// <param name="extensions">The section's mappings.</param>
    /// <returns>The map.</returns>
    public static RtpExtensionMap From(IEnumerable<SdpExtension> extensions)
    {
        ArgumentNullException.ThrowIfNull(extensions);
        int capture = 0;
        int timing = 0;
        int delay = 0;
        foreach (SdpExtension extension in extensions)
        {
            if (extension.Id is < 1 or > 255 || extension.Direction == SdpDirection.Inactive)
            {
                continue;
            }

            switch (extension.Uri)
            {
                case RtpExtensionUris.AbsoluteCaptureTime:
                    capture = extension.Id;
                    break;
                case RtpExtensionUris.VideoTiming:
                    timing = extension.Id;
                    break;
                case RtpExtensionUris.PlayoutDelay:
                    delay = extension.Id;
                    break;
                default:
                    break;
            }
        }

        return new RtpExtensionMap(capture, timing, delay);
    }

    /// <summary>
    /// Writes the extension block (RFC 8285 section 4) for the values the map has identifiers for.
    /// </summary>
    /// <param name="destination">Where the block goes, right after the fixed header and CSRCs.</param>
    /// <param name="values">The values.</param>
    /// <param name="videoTimingAt">Where the video-timing value starts in the block, or -1.</param>
    /// <returns>The block's length, its header included; zero when nothing is written.</returns>
    public int Write(Span<byte> destination, in RtpExtensionValues values, out int videoTimingAt)
    {
        videoTimingAt = -1;
        bool twoByte = TwoByte;
        int at = 4;
        if (AbsoluteCaptureTime != 0 && values.AbsoluteCaptureTimeNtp is { } ntp)
        {
            at = Element(destination, at, AbsoluteCaptureTime, 8, twoByte);
            BinaryPrimitives.WriteUInt64BigEndian(destination[at..], ntp);
            at += 8;
        }

        if (VideoTiming != 0 && values.VideoTiming is { } timing)
        {
            at = Element(destination, at, VideoTiming, Rtp.VideoTiming.Length, twoByte);
            timing.Write(destination[at..]);
            videoTimingAt = at;
            at += Rtp.VideoTiming.Length;
        }

        if (PlayoutDelay != 0 && values.PlayoutDelay is { } delay)
        {
            at = Element(destination, at, PlayoutDelay, Rtp.PlayoutDelay.Length, twoByte);
            delay.Write(destination[at..]);
            at += Rtp.PlayoutDelay.Length;
        }

        if (at == 4)
        {
            return 0;
        }

        // Zero padding to a 32-bit word: a zero byte is padding in both forms.
        int words = (at - 4 + 3) / 4;
        destination[at..(4 + (words * 4))].Clear();
        BinaryPrimitives.WriteUInt16BigEndian(
            destination,
            twoByte ? TwoByteProfile : OneByteProfile
        );
        BinaryPrimitives.WriteUInt16BigEndian(destination[2..], (ushort)words);
        return 4 + (words * 4);
    }

    /// <summary>
    /// Reads the values of the extensions the map has identifiers for from a packet's extension block,
    /// in either form; an element with an unknown identifier, or a known one of the wrong length, is
    /// skipped.
    /// </summary>
    /// <param name="profile">The block's profile: 0xBEDE, or 0x100X for the two-byte form.</param>
    /// <param name="data">The block's data, after its 4-byte header.</param>
    /// <returns>The values.</returns>
    public RtpExtensionValues Read(ushort profile, ReadOnlySpan<byte> data)
    {
        bool twoByte = (profile & 0xFFF0) == TwoByteProfile;
        if (IsEmpty || (!twoByte && profile != OneByteProfile))
        {
            return default;
        }

        ulong? capture = null;
        VideoTiming? timing = null;
        PlayoutDelay? delay = null;
        int i = 0;
        while (i < data.Length)
        {
            if (data[i] == 0)
            {
                i++;
                continue;
            }

            int id;
            int length;
            if (twoByte)
            {
                if (i + 2 > data.Length)
                {
                    break;
                }

                id = data[i];
                length = data[i + 1];
                i += 2;
            }
            else
            {
                id = data[i] >> 4;
                length = (data[i] & 0x0F) + 1;
                i++;

                // RFC 8285 section 4.2: identifier 15 ends the block.
                if (id == 15)
                {
                    break;
                }
            }

            if (i + length > data.Length)
            {
                break;
            }

            ReadOnlySpan<byte> value = data.Slice(i, length);
            if (id == AbsoluteCaptureTime && length is 8 or 16)
            {
                capture = BinaryPrimitives.ReadUInt64BigEndian(value);
            }
            else if (id == VideoTiming)
            {
                timing = Rtp.VideoTiming.Read(value);
            }
            else if (id == PlayoutDelay)
            {
                delay = Rtp.PlayoutDelay.Read(value);
            }

            i += length;
        }

        return new RtpExtensionValues(capture, timing, delay);
    }

    private static int Element(Span<byte> destination, int at, int id, int length, bool twoByte)
    {
        if (twoByte)
        {
            destination[at] = (byte)id;
            destination[at + 1] = (byte)length;
            return at + 2;
        }

        destination[at] = (byte)((id << 4) | (length - 1));
        return at + 1;
    }
}
