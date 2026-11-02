using System.Numerics.Tensors;
using System.Runtime.InteropServices;

namespace Agash.StreamTransport.Media;

/// <summary>
/// The level of a stretch of audio as RFC 6464 carries it: decibels below the overload point (-dBov), the
/// root mean square of every sample relative to full scale, from 0 (full scale) to 127, which is also
/// digital silence (section 3).
/// </summary>
public readonly record struct AudioLevel
{
    /// <summary>The value of silence, and of anything quieter than -127 dBov.</summary>
    public const int Silent = 127;

    /// <summary>A level.</summary>
    /// <param name="decibelsBelowOverload">0 to 127.</param>
    /// <param name="voice">Whether the sender believes the audio holds voice, when it says.</param>
    public AudioLevel(int decibelsBelowOverload, bool? voice = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(decibelsBelowOverload);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(decibelsBelowOverload, Silent);
        DecibelsBelowOverload = decibelsBelowOverload;
        Voice = voice;
    }

    /// <summary>Digital silence.</summary>
    public static AudioLevel Silence { get; } = new(Silent);

    /// <summary>How far below full scale, in decibels: 0 to 127.</summary>
    public int DecibelsBelowOverload { get; }

    /// <summary>
    /// Whether the sender believes the audio holds voice (the V bit); null when it does not say
    /// (<c>vad=off</c>, RFC 6464 section 4).
    /// </summary>
    public bool? Voice { get; }

    /// <summary>The level of a mean square of samples normalized to full scale (1.0).</summary>
    /// <param name="meanSquare">The mean of the squared samples.</param>
    /// <returns>The level, rounded to the nearest decibel.</returns>
    public static AudioLevel FromMeanSquare(double meanSquare)
    {
        // 10 log10 of the mean square is 20 log10 of the RMS; anything at or below -127 dBov is silence.
        const double Floor = 1.995262314968883e-13;
        if (meanSquare <= Floor)
        {
            return Silence;
        }

        double decibels = -10 * Math.Log10(Math.Min(meanSquare, 1));
        return new AudioLevel(Math.Clamp((int)(decibels + 0.5), 0, Silent));
    }

    /// <summary>
    /// The sum of the squared samples of a frame, each normalized to full scale, over every channel; with
    /// the sample count, what <see cref="FromMeanSquare"/> needs.
    /// </summary>
    /// <param name="frame">The audio.</param>
    /// <returns>The sum, and how many samples it covers.</returns>
    public static (double SumOfSquares, long Samples) Measure(in AudioFrame frame)
    {
        if (frame.Format.SampleFormat == SampleFormat.F32)
        {
            ReadOnlySpan<float> samples = MemoryMarshal.Cast<byte, float>(frame.Samples);
            return (TensorPrimitives.SumOfSquares(samples), samples.Length);
        }

        // 16-bit samples convert exactly to float a stack chunk at a time, then sum like floats.
        ReadOnlySpan<short> pcm = MemoryMarshal.Cast<byte, short>(frame.Samples);
        Span<float> chunk = stackalloc float[512];
        double sum = 0;
        for (int at = 0; at < pcm.Length; at += chunk.Length)
        {
            ReadOnlySpan<short> part = pcm.Slice(at, Math.Min(chunk.Length, pcm.Length - at));
            Span<float> converted = chunk[..part.Length];
            TensorPrimitives.ConvertTruncating(part, converted);
            sum += TensorPrimitives.SumOfSquares(converted);
        }

        return (sum / (32768.0 * 32768.0), pcm.Length);
    }
}
