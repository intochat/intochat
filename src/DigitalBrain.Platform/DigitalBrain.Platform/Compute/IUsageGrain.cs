using DigitalBrain.Framework.Compute;

namespace DigitalBrain.Platform.Compute;

// Per brain, keyed by brain id. Recent is a ring buffer of the last costs; Group reads rolling
// daily buckets rolled up by origin, app, synapse, neuron and kind. A plain grain, not a neuron,
// so no script can address it.
internal interface IUsageGrain : IGrainWithStringKey
{
    Task Add(Cost cost, string origin);
    Task<CostRow[]> Group(UsageGrouping by, DateTimeOffset since, int take);
    Task<Cost[]> Recent(int take);
}