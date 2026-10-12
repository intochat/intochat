using System.Collections.Concurrent;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Parts;
using Orleans.Core.Internal;
using Orleans.Runtime;

namespace DigitalBrain.Platform.Parts;

// The machine's own activations of a part's grains: deactivated before the part is handed to another machine, so
// none keeps serving state from a log this machine no longer holds, nor acts on the part (R107). That is every grain
// with a record in the part's log, and every neuron activated here under the part's key: a stateless one (a timer, a
// reminder) writes no record, so only its activation ties it to the part. Local grains only, never trust grains.
internal sealed class PartActivations(IGrainFactory grains, PartLogs logs, ActivatedNeurons neurons)
{
    public Task Release(PartId part) => logs.Release(part, recorded => Deactivate(part, recorded));

    internal async Task Deactivate(PartId part, string[] recorded)
    {
        HashSet<GrainId> ids = [.. neurons.Of(part)];
        foreach (var grainId in recorded)
        {
            if (GrainId.TryParse(grainId, out var id)) { ids.Add(id); }
        }
        foreach (var id in ids) { await grains.GetGrain(id).AsReference<IGrainManagementExtension>().DeactivateOnIdle(); }
    }
}

// The neurons activated on this silo, fed by its activation working set: an activation is tracked from its activation
// until it is deactivated (leaving the working set while idle does not end it).
internal sealed class ActivatedNeurons : IActivationWorkingSetObserver
{
    private readonly ConcurrentDictionary<IActivationWorkingSetMember, GrainId> _active = new(ReferenceEqualityComparer.Instance);

    public GrainId[] Of(PartId part) => [.. _active.Values.Where(id => PartRoutes.KeyedPart(id) == part)];

    public void OnAdded(IActivationWorkingSetMember member) => Track(member);

    public void OnActive(IActivationWorkingSetMember member) => Track(member);

    public void OnDeactivated(IActivationWorkingSetMember member) => _active.TryRemove(member, out _);

    private void Track(IActivationWorkingSetMember member)
    {
        if (member is IGrainContext { GrainInstance: Neuron } context) { _active.TryAdd(member, context.GrainId); }
    }
}
