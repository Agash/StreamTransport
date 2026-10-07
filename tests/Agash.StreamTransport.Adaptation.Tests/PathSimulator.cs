namespace Agash.StreamTransport.Adaptation.Tests;

// How the bottleneck treats packets.
public enum Bottleneck
{
    // A FIFO that drops what does not fit (tail drop).
    TailDrop,

    // A FIFO that marks CE once its queue passes a shallow threshold (an L4S step marker).
    L4sMarking,

    // A FIFO that marks CE once a standing queue builds (a classic ECN AQM such as PIE).
    ClassicMarking,

    // No queue: whatever exceeds the rate is dropped (a token-bucket policer).
    Policer,
}

// A closed loop on simulated time: a sender paced at the controller's rate and gated by its send window, a
// bottleneck with a capacity and a base one-way delay, and a receiver whose feedback goes through the
// delivery tracker, every 20 ms, like CCFB. Deterministic for a seed.
internal sealed class PathSimulator(ScreamCongestionController controller, int seed = 1)
{
    private const int PacketSize = 1200;
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(1);
    private static readonly TimeSpan FeedbackInterval = TimeSpan.FromMilliseconds(20);

    private readonly DeliveryTracker _tracker = new();
    private readonly Random _random = new(seed);
    private readonly Queue<(long Id, TimeSpan Departs, bool Ce)> _link = new();
    private readonly List<(long Id, TimeSpan Arrived, bool Ce)> _arrived = [];
    private readonly HashSet<long> _lost = [];
    private readonly List<PacketObservation> _observations = [];
    private readonly Queue<(TimeSpan Due, PacketReport[] Reports)> _returning = new();
    private double _sendCredit;
    private double _paceCredit;
    private TimeSpan _linkFree;
    private TimeSpan _lastFeedback;
    private long _nextId;
    private long _highestReported;
    private long _deliveredBytes;
    private TimeSpan _deliveredSince;

    public TimeSpan Now { get; private set; }

    public long CapacityBps { get; set; } = 5_000_000;

    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromMilliseconds(20);

    public Bottleneck Kind { get; set; } = Bottleneck.TailDrop;

    // The deepest queue a tail-drop bottleneck holds.
    public TimeSpan QueueLimit { get; set; } = TimeSpan.FromMilliseconds(300);

    public double RandomLoss { get; set; }

    // Whether reports carry arrival times; without them only the round trip shows the queue.
    public bool ReportArrivalTimes { get; set; } = true;

    // Running averages of what the last measurement window saw.
    public double MeanQueueDelayMs { get; private set; }

    public double MaxQueueDelayMs { get; private set; }

    public long DeliveredBps { get; private set; }

    public int Lost { get; private set; }

    public int Marked { get; private set; }

    public void Run(TimeSpan duration)
    {
        TimeSpan end = Now + duration;
        double queueSum = 0;
        int queueSamples = 0;
        MaxQueueDelayMs = 0;
        _deliveredBytes = 0;
        _deliveredSince = Now;
        while (Now < end)
        {
            Now += Step;
            Send();
            Deliver();
            if (Now - _lastFeedback >= FeedbackInterval)
            {
                Feedback();
            }

            Return();

            double queue = Math.Max(0, (_linkFree - Now).TotalMilliseconds);
            queueSum += queue;
            queueSamples++;
            MaxQueueDelayMs = Math.Max(MaxQueueDelayMs, queue);
            _ = controller.OnTick(Now);
        }

        MeanQueueDelayMs = queueSum / Math.Max(1, queueSamples);
        DeliveredBps = (long)(
            _deliveredBytes * 8 / Math.Max(0.001, (Now - _deliveredSince).TotalSeconds)
        );
    }

    // The source produces at the target rate; the pacer lets packets go at the pacing rate when the send
    // window allows.
    private void Send()
    {
        CapacityEstimate capacity = controller.Current;
        _sendCredit = Math.Min(
            _sendCredit + (capacity.TargetBitsPerSecond / 8.0 * Step.TotalSeconds),
            50 * PacketSize
        );
        _paceCredit = Math.Min(
            _paceCredit + (capacity.PacingBitsPerSecond / 8.0 * Step.TotalSeconds),
            4 * PacketSize
        );
        while (
            _sendCredit >= PacketSize
            && _paceCredit >= PacketSize
            && controller.CanTransmit(PacketSize, Now)
        )
        {
            _sendCredit -= PacketSize;
            _paceCredit -= PacketSize;
            SentPacket packet = new(++_nextId, PacketSize, Now, TrafficClass.Video);
            controller.OnPacketSent(packet);
            _tracker.OnSent(packet);
            Enter(packet.Id);
        }
    }

    // Into the bottleneck: random loss, then the bottleneck's own policy.
    private void Enter(long id)
    {
        if (RandomLoss > 0 && _random.NextDouble() < RandomLoss)
        {
            _lost.Add(id);
            return;
        }

        var serialization = TimeSpan.FromSeconds(PacketSize * 8.0 / CapacityBps);
        TimeSpan start = _linkFree > Now ? _linkFree : Now;
        TimeSpan queued = start - Now;
        bool drop = Kind switch
        {
            Bottleneck.Policer => queued > serialization,
            _ => queued > QueueLimit,
        };
        if (drop)
        {
            _lost.Add(id);
            return;
        }

        bool ce =
            (Kind == Bottleneck.L4sMarking && queued > TimeSpan.FromMilliseconds(2))
            || (Kind == Bottleneck.ClassicMarking && queued > TimeSpan.FromMilliseconds(15));
        _linkFree = start + serialization;
        _link.Enqueue((id, _linkFree + BaseDelay, ce));
    }

    private void Deliver()
    {
        while (_link.TryPeek(out (long Id, TimeSpan Departs, bool Ce) head) && head.Departs <= Now)
        {
            _ = _link.Dequeue();
            _arrived.Add((head.Id, head.Departs, head.Ce));
            _deliveredBytes += PacketSize;
        }
    }

    // A report of everything since the last one: arrivals with their times and marks, and the gaps below
    // the highest arrival as missing. It reaches the sender after the base delay.
    private void Feedback()
    {
        _lastFeedback = Now;
        if (_arrived.Count == 0)
        {
            return;
        }

        long highest = _arrived.Max(static a => a.Id);
        List<PacketReport> reports = [];
        HashSet<long> got = [.. _arrived.Select(static a => a.Id)];
        foreach ((long id, TimeSpan arrived, bool ce) in _arrived)
        {
            reports.Add(
                new PacketReport(
                    id,
                    true,
                    ReportArrivalTimes ? arrived : null,
                    ce ? EcnCodepoint.Ce : EcnCodepoint.Ect1
                )
            );
            Marked += ce ? 1 : 0;
        }

        for (long id = _highestReported + 1; id < highest; id++)
        {
            if (!got.Contains(id))
            {
                reports.Add(new PacketReport(id, false, null, EcnCodepoint.NotEct));
            }
        }

        _highestReported = Math.Max(_highestReported, highest);
        _arrived.Clear();
        reports.Sort(static (a, b) => a.Id.CompareTo(b.Id));
        _returning.Enqueue((Now + BaseDelay, [.. reports]));
    }

    // Reports that have crossed the return path reach the tracker and the controller.
    private void Return()
    {
        while (
            _returning.TryPeek(out (TimeSpan Due, PacketReport[] Reports) next) && next.Due <= Now
        )
        {
            _ = _returning.Dequeue();
            _observations.Clear();
            TimeSpan? roundTrip = _tracker.OnFeedback(next.Reports, Now, _observations);
            Lost += _observations.Count(static o => o.Outcome == PacketOutcome.Lost);
            _ = controller.OnFeedback(_observations.ToArray(), roundTrip, Now);
        }
    }
}
