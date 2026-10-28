namespace Agash.StreamTransport.WebRtc.Rtp;

/// <summary>A set of RTP header extension identifiers, 1 to 255, held as 256 bits by value.</summary>
public readonly record struct RtpExtensionIdSet
{
    private readonly ulong _low;
    private readonly ulong _midLow;
    private readonly ulong _midHigh;
    private readonly ulong _high;

    private RtpExtensionIdSet(ulong low, ulong midLow, ulong midHigh, ulong high)
    {
        _low = low;
        _midLow = midLow;
        _midHigh = midHigh;
        _high = high;
    }

    /// <summary>Whether the set holds none.</summary>
    public bool IsEmpty => (_low | _midLow | _midHigh | _high) == 0;

    /// <summary>Whether the set holds an identifier.</summary>
    /// <param name="id">The identifier.</param>
    /// <returns>Whether it does.</returns>
    public bool Contains(int id) => id is >= 0 and <= 255 && ((Word(id) >> (id & 63)) & 1) != 0;

    /// <summary>The set with an identifier added.</summary>
    /// <param name="id">The identifier, 1 to 255.</param>
    /// <returns>The set.</returns>
    public RtpExtensionIdSet With(int id)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(id, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(id, 255);
        ulong bit = 1UL << (id & 63);
        return (id >> 6) switch
        {
            0 => new(_low | bit, _midLow, _midHigh, _high),
            1 => new(_low, _midLow | bit, _midHigh, _high),
            2 => new(_low, _midLow, _midHigh | bit, _high),
            _ => new(_low, _midLow, _midHigh, _high | bit),
        };
    }

    private ulong Word(int id) =>
        (id >> 6) switch
        {
            0 => _low,
            1 => _midLow,
            2 => _midHigh,
            _ => _high,
        };
}
