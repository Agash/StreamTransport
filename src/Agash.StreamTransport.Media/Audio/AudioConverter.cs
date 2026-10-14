using System.Buffers.Binary;
using System.Collections.Immutable;

namespace Agash.StreamTransport.Media;

/// <summary>
/// Converts interleaved PCM from one <see cref="AudioFormat"/> to another: the sample type, the channel
/// count and the sample rate. One converter carries one stream: a rate change keeps filter history from
/// each frame to the next, so the output is continuous across frame boundaries.
/// </summary>
/// <remarks>
/// <para>
/// Rates are converted with a Kaiser-windowed sinc filter, interpolated between 256 phases: 64 taps, and
/// proportionally more when downsampling, with the passband ending at 92% of the lower rate's Nyquist
/// frequency so nothing above it aliases. Channels map
/// as people expect: mono is copied to every output channel, and mixed to mono as the mean; otherwise the
/// first channels are kept, which are the front left and right in every standard order, and missing ones
/// are silent.
/// </para>
/// <para>
/// Each output frame is stamped with the time of the input it was computed from, so timestamps stay exact
/// whatever the ratio; a frame can start a little before the input frame that completed it, with the
/// filter's look-ahead.
/// </para>
/// </remarks>
public sealed class AudioConverter
{
    // Taps on each side of the output sample at equal rates, the phases the filter is tabulated at, and
    // where the passband ends as a fraction of the lower Nyquist frequency: the transition band fits
    // below it, so nothing above it folds back.
    private const int BaseHalfTaps = 32;
    private const int Phases = 256;
    private const double KaiserBeta = 8.6;
    private const double Passband = 0.92;

    private readonly int _inChannels;
    private readonly int _outChannels;
    private readonly bool _resamples;
    private readonly double _step;
    private readonly int _halfTaps;
    private readonly float[]? _table;

    // Mapped input not yet consumed, interleaved at the output channel count, and where in it the next
    // output sample falls (in input samples).
    private float[] _history = [];
    private int _historyFrames;
    private double _position;
    private float[] _mapped = [];
    private float[] _resampled = [];
    private byte[] _output = [];

    /// <summary>Creates a converter between two formats.</summary>
    /// <param name="input">The PCM it is given.</param>
    /// <param name="output">The PCM it produces.</param>
    /// <exception cref="ArgumentOutOfRangeException">A rate or channel count is not positive.</exception>
    public AudioConverter(AudioFormat input, AudioFormat output)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(input.SampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(output.SampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(input.Channels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(output.Channels);
        Input = input;
        Output = output;
        _inChannels = input.Channels;
        _outChannels = output.Channels;
        _resamples = input.SampleRate != output.SampleRate;
        if (_resamples)
        {
            _step = (double)input.SampleRate / output.SampleRate;

            // Downsampling lowers the cutoff, and the filter lengthens with it so its transition band
            // stays as narrow against the output's band as it is at equal rates.
            double ratio = Math.Min(1.0, (double)output.SampleRate / input.SampleRate);
            _halfTaps = (int)Math.Ceiling(BaseHalfTaps / ratio);
            _table = Tabulate(Passband * ratio, _halfTaps);

            // The first output sample is the first input sample; the history before it is silence.
            _history = new float[_halfTaps * _outChannels];
            _historyFrames = _halfTaps;
            _position = _halfTaps;
        }
    }

    /// <summary>The PCM it is given.</summary>
    public AudioFormat Input { get; }

    /// <summary>The PCM it produces.</summary>
    public AudioFormat Output { get; }

    /// <summary>
    /// How long input waits in the filter before the output for it is complete: the look-ahead of a rate
    /// change, zero without one. Timestamps already account for it.
    /// </summary>
    public TimeSpan Delay =>
        _resamples ? TimeSpan.FromSeconds((double)_halfTaps / Input.SampleRate) : TimeSpan.Zero;

    /// <summary>
    /// A consumer that converts what it is given and passes it to <paramref name="consumer"/>, for a
    /// source connected to something that takes another format.
    /// </summary>
    /// <param name="consumer">Where converted frames go.</param>
    /// <returns>The converting consumer.</returns>
    public IAudioFrameConsumer Feeding(IAudioFrameConsumer consumer)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        return new Feed(this, consumer);
    }

    /// <summary>
    /// The format nearest <paramref name="source"/> that <paramref name="constraints"/> take: the source's
    /// own rate, sample type and channels where allowed, otherwise the constraints' most preferred.
    /// </summary>
    /// <param name="source">The PCM there is.</param>
    /// <param name="constraints">The PCM that is taken.</param>
    /// <returns>The format to convert to; <paramref name="source"/> itself when it is taken as it is.</returns>
    public static AudioFormat Target(AudioFormat source, AudioConstraints constraints)
    {
        ArgumentNullException.ThrowIfNull(constraints);
        int rate = constraints.SampleRates.Contains(source.SampleRate)
            ? source.SampleRate
            : Nearest(constraints.SampleRates, source.SampleRate);
        SampleFormat sampleFormat = constraints.SampleFormats.Contains(source.SampleFormat)
            ? source.SampleFormat
            : constraints.SampleFormats[0];
        int channels = Math.Clamp(source.Channels, 1, constraints.MaximumChannels);
        return new AudioFormat(sampleFormat, rate, channels);
    }

    /// <summary>
    /// Converts a frame and hands what is complete to <paramref name="consumer"/>: one frame, or none
    /// while a rate change is still filling its look-ahead.
    /// </summary>
    /// <param name="frame">Input in <see cref="Input"/>.</param>
    /// <param name="consumer">Where the converted frame goes; it is valid only during the call.</param>
    /// <exception cref="ArgumentException">The frame is not in <see cref="Input"/>.</exception>
    public void Convert(in AudioFrame frame, IAudioFrameConsumer consumer)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        if (frame.Format != Input)
        {
            throw new ArgumentException(
                $"The frame is {frame.Format}; the converter takes {Input}.",
                nameof(frame)
            );
        }

        int frames = frame.SampleCount;
        if (frames == 0)
        {
            return;
        }

        Span<float> mapped = Grow(ref _mapped, frames * _outChannels);
        Map(frame.Samples, frames, mapped);

        if (!_resamples)
        {
            Emit(mapped, frames, frame.Timestamp, consumer);
            return;
        }

        // Appended to the history; the frame starts where the history ended.
        int frameStart = _historyFrames;
        EnsureHistory(_historyFrames + frames);
        mapped.CopyTo(_history.AsSpan(_historyFrames * _outChannels));
        _historyFrames += frames;

        // Every output sample whose look-ahead is in the history.
        int capacity = (int)((_historyFrames - _position) / _step) + 2;
        Span<float> resampled = Grow(ref _resampled, capacity * _outChannels);
        double first = _position;
        int produced = 0;
        // The last tap of a sample at index i is i + halfTaps, which has to be in the history.
        while ((int)Math.Floor(_position) + _halfTaps < _historyFrames)
        {
            Interpolate(_position, resampled.Slice(produced * _outChannels, _outChannels));
            produced++;
            _position += _step;
        }

        // Drop what no later output sample reaches back to.
        int keepFrom = Math.Max(0, (int)Math.Floor(_position) - _halfTaps + 1);
        if (keepFrom > 0)
        {
            _history
                .AsSpan(keepFrom * _outChannels, (_historyFrames - keepFrom) * _outChannels)
                .CopyTo(_history);
            _historyFrames -= keepFrom;
            _position -= keepFrom;
        }

        if (produced == 0)
        {
            return;
        }

        var offset = TimeSpan.FromSeconds((first - frameStart) / Input.SampleRate);
        MediaTimestamp stamp = frame.Timestamp with { Time = frame.Timestamp.Time + offset };
        Emit(resampled[..(produced * _outChannels)], produced, stamp, consumer);
    }

    // The input decoded to floats and mapped to the output's channels.
    private void Map(ReadOnlySpan<byte> samples, int frames, Span<float> mapped)
    {
        int bytes = Input.BytesPerSample;
        bool s16 = Input.SampleFormat == SampleFormat.S16;
        for (int f = 0; f < frames; f++)
        {
            int at = f * _inChannels * bytes;
            Span<float> to = mapped.Slice(f * _outChannels, _outChannels);
            if (_inChannels == 1)
            {
                to.Fill(Read(samples[at..], s16));
            }
            else if (_outChannels == 1)
            {
                float sum = 0;
                for (int c = 0; c < _inChannels; c++)
                {
                    sum += Read(samples[(at + (c * bytes))..], s16);
                }

                to[0] = sum / _inChannels;
            }
            else
            {
                for (int c = 0; c < _outChannels; c++)
                {
                    to[c] = c < _inChannels ? Read(samples[(at + (c * bytes))..], s16) : 0f;
                }
            }
        }
    }

    private static float Read(ReadOnlySpan<byte> sample, bool s16) =>
        s16
            ? BinaryPrimitives.ReadInt16LittleEndian(sample) / 32768f
            : BinaryPrimitives.ReadSingleLittleEndian(sample);

    // One output sample of every channel, at a fractional input position.
    private void Interpolate(double position, Span<float> into)
    {
        int index = (int)Math.Floor(position);
        double phase = (position - index) * Phases;
        int row = (int)phase;
        float blend = (float)(phase - row);
        int taps = 2 * _halfTaps;
        ReadOnlySpan<float> low = _table.AsSpan(row * taps, taps);
        ReadOnlySpan<float> high = _table.AsSpan((row + 1) * taps, taps);
        into.Clear();
        int start = index - _halfTaps + 1;
        for (int k = 0; k < taps; k++)
        {
            float coefficient = low[k] + ((high[k] - low[k]) * blend);
            ReadOnlySpan<float> sample = _history.AsSpan((start + k) * _outChannels, _outChannels);
            for (int c = 0; c < _outChannels; c++)
            {
                into[c] += sample[c] * coefficient;
            }
        }
    }

    private void Emit(
        ReadOnlySpan<float> samples,
        int frames,
        MediaTimestamp timestamp,
        IAudioFrameConsumer consumer
    )
    {
        int bytes = Output.BytesPerSample;
        Span<byte> output = Grow(ref _output, frames * _outChannels * bytes);
        if (Output.SampleFormat == SampleFormat.S16)
        {
            for (int i = 0; i < samples.Length; i++)
            {
                float scaled = Math.Clamp(samples[i] * 32768f, short.MinValue, short.MaxValue);
                BinaryPrimitives.WriteInt16LittleEndian(
                    output[(i * 2)..],
                    (short)MathF.Round(scaled)
                );
            }
        }
        else
        {
            for (int i = 0; i < samples.Length; i++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(output[(i * 4)..], samples[i]);
            }
        }

        consumer.OnFrame(new AudioFrame(output, Output, timestamp));
    }

    private void EnsureHistory(int frames)
    {
        int needed = frames * _outChannels;
        if (_history.Length < needed)
        {
            Array.Resize(ref _history, Math.Max(needed, _history.Length * 2));
        }
    }

    private static Span<T> Grow<T>(ref T[] buffer, int length)
    {
        if (buffer.Length < length)
        {
            buffer = new T[length];
        }

        return buffer.AsSpan(0, length);
    }

    private static int Nearest(ImmutableArray<int> rates, int rate)
    {
        // The lowest rate at or above the source's keeps its bandwidth; failing that, the highest there is.
        int best = 0;
        foreach (int candidate in rates)
        {
            if (candidate >= rate && (best == 0 || candidate < best || best < rate))
            {
                best = candidate;
            }
        }

        return best != 0 ? best : rates.Max();
    }

    // The filter at Phases + 1 fractional offsets, each row normalised to unity gain at DC.
    private static float[] Tabulate(double cutoff, int halfTaps)
    {
        float[] table = new float[(Phases + 1) * 2 * halfTaps];
        double scale = Bessel0(KaiserBeta);
        for (int p = 0; p <= Phases; p++)
        {
            double fraction = (double)p / Phases;
            Span<float> row = table.AsSpan(p * 2 * halfTaps, 2 * halfTaps);
            double sum = 0;
            for (int k = 0; k < 2 * halfTaps; k++)
            {
                double x = k - halfTaps + 1 - fraction;
                double window =
                    Math.Abs(x) >= halfTaps
                        ? 0
                        : Bessel0(KaiserBeta * Math.Sqrt(1 - ((x / halfTaps) * (x / halfTaps))))
                            / scale;
                double sinc = x == 0 ? 1 : Math.Sin(Math.PI * cutoff * x) / (Math.PI * cutoff * x);
                row[k] = (float)(cutoff * sinc * window);
                sum += row[k];
            }

            for (int k = 0; k < row.Length; k++)
            {
                row[k] = (float)(row[k] / sum);
            }
        }

        return table;
    }

    private sealed class Feed(AudioConverter converter, IAudioFrameConsumer consumer)
        : IAudioFrameConsumer
    {
        public void OnFrame(in AudioFrame frame) => converter.Convert(in frame, consumer);
    }

    // The zeroth-order modified Bessel function of the first kind, by its series.
    private static double Bessel0(double x)
    {
        double sum = 1;
        double term = 1;
        double half = x / 2;
        for (int k = 1; k < 50; k++)
        {
            term *= half / k * (half / k);
            sum += term;
            if (term < sum * 1e-12)
            {
                break;
            }
        }

        return sum;
    }
}
