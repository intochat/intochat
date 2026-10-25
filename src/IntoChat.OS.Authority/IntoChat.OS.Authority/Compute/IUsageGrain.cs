using IntoChat.OS.Contracts.Compute;
using Orleans.Concurrency;

namespace IntoChat.OS.Authority.Compute;

// Per brain, keyed by brain id. Recent is a ring buffer of the last costs; Group reads rolling
// daily buckets rolled up by origin, app, synapse, neuron and kind. A plain grain, not a neuron,
// so no script can address it.
[Alias("compute.usage")]
internal interface IUsageGrain : IGrainWithStringKey
{
    // One-way: usage observes the brain's spend, so recording a cost never holds the metered call.
    [OneWay]
    Task Add(Cost cost, string origin);
    Task Apply(UsageProjection projection);
    Task<CostRow[]> Group(UsageGrouping by, DateTimeOffset since, int take);
    Task<Cost[]> Recent(int take);
}
