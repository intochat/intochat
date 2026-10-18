using DigitalBrain.Framework;
using DigitalBrain.Framework.Compute;

namespace DigitalBrain.Authority.Compute;

internal sealed class ComputeReader(AuthorityNeurons neurons) : IComputeReader
{
    public async Task<BrainUsageSummary> Read(DateTimeOffset since)
    {
        var usage = neurons.Usage(BrainScope.CurrentId());
        var rows = await usage.Group(UsageGrouping.App, since, ComputeEndpoints.MaximumTake);
        var totals = await usage.Group(UsageGrouping.Kind, since, ComputeEndpoints.MaximumTake);
        return new(totals.Sum(row => row.Micros), rows);
    }
}
