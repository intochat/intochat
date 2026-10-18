using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntoChat.OS.Authority.Storage;

// Running totals of brain-log ownership events in this silo. The writer lock reports none ("none"). A lease-based
// ownership registers its source with UseBrainOwnershipCounters, before or after AddIntoChatOSPlatform: the
// platform only TryAdds the no-op default, and the helper replaces whatever is registered, so exactly one remains.
public interface IBrainOwnershipCounters
{
    BrainOwnershipCounts Snapshot();
}

public sealed record BrainOwnershipCounts(string Mechanism, long Acquired, long Refused, long Lost, long Released, long Renewed, long Fenced);

internal sealed class NoBrainOwnershipCounters : IBrainOwnershipCounters
{
    private static readonly BrainOwnershipCounts None = new("none", 0, 0, 0, 0, 0, 0);
    public BrainOwnershipCounts Snapshot() => None;
}

// brainlog.ownership{mechanism,event}: observed from the registered source, exported through the IntoChat.OS.* meter rule.
internal sealed class BrainOwnershipMetrics
{
    public const string MeterName = "IntoChat.OS.Authority.Storage";
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
                new Measurement<long>(counts.Renewed, mechanism, new("event", "renewed")),
                new Measurement<long>(counts.Fenced, mechanism, new("event", "fenced")),
            };
        }, unit: "{event}", description: "Brain-log ownership events in this silo.");
    }
}

public static class BrainOwnershipCountersRegistration
{
    // Replaces the registered counters (the no-op default or an earlier source) with this one, in either order
    // relative to AddIntoChatOSPlatform.
    public static IServiceCollection UseBrainOwnershipCounters(this IServiceCollection services, IBrainOwnershipCounters counters)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(counters);
        services.RemoveAll<IBrainOwnershipCounters>();
        services.AddSingleton(counters);
        return services;
    }

    public static IServiceCollection UseBrainOwnershipCounters<TCounters>(this IServiceCollection services)
        where TCounters : class, IBrainOwnershipCounters
    {
        ArgumentNullException.ThrowIfNull(services);
        services.RemoveAll<IBrainOwnershipCounters>();
        services.AddSingleton<IBrainOwnershipCounters, TCounters>();
        return services;
    }
}
