using DigitalBrain.Framework;
using Orleans.Runtime;

namespace DigitalBrain.Platform.Discovery;

internal sealed class ActivationCensus(IGrainFactory grains) : IActivationCensus
{
    public async Task<ActivationRecord[]> Activations(string grainTypeId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(grainTypeId);
        var statistics = await grains.GetGrain<IManagementGrain>(0).GetDetailedGrainStatistics().WaitAsync(ct);
        return statistics.Where(item => item.GrainId.Type.ToString() == grainTypeId)
            .Select(item => new ActivationRecord(item.GrainId.Type.ToString(), item.GrainId.Key.ToString())).Distinct().ToArray();
    }
}
