using DigitalBrain.Framework.Parts;
using Orleans.Core.Internal;
using Orleans.Runtime;

namespace DigitalBrain.Platform.Parts;

// The machine's own activations of a part's grains: deactivated before the part is handed to another machine, so
// none keeps serving state from a log this machine no longer holds. Local grains only, never trust grains.
internal sealed class PartActivations(IGrainFactory grains, PartLogs logs)
{
    public Task Release(PartId part) => logs.Release(part, Deactivate);

    private async Task Deactivate(string[] grainIds)
    {
        foreach (var grainId in grainIds)
        {
            if (!GrainId.TryParse(grainId, out var id)) { continue; }
            await grains.GetGrain(id).AsReference<IGrainManagementExtension>().DeactivateOnIdle();
        }
    }
}
