using Agash.StreamTransport.Media;

namespace Agash.StreamTransport.WebRtc.Transport;

/// <summary>The media values the audio level and CVO extensions carry, to and from their wire bytes.</summary>
internal static class MediaExtensionValues
{
    // 3GPP TS 26.114 section 7.4.5: 0 0 0 0 C F R1 R0. F flips the picture left to right; R1 R0 is the
    // clockwise quarter turns that make it upright, the meaning libwebrtc and browsers give them.
    private const byte Flip = 0x04;

    /// <summary>The CVO byte of an orientation.</summary>
    /// <param name="orientation">The orientation.</param>
    /// <returns>The byte.</returns>
    public static byte ToCvo(VideoOrientation orientation) =>
        (byte)(
            (orientation.Mirrored ? Flip : 0)
            | orientation.Rotation switch
            {
                VideoRotation.Clockwise90 => 1,
                VideoRotation.Rotate180 => 2,
                VideoRotation.CounterClockwise90 => 3,
                _ => 0,
            }
        );

    /// <summary>The orientation a CVO byte gives; the camera bit and the reserved bits are ignored.</summary>
    /// <param name="cvo">The byte.</param>
    /// <returns>The orientation.</returns>
    public static VideoOrientation FromCvo(byte cvo) =>
        new(
            (cvo & 0x03) switch
            {
                1 => VideoRotation.Clockwise90,
                2 => VideoRotation.Rotate180,
                3 => VideoRotation.CounterClockwise90,
                _ => VideoRotation.None,
            },
            (cvo & Flip) != 0
        );

    /// <summary>The audio level byte (RFC 6464 section 3): the V bit, then -dBov.</summary>
    /// <param name="level">The level.</param>
    /// <returns>The byte; V set only when the level says voice.</returns>
    public static byte ToAudioLevel(AudioLevel level) =>
        (byte)((level.Voice == true ? 0x80 : 0) | level.DecibelsBelowOverload);

    /// <summary>The level an audio level byte gives.</summary>
    /// <param name="value">The byte.</param>
    /// <param name="voiceActivity">Whether the sender uses the V bit (its mapping is not <c>vad=off</c>).</param>
    /// <returns>The level.</returns>
    public static AudioLevel FromAudioLevel(byte value, bool voiceActivity) =>
        new(value & 0x7F, voiceActivity ? (value & 0x80) != 0 : null);
}
