using DigitalBrain.Framework;
using DigitalBrain.Framework.Compute;

namespace DigitalBrain.Platform.Compute;

internal sealed class ComputeReader(IGrainFactory grains) : IComputeReader
{
    public async Task<BrainUsageSummary> Read(DateTimeOffset since)
    {
        var usage = grains.GetGrain<IUsageGrain>(BrainScope.CurrentId());
        var rows = await usage.Group(UsageGrouping.App, since, ComputeEndpoints.MaximumTake);
        var totals = await usage.Group(UsageGrouping.Kind, since, ComputeEndpoints.MaximumTake);
        return new(totals.Sum(row => row.Micros), rows);
    }
}
