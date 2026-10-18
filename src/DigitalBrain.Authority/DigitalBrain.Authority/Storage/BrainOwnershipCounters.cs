using System.Diagnostics.Metrics;

namespace DigitalBrain.Authority.Storage;

// Running totals of brain-log ownership events in this silo. The writer lock reports none ("none"); a lease-based
// ownership registers its own source before the platform, and diagnostics and metrics read that instead.
public interface IBrainOwnershipCounters
{
    BrainOwnershipCounts Snapshot();
}

public sealed record BrainOwnershipCounts(string Mechanism, long Acquired, long Refused, long Lost, long Released);

internal sealed class NoBrainOwnershipCounters : IBrainOwnershipCounters
{
    private static readonly BrainOwnershipCounts None = new("none", 0, 0, 0, 0);
    public BrainOwnershipCounts Snapshot() => None;
}

// brainlog.ownership{mechanism,event}: observed from the registered source, exported through the DigitalBrain.* meter rule.
internal sealed class BrainOwnershipMetrics
{
    public const string MeterName = "DigitalBrain.Authority.Storage";
    public const string InstrumentName = "brainlog.ownership";

    public BrainOwnershipMetrics(IMeterFactory meters, IBrainOwnershipCounters counters)
    {
        var meter = meters.Create(MeterName);
        meter.CreateObservableCounter(InstrumentName, () =>
        {
            var counts = counters.Snapshot();
            var mechanism = new KeyValuePair<string, object?>("mechanism", counts.Mechanism);
            return new[]
            {
                new Measurement<long>(counts.Acquired, mechanism, new("event", "acquired")),
                new Measurement<long>(counts.Refused, mechanism, new("event", "refused")),
                new Measurement<long>(counts.Lost, mechanism, new("event", "lost")),
                new Measurement<long>(counts.Released, mechanism, new("event", "released")),
            };
        }, unit: "{event}", description: "Brain-log ownership events in this silo.");
    }
}
