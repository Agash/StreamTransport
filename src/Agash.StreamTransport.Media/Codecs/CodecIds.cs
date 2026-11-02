using System.Collections.Immutable;

namespace Agash.StreamTransport.Media;

/// <summary>
/// A video codec, named as RTP names it (<c>a=rtpmap</c>), compared without regard to case. The codecs
/// StreamTransport implements are properties; any other is named by the library that brings its
/// encoder, decoder and payload format.
/// </summary>
public readonly struct VideoCodecId : IEquatable<VideoCodecId>
{
    /// <summary>A codec by its RTP encoding name.</summary>
    /// <param name="name">The encoding name, such as <c>VP9</c>.</param>
    public VideoCodecId(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    /// <summary>H.264 / AVC, the codec every WebRTC peer and WHIP client speaks.</summary>
    public static VideoCodecId H264 { get; } = new("H264");

    /// <summary>H.265 / HEVC.</summary>
    public static VideoCodecId H265 { get; } = new("H265");

    /// <summary>AV1.</summary>
    public static VideoCodecId AV1 { get; } = new("AV1");

    /// <summary>The codecs StreamTransport implements.</summary>
    public static ImmutableArray<VideoCodecId> BuiltIn { get; } = [H264, H265, AV1];

    /// <summary>The RTP encoding name; empty for a default value.</summary>
    public string Name => field ?? string.Empty;

    /// <inheritdoc/>
    public bool Equals(VideoCodecId other) =>
        string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is VideoCodecId other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Name);

    /// <inheritdoc/>
    public override string ToString() => Name;

    /// <summary>Whether two ids name the same codec.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns>True when they are equal.</returns>
    public static bool operator ==(VideoCodecId left, VideoCodecId right) => left.Equals(right);

    /// <summary>Whether two ids name different codecs.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns>True when they differ.</returns>
    public static bool operator !=(VideoCodecId left, VideoCodecId right) => !left.Equals(right);
}

/// <summary>
/// An audio codec, named as RTP names it (<c>a=rtpmap</c>), compared without regard to case. The codecs
/// StreamTransport implements are properties; any other is named by the library that brings its
/// encoder, decoder and payload format.
/// </summary>
public readonly struct AudioCodecId : IEquatable<AudioCodecId>
{
    /// <summary>A codec by its RTP encoding name.</summary>
    /// <param name="name">The encoding name, such as <c>G722</c>.</param>
    public AudioCodecId(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    /// <summary>Opus.</summary>
    public static AudioCodecId Opus { get; } = new("opus");

    /// <summary>The codecs StreamTransport implements.</summary>
    public static ImmutableArray<AudioCodecId> BuiltIn { get; } = [Opus];

    /// <summary>The RTP encoding name; empty for a default value.</summary>
    public string Name => field ?? string.Empty;

    /// <inheritdoc/>
    public bool Equals(AudioCodecId other) =>
        string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is AudioCodecId other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Name);

    /// <inheritdoc/>
    public override string ToString() => Name;

    /// <summary>Whether two ids name the same codec.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns>True when they are equal.</returns>
    public static bool operator ==(AudioCodecId left, AudioCodecId right) => left.Equals(right);

    /// <summary>Whether two ids name different codecs.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns>True when they differ.</returns>
    public static bool operator !=(AudioCodecId left, AudioCodecId right) => !left.Equals(right);
}
