using System.Buffers;
using System.Collections.Immutable;
using Agash.StreamTransport.WebRtc.Sdp;

namespace Agash.StreamTransport.WebRtc.Rtp.PayloadFormats;

/// <summary>
/// The Opus RTP payload format (RFC 7587): each Opus packet is one RTP payload, at a 48 kHz RTP clock
/// whatever the audio's sample rate, offered as stereo with in-band forward error correction.
/// </summary>
public sealed class OpusPayloadFormat : RtpPayloadFormat
{
    private OpusPayloadFormat() { }

    /// <summary>The format.</summary>
    public static OpusPayloadFormat Instance { get; } = new();

    /// <inheritdoc/>
    public override string EncodingName => "opus";

    /// <inheritdoc/>
    public override SdpMediaKind Kind => SdpMediaKind.Audio;

    /// <inheritdoc/>
    public override int ClockRate => 48_000;

    /// <inheritdoc/>
    public override int? Channels => 2;

    /// <inheritdoc/>
    public override ImmutableArray<string?> FormatParameterSets => ["minptime=10;useinbandfec=1"];

    /// <inheritdoc/>
    public override IRtpPacketizer CreatePacketizer(int maxPayloadSize) =>
        new SingleFramePacketizer(maxPayloadSize);

    /// <inheritdoc/>
    public override IRtpDepacketizer CreateDepacketizer() => new SingleFrameDepacketizer();
}

/// <summary>Packetizes a format whose frames each travel whole in one RTP payload.</summary>
/// <param name="maxPayloadSize">The largest RTP payload in bytes.</param>
public sealed class SingleFramePacketizer(int maxPayloadSize) : IRtpPacketizer
{
    /// <inheritdoc/>
    /// <exception cref="ArgumentException">The frame is larger than a payload may be.</exception>
    public void Packetize(ReadOnlySpan<byte> frame, RtpPayloadWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (frame.Length > maxPayloadSize)
        {
            throw new ArgumentException(
                $"The frame is {frame.Length} bytes; a payload holds at most {maxPayloadSize}.",
                nameof(frame)
            );
        }

        writer.Reset();
        frame.CopyTo(writer.Add(frame.Length));
    }
}

/// <summary>Depacketizes a format whose frames each travel whole in one RTP payload.</summary>
public sealed class SingleFrameDepacketizer : IRtpDepacketizer
{
    /// <inheritdoc/>
    public bool TryPush(ReadOnlySpan<byte> payload, bool marker, out EncodedFrameBuffer frame)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(Math.Max(payload.Length, 1));
        payload.CopyTo(buffer);
        frame = new EncodedFrameBuffer(buffer, payload.Length);
        return true;
    }

    /// <inheritdoc/>
    public void Dispose() { }
}
