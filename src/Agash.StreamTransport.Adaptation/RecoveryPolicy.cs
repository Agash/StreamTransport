namespace Agash.StreamTransport.Adaptation;

/// <summary>What a sender's loss repair is planned from.</summary>
/// <param name="LossFraction">The path's packet loss, smoothed, before any repair.</param>
/// <param name="RoundTrip">The smoothed round trip.</param>
/// <param name="BitsPerSecond">The media target rate.</param>
/// <param name="FramesPerSecond">The frame rate sent.</param>
/// <param name="PacketsPerFrame">The packets a frame takes on average.</param>
public readonly record struct RecoveryInputs(
    double LossFraction,
    TimeSpan RoundTrip,
    long BitsPerSecond,
    double FramesPerSecond,
    double PacketsPerFrame
);

/// <summary>How a sender repairs loss.</summary>
/// <param name="FecGroupSize">
/// Media packets one FEC repair protects, or 0 for no FEC. One repair recovers one loss in its group.
/// </param>
/// <param name="Retransmit">Whether lost packets are retransmitted on request (NACK).</param>
public readonly record struct RecoveryPlan(int FecGroupSize, bool Retransmit)
{
    /// <summary>Whether FEC repairs are sent.</summary>
    public bool Fec => FecGroupSize > 0;
}

/// <summary>The tunables of a <see cref="RecoveryPolicy"/>; the defaults follow libwebrtc's hybrid NACK/FEC.</summary>
public sealed record RecoveryPolicyOptions
{
    /// <summary>
    /// The chance of an unrecoverable group (two or more of its packets lost) FEC is sized for; what it
    /// leaves, retransmission repairs. 1%.
    /// </summary>
    public double TargetUnrecoverable { get; init; } = 0.01;

    /// <summary>Below this round trip, retransmission alone repairs in time and FEC is off. 20 ms.</summary>
    public TimeSpan RetransmitOnlyRoundTrip { get; init; } = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// Frames smaller than this carry too few packets for FEC to pay, below
    /// <see cref="SmallFrameRoundTrip"/>. 700 bytes.
    /// </summary>
    public int MinimumBytesPerFrame { get; init; } = 700;

    /// <summary>The round trip below which small frames get no FEC. 200 ms.</summary>
    public TimeSpan SmallFrameRoundTrip { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>The most frames one repair may span, so it arrives within a round trip of them. 6.</summary>
    public int MaximumFramesSpanned { get; init; } = 6;

    /// <summary>The largest group one repair protects: the 15-bit FlexFEC mask. 15.</summary>
    public int MaximumGroupSize { get; init; } = 15;
}

/// <summary>
/// Decides a sender's loss repair from the path and the media, as libwebrtc's hybrid NACK/FEC does:
/// retransmission alone on a short round trip, FEC with retransmission of what it leaves otherwise, no FEC
/// for small frames on a moderate round trip, and a repair spanning at most about two round trips of frames.
/// The group size is derived from the loss: the largest group whose chance of losing two or more packets,
/// which one XOR repair cannot recover, stays under the target. Retransmission stays on throughout; FEC
/// repairs without the round trip, retransmission repairs the rest when the playout buffer allows.
/// </summary>
/// <param name="options">The tunables; the defaults when null.</param>
public sealed class RecoveryPolicy(RecoveryPolicyOptions? options = null)
{
    private readonly RecoveryPolicyOptions _options = options ?? new RecoveryPolicyOptions();

    /// <summary>The repair for these conditions.</summary>
    /// <param name="inputs">The path and the media.</param>
    /// <returns>The plan.</returns>
    public RecoveryPlan Plan(in RecoveryInputs inputs)
    {
        double loss = Math.Clamp(inputs.LossFraction, 0, 1);
        if (loss <= 0 || inputs.RoundTrip < _options.RetransmitOnlyRoundTrip)
        {
            return new RecoveryPlan(0, true);
        }

        double fps = Math.Max(1, inputs.FramesPerSecond);
        double bytesPerFrame = inputs.BitsPerSecond / 8.0 / fps;
        if (
            bytesPerFrame < _options.MinimumBytesPerFrame
            && inputs.RoundTrip < _options.SmallFrameRoundTrip
        )
        {
            return new RecoveryPlan(0, true);
        }

        int frames = Math.Clamp(
            (int)Math.Round(2 * fps * inputs.RoundTrip.TotalSeconds),
            1,
            _options.MaximumFramesSpanned
        );
        int span = Math.Clamp(
            (int)Math.Floor(frames * Math.Max(1, inputs.PacketsPerFrame)),
            2,
            _options.MaximumGroupSize
        );

        // At most half the media rate in repairs: two packets per repair is as dense as it gets.
        int group = 2;
        for (int n = span; n >= 2; n--)
        {
            if (Unrecoverable(loss, n + 1) <= _options.TargetUnrecoverable)
            {
                group = n;
                break;
            }
        }

        return new RecoveryPlan(group, true);
    }

    // The chance that two or more of m packets are lost, each independently with probability p.
    internal static double Unrecoverable(double p, int m) =>
        1 - Math.Pow(1 - p, m) - (m * p * Math.Pow(1 - p, m - 1));
}
