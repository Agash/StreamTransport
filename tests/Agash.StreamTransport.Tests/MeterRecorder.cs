using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace Agash.StreamTransport.Tests;

// An IMeterFactory whose meters are this recorder's alone, as a host's container makes them, with a
// listener that hears only those.
internal sealed class MeterRecorder : IMeterFactory
{
    private readonly MeterListener _listener = new();
    private readonly ConcurrentQueue<(string Instrument, double Value, string? Tag)> _measurements =
        new();

    public MeterRecorder()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Scope == this)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((i, v, t, _) => Record(i, v, t));
        _listener.SetMeasurementEventCallback<double>((i, v, t, _) => Record(i, v, t));
        _listener.Start();
    }

    public Meter Create(MeterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Scope = this;
        return new Meter(options);
    }

    // The sum of an instrument's measurements, those tagged with a value when one is given.
    public double Sum(string instrument, string? tag = null) =>
        Matching(instrument, tag).Sum(static m => m.Value);

    public int Count(string instrument, string? tag = null) => Matching(instrument, tag).Count();

    // An instrument's measurements so far, in the order they were recorded.
    public double[] Values(string instrument, string? tag = null) =>
        [.. Matching(instrument, tag).Select(static m => m.Value)];

    public void Dispose() => _listener.Dispose();

    private IEnumerable<(string Instrument, double Value, string? Tag)> Matching(
        string instrument,
        string? tag
    ) => _measurements.Where(m => m.Instrument == instrument && (tag is null || m.Tag == tag));

    private void Record(
        Instrument instrument,
        double value,
        ReadOnlySpan<KeyValuePair<string, object?>> tags
    )
    {
        string? tag = null;
        foreach (KeyValuePair<string, object?> pair in tags)
        {
            if (
                pair.Key
                is "streamtransport.codec"
                    or "streamtransport.outcome"
                    or "streamtransport.reason"
                    or "streamtransport.webrtc.ice.local_kind"
                    or "streamtransport.signaling.message_type"
            )
            {
                tag = pair.Value as string;
            }
        }

        _measurements.Enqueue((instrument.Name, value, tag));
    }
}
