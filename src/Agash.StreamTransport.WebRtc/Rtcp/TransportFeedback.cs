using System.Buffers.Binary;

namespace Agash.StreamTransport.WebRtc.Rtcp;

/// <summary>One packet a transport feedback packet reports: its transport-wide sequence number and arrival.</summary>
/// <param name="Sequence">The transport-wide sequence number.</param>
/// <param name="ArrivalMicros">
/// When it arrived on the receiver's clock, in microseconds, at the feedback's 250 µs resolution; null
/// when it was not received.
/// </param>
public readonly record struct TransportPacketArrival(ushort Sequence, long? ArrivalMicros);

/// <summary>
/// The transport-wide congestion control feedback packet (RTPFB, PT 205, FMT 15) of
/// <c>draft-holmer-rmcat-transport-wide-cc-extensions-01</c>, which every browser sends and reads. It
/// carries what RFC 8888's report carries but ECN: per packet, whether it arrived and when, keyed on a
/// sequence number every packet of the connection takes from one counter (the transport-wide sequence
/// header extension). This stack sends and reads it where the peer offers it and not RFC 8888.
/// </summary>
/// <remarks>
/// After the fixed part (sender and media SSRC, base sequence, status count, a 24-bit reference time in
/// 64 ms units and a feedback counter) come the packet statuses as run-length and status-vector chunks,
/// then one receive delta per received packet in 250 µs units: one byte when small and not negative, two
/// signed bytes otherwise.
/// </remarks>
public static class TransportFeedback
{
    /// <summary>The feedback message type within RTPFB.</summary>
    public const int Fmt = 15;

    /// <summary>The reference time's unit, in microseconds.</summary>
    public const long ReferenceUnitMicros = 64_000;

    /// <summary>A receive delta's unit, in microseconds.</summary>
    public const long DeltaUnitMicros = 250;

    private const int FixedLength = 20;
    private const int MaxRun = 8191;

    // The packet statuses.
    private const int NotReceived = 0;
    private const int SmallDelta = 1;
    private const int LargeDelta = 2;

    /// <summary>
    /// Builds one feedback packet from the start of <paramref name="arrivals"/>: as many packets as fit
    /// <paramref name="maxBytes"/> and whose receive deltas the format can express. The first packet
    /// reported must have arrived, since it sets the reference time.
    /// </summary>
    /// <param name="destination">Where the packet goes.</param>
    /// <param name="senderSsrc">The reporting endpoint's SSRC.</param>
    /// <param name="mediaSsrc">A media source the report is about, as the header needs one; any of the peer's.</param>
    /// <param name="feedbackCount">This packet's number in the sequence of feedback packets, wrapping at 256.</param>
    /// <param name="arrivals">Consecutive transport-wide sequence numbers from the base, received or not.</param>
    /// <param name="maxBytes">The most the packet may take.</param>
    /// <param name="consumed">How many of <paramref name="arrivals"/> the packet reports.</param>
    /// <returns>The packet's length, padded to 32 bits.</returns>
    public static int Build(
        Span<byte> destination,
        uint senderSsrc,
        uint mediaSsrc,
        byte feedbackCount,
        ReadOnlySpan<TransportPacketArrival> arrivals,
        int maxBytes,
        out int consumed
    )
    {
        consumed = 0;
        if (arrivals.IsEmpty || arrivals[0].ArrivalMicros is not { } first)
        {
            throw new ArgumentException(
                "The first packet reported must have arrived.",
                nameof(arrivals)
            );
        }

        long reference = FloorDiv(first, ReferenceUnitMicros);

        // Statuses and deltas, as far as the deltas fit and the estimated size allows: a chunk of two
        // bytes covers at least seven packets, and a delta takes up to two.
        Span<byte> symbols =
            arrivals.Length <= 1024 ? stackalloc byte[arrivals.Length] : new byte[arrivals.Length];
        long previous = reference * ReferenceUnitMicros;
        int deltaBytes = 0;
        int count = 0;
        for (; count < arrivals.Length && count < ushort.MaxValue; count++)
        {
            int symbol = NotReceived;
            if (arrivals[count].ArrivalMicros is { } arrived)
            {
                long units = (arrived - previous) / DeltaUnitMicros;
                if (units is < short.MinValue or > short.MaxValue)
                {
                    break;
                }

                symbol = units is >= 0 and <= byte.MaxValue ? SmallDelta : LargeDelta;
                previous += units * DeltaUnitMicros;
            }

            int estimate = FixedLength + (2 * ((count / 7) + 1)) + deltaBytes + symbol;
            if (estimate > maxBytes)
            {
                break;
            }

            symbols[count] = (byte)symbol;
            deltaBytes += symbol;
        }

        consumed = count;
        destination[0] = 0x80 | Fmt;
        destination[1] = (byte)RtcpPacketType.TransportFeedback;
        BinaryPrimitives.WriteUInt32BigEndian(destination[4..], senderSsrc);
        BinaryPrimitives.WriteUInt32BigEndian(destination[8..], mediaSsrc);
        BinaryPrimitives.WriteUInt16BigEndian(destination[12..], arrivals[0].Sequence);
        BinaryPrimitives.WriteUInt16BigEndian(destination[14..], (ushort)count);
        int referenceField = (int)(reference & 0xFFFFFF);
        destination[16] = (byte)(referenceField >> 16);
        destination[17] = (byte)(referenceField >> 8);
        destination[18] = (byte)referenceField;
        destination[19] = feedbackCount;

        int at = FixedLength;
        ReadOnlySpan<byte> statuses = symbols[..count];
        for (int i = 0; i < count; )
        {
            int run = 1;
            while (i + run < count && run < MaxRun && statuses[i + run] == statuses[i])
            {
                run++;
            }

            ushort chunk;
            if (run >= 14 || (run >= 7 && statuses[i] == LargeDelta))
            {
                // Run length: T=0, the status, the run.
                chunk = (ushort)((statuses[i] << 13) | run);
                i += run;
            }
            else if (OneBit(statuses[i..Math.Min(count, i + 14)]))
            {
                // A vector of fourteen one-bit statuses: T=1, S=0.
                chunk = 0x8000;
                for (int k = 0; k < 14 && i + k < count; k++)
                {
                    chunk |= (ushort)(statuses[i + k] << (13 - k));
                }

                i += 14;
            }
            else
            {
                // A vector of seven two-bit statuses: T=1, S=1.
                chunk = 0xC000;
                for (int k = 0; k < 7 && i + k < count; k++)
                {
                    chunk |= (ushort)(statuses[i + k] << (12 - (2 * k)));
                }

                i += 7;
            }

            BinaryPrimitives.WriteUInt16BigEndian(destination[at..], chunk);
            at += 2;
        }

        previous = reference * ReferenceUnitMicros;
        for (int i = 0; i < count; i++)
        {
            if (arrivals[i].ArrivalMicros is not { } arrived)
            {
                continue;
            }

            long units = (arrived - previous) / DeltaUnitMicros;
            previous += units * DeltaUnitMicros;
            if (statuses[i] == SmallDelta)
            {
                destination[at++] = (byte)units;
            }
            else
            {
                BinaryPrimitives.WriteInt16BigEndian(destination[at..], (short)units);
                at += 2;
            }
        }

        // Zero padding to a word; the length counts words less one (RFC 3550 section 6.4.1).
        int padded = (at + 3) / 4 * 4;
        destination[at..padded].Clear();
        BinaryPrimitives.WriteUInt16BigEndian(destination[2..], (ushort)((padded / 4) - 1));
        return padded;
    }

    /// <summary>
    /// Reads every transport feedback packet in a compound RTCP packet, appending what each reports in
    /// sequence order.
    /// </summary>
    /// <param name="rtcp">The compound packet.</param>
    /// <param name="arrivals">Where the reported packets go.</param>
    /// <returns>Whether any transport feedback packet was read.</returns>
    public static bool TryParse(ReadOnlySpan<byte> rtcp, List<TransportPacketArrival> arrivals)
    {
        ArgumentNullException.ThrowIfNull(arrivals);
        bool any = false;
        while (rtcp.Length >= 4)
        {
            int length = (BinaryPrimitives.ReadUInt16BigEndian(rtcp[2..]) + 1) * 4;
            if (length > rtcp.Length)
            {
                break;
            }

            ReadOnlySpan<byte> packet = rtcp[..length];
            if (
                packet[1] == (byte)RtcpPacketType.TransportFeedback
                && (packet[0] & 0x1F) == Fmt
                && TryParseOne(packet, arrivals)
            )
            {
                any = true;
            }

            rtcp = rtcp[length..];
        }

        return any;
    }

    private static bool TryParseOne(
        ReadOnlySpan<byte> packet,
        List<TransportPacketArrival> arrivals
    )
    {
        if (packet.Length < FixedLength)
        {
            return false;
        }

        ushort baseSequence = BinaryPrimitives.ReadUInt16BigEndian(packet[12..]);
        int count = BinaryPrimitives.ReadUInt16BigEndian(packet[14..]);
        int referenceField = (packet[16] << 16) | (packet[17] << 8) | packet[18];
        long reference = (referenceField << 8) >> 8; // 24-bit signed.
        Span<byte> statuses = count <= 2048 ? stackalloc byte[count] : new byte[count];
        int at = FixedLength;
        int decoded = 0;
        while (decoded < count)
        {
            if (at + 2 > packet.Length)
            {
                return false;
            }

            ushort chunk = BinaryPrimitives.ReadUInt16BigEndian(packet[at..]);
            at += 2;
            if ((chunk & 0x8000) == 0)
            {
                int status = (chunk >> 13) & 0x3;
                int run = chunk & 0x1FFF;
                for (int k = 0; k < run && decoded < count; k++)
                {
                    statuses[decoded++] = (byte)status;
                }
            }
            else if ((chunk & 0x4000) == 0)
            {
                for (int k = 0; k < 14 && decoded < count; k++)
                {
                    statuses[decoded++] = (byte)((chunk >> (13 - k)) & 0x1);
                }
            }
            else
            {
                for (int k = 0; k < 7 && decoded < count; k++)
                {
                    statuses[decoded++] = (byte)((chunk >> (12 - (2 * k))) & 0x3);
                }
            }
        }

        long arrival = reference * ReferenceUnitMicros;
        int start = arrivals.Count;
        for (int i = 0; i < count; i++)
        {
            ushort sequence = (ushort)(baseSequence + i);
            switch (statuses[i])
            {
                case SmallDelta:
                    if (at + 1 > packet.Length)
                    {
                        arrivals.RemoveRange(start, arrivals.Count - start);
                        return false;
                    }

                    arrival += packet[at++] * DeltaUnitMicros;
                    arrivals.Add(new TransportPacketArrival(sequence, arrival));
                    break;
                case LargeDelta:
                    if (at + 2 > packet.Length)
                    {
                        arrivals.RemoveRange(start, arrivals.Count - start);
                        return false;
                    }

                    arrival += BinaryPrimitives.ReadInt16BigEndian(packet[at..]) * DeltaUnitMicros;
                    at += 2;
                    arrivals.Add(new TransportPacketArrival(sequence, arrival));
                    break;
                default:
                    arrivals.Add(new TransportPacketArrival(sequence, null));
                    break;
            }
        }

        return true;
    }

    private static bool OneBit(ReadOnlySpan<byte> statuses)
    {
        foreach (byte status in statuses)
        {
            if (status > SmallDelta)
            {
                return false;
            }
        }

        return true;
    }

    private static long FloorDiv(long value, long divisor) =>
        value >= 0 ? value / divisor : -((-value + divisor - 1) / divisor);
}
