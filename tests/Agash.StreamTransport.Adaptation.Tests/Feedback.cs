namespace Agash.StreamTransport.Adaptation.Tests;

// Feedback for controller tests: packets as (send time, receive time) in microseconds, -1 for a loss,
// turned into resolved observations and a round-trip sample from the latest-sent arrival.
internal static class Feedback
{
    public static Sent Packet(
        ushort sequence,
        int size,
        long sentMicros,
        long receivedMicros,
        EcnCodepoint ecn = EcnCodepoint.NotEct
    ) => new(sequence, size, sentMicros, receivedMicros, ecn);

    public static TimeSpan Time(long micros) => TimeSpan.FromMicroseconds(micros);

    public static CapacityEstimate Feed(
        ICongestionController controller,
        Sent[] packets,
        long nowMicros
    )
    {
        var observations = new PacketObservation[packets.Length];
        long latestSent = long.MinValue;
        for (int i = 0; i < packets.Length; i++)
        {
            Sent p = packets[i];
            bool received = p.ReceivedMicros >= 0;
            observations[i] = new PacketObservation(
                new SentPacket(p.Sequence, p.Size, Time(p.SentMicros), TrafficClass.Video),
                received ? PacketOutcome.Delivered : PacketOutcome.Lost,
                received ? Time(p.ReceivedMicros) : null,
                p.Ecn
            );
            if (received)
            {
                latestSent = Math.Max(latestSent, p.SentMicros);
            }
        }

        TimeSpan? roundTrip = latestSent == long.MinValue ? null : Time(nowMicros - latestSent);
        return controller.OnFeedback(observations, roundTrip, Time(nowMicros));
    }
}

internal readonly record struct Sent(
    ushort Sequence,
    int Size,
    long SentMicros,
    long ReceivedMicros,
    EcnCodepoint Ecn = EcnCodepoint.NotEct
);
