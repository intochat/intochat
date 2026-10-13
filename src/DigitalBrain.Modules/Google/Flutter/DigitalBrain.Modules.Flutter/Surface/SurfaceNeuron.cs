using DigitalBrain;
using DigitalBrain.Framework;
using Orleans.Runtime;

namespace DigitalBrain.Flutter.Surface;

[GrainType(UIVocabulary.SurfaceType)]
internal sealed class SurfaceNeuron(
    [PersistentState("state", DigitalBrainNames.ClusterGrainStorage)] IPersistentState<SurfaceState> store)
    : Neuron, ISurface
{
    public async Task Set(SurfaceDefinition definition, long expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Title);
        CompositionValidation.Children(definition.Children);
        if (store.State.Revision != expectedRevision) { throw new RevisionConflictException("The UI changed. Read its latest revision before retrying.", store.State.Revision); }
        var previous = store.State;
        store.State = new() { Name = this.GetPrimaryKeyString(), Revision = checked(previous.Revision + 1), Definition = definition };
        try { await store.WriteStateAsync(); }
        catch { store.State = previous; throw; }
        await PublishAsync(new SurfaceChanged(this.GetPrimaryKeyString(), store.State.Revision));
    }

    public Task<SurfaceState> Read() => Task.FromResult(store.State);
}
