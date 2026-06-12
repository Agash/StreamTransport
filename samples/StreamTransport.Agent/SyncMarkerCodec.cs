using System.Diagnostics;

namespace StreamTransport.Agent;

/// <summary>
/// The sync-marker codec used by <c>--verify</c> to measure true A/V lip-sync end to end. Once per wall-clock
/// second both the video and audio sources emit a correlated marker carrying the same <b>sequence id</b>
/// (derived from the shared clock, so no coordination is needed) plus, on the video side, the sender's capture
/// timestamp. The receiver recovers the id from each stream independently and pairs them, so the measured skew
/// is between two events known to be the same one - immune to the brightness/RMS-threshold guesswork the old
/// heuristic used, and to which marker frame happens to be detected.
///
/// <para>Video: the marker frame is a forced keyframe (so it decodes cleanly on its own) whose top strip carries
/// the payload as large luma blocks (a preamble, then the id and capture-ms bits); the rest of the frame is
/// white so the frame still reads as "bright". Audio: the marker burst is a single tone whose frequency encodes
/// the id, recovered by a Goertzel filter bank.</para>
/// </summary>
internal static class SyncMarkerCodec
{
    /// <summary>For the first this-many ms of each wall second, both sources emit the marker.</summary>
    public const int WindowMs = 120;

    // Sequence id space. 64 ids (one per second) is unique well beyond the receiver's pairing window, and small
    // enough that the audio frequency code stays inside an easily-resolved band.
    public const int SeqIdModulo = 64;

    // Video luma-strip payload: an 8-bit preamble to recognise the strip, then the 6-bit id and 26 bits of the
    // sender capture time in ms (~67 s range, ms resolution - enough for a same-machine capture->present latency
    // readout). Laid MSB-first as equal-width full-height blocks across the top StripRows of the luma plane.
    private const int Preamble = 0xB2;
    private const int PreambleBits = 8;
    private const int SeqIdBits = 6;
    private const int CaptureMsBits = 26;
    private const int TotalBits = PreambleBits + SeqIdBits + CaptureMsBits;
    private const int StripRows = 40;
    private const byte BitHigh = 240;
    private const byte BitLow = 16;

    // Audio frequency code: id -> tone frequency. The 100 Hz step is twice a 20 ms frame's ~50 Hz Goertzel
    // resolution, so adjacent ids never alias. The RMS gate sits between the steady 0.3-amplitude tone (~7000)
    // and the 0.95-amplitude marker burst (~22000), so only burst frames are decoded for an id.
    private const double AudioBaseFreq = 500.0;
    private const double AudioFreqStep = 100.0;
    private const double AudioMarkerMinRms = 15000.0;

    /// <summary>The marker sequence id for the current instant - identical across sources reading the same clock.</summary>
    public static int CurrentSeqId() => (int)((NowMs() / 1000) % SeqIdModulo);

    /// <summary>True during the marker window at the start of each wall second.</summary>
    public static bool IsOn() => NowMs() % 1000 < WindowMs;

    /// <summary>The tone frequency that encodes <paramref name="seqId"/> in the audio marker burst.</summary>
    public static double AudioFrequency(int seqId) => AudioBaseFreq + (seqId * AudioFreqStep);

    private static long NowMs() => Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;

    /// <summary>The sender capture clock in ms, masked to the bits the video strip carries.</summary>
    public static long CaptureMsNow() => (Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency) & ((1L << CaptureMsBits) - 1);

    /// <summary>
    /// The capture-ms field for a given frame capture timestamp (the same monotonic ns value used as the
    /// frame's <c>PresentationTimeNs</c>), so the embedded marker time matches the abs-capture time the
    /// transport stamps - the two must reference the same instant for the verify metric to be meaningful.
    /// </summary>
    public static long CaptureMsFromNs(long presentationTimeNs) => (presentationTimeNs / 1_000_000) & ((1L << CaptureMsBits) - 1);

    // ---- Video encode (sender) ----

    /// <summary>
    /// Render the video marker into an NV12 luma plane: a payload strip across the top rows, white below. The
    /// frame should be sent as a forced keyframe so the strip decodes losslessly enough to read back.
    /// </summary>
    public static void RenderVideoMarker(Span<byte> nv12, int width, int height, int seqId, long captureMs)
    {
        long payload = ((long)Preamble << (SeqIdBits + CaptureMsBits))
            | ((long)(seqId & ((1 << SeqIdBits) - 1)) << CaptureMsBits)
            | (captureMs & ((1L << CaptureMsBits) - 1));

        int strip = Math.Min(StripRows, height);
        int blockWidth = width / TotalBits;

        // White background (luma plane). Chroma is set neutral by the caller's white fill.
        nv12[..(width * height)].Fill(255);

        for (int bit = 0; bit < TotalBits; bit++)
        {
            byte value = ((payload >> (TotalBits - 1 - bit)) & 1) != 0 ? BitHigh : BitLow;
            int x0 = bit * blockWidth;
            int x1 = bit == TotalBits - 1 ? width : x0 + blockWidth;
            for (int row = 0; row < strip; row++)
            {
                nv12.Slice((row * width) + x0, x1 - x0).Fill(value);
            }
        }
    }

    // ---- Video decode (receiver) ----

    /// <summary>
    /// Recover the marker payload from a decoded NV12/I420 luma plane. Returns false when the preamble is absent
    /// (the frame is not a marker, or is too degraded to trust).
    /// </summary>
    public static bool TryReadVideoMarker(ReadOnlySpan<byte> luma, int width, int height, out int seqId, out long captureMs)
    {
        seqId = 0;
        captureMs = 0;
        if (luma.Length < width * height || width < TotalBits)
        {
            return false;
        }

        int strip = Math.Min(StripRows, height);
        int sampleRow = strip / 2;
        int blockWidth = width / TotalBits;
        long payload = 0;
        for (int bit = 0; bit < TotalBits; bit++)
        {
            int x0 = bit * blockWidth;
            int x1 = bit == TotalBits - 1 ? width : x0 + blockWidth;
            int cx = (x0 + x1) / 2;
            // Average a small patch at the block centre for noise immunity.
            long sum = 0;
            int n = 0;
            for (int row = Math.Max(0, sampleRow - 4); row < Math.Min(strip, sampleRow + 4); row++)
            {
                for (int x = Math.Max(x0, cx - 4); x < Math.Min(x1, cx + 4); x++)
                {
                    sum += luma[(row * width) + x];
                    n++;
                }
            }

            int avg = n > 0 ? (int)(sum / n) : 0;
            payload = (payload << 1) | (avg >= 128 ? 1u : 0u);
        }

        if ((int)((payload >> (SeqIdBits + CaptureMsBits)) & 0xFF) != Preamble)
        {
            return false;
        }

        seqId = (int)((payload >> CaptureMsBits) & ((1 << SeqIdBits) - 1));
        captureMs = payload & ((1L << CaptureMsBits) - 1);
        return true;
    }

    // ---- Audio decode (receiver) ----

    /// <summary>
    /// Recover the marker id from a burst of 16-bit PCM via a Goertzel filter bank over the candidate
    /// frequencies. Reads only channel 0 of an interleaved <paramref name="channels"/>-channel buffer - the
    /// receiver decodes Opus to stereo, and reading interleaved stereo as mono sample-and-holds each sample,
    /// halving the apparent frequency and so the decoded id. Returns false when the frame is not a loud burst.
    /// </summary>
    public static bool TryReadAudioMarker(ReadOnlySpan<byte> pcm16, int sampleRate, int channels, out int seqId)
    {
        seqId = 0;
        int stride = Math.Max(1, channels);
        int samples = pcm16.Length / 2 / stride;
        if (samples < 64)
        {
            return false;
        }

        // Gate on energy: only burst frames carry a marker.
        double sumSq = 0;
        for (int i = 0; i < samples; i++)
        {
            int b = 2 * i * stride;
            short s = (short)(pcm16[b] | (pcm16[b + 1] << 8));
            sumSq += (double)s * s;
        }

        if (Math.Sqrt(sumSq / samples) < AudioMarkerMinRms)
        {
            return false;
        }

        double bestEnergy = 0;
        int bestId = -1;
        for (int id = 0; id < SeqIdModulo; id++)
        {
            double energy = Goertzel(pcm16, samples, stride, sampleRate, AudioFrequency(id));
            if (energy > bestEnergy)
            {
                bestEnergy = energy;
                bestId = id;
            }
        }

        if (bestId < 0)
        {
            return false;
        }

        seqId = bestId;
        return true;
    }

    private static double Goertzel(ReadOnlySpan<byte> pcm16, int samples, int stride, int sampleRate, double freq)
    {
        double w = 2.0 * Math.PI * freq / sampleRate;
        double coeff = 2.0 * Math.Cos(w);
        double s0 = 0, s1 = 0, s2 = 0;
        for (int i = 0; i < samples; i++)
        {
            int b = 2 * i * stride;
            short sample = (short)(pcm16[b] | (pcm16[b + 1] << 8));
            s0 = sample + (coeff * s1) - s2;
            s2 = s1;
            s1 = s0;
        }

        return (s1 * s1) + (s2 * s2) - (coeff * s1 * s2);
    }
}
